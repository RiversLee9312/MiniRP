using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private Dictionary<string, RoomCharacter> roomCharacters = new(StringComparer.Ordinal);

    internal AgentSession? LoadedCharacterSession(string participant, string role) =>
        roomCharacters.TryGetValue(participant, out var actor) ? actor.LoadedSession(role) : null;

    private void CloseRoomCharacters()
    {
        foreach (var actor in roomCharacters.Values) actor.Dispose();
        roomCharacters.Clear();
    }

    private async Task PrepareRoom(Workspace workspace, EnsembleConversation room, PageSource web,
        Dictionary<string, RoomCharacter> characters, bool newRoom, CancellationToken token)
    {
        foreach (var definition in room.Agents)
        {
            token.ThrowIfCancellationRequested();
            var id = definition.GetString("id");
            characters.Add(id, new(this, room, workspace, web, id));
        }
        var pending = characters.Values.Where(character => !character.IsReady).ToArray();
        if (pending.Length == 0) return;
        if (!CanGreet) throw new InvalidOperationException("Configure a provider and model first.");

        var ready = characters.Count - pending.Length;
        var progress = new int[pending.Length];
        var progressGate = new object();
        void EmitLoading(bool loading) => Emit(new()
        {
            ["kind"] = "room_loading", ["preset"] = workspace.PresetId, ["room"] = workspace.ChatId,
            ["value"] = loading, ["ready"] = ready, ["total"] = characters.Count,
            ["progress"] = (ready + progress.Sum() / 100d) / characters.Count,
            ["messages"] = workspace.Config.GetObject("ensemble").GetArray("loading_messages").DeepClone()
        });

        EmitLoading(true);
        try
        {
            if (newRoom) workspace.Save();
            await Task.WhenAll(pending.Select(async (character, index) =>
            {
                await character.Prepare(percent =>
                {
                    lock (progressGate)
                    {
                        progress[index] = percent;
                        EmitLoading(true);
                    }
                }, token);
                lock (progressGate)
                {
                    ready++;
                    progress[index] = 0;
                    EmitLoading(true);
                }
            }));
        }
        catch
        {
            if (newRoom)
                await Index(workspace);
            throw;
        }
        finally { EmitLoading(false); }
    }

    /// <summary>The durable character has one active turn context at a time in the room worker.</summary>
    private sealed class RoomCharacter : IDisposable
    {
        private readonly Engine engine;
        private readonly EnsembleConversation room;
        private readonly Workspace workspace;
        private readonly string participant;
        private readonly string name;
        private AgentSession? search;
        private string conversation = "";
        private Action<string, JsonObject>? posted;

        public AgentSession Session { get; }
        public ChatAgent Agent { get; }
        public bool IsReady => Session.Db.Get("_room/ready") == "true";

        public RoomCharacter(Engine engine, EnsembleConversation room, Workspace workspace, PageSource web, string participant)
        {
            this.engine = engine;
            this.room = room;
            this.workspace = workspace;
            this.participant = participant;
            var definition = room.Agent(participant);
            name = definition.GetString("name");
            var prompt = definition.GetString("prompt");
            var identity = "Your character is " + name + ". " + prompt;
            Session = engine.agentSessions.Open("chat", room.Database(participant, "chat"), () => engine.turnRouting.For("chat"),
                () => Context("chat"), (item, info) => Record("chat", item, info),
                () => [room.CharacterSkill(participant, conversation, posted!, PublishAssets)], CanReadChat);
            var research = new SearchAgent(workspace, SearchSession, web, "Research for " + name + ". " + prompt,
                () => RecentEnsembleChatReference(room, conversation));
            Agent = new(workspace, Session, research, identity);
            if (workspace.AgenticSearch) _ = SearchSession();
        }

        public async Task Prepare(Action<int> progress, CancellationToken token)
        {
            var chats = room.List().OfType<JsonObject>()
                .Where(chat => CanReadChat(chat.GetString("id"))).Select(chat => chat.GetString("name"));
            var context = "Your chats: " + string.Join(", ", chats) + "\n" + workspace.AgentPrompt("chat");
            using var preparation = engine.agentSessions.Open("room-preparation", ":memory:",
                () => engine.turnRouting.For("chat"), () => Context("chat"), _ => { });
            await Agent.Prepare(preparation, context, progress, token);
            Session.Db.Write("_room/ready", "true", internalWrite: true);
        }

        public void Begin(string chat, TurnInbox inbox, Action<string, JsonObject> publishMessage)
        {
            conversation = chat;
            Session.Inbox = inbox;
            posted = publishMessage;
        }

        public void Synchronize(bool delegatedSearch, bool opening)
        {
            Session.ConfigureWorldMode(delegatedSearch);
            if (delegatedSearch) _ = SearchSession();
            if (opening) return;
            EnsembleConversation.DeliverPrivate(Session);
            room.Deliver(participant, Session);
        }

        public void End()
        {
            Session.Inbox = null;
            posted = null;
            conversation = "";
        }

        private bool CanReadChat(string target) => room.IsMember(target, participant);

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
            ["participant"] = participant, ["session"] = role + "-" + workspace.ChatId + "-" + participant
        };

        private AgentSession SearchSession() => search ??= engine.agentSessions.Open("search", room.Database(participant, "search"),
            () => engine.turnRouting.For("search"), () => Context("search"), (item, info) => Record("search", item, info),
            canReadChat: CanReadChat);

        private void Record(string role, JsonObject item, SessionModelInfo? info)
        {
            if (conversation.Length > 0)
                engine.RecordEnsembleTrace(room, conversation, participant, name, item, Context(role), info);
        }

        private void PublishAssets(JsonObject message, string text) => Session.AddPublishedAssets(message, text);

        public void Dispose()
        {
            search?.Dispose();
            Session.Dispose();
        }
    }
}
