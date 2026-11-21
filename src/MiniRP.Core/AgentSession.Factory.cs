using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class AgentSession
{
    private enum PluginAccess { Scoped, BuiltIn, None }

    private sealed record Profile(string Name, PluginAccess Plugins = PluginAccess.Scoped,
        bool Cards = false, bool InteractiveUi = false, bool Memory = true, bool FollowUp = true);

    private sealed record Bindings(Func<Provider, Action<JsonObject>, IEnumerable<AgentSkill>> Skills, Func<string, JsonObject?> State,
        AgentAssets? Assets, Func<string> OpenUi);

    /// <summary>Resolves role permissions and attaches every session to the same runtime.</summary>
    internal sealed class Factory(HostPort port, PluginRuntime? plugins, string? attachmentRoot, Func<string> openUi,
        Func<Workspace>? workspace = null, AgentContainer.Rooms? containers = null, Func<bool>? curlAvailable = null)
    {
        public AgentSession Open(string role, string database, Func<Provider> provider, Func<JsonObject> context,
            Action<JsonObject> emit, Func<IEnumerable<AgentSkill>>? skills = null, Func<string, bool>? canReadChat = null,
            TurnInbox? inbox = null) => Open(role, database, provider, context, (item, _) => emit(item), skills, canReadChat, inbox);

        public AgentSession Open(string role, string database, Func<Provider> provider, Func<JsonObject> context,
            Action<JsonObject, SessionModelInfo?> emit, Func<IEnumerable<AgentSkill>>? skills = null, Func<string, bool>? canReadChat = null,
            TurnInbox? inbox = null)
        {
            var profile = role switch
            {
                "chat" => new Profile("ChatAgent", Cards: true, InteractiveUi: true),
                "env" => new Profile("EnvAgent", Cards: true),
                "preset" => new Profile("PresetAgent", Cards: true),
                "search" => new Profile("SearchAgent"),
                "fetch" => new Profile("FetchAgent", FollowUp: false),
                "plugin" => new Profile("PluginAgent", PluginAccess.BuiltIn),
                "temporary" => new Profile("TemporaryAgent"),
                "title" => new Profile("TitleAgent", PluginAccess.None, Memory: false, FollowUp: false),
                "plugin-generation" => new Profile("PluginGeneration", PluginAccess.None, Memory: false, FollowUp: false),
                _ => throw new ArgumentException("Unknown agent role.", nameof(role))
            };
            var model = new ModelClient(port, provider, attachmentRoot);
            return new AgentSession(profile, database, model, emit, inbox, session =>
            {
                if (role == "chat" && database != ":memory:") AgentContainer.RecoverJobs(session.Db);
                var scoped = profile.Plugins == PluginAccess.Scoped;
                var assets = profile.Plugins != PluginAccess.None && plugins is not null && attachmentRoot is not null
                    ? new AgentAssets(plugins, session.Db, attachmentRoot, context, role is "chat" or "search" ? workspace : null) : null;
                IEnumerable<AgentSkill> ResolveSkills(Provider selected, Action<JsonObject> attach)
                {
                    if (assets is not null && attachmentRoot is not null && database != ":memory:")
                    {
                        yield return assets.StorageSkill();
                        if (curlAvailable?.Invoke() == true)
                            yield return new AgentCurl(port, assets, Path.GetDirectoryName(attachmentRoot)!).Skill();
                    }
                    if (plugins is not null && profile.Plugins != PluginAccess.None)
                        foreach (var skill in plugins.Skills(role, context(), builtInOnly: !scoped,
                                     assets: assets, showCard: profile.Cards ? session.ShowCard : null, provider: selected, attach: attach,
                                     sessionInfo: () => session.sessionInfo))
                            yield return skill;
                    if (role == "chat" && containers is not null && assets is not null && database != ":memory:")
                    {
                        var owner = context();
                        var preset = owner.GetString("preset");
                        var room = owner.GetString("room", owner.GetString("chat"));
                        if (WorkspaceIds.IsValid(preset) && WorkspaceIds.IsValid(room))
                            yield return new AgentContainer(containers, assets, session.Db, preset, room).Skill();
                    }
                    if (skills is not null)
                        foreach (var skill in skills()) yield return skill;
                }
                return new(ResolveSkills, key => scoped ? plugins?.ReadStateProjection(role, context(), key, canReadChat) : null,
                    assets, () => profile.InteractiveUi ? openUi() : "");
            });
        }
    }
}
