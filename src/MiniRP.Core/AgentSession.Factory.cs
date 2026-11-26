using System.Text.Json.Nodes;

namespace MiniRP.Core;

public sealed partial class AgentSession
{
    private enum PluginAccess { Scoped, BuiltIn, None }

    private sealed record Profile(string Name, PluginAccess Plugins = PluginAccess.Scoped,
        bool Cards = false, bool InteractiveUi = false, bool Memory = true, bool FollowUp = true, bool BackgroundMessages = false,
        Func<string, string>? LanguageGuidance = null);

    private sealed record Bindings(Func<Provider, Action<JsonObject>, string, MediaDescriptions?, AgentAssetWatches?, IEnumerable<AgentSkill>> Skills,
        Func<string, JsonObject?> State, AgentAssets? Assets, Func<string> OpenUi, Func<string> Locale,
        string? CheckpointRoot = null, Func<Provider, string, MediaDescriptions?>? DescribeMedia = null);

    private AgentSession OpenCheckpointBranch(string database, ModelClient model, JsonArray history, string guide,
        string locale, Action<string>? reasoning) =>
        new AgentSession(profile with { FollowUp = false, BackgroundMessages = false, LanguageGuidance = null },
            database, model, (item, _) =>
            {
                if (item.GetString("kind") == "delta" && item.GetString("type") == "reasoning")
                    reasoning?.Invoke(item.GetString("text"));
            }, null, _ => new((_, _, _, _, _) => [], _ => null, null, () => "", () => locale))
        {
            history = history,
            // The parent's first prompt message already includes its previous checkpoint.
            instructionState = new() { ["base"] = guide },
            Summary = "",
            delegatedSearch = delegatedSearch
        };

    /// <summary>Resolves role permissions and attaches every session to the same runtime.</summary>
    internal sealed class Factory(HostPort port, PluginRuntime? plugins, string? attachmentRoot, Func<string> openUi,
        Func<Workspace>? workspace = null, AgentContainer.Rooms? containers = null, Func<bool>? curlAvailable = null,
        AgentContainer.Rooms? presetContainers = null, Func<string?>? requestedLocale = null, Func<bool>? mediaAvailable = null,
        Func<(Provider? Image, Provider? Audio)>? descriptionModels = null)
    {
        public AgentSession Open(string role, string database, Func<Provider> provider, Func<JsonObject> context,
            Action<JsonObject> emit, Func<IEnumerable<AgentSkill>>? skills = null, Func<string, bool>? canReadChat = null,
            TurnInbox? inbox = null, Workspace? presetWorkspace = null, MemoryDb? sharedAssets = null, string? delegatedLocale = null,
            Workspace? conversationWorkspace = null) =>
            Open(role, database, provider, context, (item, _) => emit(item), skills, canReadChat, inbox, presetWorkspace,
                sharedAssets, delegatedLocale, conversationWorkspace);

        public AgentSession Open(string role, string database, Func<Provider> provider, Func<JsonObject> context,
            Action<JsonObject, SessionModelInfo?> emit, Func<IEnumerable<AgentSkill>>? skills = null, Func<string, bool>? canReadChat = null,
            TurnInbox? inbox = null, Workspace? presetWorkspace = null, MemoryDb? sharedAssets = null, string? delegatedLocale = null,
            Workspace? conversationWorkspace = null)
        {
            var profile = role switch
            {
                "chat" => new Profile("ChatAgent", Cards: true, InteractiveUi: true),
                "room-preparation" => new Profile("ChatAgent", PluginAccess.None, Memory: false, FollowUp: false),
                "env" => new Profile("EnvAgent", Cards: true),
                "preset" => new Profile("PresetAgent", Cards: true, BackgroundMessages: true, LanguageGuidance: PresetAgent.LanguageGuidance),
                "search" => new Profile("SearchAgent"),
                "fetch" => new Profile("BrowserAgent", FollowUp: false),
                "plugin" => new Profile("PluginAgent", PluginAccess.BuiltIn, LanguageGuidance: PluginAgent.LanguageGuidance),
                "temporary" => new Profile("TemporaryAgent"),
                "title" => new Profile("TitleAgent", PluginAccess.None, Memory: false, FollowUp: false),
                "plugin-generation" => new Profile("PluginGeneration", PluginAccess.None, Memory: false, FollowUp: false),
                _ => throw new ArgumentException("Unknown agent role.", nameof(role))
            };
            var model = new ModelClient(port, provider, attachmentRoot);
            return new AgentSession(profile, database, model, emit, inbox, session =>
            {
                if ((role is "chat" or "preset" or "plugin") && database != ":memory:") AgentContainer.RecoverJobs(session.Db);
                var scoped = profile.Plugins == PluginAccess.Scoped;
                var assetDb = sharedAssets ?? session.Db;
                var presetResearch = presetWorkspace is null ? null : new PresetResearch(presetWorkspace, assetDb);
                if (role == "preset" && presetWorkspace is not null)
                    AgentAssets.ImportPluginGrants(assetDb, SessionFiles.PluginDirectory(presetWorkspace.Root, presetWorkspace.PresetId));
                var assetWorkspace = conversationWorkspace is null ? workspace : () => conversationWorkspace;
                var assets = profile.Plugins != PluginAccess.None && plugins is not null && attachmentRoot is not null
                    ? new AgentAssets(plugins, assetDb, attachmentRoot, context, role is "chat" or "search" ? assetWorkspace : null,
                        presetResearch, protectDraftAssets: role == "preset") : null;
                MediaDescriptions? DescribeMedia(Provider selected, string locale)
                {
                    if (assets is null || descriptionModels is null) return null;
                    var (image, audio) = descriptionModels();
                    return image is null && audio is null ? null : new(port, attachmentRoot!, selected, image, audio, locale);
                }
                IEnumerable<AgentSkill> ResolveSkills(Provider selected, Action<JsonObject> attach, string locale,
                    MediaDescriptions? descriptions, AgentAssetWatches? watches)
                {
                    var persistent = database != ":memory:";
                    if (assets is not null)
                    {
                        var commands = new AgentAssetCommand(port, assets, Path.GetDirectoryName(attachmentRoot!)!);
                        if (persistent && curlAvailable?.Invoke() == true) yield return commands.Curl();
                        if (mediaAvailable?.Invoke() == true)
                        {
                            yield return commands.Media("ffmpeg", persistent);
                            yield return commands.Media("ffprobe", persistent);
                        }
                    }
                    if (plugins is not null && profile.Plugins != PluginAccess.None)
                    {
                        var environment = (JsonObject)context().DeepClone();
                        environment["locale"] = locale;
                        foreach (var skill in plugins.Skills(role, environment, builtInOnly: !scoped,
                                     assets: assets, showCard: profile.Cards ? session.ShowCard : null, provider: selected, attach: attach,
                                     sessionInfo: () => session.sessionInfo, manageAssets: persistent, descriptions: descriptions, watches: watches))
                            yield return skill;
                    }
                    var container = role switch { "chat" => containers, "preset" or "plugin" => presetContainers, _ => null };
                    if (container is not null && assets is not null && database != ":memory:")
                    {
                        var owner = context();
                        var preset = owner.GetString("preset");
                        var room = role is "preset" or "plugin" ? preset : owner.GetString("room", owner.GetString("chat"));
                        if (!owner.GetBoolean("_private_ensemble_dm") && WorkspaceIds.IsValid(preset) && WorkspaceIds.IsValid(room))
                            yield return new AgentContainer(container, assets, session.Db, preset, room,
                                presetWorkspace is null ? null : assetDb.Path).Skill();
                    }
                    if (skills is not null)
                        foreach (var skill in skills()) yield return skill;
                }
                return new(ResolveSkills, key => scoped ? plugins?.ReadStateProjection(role, context(), key, canReadChat) : null,
                    assets, () => profile.InteractiveUi ? openUi() : "",
                    () => AgentLanguage.Normalize(delegatedLocale) ?? AgentLanguage.Normalize(requestedLocale?.Invoke()) ??
                        AgentLanguage.Normalize(session.Db.Get(LocaleKey)) ?? AgentLanguage.Normalize(context().GetString("locale")) ?? AgentLanguage.Default,
                    attachmentRoot is null ? null : Path.Combine(Path.GetDirectoryName(attachmentRoot)!, "checkpoint-runs"), DescribeMedia);
            });
        }
    }
}
