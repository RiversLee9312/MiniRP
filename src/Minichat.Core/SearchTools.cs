using System.Text.Json.Nodes;
namespace Minichat.Core;

internal static class SearchTools
{
    public static JsonArray Create()
    {
        var fields = new JsonObject { ["ref"] = Tools.String(), ["title"] = Tools.String(), ["url"] = Tools.String(), ["snippet"] = Tools.String(), ["source"] = Tools.String(), ["date"] = Tools.String(), ["credibility"] = Tools.String() };
        JsonObject F(params (string, JsonObject)[] extras)
        {
            var f = (JsonObject)fields.DeepClone();
            foreach (var (k, v) in extras)
            {
                f[k] = v;
            }

            return f;
        }
        return new JsonArray(
            Tools.Define("real_search", "Search real web, multiple requests allowed. Upstream per-request limit 1..50 does not limit final results.", new()
            {
                ["query"] = Tools.String(),
                ["limit"] = Tools.Integer()
            }, "query"),
            Tools.Define("real_fetch", "Fetch page. backend=auto|api|local|api_then_local. Explicit local bypasses API length limits. Full body stored in DB; read pages using content_key.", new()
            {
                ["url"] = Tools.String(),
                ["backend"] = Tools.String()
            }, "url"),
            Tools.Define("virtual_search", "Recall cached results/pages by literal query or exact URL; no args lists recent items.", new()
            {
                ["query"] = Tools.String(),
                ["url"] = Tools.String(),
                ["limit"] = Tools.Integer()
            }),
            Tools.Define("inspect_world", "Inspect current rules and established facts."),
            Tools.Define("add_result", "Copy ref or create a result and append to selected list.", F()),
            Tools.Define("select_results", "Set selected refs in requested order. No final count limit.", new()
            {
                ["refs"] = Tools.StringArray()
            }, "refs"),
            Tools.Define("edit_result", "Precisely change supplied fields only.", F(), "ref"),
            Tools.Define("create_page", "Create simulated page; omit URL for current fetch URL.", F(("content", Tools.String())), "title", "content"),
            Tools.Define("edit_page", "Precise old_string/new_string replacement on page ref.", new()
            {
                ["ref"] = Tools.String(),
                ["old_string"] = Tools.String(),
                ["new_string"] = Tools.String(),
                ["replace_all"] = Tools.Boolean()
            }, "ref", "old_string", "new_string"),
            Tools.Define("set_page", "Select recalled/fetched/simulated page.", new()
            {
                ["ref"] = Tools.String()
            }, "ref"),
            Tools.Define("cache_search_results", "Stage cache of completed refs.", new()
            {
                ["query"] = Tools.String(),
                ["refs"] = Tools.StringArray()
            }),
            Tools.Define("cache_page", "Stage completed page cache.", new()
            {
                ["ref"] = Tools.String()
            }, "ref"),
            Tools.Define("submit_search_results", "Submit selected or supplied refs. Ends request; must be last tool in batch. Empty list allowed.", new()
            {
                ["refs"] = Tools.StringArray()
            }),
            Tools.Define("submit_page", "Submit corresponding requested URL page. Ends request; must be last tool.", new()
            {
                ["ref"] = Tools.String()
            }),
            Tools.Define("fail_request", "End failed request without committing staged cache changes.", new()
            {
                ["message"] = Tools.String()
            }, "message"));
    }
}
