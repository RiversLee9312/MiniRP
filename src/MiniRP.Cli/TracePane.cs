using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MiniRP.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Editor;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace MiniRP.Cli;

/// <summary>One transcript renderer for all roles. Disclosure controls are independent of the /cot default.</summary>
internal sealed class TracePane : View
{
    private readonly List<JsonObject> blocks = [];
    private readonly Dictionary<string, bool> expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> headers = [];
    private readonly Dictionary<int, (string Block, string Url)> links = [];
    private static readonly Regex AssetLink = new(@"\[([^\]\r\n]+)\]\((minirp://assets/[a-z0-9._]+/[A-Za-z0-9_.-]+)\)", RegexOptions.NonBacktracking);
    private readonly Editor transcript = new() { ReadOnly = true, WordWrap = false, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly Editor composer = new() { WordWrap = true, Width = Dim.Fill(), Height = 2 };
    private int lastLines;
    private int composerHeight = 2;
    private bool dirty;

    public event Action<string>? Submitted;
    public event Action<string, string>? Download;
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
                    Activate(position.Y + transcript.Viewport.Y);
                }
            }
        };
        transcript.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                Activate((transcript.Document!.GetLineByOffset(transcript.CaretOffset).LineNumber - 1));
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
            var id = item.GetString("block", JsonValues.NewId());
            var block = new JsonObject
            {
                ["id"] = id,
                ["kind"] = "user",
                ["agent"] = item.GetString("agent"),
                ["content"] = item.GetString("text")
            };
            var index = blocks.FindIndex(existing => existing.GetString("id") == id);
            if (index < 0) blocks.Add(block);
            else blocks[index] = block;
        }
        else if (kind == "ensemble_assistant")
        {
            blocks.Add((JsonObject)item.GetObject("item").DeepClone());
        }
        else if (kind == "turn_end" && item["asset_links"] is JsonObject published)
        {
            var block = blocks.FirstOrDefault(block => block.GetString("id") == item.GetString("block"));
            if (block is not null) block["asset_links"] = published.DeepClone();
        }
        else if (kind is "delta" or "tool_start" or "tool_end" or "tool_trace_update" or "context_trace")
        {
            var id = item.GetString("block");
            var block = blocks.FirstOrDefault(block => block.GetString("id") == id);
            if (block is null)
            {
                if (kind == "tool_trace_update") return;
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
            else if (kind == "context_trace")
            {
                block["compaction"] = true;
                block["result"] = item.GetString("text");
            }
            else if (kind is "tool_start" or "tool_end")
            {
                block["tool"] = item.GetString("name");
                block[kind == "tool_start" ? "arguments" : "result"] = kind == "tool_start" ? item.GetString("arguments") : item.GetString("text");
            }
            if (kind is "tool_start" or "tool_end" or "tool_trace_update")
            {
                if (item.TryGetPropertyValue("tool_title", out var tool)) block["tool_title"] = JsonValues.Clone(tool);
                if (item.TryGetPropertyValue("skill_title", out var skill)) block["skill_title"] = JsonValues.Clone(skill);
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

    private void Activate(int row)
    {
        if (headers.TryGetValue(row, out var key))
        {
            expanded[key] = !expanded.GetValueOrDefault(key, DefaultExpanded);
            Render();
        }
        else if (links.TryGetValue(row, out var link)) Download?.Invoke(link.Block, link.Url);
    }

    private void Render()
    {
        var rows = new List<string>();
        var width = Math.Max(10, Viewport.Width - 2);
        var follow = (transcript.Document!.GetLineByOffset(transcript.CaretOffset).LineNumber - 1) >= lastLines - 4;
        headers.Clear();
        links.Clear();
        foreach (var block in blocks)
        {
            if (block.GetString("kind") == "user")
            {
                rows.AddRange(Wrap("› " + block.GetString("content"), width));
            }
            else
            {
                Fold(block, "reasoning", "Thoughts", block.GetString("reasoning"));
                if (block.GetBoolean("compaction") || block.GetString("tool") == "compact_context")
                    Fold(block, "compaction", "Context compaction", block.GetString("result"));
                else
                {
                    var label = block.GetString("tool_title").Trim();
                    if (label.Length == 0) label = block.GetString("tool");
                    var skill = block.GetString("skill_title").Trim();
                    if (skill.Length > 0 && skill != label) label += " · " + skill;
                    Fold(block, "tool", label, block.GetString("arguments") + "\n" + block.GetString("result"));
                }
                if (block.GetString("content").Length > 0)
                {
                    var published = block.GetObject("asset_links");
                    var downloads = new Dictionary<string, string>(StringComparer.Ordinal);
                    var markdown = AssetLink.Replace(block.GetString("content"), match =>
                    {
                        var url = match.Groups[2].Value;
                        if (!published.ContainsKey(url)) return match.Value;
                        var label = match.Groups[1].Value;
                        downloads.TryAdd(url, label);
                        return label;
                    });
                    rows.AddRange(Wrap(markdown, width));
                    foreach (var (url, label) in downloads)
                        foreach (var row in Wrap("↓ " + label, width))
                        {
                            links[rows.Count] = (block.GetString("id"), url);
                            rows.Add(row);
                        }
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
