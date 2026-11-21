using System.Globalization;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal static class WorldCalendar
{
    internal const string Real = "real";
    internal const string Plugin = "plugin";
    private const string Format = "yyyy-MM-dd";

    internal static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    internal static string Render(DateOnly date) => date.ToString(Format, CultureInfo.InvariantCulture);
    internal static string Mode(JsonObject config) => config.GetString("date_mode") == Real ? Real : Plugin;

    internal static DateOnly RequireDate(string value) => Parse(value) ??
        throw new ArgumentException("Provide a date in YYYY-MM-DD format.");

    private static DateOnly? Parse(string value) =>
        DateOnly.TryParseExact(value, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    internal static void Configure(JsonObject config, string mode, DateOnly today)
    {
        if (mode is not (Real or Plugin)) throw new ArgumentException("Choose real or plugin date mode.");
        if (mode == Plugin && Parse(config.GetString("sim_date")) is null) config["sim_date"] = Render(today);
        config["date_mode"] = mode;
    }

    internal static DateOnly Date(JsonObject config, JsonObject state, DateOnly today)
    {
        if (Mode(config) == Plugin)
            return Parse(state.GetString("sim_date")) ?? Parse(config.GetString("sim_date")) ?? today;

        var manual = state.GetObject("manual_date");
        return Parse(manual.GetString("local_day")) == today ? Parse(manual.GetString("date")) ?? today : today;
    }

    internal static void Set(JsonObject config, JsonObject state, DateOnly date, DateOnly today)
    {
        if (Mode(config) == Real)
            state["manual_date"] = new JsonObject { ["date"] = Render(date), ["local_day"] = Render(today) };
        else
            state["sim_date"] = Render(date);
    }

    internal static void RequirePluginMode(JsonObject config)
    {
        if (Mode(config) != Plugin) throw new InvalidOperationException("This preset follows the real date.");
    }

    internal static JsonObject Snapshot(JsonObject config, JsonObject state, DateOnly today) => new()
        { ["mode"] = Mode(config), ["date"] = Render(Date(config, state, today)) };

    internal static JsonObject ModeSchema() => new() { ["type"] = "string", ["enum"] = new JsonArray(Real, Plugin) };
}
