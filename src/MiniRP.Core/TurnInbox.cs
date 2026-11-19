using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Messages addressed to the active turn. Only the model stream is interrupted.</summary>
internal sealed class TurnInbox(string chat, string session, Action<JsonObject> accept, bool keepOpen = false,
    string conversation = "")
{
    public string Agent => session;
    private readonly object gate = new();
    private readonly Queue<JsonObject> queue = new();
    private CancellationTokenSource? generation;
    private bool sealedInbox;

    public bool TryPost(string targetChat, string role, JsonObject message, string targetConversation = "")
    {
        lock (gate)
        {
            if (sealedInbox || role != session || conversation != targetConversation ||
                (targetChat.Length > 0 && chat != targetChat) || queue.Count >= 32)
            {
                return false;
            }
            queue.Enqueue(message);
            generation?.Cancel();
            return true;
        }
    }

    public bool Drain(Action<JsonObject> append, bool finish = false)
    {
        JsonObject[] messages;
        lock (gate)
        {
            if (queue.Count == 0)
            {
                sealedInbox |= finish && !keepOpen;
                return false;
            }
            messages = queue.ToArray();
            queue.Clear();
        }
        foreach (var message in messages)
        {
            accept(message);
            append(new JsonObject { ["role"] = message.GetString("role", "user"), ["content"] = message.GetString("text") });
        }
        return true;
    }

    public CancellationTokenSource BeginGeneration(CancellationToken token)
    {
        lock (gate)
        {
            generation = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (queue.Count > 0)
            {
                generation.Cancel();
            }
            return generation;
        }
    }

    public void EndGeneration()
    {
        lock (gate)
        {
            generation?.Dispose();
            generation = null;
        }
    }

    public void Close()
    {
        lock (gate) sealedInbox = true;
    }
}
