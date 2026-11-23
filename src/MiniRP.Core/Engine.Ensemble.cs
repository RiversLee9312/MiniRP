using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private EnsembleConversation? ensemble;
    private string activeEnsembleChat = "";

    private void SelectEnsembleChat(JsonObject command)
    {
        RequireActiveChat(command);
        var conversation = command.GetString("conversation");
        var hub = ensemble ?? throw new InvalidOperationException("This room has no chats.");
        if (conversation.Length == 0)
        {
            activeEnsembleChat = "";
            Emit(new() { ["kind"] = "ensemble_chat_loaded", ["request"] = command.GetString("request"),
                ["chat"] = Workspace.ChatId, ["conversation"] = "", ["blocks"] = new JsonArray() });
            return;
        }
        _ = hub.Chat(conversation);
        activeEnsembleChat = conversation;
        Emit(new() { ["kind"] = "ensemble_chat_loaded", ["request"] = command.GetString("request"),
            ["chat"] = Workspace.ChatId, ["conversation"] = conversation, ["blocks"] = new JsonArray(hub.Blocks(conversation)
                .OfType<JsonObject>().Select(block => LoadBlock(block, conversation)).ToArray()) });
    }

    private void PostEnsembleMessage(string conversation, JsonObject message)
    {
        IndexDisplayAssets(message, message.GetString("id"), conversation);
        Emit(new() { ["kind"] = "ensemble_message", ["chat"] = Workspace.ChatId,
            ["conversation"] = conversation, ["item"] = message.DeepClone() });
    }

    private void EnsembleChatsChanged() =>
        Emit(new() { ["kind"] = "ensemble_chats", ["chat"] = Workspace.ChatId, ["items"] = ensemble?.List() });

    private IEnumerable<AgentSkill> ModeSkills(string role, Workspace workspace)
    {
        if (role == "chat" && workspace.Mode == "tabletop") yield return PresetDesign.Tabletop(Port);
        if (role != "env") yield break;
        if (ensemble is { } hub)
        {
            yield return hub.WorldSkill(PostEnsembleMessage, EnsembleChatsChanged);
            yield break;
        }
        yield return new("world-notifications", "Send targeted system updates to the conversation agent.", new JsonArray(
            Tools.Define("notify_chat", "Tell ChatAgent what it needs to know about a world update. Preserve hidden setting details.", new()
                { ["text"] = Tools.String() }, "text")),
            (_, args, _) =>
            {
                sessions["chat"].Receive("world-" + JsonValues.NewId(), EnsembleConversation.Text(args));
                return Task.FromResult<JsonNode?>(new JsonObject { ["sent"] = true });
            }, Required: true);
    }

    private async Task RunEnsembleChat(JsonObject command, CancellationToken token, string? opening = null)
    {
        RequireActiveChat(command);
        var hub = ensemble ?? throw new InvalidOperationException("This room has no chats.");
        var conversation = command.GetString("conversation", activeEnsembleChat);
        var role = command.GetString("role", "user");
        var input = command.GetString("text");
        var attachments = command.GetArray("attachments");
        var interaction = "";
        var members = hub.Members(conversation);
        var greeted = Workspace.Chat.GetArray("greeted_participants");
        if (opening is not null)
        {
            var completed = greeted.Select(item => JsonValues.GetText(item)).ToHashSet(StringComparer.Ordinal);
            members = members.Where(member => !completed.Contains(member)).ToArray();
            if (members.Length == 0) return;
        }
        else if (role is not ("user" or "system") || (role == "system" && attachments.Count > 0) ||
            (string.IsNullOrWhiteSpace(input) && attachments.Count == 0))
            throw new ArgumentException("A message must contain text or supported attachments.");
        if (command["open_ui_action"] is JsonObject action)
        {
            if (role != "user" || !hub.Blocks(conversation).Any(block => block.GetString("id") == action.GetString("block") &&
                    block.GetString("open_ui").Length > 0))
                throw new ArgumentException("This chat's interactive reply is unavailable.");
            var data = JsonValues.Serialize(action.GetObject("data"));
            if (data.Length > 16384) throw new ArgumentException("The interactive reply data is too large.");
            interaction = "\nInteractive reply data: " + data;
        }
        attachments = await PrepareAttachments(attachments, GroupContext("chat", "", conversation), token);
        if (opening is null) PostEnsembleMessage(conversation, hub.Post(conversation, "", role, input, attachments));
        var turn = new TurnInbox(Workspace.ChatId, "chat", message =>
        {
            lock (hub.SyncRoot)
            {
                var posted = hub.Post(conversation, "", message.GetString("role", "user"), message.GetString("text"),
                    blockId: message[TurnInbox.QueuedBlockId]?.GetValue<string>());
                PostEnsembleMessage(conversation, posted);
                return EnsembleConversation.DeliveryId(posted);
            }
        }, keepOpen: true, conversation: conversation, queued: message => PreviewSteer("chat", message, conversation));
        var participants = new List<(string Member, RoomCharacter Character, TurnInbox Inbox)>();
        foreach (var member in members)
        {
            var character = roomCharacters[member];
            if (opening is not null && character.Session.HasHistory)
            {
                RememberEnsembleGreeting(greeted, member);
                continue;
            }
            character.Synchronize(Workspace.AgenticSearch, opening is not null);
            participants.Add((member, character, turn.Subscribe()));
        }
        var context = "The active chat is " + hub.Name(conversation) + " (" + conversation + "). Only chat_send posts a visible reply.";
        BeginInteractiveTurn(turn);
        var completedTurn = false;
        try
        {
            var request = command.GetString("request");
            if (request.Length > 0)
                Emit(new() { ["kind"] = "message_accepted", ["request"] = request, ["chat"] = Workspace.ChatId });
            await Task.WhenAll(participants.Select(async entry =>
            {
                token.ThrowIfCancellationRequested();
                var (member, character, characterInbox) = entry;
                var postedOpening = false;
                void Posted(string target, JsonObject message)
                {
                    if (opening is not null && target == conversation && !postedOpening)
                    {
                        RememberEnsembleGreeting(greeted, member);
                        postedOpening = true;
                    }
                    PostEnsembleMessage(target, message);
                }
                character.Begin(conversation, characterInbox, Posted);
                try
                {
                    var answer = await character.Agent.Run(
                        opening is null
                            ? "It is your turn in the active chat. Use chat_send to reply if you have something to say. You may pass." + interaction
                            : opening + "\nUse chat_send to post your opening in the active chat.",
                        token, attachments, inputRole: opening is null ? "user" : "system", privateInput: opening is not null,
                        turnInstructions: context);
                    if (opening is not null && !postedOpening)
                    {
                        if (string.IsNullOrWhiteSpace(answer))
                            throw new IOException("The model finished without a visible opening.");
                        token.ThrowIfCancellationRequested();
                        lock (hub.SyncRoot)
                            Posted(conversation, hub.PostCharacterMessage(conversation, member, answer, character.Session.AddPublishedAssets));
                    }
                }
                finally { character.End(); }
            }));
            if (opening is null) StartTitle(force: false);
            completedTurn = true;
        }
        finally
        {
            EndInteractiveTurn(turn, interrupted: !completedTurn && !token.IsCancellationRequested);
        }
    }

    private void RememberEnsembleGreeting(JsonArray greeted, string member)
    {
        if (greeted.Parent is null) Workspace.Chat["greeted_participants"] = greeted;
        greeted.AddNode(member);
        Workspace.Save();
    }

    private void RecordEnsembleTrace(EnsembleConversation hub, string conversation, string participant, string name, JsonObject item,
        JsonObject context, SessionModelInfo? info)
    {
        // Only chat_send publishes dialogue. Private model prose stays in the character session.
        if (item.GetString("kind") == "delta" && item.GetString("type") == "content") return;
        if (item.GetString("kind") == "turn_end") item.Remove("asset_links");
        if (item.GetString("block") is { Length: > 0 } block) item["block"] = participant + "-" + block;
        item["conversation"] = conversation;
        item["chat"] = Workspace.ChatId;
        item["participant"] = participant;
        item["speaker"] = name;
        item["session"] = context.GetString("session");
        lock (hub.SyncRoot) Record(item, hub.Blocks(conversation), info);
    }

    private JsonObject GroupContext(string role, string participant, string conversation)
    {
        var context = PluginEnvironment(role);
        context["chat"] = conversation.Length > 0 ? conversation : null;
        if (participant.Length > 0)
        {
            context["participant"] = participant;
            context["session"] = role + "-" + Workspace.ChatId + "-" + participant;
        }
        return context;
    }

    private static string RecentEnsembleChatReference(EnsembleConversation hub, string conversation)
    {
        lock (hub.SyncRoot)
        {
            var result = string.Join('\n', hub.Chat(conversation).GetArray("messages").TakeLast(8)
                .Select(message => message.GetString("speaker", "User") + ": " + message.GetString("content")));
            return result[^Math.Min(result.Length, 2400)..];
        }
    }

    private void NotifyChat(JsonObject command)
    {
        var text = EnsembleConversation.Text(command);
        if (ensemble is not { } hub)
        {
            sessions["chat"].Receive("notification-" + JsonValues.NewId(), text);
            return;
        }
        if (command.GetString("participant") is { Length: > 0 } participant)
        {
            hub.Notify(participant, text);
            return;
        }
        var conversation = command.GetString("conversation", activeEnsembleChat);
        if (conversation.Length == 0) throw new ArgumentException("Choose a chat or a character for this notification.");
        PostEnsembleMessage(conversation, hub.Post(conversation, "", "system", text));
    }
}
