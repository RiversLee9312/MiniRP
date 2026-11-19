using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class Engine
{
    private const string GreetingPending = "greeting_pending";

    private bool CanGreet => turnRouting.For("chat") is { Model.Length: > 0, Url.Length: > 0 };

    private async Task StartChat(string preset, CancellationToken token)
    {
        // The explicit New Chat action opens a conversation early only when it has an opening.
        if (preset.Length > 0)
        {
            var selected = new Workspace(Workspace.Root, seedDefaultPreset: false);
            selected.UsePreset(preset);
            if (selected.ChatGreeting.Length > 0 && selected.Mode != "ensemble")
            {
                SelectConversation(preset, null);
                await GreetChat("", token);
                return;
            }
        }
        ShowHome(preset);
    }

    private async Task GreetChat(string conversation, CancellationToken token)
    {
        if (!Workspace.Chat.GetBoolean(GreetingPending) || !CanGreet || Workspace.ChatGreeting is not { Length: > 0 } greeting)
            return;
        var opening = greeting + "\n\nGenerate the opening for the user now. Keep the setup instructions private.";
        if (ensemble is not null)
        {
            if (conversation.Length > 0)
                await RunEnsembleChat(new() { ["conversation"] = conversation }, token, opening);
            return;
        }

        if (sessions["chat"].History.Count == 0)
            await RunMessage("chat", opening, false, new(), token, inputRole: "system", hiddenInput: true);
        Workspace.Chat.Remove(GreetingPending);
        Workspace.Save();
    }
}
