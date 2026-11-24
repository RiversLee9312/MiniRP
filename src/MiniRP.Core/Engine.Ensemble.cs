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
        lock (hub.SyncRoot)
        {
            if (conversation.Length > 0) _ = hub.Chat(conversation);
            EmitEnsembleChat(hub, conversation, command.GetString("request"));
        }
    }

    private void OpenDirectEnsembleChat(JsonObject command)
    {
        RequireActiveChat(command);
        RequirePreset(command);
        var hub = ensemble ?? throw new InvalidOperationException("Choose an ensemble room.");
        lock (hub.SyncRoot) EmitEnsembleChat(hub, hub.OpenDirectChat(command.GetString("participant")), command.GetString("request"));
    }

    private void EmitEnsembleChat(EnsembleConversation hub, string conversation, string request)
    {
        activeEnsembleChat = conversation;
        var item = new JsonObject
        {
            ["kind"] = "ensemble_chat_loaded", ["request"] = request, ["preset"] = Workspace.PresetId,
            ["chat"] = Workspace.ChatId, ["conversation"] = conversation,
            ["private_in_story"] = hub.DirectMessagesInStory,
            ["activity"] = Volatile.Read(ref roomActivity)?.Snapshot(conversation) ?? new JsonArray()
        };
        if (conversation.Length > 0 && hub.PrivateParticipant(conversation) is { Length: > 0 } participant)
            item["private_participant"] = participant;
        Emit(ProjectEnsembleTranscript(item, hub, conversation));
    }

    private void ReadEnsembleMemberTrace(JsonObject command)
    {
        RequireActiveChat(command);
        RequirePreset(command);
        var hub = ensemble ?? throw new InvalidOperationException("Choose an ensemble room.");
        var participant = command.GetString("participant");
        lock (hub.SyncRoot)
        {
            _ = hub.Agent(participant);
            var blocks = new JsonArray();
            foreach (var (conversation, scene, source) in hub.PublicTrace(participant))
            {
                var block = LoadBlock(source, conversation);
                block["running"] = source.GetBoolean("running");
                block["scene_name"] = scene;
                block["conversation"] = conversation;
                blocks.AddNode(block);
            }
            events.Enqueue(new()
            {
                ["kind"] = "ensemble_member_trace", ["request"] = command.GetString("request"),
                ["preset"] = Workspace.PresetId, ["chat"] = Workspace.ChatId, ["participant"] = participant, ["blocks"] = blocks
            });
        }
    }

    private void PostEnsembleMessage(string conversation, JsonObject message, bool notifyCharacters = true)
    {
        IndexDisplayAssets(message, message.GetString("id"), conversation);
        var item = new JsonObject { ["kind"] = "ensemble_message", ["preset"] = Workspace.PresetId, ["chat"] = Workspace.ChatId,
            ["conversation"] = conversation, ["item"] = message.DeepClone() };
        if (ensemble?.PrivateParticipant(conversation) is { Length: > 0 } participant) item["private_participant"] = participant;
        Emit(item);
        if (notifyCharacters)
            Volatile.Read(ref roomActivity)?.Publish(conversation, message, locale: operationLocale.Value);
    }

    private void EnsembleChatsChanged() =>
        Emit(new() { ["kind"] = "ensemble_chats", ["chat"] = Workspace.ChatId, ["items"] = ensemble?.List() });

    private IEnumerable<AgentSkill> ModeSkills(string role, Workspace workspace)
    {
        if (role == "chat" && workspace.Mode == "tabletop") yield return PresetDesign.Tabletop(Port);
        if (role != "env") yield break;
        if (ensemble is { } hub)
        {
            yield return hub.WorldSkill((conversation, message) => PostEnsembleMessage(conversation, message), EnsembleChatsChanged);
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

    private async Task SubmitEnsembleMessage(JsonObject command, CancellationToken token)
    {
        RequireActiveChat(command);
        var hub = ensemble ?? throw new InvalidOperationException("This room has no chats.");
        var conversation = command.GetString("conversation", activeEnsembleChat);
        var role = command.GetString("role", "user");
        var input = command.GetString("text");
        var attachments = command.GetArray("attachments");
        if (role is not ("user" or "system") || role == "system" && attachments.Count > 0 ||
            string.IsNullOrWhiteSpace(input) && attachments.Count == 0)
            throw new ArgumentException("A message must contain text or supported attachments.");
        var interaction = "";
        bool direct;
        lock (hub.SyncRoot)
        {
            direct = hub.PrivateParticipant(conversation).Length > 0;
            if (direct && role != (hub.DirectMessagesInStory ? "user" : "system"))
                throw new ArgumentException(hub.DirectMessagesInStory ?
                    "Send an in-story direct message as the user." : "This preset permits only out-of-character system messages in direct chats.");
            if (command["open_ui_action"] is JsonObject action)
            {
                if (role != "user" || !hub.Blocks(conversation).Any(block => block.GetString("id") == action.GetString("block") &&
                    block.GetString("open_ui").Length > 0))
                    throw new ArgumentException("This chat's interactive reply is unavailable.");
                var data = JsonValues.Serialize(action.GetObject("data"));
                if (data.Length > 16384) throw new ArgumentException("The interactive reply data is too large.");
                interaction = "\nInteractive reply data: " + data;
            }
        }
        attachments = await PrepareAttachments(attachments, GroupContext("chat", "", conversation), token);
        token.ThrowIfCancellationRequested();
        lock (hub.SyncRoot)
        {
            var message = hub.Post(conversation, "", role, input, attachments);
            PostEnsembleMessage(conversation, message, notifyCharacters: false);
            var request = command.GetString("request");
            if (request.Length > 0)
                Emit(new() { ["kind"] = "message_accepted", ["request"] = request, ["chat"] = Workspace.ChatId });
            Volatile.Read(ref roomActivity)?.Publish(conversation, message, interaction, locale: operationLocale.Value);
            if (!direct) StartTitle(force: false);
        }
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
        lock (hub.SyncRoot)
        {
            if (hub.PrivateParticipant(conversation).Length > 0)
                throw new ArgumentException("Use the direct-message composer.");
            PostEnsembleMessage(conversation, hub.Post(conversation, "", "system", text));
        }
    }
}
