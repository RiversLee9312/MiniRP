using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private EnsembleConversation? ensemble;
    private PageSource? ensembleWeb;
    private string activeEnsembleChat = "";

    private void SelectEnsembleChat(JsonObject command)
    {
        RequireActiveChat(command);
        var conversation = command.GetString("conversation");
        var hub = ensemble ?? throw new InvalidOperationException("This room has no chats.");
        if (conversation.Length == 0)
        {
            activeEnsembleChat = "";
            return;
        }
        _ = hub.Chat(conversation);
        activeEnsembleChat = conversation;
        Emit(new() { ["kind"] = "ensemble_chat_loaded", ["request"] = command.GetString("request"),
            ["chat"] = Workspace.ChatId, ["conversation"] = conversation, ["blocks"] = new JsonArray(hub.Blocks(conversation)
                .OfType<JsonObject>().Select(LoadBlock).ToArray()) });
    }

    private void PostEnsembleMessage(string conversation, JsonObject message)
    {
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
        if (attachments.OfType<JsonObject>().Any(item => item.GetString("type").Length > 0))
            attachments = await plugins.ProcessAttachments(attachments, GroupContext("chat", "", conversation), token);
        new MessageAttachments(attachmentRoot).Validate(attachments, turnRouting.For("chat"));
        if (opening is null) PostEnsembleMessage(conversation, hub.Post(conversation, "", role, input, attachments));
        var turn = new TurnInbox(Workspace.ChatId, "chat", message =>
            PostEnsembleMessage(conversation, hub.Post(conversation, "", message.GetString("role", "user"), message.GetString("text"))),
            keepOpen: true, conversation: conversation);
        Volatile.Write(ref inbox, turn);
        Emit(new() { ["kind"] = "generating", ["agent"] = "chat", ["value"] = true });
        var request = command.GetString("request");
        if (request.Length > 0)
            Emit(new() { ["kind"] = "message_accepted", ["request"] = request, ["chat"] = Workspace.ChatId });
        try
        {
            foreach (var member in members)
            {
                token.ThrowIfCancellationRequested();
                var character = hub.Agent(member);
                var name = character.GetString("name");
                var (roleContext, searchContext) = (GroupContext("chat", member, conversation), GroupContext("search", member, conversation));
                var chatModel = new ModelClient(Port, () => turnRouting.For("chat"), attachmentRoot);
                var searchModel = new ModelClient(Port, () => turnRouting.For("search"), attachmentRoot);
                using var own = new AgentSession("ChatAgent", hub.Database(member, "chat"), chatModel, item =>
                    RecordEnsembleTrace(hub, conversation, member, name, item, roleContext));
                if (opening is not null && own.History.Count > 0)
                {
                    RememberEnsembleGreeting(greeted, member);
                    continue;
                }
                using var research = Workspace.AgenticSearch
                    ? new AgentSession("SearchAgent", hub.Database(member, "search"), searchModel, item =>
                        RecordEnsembleTrace(hub, conversation, member, name, item, searchContext))
                    : null;
                own.ConfigureWorldMode(Workspace.AgenticSearch);
                ConfigureSkillAccess(own, "chat", chatModel, () => roleContext, target => hub.IsMember(target, member));
                if (research is not null)
                    ConfigureSkillAccess(research, "search", searchModel, () => searchContext, target => hub.IsMember(target, member));
                var pluginsForCharacter = own.AdditionalSkills!;
                var postedOpening = false;
                var dialogue = hub.CharacterSkill(member, conversation, (target, message) =>
                {
                    if (opening is not null && target == conversation && !postedOpening)
                    {
                        RememberEnsembleGreeting(greeted, member);
                        postedOpening = true;
                    }
                    PostEnsembleMessage(target, message);
                });
                own.AdditionalSkills = () => pluginsForCharacter().Append(dialogue);
                own.OpenUiInstructions = () => turnRouting.Settings.GetString("android_open_ui");
                own.Inbox = turn;
                if (opening is null)
                {
                    EnsembleConversation.DeliverPrivate(own);
                    hub.Deliver(member, own);
                }
                var identity = "Your character is " + name + ". " + character.GetString("prompt") +
                    "\nThe active chat is " + hub.Name(conversation) + " (" + conversation + "). Only chat_send posts a visible reply.";
                var characterSearch = new SearchAgent(Workspace, research ?? own, ensembleWeb!,
                    "Research for " + name + ". " + character.GetString("prompt"),
                    () => RecentEnsembleChatReference(hub, conversation));
                var answer = await new ChatAgent(Workspace, own, characterSearch, identity).Run(
                    opening is null
                        ? "It is your turn in the active chat. Use chat_send to reply if you have something to say. You may pass." + interaction
                        : opening + "\nUse chat_send to post your opening in the active chat.",
                    token, attachments, inputRole: opening is null ? "user" : "system", privateInput: opening is not null);
                if (opening is not null && !postedOpening)
                {
                    if (string.IsNullOrWhiteSpace(answer))
                        throw new IOException("The model finished without a visible opening.");
                    await dialogue.Execute("chat_send", new() { ["chat"] = conversation, ["text"] = answer }, token);
                }
            }
            if (opening is null) StartTitle(force: false);
        }
        finally
        {
            turn.Close();
            turn.Drain(_ => { });
            Volatile.Write(ref inbox, null);
            Emit(new() { ["kind"] = "generating", ["agent"] = "chat", ["value"] = false });
        }
    }

    private void RememberEnsembleGreeting(JsonArray greeted, string member)
    {
        if (greeted.Parent is null) Workspace.Chat["greeted_participants"] = greeted;
        greeted.AddNode(member);
        Workspace.Save();
    }

    private void RecordEnsembleTrace(EnsembleConversation hub, string conversation, string participant, string name, JsonObject item, JsonObject context)
    {
        // Only chat_send publishes dialogue. Private model prose stays in the character session.
        if (item.GetString("kind") == "delta" && item.GetString("type") == "content") return;
        if (item.GetString("block") is { Length: > 0 } block) item["block"] = participant + "-" + block;
        item["conversation"] = conversation;
        item["chat"] = Workspace.ChatId;
        item["participant"] = participant;
        item["speaker"] = name;
        item["session"] = context.GetString("session");
        Record(item, hub.Blocks(conversation));
    }

    private JsonObject GroupContext(string role, string participant, string conversation)
    {
        var context = PluginEnvironment(role);
        context["chat"] = conversation;
        if (participant.Length > 0)
        {
            context["participant"] = participant;
            context["session"] = role + "-" + Workspace.ChatId + "-" + participant;
        }
        return context;
    }

    private static string RecentEnsembleChatReference(EnsembleConversation hub, string conversation)
    {
        var result = string.Join('\n', hub.Chat(conversation).GetArray("messages").TakeLast(8)
            .Select(message => message.GetString("speaker", "User") + ": " + message.GetString("content")));
        return result[^Math.Min(result.Length, 2400)..];
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
