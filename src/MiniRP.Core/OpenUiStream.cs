using System.Text;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

/// <summary>Separates a ChatAgent reply into Markdown and progressively rendered OpenUI documents.</summary>
internal sealed class OpenUiStream(string agent, string firstTextId, Action<JsonObject> emit)
{
    private const int MaxProgramLength = 64 * 1024;
    private const int MaxSurfaces = 6;
    private readonly StringBuilder probe = new();
    private string? textId = firstTextId;
    private string? surfaceId;
    private bool readingBody;
    private string markdownFence = "";
    private int programLength;
    private int surfaces;
    private const string Syntax = "```openui-lang";

    public void Append(string chunk)
    {
        var remaining = chunk.AsSpan();
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf('\n');
            var length = newline < 0 ? remaining.Length : newline + 1;
            var portion = remaining[..length];
            remaining = remaining[length..];
            if (readingBody || probe.Length == 0 && portion[0] is not ('`' or '~'))
                Write(portion);
            else
            {
                // Only possible fence lines need buffering. Stream every other line immediately.
                var head = Math.Min(portion.Length, 128 - probe.Length);
                probe.Append(portion[..head]);
                var complete = newline >= 0 && head == portion.Length;
                if (complete) ReadFence();
                else if (probe.Length == 128 || !CouldBeFence())
                {
                    Write(probe.ToString());
                    probe.Clear();
                    Write(portion[head..]);
                    readingBody = newline < 0;
                }
            }
            if (newline >= 0) readingBody = false;
            else if (probe.Length == 0) readingBody = true;
        }
    }

    private bool CouldBeFence()
    {
        if (probe.Length < 3)
            return Enumerable.Range(1, probe.Length - 1).All(index => probe[index] == probe[0]);
        return probe[0] == probe[1] && probe[1] == probe[2];
    }

    private void ReadFence()
    {
        var raw = probe.ToString();
        probe.Clear();
        var line = raw.TrimEnd();
        var length = 0;
        while (length < line.Length && line[length] == line[0]) length++;
        var fence = length >= 3 && line[0] is '`' or '~' ? line[..length] : "";
        if (surfaceId is not null)
        {
            if (fence.StartsWith("```", StringComparison.Ordinal) && line.Length == fence.Length)
                EndSurface();
            else Write(raw);
            return;
        }
        if (markdownFence.Length == 0 && line == Syntax && surfaces < MaxSurfaces)
        {
            EndText(interrupted: false);
            surfaceId = JsonValues.NewId();
            programLength = 0;
            surfaces++;
            return;
        }
        if (fence.Length > 0)
        {
            if (markdownFence.Length == 0) markdownFence = fence;
            else if (line.Length == fence.Length && fence.StartsWith(markdownFence, StringComparison.Ordinal))
                markdownFence = "";
        }
        Write(raw);
    }

    private void Write(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        if (surfaceId is not null)
        {
            var count = Math.Min(text.Length, MaxProgramLength - programLength);
            if (count == 0) return;
            programLength += count;
            emit(new() { ["kind"] = "open_ui_delta", ["agent"] = agent, ["block"] = surfaceId, ["text"] = text[..count].ToString() });
        }
        else
        {
            textId ??= JsonValues.NewId();
            emit(new() { ["kind"] = "delta", ["agent"] = agent, ["block"] = textId, ["type"] = "content", ["text"] = text.ToString() });
        }
    }

    public void Finish(bool interrupted)
    {
        if (probe.Length > 0) ReadFence();
        EndSurface();
        EndText(interrupted);
    }

    private void EndSurface()
    {
        if (surfaceId is not null && programLength > 0)
            emit(new() { ["kind"] = "open_ui_end", ["agent"] = agent, ["block"] = surfaceId });
        surfaceId = null;
    }

    private void EndText(bool interrupted)
    {
        if (textId is not null)
            emit(new() { ["kind"] = "turn_end", ["agent"] = agent, ["block"] = textId, ["interrupted"] = interrupted });
        textId = null;
    }
}
