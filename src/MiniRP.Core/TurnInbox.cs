using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Messages addressed to the active turn. Only the model stream is interrupted.</summary>
internal sealed class TurnInbox(string chat, string session, Func<JsonObject, string?> accept, bool keepOpen = false,
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
                (targetChat.Length > 0 && chat != targetChat))
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
            var delivery = accept(message);
            var input = new JsonObject { ["role"] = message.GetString("role", "user"), ["content"] = message.GetString("text") };
            if (delivery is not null) input["_inbox_id"] = delivery;
            append(input);
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

    public void Complete(Action<JsonObject>? append = null)
    {
        lock (gate) sealedInbox = true;
        Drain(append ?? (_ => { }));
    }
}
