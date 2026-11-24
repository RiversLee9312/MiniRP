using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private Dictionary<string, RoomCharacter> roomCharacters = new(StringComparer.Ordinal);
    private RoomActivity? roomActivity;
    private CancellationTokenSource? preparingRoom;

    internal AgentSession? LoadedCharacterSession(string participant, string role) =>
        Volatile.Read(ref roomCharacters).TryGetValue(participant, out var actor) ? actor.LoadedSession(role) : null;

    private void CloseRoomCharacters()
    {
        Interlocked.Exchange(ref roomActivity, null)?.Stop();
        var closing = Interlocked.Exchange(ref roomCharacters, new(StringComparer.Ordinal));
        foreach (var actor in closing.Values) actor.Dispose();
    }

    private async Task PrepareRoom(Workspace workspace, EnsembleConversation room,
        Dictionary<string, RoomCharacter> characters, bool newRoom, CancellationToken token)
    {
        foreach (var definition in room.Agents)
        {
            var id = definition.GetString("id");
            characters.Add(id, new(this, room, workspace, id));
        }
        var greeting = workspace.ChatGreeting;
        var openings = greeting.Length == 0 ? [] : characters.Values
            .SelectMany(character => character.Scenes().Where(scene => !room.Opened(scene, character.Id))
                .Select(scene => (Character: character, Scene: scene))).ToArray();

        var ready = 0;
        var opened = 0;
        var progress = new int[characters.Count];
        var progressGate = new object();
        void EmitLoading(bool loading) => Emit(new()
        {
            ["kind"] = "room_loading", ["preset"] = workspace.PresetId, ["room"] = workspace.ChatId,
            ["value"] = loading, ["ready"] = ready, ["total"] = characters.Count,
            ["progress"] = (ready + progress.Sum() / 100d + opened) / (characters.Count + openings.Length),
            ["messages"] = workspace.Config.GetObject("ensemble").GetArray("loading_messages").DeepClone()
        });
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Volatile.Write(ref preparingRoom, preparation);
        async Task Together<T>(IEnumerable<T> work, Func<T, Task> run) => await Task.WhenAll(work.Select(async item =>
        {
            try { await run(item); }
            catch
            {
                await preparation.CancelAsync();
                throw;
            }
        }));

        var saved = false;
        try
        {
            EmitLoading(true);
            await Together(characters.Values, character => character.Initialize(preparation.Token));
            var pending = characters.Values.Where(character => !character.IsReady).ToArray();
            ready = characters.Count - pending.Length;
            EmitLoading(true);
            if (pending.Length == 0 && openings.Length == 0) return;
            if (!CanGreet) throw new InvalidOperationException("Configure a provider and model first.");
            if (newRoom)
            {
                workspace.Save();
                saved = true;
            }
            await Together(pending.Select((character, index) => (character, index)), async item =>
            {
                var (character, index) = item;
                await character.Prepare(percent =>
                {
                    lock (progressGate)
                    {
                        progress[index] = percent;
                        EmitLoading(true);
                    }
                }, preparation.Token);
                lock (progressGate)
                {
                    ready++;
                    progress[index] = 0;
                    EmitLoading(true);
                }
            });
            await Together(openings.GroupBy(item => item.Character), async group =>
            {
                foreach (var opening in group)
                {
                    preparation.Token.ThrowIfCancellationRequested();
                    await opening.Character.Open(opening.Scene, greeting, preparation.Token);
                    lock (progressGate)
                    {
                        opened++;
                        EmitLoading(true);
                    }
                }
            });
            if (openings.Length > 0)
                await Together(characters.Values, character => Task.Run(character.Synchronize, preparation.Token));
        }
        catch
        {
            if (saved)
                await Index(workspace);
            throw;
        }
        finally
        {
            Volatile.Write(ref preparingRoom, null);
            EmitLoading(false);
        }
    }

    // Related replies share a causal graph. A repeated edge still delivers the message
    // to history, but does not start another model turn on the same chain.
    private sealed class RoomCause
    {
        private readonly HashSet<(string Scene, string Sender, string Recipient)> prompted = [];
        public bool First(string scene, string sender, string recipient) => prompted.Add((scene, sender, recipient));
    }

    private sealed record RoomDelivery(string Scene, JsonObject Message, string Interaction, string? Locale, RoomCause Cause,
        string[] Ancestors, bool Respond, CancellationToken Token);

    private sealed class RoomActivity(Engine engine, EnsembleConversation room, Dictionary<string, RoomCharacter> characters)
    {
        private readonly CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(engine.lifetime.Token);
        private readonly Dictionary<string, CancellationTokenSource> turns = new(StringComparer.Ordinal);
        private readonly List<CancellationTokenSource> retired = [];
        private Task workers = Task.CompletedTask;
        private bool closing;

        public void Start() => workers = Task.WhenAll(characters.Values.Select(character =>
            Task.Run(() => character.Listen(this, stopped.Token))));

        public void Publish(string scene, JsonObject item, string interaction = "", string? locale = null,
            RoomCause? cause = null, string[]? ancestry = null)
        {
            lock (room.SyncRoot)
            {
                if (closing || stopped.IsCancellationRequested) return;
                cause ??= new();
                ancestry ??= [];
                var sender = item.GetString("participant");
                if (!turns.TryGetValue(scene, out var turn))
                    turns.Add(scene, turn = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token));
                foreach (var member in room.Members(scene))
                {
                    if (member == sender || !characters.TryGetValue(member, out var character)) continue;
                    var respond = !ancestry.Contains(member, StringComparer.Ordinal) && cause.First(scene, sender, member);
                    character.Deliver(new(scene, item, interaction, locale, cause, ancestry, respond, turn.Token));
                }
            }
        }

        public void Cancel(string scene = "")
        {
            lock (room.SyncRoot)
            {
                if (closing) return;
                var scenes = scene.Length > 0 ? [scene] : turns.Keys.ToArray();
                foreach (var key in scenes)
                    if (turns.Remove(key, out var turn))
                    {
                        retired.Add(turn);
                        turn.Cancel();
                    }
            }
        }

        public void Stop()
        {
            lock (room.SyncRoot) closing = true;
            stopped.Cancel();
            workers.GetAwaiter().GetResult();
            foreach (var turn in turns.Values.Concat(retired)) turn.Dispose();
            stopped.Dispose();
        }
    }

    /// <summary>Owns a character's durable chat and optional search sessions and a single model worker.</summary>
    private sealed class RoomCharacter : IDisposable
    {
        private readonly Engine engine;
        private readonly EnsembleConversation room;
        private readonly Workspace workspace;
        private readonly string name;
        private readonly string prompt;
        private readonly Channel<RoomDelivery> deliveries = Channel.CreateUnbounded<RoomDelivery>(new()
            { SingleReader = true, AllowSynchronousContinuations = false });
        private readonly Dictionary<string, JsonObject> currentTrace = new(StringComparer.Ordinal);
        private AgentSession? search;
        private string conversation = "";
        private bool opening;
        private Action<string, JsonObject>? posted;

        public string Id { get; }
        public AgentSession Session { get; private set; } = null!;
        private ChatAgent Agent { get; set; } = null!;
        public bool IsReady => Session.Db.Get("_room/ready") == "true";

        public RoomCharacter(Engine engine, EnsembleConversation room, Workspace workspace, string participant)
        {
            this.engine = engine;
            this.room = room;
            this.workspace = workspace;
            Id = participant;
            var definition = room.Agent(participant);
            name = definition.GetString("name");
            prompt = definition.GetString("prompt");
        }

        public async Task Initialize(CancellationToken token)
        {
            var chatStart = Task.Run(() => Session = engine.agentSessions.Open("chat", room.Database(Id, "chat"),
                () => Volatile.Read(ref engine.routing).For("chat"), () => Context("chat"), (item, info) => Record("chat", item, info),
                () => [room.CharacterSkill(Id, conversation, posted!, PublishAssets, opening)], CanReadChat,
                conversationWorkspace: workspace), token);
            var searchStart = workspace.AgenticSearch ? Task.Run(() => SearchSession(), token) : Task.CompletedTask;
            await Task.WhenAll(chatStart, searchStart);
            var research = new SearchAgent(workspace, SearchSession, CreatePageSource(), "Research for " + name + ". " + prompt,
                () => RecentEnsembleChatReference(room, conversation));
            Agent = new(workspace, Session, research, "Your character is " + name + ". " + prompt);
        }

        public async Task Prepare(Action<int> progress, CancellationToken token)
        {
            var context = "Your chats: " + string.Join(", ", Scenes().Select(room.Name)) + "\n" + workspace.AgentPrompt("chat");
            using var preparation = engine.agentSessions.Open("room-preparation", ":memory:",
                () => Volatile.Read(ref engine.routing).For("chat"), () => Context("chat"), _ => { });
            await Agent.Prepare(preparation, context, progress, token);
            Session.Db.Write("_room/ready", "true", internalWrite: true);
        }

        public string[] Scenes() => room.JoinedScenes(Id);

        public async Task Open(string scene, string greeting, CancellationToken token)
        {
            if (room.Opened(scene, Id)) return;
            await Run(scene, greeting + "\nGive your opening in this chat if appropriate. Use chat_send to post it. " +
                "You may stay silent when the opening calls for it.", (_, _) => room.ConfirmOpening(scene, Id), token, isOpening: true);
            room.ConfirmOpening(scene, Id);
        }

        public void Deliver(RoomDelivery delivery) => deliveries.Writer.TryWrite(delivery);

        public async Task Listen(RoomActivity activity, CancellationToken stopping)
        {
            try
            {
                await foreach (var delivery in deliveries.Reader.ReadAllAsync(stopping))
                {
                    if (delivery.Token.IsCancellationRequested || !CanReadChat(delivery.Scene)) continue;
                    try
                    {
                        Synchronize();
                        if (!delivery.Respond) continue;
                        var input = "A new post in this chat may need your response. In the conversation history, the triggering post has ID " +
                            delivery.Message.GetString("id") + ". Decide whether a useful visible reply is appropriate; otherwise stay silent." +
                            delivery.Interaction;
                        var lineage = delivery.Ancestors.Append(Id).ToArray();
                        engine.operationLocale.Value = delivery.Locale;
                        await Run(delivery.Scene, input, (target, message) =>
                        {
                            engine.PostEnsembleMessage(target, message, notifyCharacters: false);
                            activity.Publish(target, message, locale: delivery.Locale, cause: delivery.Cause, ancestry: lineage);
                        }, delivery.Token, attachments: delivery.Message.GetArray("attachments"));
                    }
                    catch (OperationCanceledException) when (delivery.Token.IsCancellationRequested || stopping.IsCancellationRequested) { }
                    catch (Exception error)
                    {
                        engine.Emit(new() { ["kind"] = "error", ["agent"] = "chat", ["model_turn"] = true,
                            ["preset"] = workspace.PresetId, ["chat"] = workspace.ChatId, ["conversation"] = delivery.Scene, ["participant"] = Id,
                            ["message"] = error.Message });
                    }
                    finally { engine.operationLocale.Value = null; }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }

        private async Task Run(string scene, string input, Action<string, JsonObject> publish, CancellationToken token,
            bool isOpening = false, JsonArray? attachments = null)
        {
            conversation = scene;
            opening = isOpening;
            posted = publish;
            var finishMessage = "Cancelled.";
            try
            {
                if (isOpening) Synchronize();
                var context = "The active chat is " + room.Name(scene) + " (" + scene + "). Only chat_send posts a visible reply.";
                await Agent.Run(input, token, attachments, inputRole: "system", privateInput: true, turnInstructions: context);
                finishMessage = "Finished.";
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested) finishMessage = error.Message;
                throw;
            }
            finally
            {
                lock (room.SyncRoot)
                {
                    foreach (var block in currentTrace.Values.Where(block => block.GetBoolean("running")).ToArray())
                    {
                        var end = new JsonObject { ["kind"] = block.GetBoolean("compaction") ? "context_trace" : "tool_end",
                            ["agent"] = block.GetString("agent"), ["block"] = block.GetString("id"), ["chat"] = workspace.ChatId,
                            ["preset"] = workspace.PresetId, ["conversation"] = scene, ["participant"] = Id, ["speaker"] = name,
                            ["session"] = JsonValues.Clone(block["session"]),
                            ["name"] = block.GetString("tool"), ["text"] = finishMessage };
                        RecordTrace(end, currentTrace, room.Blocks(scene));
                        if (!isOpening) engine.Emit(end);
                    }
                    currentTrace.Clear();
                    workspace.Save();
                }
                posted = null;
                conversation = "";
                opening = false;
            }
        }

        public void Synchronize()
        {
            Session.ConfigureWorldMode(workspace.AgenticSearch);
            if (workspace.AgenticSearch) _ = SearchSession();
            lock (room.SyncRoot)
            {
                EnsembleConversation.DeliverPrivate(Session);
                room.Deliver(Id, Session);
            }
        }

        private bool CanReadChat(string target) => room.IsMember(target, Id);

        public AgentSession? LoadedSession(string role) => role switch
        {
            "chat" => Session,
            "search" => search,
            _ => null
        };

        private JsonObject Context(string role) => new()
        {
            ["agent"] = role, ["preset"] = workspace.PresetId, ["room"] = workspace.ChatId,
            ["chat"] = conversation.Length > 0 ? conversation : null, ["_ensemble"] = true,
            ["participant"] = Id, ["session"] = role + "-" + workspace.ChatId + "-" + Id
        };

        private AgentSession SearchSession() => search ??= engine.agentSessions.Open("search", room.Database(Id, "search"),
            () => Volatile.Read(ref engine.routing).For("search"), () => Context("search"), (item, info) => Record("search", item, info),
            canReadChat: CanReadChat, conversationWorkspace: workspace);

        private PageSource CreatePageSource()
        {
            Routing Selected() => Volatile.Read(ref engine.routing);
            var fetch = new FetchAgent(engine.Port, job =>
            {
                var context = Context("fetch");
                context["session"] = "fetch-" + job;
                return engine.agentSessions.Open("fetch", SessionFiles.Fetch(workspace.ChatRoot, job), () => Selected().For("fetch"),
                    () => context, (item, info) => Record("fetch", item, info, context), canReadChat: CanReadChat);
            }, () => workspace.AgentPrompt("fetch"));
            return new(engine.Port, fetch, () => Selected().Settings);
        }

        private void Record(string role, JsonObject item, SessionModelInfo? info, JsonObject? fetchContext = null)
        {
            // Dialogue goes through chat_send. Plain model prose remains in the character's history.
            var scene = fetchContext?.GetString("chat") ?? conversation;
            if (scene.Length == 0 || item.GetString("kind") == "delta" && item.GetString("type") == "content") return;
            if (item.GetString("kind") == "turn_end") item.Remove("asset_links");
            var session = (fetchContext ?? Context(role)).GetString("session");
            if (item.GetString("block") is { Length: > 0 } block)
                item["block"] = fetchContext is null ? Id + "-" + block : Id + "-" + session + "-" + block;
            item["preset"] = workspace.PresetId;
            item["conversation"] = scene;
            item["chat"] = workspace.ChatId;
            item["participant"] = Id;
            item["speaker"] = name;
            item["session"] = session;
            lock (room.SyncRoot)
            {
                RecordTrace(item, currentTrace, room.Blocks(scene));
                if (!opening)
                {
                    engine.RecordWebDisplay(item, currentTrace, scene, workspace);
                    engine.IndexDisplayAssets(item, item.GetString("block"), scene, scope: workspace);
                    engine.Emit(item, info);
                }
            }
        }

        private void PublishAssets(JsonObject message, string text) => Session.AddPublishedAssets(message, text);

        public void Dispose()
        {
            search?.Dispose();
            Session?.Dispose();
        }
    }
}
