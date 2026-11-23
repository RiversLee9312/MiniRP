using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Messages addressed to the active turn. Only the model stream is interrupted.</summary>
internal sealed class TurnInbox(string chat, string session, Func<JsonObject, string?> accept, bool keepOpen = false,
    string conversation = "", string preset = "", Action<JsonObject>? queued = null)
{
    internal const string QueuedBlockId = "_steer_block";
    public string Agent => session;
    private readonly object gate = new();
    private sealed record PendingInput(JsonObject Message, Lazy<string?> Accept);
    private readonly Queue<PendingInput> queue = new();
    private readonly List<TurnInbox> subscribers = [];
    private CancellationTokenSource? generation;
    private bool sealedInbox;
    private bool systemMessagesChanged;

    // Subscribe before publishing the turn. Each session owns its queue and generation token.
    public TurnInbox Subscribe()
    {
        var subscriber = new TurnInbox(chat, session, accept, conversation: conversation);
        subscribers.Add(subscriber);
        return subscriber;
    }

    public bool TryPost(string targetChat, string role, JsonObject message, string targetConversation = "")
    {
        lock (gate)
        {
            if (sealedInbox || role != session || conversation != targetConversation ||
                (targetChat.Length > 0 && chat != targetChat))
            {
                return false;
            }
            if (queued is not null) message[QueuedBlockId] = JsonValues.NewId();
            queued?.Invoke(message);
            var input = new PendingInput(message, new(() => accept(message)));
            Enqueue(input);
            foreach (var subscriber in subscribers) subscriber.Enqueue(input);
            return true;
        }
    }

    private void Enqueue(PendingInput input)
    {
        lock (gate)
        {
            if (sealedInbox) return;
            queue.Enqueue(input);
            generation?.Cancel();
        }
    }

    public bool Drain(Action<JsonObject> append, bool finish = false)
    {
        PendingInput[] messages;
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
        foreach (var (message, acknowledge) in messages)
        {
            var delivery = acknowledge.Value;
            var input = new JsonObject { ["role"] = message.GetString("role", "user"), ["content"] = message.GetString("text") };
            if (delivery is not null) input["_inbox_id"] = delivery;
            append(input);
        }
        return true;
    }

    public void SteerSystemMessages(string owner)
    {
        lock (gate)
        {
            if (sealedInbox || session != "preset" || preset != owner) return;
            systemMessagesChanged = true;
            generation?.Cancel();
        }
    }

    public void ClearSystemMessageSignal()
    {
        lock (gate) systemMessagesChanged = false;
    }

    public CancellationTokenSource BeginGeneration(CancellationToken token)
    {
        lock (gate)
        {
            generation = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (queue.Count > 0 || systemMessagesChanged)
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
