namespace MiniRP.Core;

internal static class BuiltInSkillIds
{
    internal const string PluginDev = "plugin-dev";

    internal static string Current(string id) => id switch
    {
        "base-plugin-development" or "plugin-development" => "subagent",
        "base-plugin-project" or "plugin-project" or "project_assets" => PluginDev,
        "base-plugin-docs" => "plugin-docs",
        "base-preset-docs" => "preset-docs",
        "plugin-task" or "temporary-task" => "subagent-task",
        "asset_storage" or "view_asset" or "edit_asset" => AgentAssetStorage.SkillId,
        _ => id
    };

    // Old IDs are used for saved traces. Only current IDs are offered to models.
    internal static string Tool(string id)
    {
        const string prefix = "plugin_base_";
        return id switch
        {
            "plugin_base_plugin_spawn" => "subagent_spawn",
            "plugin_base_plugin_list" => "subagent_list",
            "plugin_base_plugin_send" => "subagent_send",
            "plugin_base_plugin_wait" => "subagent_wait",
            "plugin_base_plugin_archive" => "subagent_archive",
            "plugin_base_plugin_resume" => "subagent_resume",
            "plugin_base_project_debug" => "plugin_debug",
            "plugin_base_project_publish" => "plugin_publish",
            "plugin_base_project_verify" => "plugin_verify",
            "plugin_base_project_card_catalog" => "plugin_card_catalog",
            "plugin_base_project_card_preview" => "plugin_card_preview",
            "plugin_base_project_remove" => "plugin_uninstall",
            "project_asset_list" => "plugin_file_list",
            "project_asset_add" => "plugin_file_add",
            "project_asset_remove" => "plugin_file_remove",
            _ => id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id
        };
    }
}
