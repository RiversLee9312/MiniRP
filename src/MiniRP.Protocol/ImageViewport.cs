using System.Text.Json.Nodes;

namespace MiniRP.Protocol;

public readonly record struct ImageViewport(int X, int Y, int Width, int Height)
{
    public const int MaxSide = 4096;

    public static ImageViewport? Read(JsonObject options)
    {
        if (options["viewport"] is null) return null;
        if (options["viewport"] is not JsonObject rect)
            throw new ArgumentException("viewport must be an object with x, y, width and height.");

        int ReadInt(string key, int min)
        {
            if (rect[key] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < min)
                throw new ArgumentException("viewport requires nonnegative integer x and y and positive integer width and height.");
            return number;
        }
        return new(ReadInt("x", 0), ReadInt("y", 0), ReadInt("width", 1), ReadInt("height", 1));
    }

    public void RequireInside(int width, int height)
    {
        if ((long)X + Width > width || (long)Y + Height > height)
            throw new ArgumentException($"The viewport is outside the {width} × {height} source.");
    }

    public JsonObject Describe() => new() { ["x"] = X, ["y"] = Y, ["width"] = Width, ["height"] = Height };
}
