using System.Text.Json;
using System.Text.Json.Nodes;

namespace MiniRP.Core;

internal sealed class ToolFailure(string message, string code, string phase, string nextStep, JsonObject? details = null)
    : InvalidOperationException(message)
{
    internal string Code { get; } = code;
    internal string Phase { get; } = phase;
    internal string NextStep { get; } = nextStep;
    internal JsonObject? Details { get; } = details;
}

internal static class ToolDiagnostics
{
    internal static JsonObject ReadArguments(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments) switch
            {
                JsonObject value => value,
                null => new(),
                _ => throw new ToolFailure("Tool arguments must be a JSON object.", "invalid_arguments", "arguments",
                    "Send an object with the fields in the tool definition."),
            };
        }
        catch (JsonException error)
        {
            var line = (error.LineNumber ?? 0) + 1;
            var column = (error.BytePositionInLine ?? 0) + 1;
            throw new ToolFailure($"Tool arguments contain invalid JSON on line {line}, byte {column}.",
                "invalid_arguments", "arguments", "Fix the JSON and resend the arguments as an object.",
                new() { ["line"] = line, ["byte"] = column });
        }
    }

    internal static JsonObject FromException(string tool, string? skill, Exception error)
    {
        var (message, code, phase, next, details) = error switch
        {
            ToolFailure failure => (failure.Message, failure.Code, failure.Phase, failure.NextStep, failure.Details),
            HostPort.HttpError http => ($"HTTP {http.Status} from the remote service.", "http_error", "execution",
                HttpGuidance(http.Status), new JsonObject { ["http_status"] = http.Status }),
            HostPort.PlatformError platform => (platform.Message, platform.TimedOut ? "timeout" : "platform_error", "platform",
                platform.TimedOut ? "Check whether the operation completed before retrying." :
                    "Check that this platform supports the operation and that any required app or system access is available.",
                new JsonObject { ["operation"] = platform.Operation }),
            UnauthorizedAccessException => (error.Message, "access_denied", "execution",
                "Check the required permission, resource grant, and current scope.", null),
            ArgumentException or FormatException or JsonException => (error.Message, "invalid_arguments", "execution",
                "Check the fields and value types in the tool definition.", null),
            InvalidDataException => (error.Message, "invalid_data", "execution",
                "Inspect the input or fixture named in the error before retrying.", null),
            FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException =>
                (error.Message, "not_found", "execution", "List the available resources and use the identifier the tool returned.", null),
            NotSupportedException => (error.Message, "unsupported", "execution",
                "Use a supported input or an available tool for this session.", null),
            TimeoutException => (error.Message, "timeout", "execution",
                "Check whether the operation completed before retrying.", null),
            OperationCanceledException => ("The tool's operation was interrupted.", "operation_interrupted", "execution",
                "Check whether the operation completed before retrying.", null),
            InvalidOperationException => (error.Message, "precondition_failed", "execution",
                "Address the state or prerequisite described in the error before retrying.", null),
            IOException => (error.Message, "io_error", "execution",
                "Check that the resource is still reachable or readable before retrying.", null),
            _ => (error.Message, "execution_failed", "execution",
                "Use the error detail to check the smallest relevant input or operation.", null),
        };
        var result = JsonValues.Error(message, code);
        result["diagnostic"] = Describe(tool, skill, code, phase, next, details);
        return result;
    }

    internal static JsonNode? AnnotateResult(string tool, string? skill, JsonNode? value)
    {
        if (value is not JsonObject result) return value;
        if (tool == "plugin_base_project_publish" && result["verification"] is JsonObject verification &&
            verification.GetString("error").Length > 0 &&
            !verification.ContainsKey("diagnostic"))
            verification["diagnostic"] = Describe(tool, skill, "verification_failed", "post_install",
                "The plugin is installed. Inspect the verification error before retrying with project_verify.");
        if (result.ContainsKey("diagnostic")) return result;
        if (tool == "plugin_base_project_card_preview" &&
            (!result.GetBoolean("available", true) || !result.GetBoolean("rendered", true)))
        {
            var available = result.GetBoolean("available", true);
            result["diagnostic"] = Describe(tool, skill, available ? "preview_failed" : "preview_unavailable", "platform",
                available ? "Read the renderer diagnostics and re-run the same card fixture after correcting the input." :
                    "Run the card preview on a platform with an Android OpenUI renderer.");
        }
        else if (tool is ("shell" or "curl") && result.ContainsKey("exit_code") && result.GetInt32("exit_code") != 0)
            result["diagnostic"] = Describe(tool, skill, "command_failed", "execution",
                tool == "shell" ? "Read the command output before retrying." : "Read curl's reported error and output before retrying.",
                new() { ["exit_code"] = result.GetInt32("exit_code") });
        else if (result.GetString("error").Length > 0)
            result["diagnostic"] = Describe(tool, skill, result.GetString("code", "tool_reported_error"), "execution",
                "Check the specific failure in the tool result before retrying.");
        return result;
    }

    private static JsonObject Describe(string tool, string? skill, string code, string phase, string nextStep, JsonObject? details = null)
    {
        var diagnostic = new JsonObject { ["tool"] = tool, ["code"] = code, ["phase"] = phase, ["next_step"] = nextStep };
        if (skill is not null) diagnostic["skill"] = skill;
        if (details is not null) diagnostic["details"] = details.DeepClone();
        return diagnostic;
    }

    private static string HttpGuidance(int status) => status switch
    {
        401 or 403 => "Check the service credentials and access policy.",
        404 => "Check the endpoint or resource identifier.",
        429 => "Check the service's rate limit before retrying.",
        >= 500 => "Check service availability before retrying.",
        _ => "Check the service's API contract and the request input.",
    };
}
