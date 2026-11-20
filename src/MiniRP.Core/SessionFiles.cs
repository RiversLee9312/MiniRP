namespace MiniRP.Core;

internal static class SessionFiles
{
    public static string PresetDirectory(string root, string preset) => Path.Combine(root, "preset_sessions", preset);
    public static string PresetDatabase(string directory) => Path.Combine(directory, "agent.sqlite");
    public static string ConversationDirectory(string room) => Path.Combine(room, "agents");
    public static string Conversation(string room, string role) => Path.Combine(ConversationDirectory(room), role + ".sqlite");
    public static string Ensemble(string room, string participant, string role) =>
        Path.Combine(room, "ensemble", PresetDesign.Id(participant), role + ".sqlite");
    public static string Fetch(string room, string job) => Path.Combine(room, "fetch", Workspace.Safe(job) + ".sqlite");
    public static string PluginDirectory(string root, string preset) => Path.Combine(PresetDirectory(root, preset), "plugin_agents");
}
