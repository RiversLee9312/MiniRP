using System.Text.Json.Nodes;

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
        var participants = characters.Values.Where(character => room.IsEnabled(character.Id)).ToArray();
        var openings = greeting.Length == 0 ? [] : participants.Where(character => !room.Opened(character.Id)).ToArray();

        var ready = 0;
        var opened = 0;
        var limits = plugins.RoomLoadingLimits(workspace.PresetId, participants.Select(character => character.Id));
        var declaredProgress = limits.Count > 0;
        var totalProgress = declaredProgress ? limits.Values.Sum() : 100L * (participants.Length + openings.Length);
        long Maximum(string id) => declaredProgress ? limits.GetValueOrDefault(id) : 100;
        var progress = new Dictionary<string, long>(StringComparer.Ordinal);
        var progressGate = new object();
        void EmitLoading(bool loading) => Emit(new()
        {
            ["kind"] = "room_loading", ["preset"] = workspace.PresetId, ["room"] = workspace.ChatId,
            ["value"] = loading, ["ready"] = ready, ["total"] = participants.Length,
            ["declared_progress"] = declaredProgress,
            ["progress"] = totalProgress == 0 ? 1d :
                (progress.Values.Sum() + (declaredProgress ? 0 : 100L * opened)) / (double)totalProgress,
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
            var pending = participants.Where(character => !character.IsReady).ToArray();
            ready = participants.Length - pending.Length;
            foreach (var character in participants.Where(character => character.IsReady))
                progress[character.Id] = Maximum(character.Id);
            EmitLoading(true);
            if (pending.Length == 0 && openings.Length == 0) return;
            if (!CanGreet) throw new InvalidOperationException("Configure a provider and model first.");
            if (newRoom)
            {
                workspace.Save();
                saved = true;
            }
            await Together(pending, async character =>
            {
                var maximum = Maximum(character.Id);
                await character.Prepare(maximum, declaredProgress, value =>
                {
                    lock (progressGate)
                    {
                        progress[character.Id] = value;
                        EmitLoading(true);
                    }
                }, preparation.Token);
                lock (progressGate)
                {
                    ready++;
                    progress[character.Id] = maximum;
                    EmitLoading(true);
                }
            });
            await Together(openings, async character =>
            {
                await character.Open(greeting, preparation.Token);
                lock (progressGate)
                {
                    opened++;
                    EmitLoading(true);
                }
            });
            if (openings.Length > 0)
                await Together(characters.Values, character => Task.Run(() => character.Synchronize(), preparation.Token));
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
        private readonly Dictionary<string, JsonObject> thinking = new(StringComparer.Ordinal);
        private readonly List<CancellationTokenSource> retired = [];
        private Task workers = Task.CompletedTask;
        private bool closing;

        public void Start() => workers = Task.WhenAll(characters.Values.Select(character =>
            Task.Run(() => character.Listen(this, stopped.Token))));

        public void Report(Workspace workspace, string scene, string participant, string name, bool running)
        {
            lock (room.SyncRoot)
            {
                if (running && (!room.IsActive(scene) || !room.CanPost(participant))) return;
                var item = new JsonObject
                {
                    ["kind"] = "ensemble_activity", ["preset"] = workspace.PresetId, ["chat"] = workspace.ChatId,
                    ["conversation"] = scene, ["participant"] = participant, ["speaker"] = name, ["running"] = running
                };
                if (running) thinking[participant] = item;
                else thinking.Remove(participant);
                engine.Emit(item);
            }
        }

        public JsonArray Snapshot(string? scene = null)
        {
            lock (room.SyncRoot)
                return new JsonArray(thinking.Values.Where(item => room.IsActive(item.GetString("conversation")) &&
                    (scene is null || item.GetString("conversation") == scene))
                    .Select(JsonValues.Clone).ToArray());
        }

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
                    if (member == sender || !characters.TryGetValue(member, out var character) || !room.IsEnabled(member)) continue;
                    var respond = !ancestry.Contains(member, StringComparer.Ordinal) && cause.First(scene, sender, member);
                    character.Deliver(new(scene, item, interaction, locale, cause, ancestry, respond, turn.Token));
                }
            }
        }

        public JsonObject RequestLifecycle(string member, bool enable, string message)
        {
            lock (room.SyncRoot)
            {
                if (closing || !characters.TryGetValue(member, out var character))
                    throw new InvalidOperationException("The character's room worker is unavailable.");
                return character.RequestLifecycle(enable, message);
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
    private sealed partial class RoomCharacter : IDisposable
    {
        private readonly Engine engine;
        private readonly EnsembleConversation room;
        private readonly Workspace workspace;
        private readonly string name;
        private readonly string prompt;
        private readonly Dictionary<string, JsonObject> currentTrace = new(StringComparer.Ordinal);
        private AgentSession? search;
        private AgentSession? directSession;
        private AgentSession? directSearch;
        private ChatAgent? directAgent;
        private string conversation = "";
        private bool opening;
        private bool direct;
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
            var chatStart = Task.Run(() => Session = ChatSession(isDirect: false), token);
            var searchStart = workspace.AgenticSearch ? Task.Run(() => SearchSession(isDirect: false), token) : Task.CompletedTask;
            await Task.WhenAll(chatStart, searchStart);
            Agent = CreateAgent(Session, isDirect: false);
        }

        private AgentSession ChatSession(bool isDirect) => engine.agentSessions.Open("chat", room.Database(Id, "chat", isDirect),
            () => Volatile.Read(ref engine.routing).For("chat"), () => Context("chat"), (item, info) => Record("chat", item, info),
            CharacterSkills, CanReadChat,
            conversationWorkspace: workspace);

        private AgentSession DirectSession() => directSession ??= ChatSession(isDirect: true);

        private ChatAgent CreateAgent(AgentSession session, bool isDirect)
        {
            var research = new SearchAgent(workspace, () => SearchSession(isDirect), CreatePageSource(),
                "Research for " + name + ". " + prompt, () => RecentEnsembleChatReference(room, conversation), persistResults: !isDirect);
            return new(workspace, session, research, "Your character is " + name + ". " + prompt);
        }

        public async Task Prepare(long maximum, bool declaredProgress, Action<long> progress, CancellationToken token)
        {
            var context = "Your chats: " + string.Join(", ", Scenes().Select(room.Name)) + "\n" + workspace.AgentPrompt("chat");
            using var preparation = engine.agentSessions.Open("room-preparation", ":memory:",
                () => Volatile.Read(ref engine.routing).For("chat"), () => Context("chat"), _ => { });
            await Agent.Prepare(preparation, context, maximum, declaredProgress, progress, token);
            Session.Db.Write("_room/ready", "true", internalWrite: true);
        }

        public string[] Scenes() => room.JoinedScenes(Id);

        public async Task Open(string greeting, CancellationToken token)
        {
            await Run("", greeting, (_, _) => { }, token, isOpening: true);
            room.ConfirmOpening(Id);
        }

        public async Task Listen(RoomActivity activity, CancellationToken stopping)
        {
            wake.Writer.TryWrite(true);
            try
            {
                await foreach (var _ in wake.Reader.ReadAllAsync(stopping))
                while (!stopping.IsCancellationRequested)
                {
                    RoomDelivery? delivery = null;
                    JsonObject? controls = null;
                    lock (room.SyncRoot)
                    {
                        var state = room.CharacterState(Id);
                        if (state is "starting" or "stopping")
                            controls = room.Lifecycle(Id);
                        else if (state != "enabled" || !pending.TryDequeue(out delivery)) break;
                        inFlight = delivery;
                    }
                    try
                    {
                        if (controls is not null)
                        {
                            await Run("", LifecyclePrompt(controls), (target, message) => engine.PostEnsembleMessage(target, message),
                                stopping, control: controls);
                            continue;
                        }
                        if (delivery is null || delivery.Token.IsCancellationRequested || !room.IsMember(delivery.Scene, Id)) continue;
                        if (!delivery.Respond)
                        {
                            if (room.IsEnabled(Id) && !room.IsPrivateChat(delivery.Scene)) Synchronize();
                            continue;
                        }
                        var input = "A new post may need your attention. In the conversation history, its ID is " +
                            delivery.Message.GetString("id") + ". Decide whether to send a visible message and in which joined chat, or stay silent." +
                            delivery.Interaction;
                        var lineage = delivery.Ancestors.Append(Id).ToArray();
                        engine.operationLocale.Value = delivery.Locale;
                        await Run(delivery.Scene, input, (target, message) =>
                        {
                            engine.PostEnsembleMessage(target, message, notifyCharacters: false);
                            activity.Publish(target, message, locale: delivery.Locale, cause: delivery.Cause, ancestry: lineage);
                        }, delivery.Token, attachments: new JsonArray(delivery.Message.GetArray("attachments")
                            .Where(file => !MessageAttachments.IsQuote(file)).Select(JsonValues.Clone).ToArray()), activity: activity);
                    }
                    catch (OperationCanceledException) when (delivery?.Token.IsCancellationRequested == true || stopping.IsCancellationRequested) { }
                    catch (Exception error)
                    {
                        if (controls is not null) FailLifecycle(controls);
                        engine.Emit(new() { ["kind"] = "error", ["agent"] = "chat", ["model_turn"] = true,
                            ["preset"] = workspace.PresetId, ["chat"] = workspace.ChatId, ["conversation"] = delivery?.Scene ?? "", ["participant"] = Id,
                            ["private_participant"] = delivery is not null && room.IsPrivateChat(delivery.Scene) ? Id : "", ["message"] = error.Message });
                    }
                    finally
                    {
                        lock (room.SyncRoot) inFlight = null;
                        engine.operationLocale.Value = null;
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        }

        private async Task Run(string scene, string input, Action<string, JsonObject> publish, CancellationToken token,
            bool isOpening = false, JsonArray? attachments = null, RoomActivity? activity = null, JsonObject? control = null)
        {
            AgentSession session;
            lock (room.SyncRoot)
            {
                if (!isOpening && control is null && !room.IsEnabled(Id)) return;
                conversation = scene;
                opening = isOpening;
                direct = room.IsPrivateChat(scene);
                posted = publish;
                confirmedLifecycle = "";
                session = direct ? DirectSession() : Session;
                activeInbox = session.Inbox = new TurnInbox(workspace.ChatId, "chat", _ =>
                {
                    if (!direct) Volatile.Write(ref publicStopNoticeDelivered, true);
                    return null;
                }, conversation: scene);
            }
            var finishMessage = "Cancelled.";
            try
            {
                activity?.Report(workspace, scene, Id, name, running: true);
                if (control is null) Synchronize(direct ? scene : "");
                else session.ConfigureWorldMode(workspace.AgenticSearch);
                token.ThrowIfCancellationRequested();
                var context = direct
                    ? "This is a private exchange with the user. The only permitted recipient is this private chat (" + scene +
                        "). Use chat_send for your visible reply. Respect the in-story or out-of-character framing of the message."
                    : control is not null
                        ? "Follow the room lifecycle notice and use the lifecycle confirmation tool when ready."
                    : isOpening
                        ? "This is your room opening. Use my_chats to see your joined chats. Decide whether and where to post, " +
                          "and use chat_send for each message. You may post in several chats or stay silent. Avoid repeating anything you already sent."
                        : "The triggering chat is " + room.ActivityName(scene) + " (" + scene +
                          "). It is the source of the notification. Use my_chats and decide where your character would post. Only chat_send posts visibly.";
                var agent = direct ? directAgent ??= CreateAgent(DirectSession(), isDirect: true) : Agent;
                await agent.Run(input, token, attachments, inputRole: "system", privateInput: true, turnInstructions: context,
                    finishAfterTool: () => confirmedLifecycle.Length > 0, terminalTools: LifecycleTools);
                lock (room.SyncRoot)
                {
                    activeInbox = null;
                    if (confirmedLifecycle.Length > 0) CompleteLifecycle();
                }
                if (control is not null && confirmedLifecycle.Length == 0)
                    throw new InvalidOperationException("The character did not confirm its lifecycle change.");
                finishMessage = "Finished.";
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested) finishMessage = error.Message;
                throw;
            }
            finally
            {
                try
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
                            if (direct) end["private_participant"] = Id;
                            RecordTrace(end, currentTrace, room.TraceBlocks(scene, Id));
                            if (!isOpening && room.IsActive(scene)) engine.Emit(end);
                        }
                        workspace.Save();
                    }
                }
                finally
                {
                    lock (room.SyncRoot) activeInbox = null;
                    session.CompleteInbox();
                    currentTrace.Clear();
                    posted = null;
                    conversation = "";
                    opening = false;
                    direct = false;
                    activity?.Report(workspace, scene, Id, name, running: false);
                }
            }
        }

        public void Synchronize(string directScene = "")
        {
            var isDirect = directScene.Length > 0;
            var session = isDirect ? DirectSession() : Session;
            session.ConfigureWorldMode(workspace.AgenticSearch);
            if (workspace.AgenticSearch) _ = SearchSession(isDirect);
            lock (room.SyncRoot)
            {
                if (!room.IsEnabled(Id)) return;
                if (!isDirect) EnsembleConversation.DeliverPrivate(session);
                room.Deliver(Id, session, directScene);
            }
        }

        private bool CanReadChat(string target) => room.IsMember(target, Id) &&
            (direct ? target == conversation : !room.IsPrivateChat(target));

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
            ["_private_ensemble_dm"] = direct,
            ["participant"] = Id, ["session"] = role + "-" + workspace.ChatId + "-" + Id + (direct ? "-direct" : "")
        };

        private AgentSession SearchSession(bool isDirect)
        {
            if (isDirect) return directSearch ??= OpenSearch(isDirect);
            return search ??= OpenSearch(isDirect);
        }

        private AgentSession OpenSearch(bool isDirect) => engine.agentSessions.Open("search", room.Database(Id, "search", isDirect),
            () => Volatile.Read(ref engine.routing).For("search"), () => Context("search"), (item, info) => Record("search", item, info),
            canReadChat: CanReadChat, conversationWorkspace: workspace);

        private PageSource CreatePageSource()
        {
            Routing Selected() => Volatile.Read(ref engine.routing);
            var browser = new BrowserAgent(engine.Port, (job, parent) =>
            {
                var context = Context("fetch");
                context["session"] = "fetch-" + job;
                return engine.agentSessions.Open("fetch", SessionFiles.Fetch(workspace.ChatRoot, job), () => Selected().For("fetch"),
                    () => context, (item, info) =>
                    {
                        if (parent is not null) item["parent_agent"] = parent;
                        Record("fetch", item, info, context);
                    }, canReadChat: CanReadChat);
            }, () => workspace.AgentPrompt("fetch"));
            return new(engine.Port, browser, () => Selected().Settings);
        }

        private void Record(string role, JsonObject item, SessionModelInfo? info, JsonObject? fetchContext = null)
        {
            // Dialogue goes through chat_send. Plain model prose remains in the character's history.
            var scene = fetchContext?.GetString("chat") ?? conversation;
            if (scene.Length == 0 && !opening || item.GetString("kind") == "delta" && item.GetString("type") == "content") return;
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
            if (direct) item["private_participant"] = Id;
            lock (room.SyncRoot)
            {
                RecordTrace(item, currentTrace, room.TraceBlocks(scene, Id));
                if (!opening && room.IsActive(scene))
                {
                    engine.RecordWebDisplay(item, currentTrace, scene, workspace);
                    engine.IndexDisplayAssets(item, item.GetString("block"), scene, scope: workspace);
                    engine.Emit(item, info);
                }
            }
        }

        private void PublishAssets(JsonObject message, string text) =>
            (direct ? DirectSession() : Session).AddPublishedAssets(message, text);

        public void Dispose()
        {
            search?.Dispose();
            Session?.Dispose();
            directSearch?.Dispose();
            directSession?.Dispose();
        }
    }
}
