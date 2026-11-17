using System.Text;
using System.Text.Json.Nodes;
using Minichat.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Editor;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Minichat.Cli;

/// <summary>One transcript renderer for all roles. Disclosure controls are independent of the /cot default.</summary>
internal sealed class TracePane : View
{
    private readonly List<JsonObject> blocks = [];
    private readonly Dictionary<string, bool> expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> headers = [];
    private readonly Editor transcript = new() { ReadOnly = true, WordWrap = false, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Editor composer = new() { WordWrap = true, Width = Dim.Fill(), Height = 2 };
    private int lastLines;
    private int composerHeight = 2;
    private bool dirty;

    public event Action<string>? Submitted;
    public bool DefaultExpanded
    {
        get; set;
    }
    public string Role
    {
        get;
    }

    public TracePane(string role)
    {
        Role = role;
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();
        composer.Y = Pos.AnchorEnd(2);
        transcript.Height = Dim.Fill(3);
        composer.ViewportSettings = ViewportSettingsFlags.HasScrollBars;
        transcript.ViewportSettings = ViewportSettingsFlags.HasScrollBars;
        Add(transcript, composer);
        if (role != "chat")
        {
            Add(new Label { Text = role == "env" ? "EnvAgent" : "PresetAgent", X = 0, Y = 0 });
            transcript.Y = 1;
        }
        composer.ContentChanged += (_, _) => ResizeComposer();
        composer.KeyBindings.Add(Key.Enter.WithAlt, Command.NewLine);
        composer.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                key.Handled = true;
                var text = composer.Text.Trim();
                if (text.Length > 0)
                {
                    composer.Text = "";
                    Submitted?.Invoke(text);
                }
            }
        };
        transcript.MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
            {
                if (mouse.Position is { } position)
                {
                    Toggle(position.Y + transcript.Viewport.Y);
                }
            }
        };
        transcript.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                Toggle((transcript.Document!.GetLineByOffset(transcript.CaretOffset).LineNumber - 1));
                key.Handled = true;
            }
        };
        SubViewsLaidOut += (_, _) => ResizeComposer();
    }

    public void FocusInput() => composer.SetFocus();

    public void Load(IEnumerable<JsonObject> history)
    {
        blocks.Clear();
        expanded.Clear();
        blocks.AddRange(history.Select(block => (JsonObject)block.DeepClone()));
        dirty = true;
    }

    public void Flush()
    {
        if (dirty)
        {
            dirty = false;
            Render();
        }
    }

    public void Receive(JsonObject item)
    {
        var kind = item.GetString("kind");
        if (kind == "user")
        {
            blocks.Add(new()
            {
                ["id"] = JsonValues.NewId(),
                ["kind"] = "user",
                ["agent"] = item.GetString("agent"),
                ["content"] = item.GetString("text")
            });
        }
        else if (kind is "delta" or "tool_start" or "tool_end")
        {
            var id = item.GetString("block");
            var block = blocks.FirstOrDefault(block => block.GetString("id") == id);
            if (block is null)
            {
                block = new()
                {
                    ["id"] = id,
                    ["agent"] = item.GetString("agent")
                };
                blocks.Add(block);
            }
            if (kind == "delta")
            {
                var type = item.GetString("type");
                block[type] = block.GetString(type) + item.GetString("text");
            }
            else
            {
                block["tool"] = item.GetString("name");
                block[kind == "tool_start" ? "arguments" : "result"] = kind == "tool_start" ? item.GetString("arguments") : item.GetString("text");
            }
        }
        dirty = true;
    }

    public void Notice(string text)
    {
        blocks.Add(new()
        {
            ["content"] = text
        });
        Render();
    }

    private void Toggle(int row)
    {
        if (headers.TryGetValue(row, out var key))
        {
            expanded[key] = !expanded.GetValueOrDefault(key, DefaultExpanded);
            Render();
        }
    }

    private void Render()
    {
        var rows = new List<string>();
        var width = Math.Max(10, Viewport.Width - 2);
        var follow = (transcript.Document!.GetLineByOffset(transcript.CaretOffset).LineNumber - 1) >= lastLines - 4;
        headers.Clear();
        foreach (var block in blocks)
        {
            if (block.GetString("kind") == "user")
            {
                rows.AddRange(Wrap("› " + block.GetString("content"), width));
            }
            else
            {
                Fold(block, "reasoning", "Thoughts", block.GetString("reasoning"));
                Fold(block, "tool", block.GetString("tool"), block.GetString("arguments") + "\n" + block.GetString("result"));
                if (block.GetString("content").Length > 0)
                {
                    rows.AddRange(Wrap(block.GetString("content"), width));
                }
            }
            rows.Add("");
        }
        transcript.Text = string.Join('\n', rows);
        lastLines = rows.Count;
        if (follow)
        {
            transcript.CaretOffset = transcript.Text.Length;
        }

        void Fold(JsonObject block, string type, string label, string text)
        {
            if (block.GetString(type).Length == 0)
            {
                return;
            }
            var key = block.GetString("id") + ":" + type;
            if (!expanded.ContainsKey(key))
            {
                expanded[key] = type == "reasoning" && DefaultExpanded;
            }
            var open = expanded[key];
            headers[rows.Count] = key;
            rows.Add((open ? "⌄ " : "› ") + label + " · " + block.GetString("agent"));
            if (open)
            {
                rows.AddRange(Wrap(text, width));
            }
        }
    }

    private void ResizeComposer()
    {
        var lines = Wrap(composer.Text + " ", Math.Max(10, composer.Viewport.Width)).Count();
        var height = Math.Clamp(lines + 1, 2, 8);
        if (height == composerHeight)
        {
            return;
        }
        composerHeight = height;
        composer.Height = height;
        composer.Y = Pos.AnchorEnd(height);
        transcript.Height = Dim.Fill(height + 1);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var current = new StringBuilder();
            var columns = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                var size = Math.Max(0, rune.GetColumns());
                if (columns + size > width && current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                    columns = 0;
                }
                current.Append(rune);
                columns += size;
            }
            yield return current.ToString();
        }
    }
}
