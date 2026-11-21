using System.Text.Json.Nodes;
namespace MiniRP.Core;

internal static class SearchTools
{
    public static JsonArray Create(bool direct = false)
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
            Tools.Define("inspect_world", "Inspect current rules and established facts."),
            Tools.Define("add_result", "Copy ref or create a result and append to selected list.", F()),
            Tools.Define("select_results", "Set selected refs in requested order. No final count limit.", new()
            {
                ["refs"] = Tools.StringArray()
            }, "refs"),
            Tools.Define("create_page", "Create simulated page; omit URL for current fetch URL.", F(("content", Tools.String())), "title", "content"),
            Tools.Define("set_page", "Select recalled/fetched/simulated page.", new()
            {
                ["ref"] = Tools.String()
            }, "ref"),
            Tools.Define("submit_search_results", direct ? "Read and cache selected or supplied refs for query. Continue answering after this tool." : "Submit selected or supplied refs. Ends request; must be last tool in batch. Empty list allowed.", new()
            {
                ["refs"] = Tools.StringArray(),
                ["query"] = Tools.String()
            }, direct ? ["query"] : []),
            Tools.Define("submit_page", direct ? "Read and cache the selected or supplied page. Continue answering after this tool." : "Submit corresponding requested URL page. Ends request; must be last tool.", new()
            {
                ["ref"] = Tools.String()
            }),
            Tools.Define("fail_request", "End failed request without committing staged cache changes.", new()
            {
                ["message"] = Tools.String()
            }, "message"));
    }
}
