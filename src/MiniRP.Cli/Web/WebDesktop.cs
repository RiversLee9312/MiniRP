using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using MiniRP.Protocol;

namespace MiniRP.Cli;

internal static class WebDesktop
{
    private static readonly FileExtensionContentTypeProvider Types = new();
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "home", "list_presets", "list_chats", "library_page", "select_chat", "start_chat", "create_preset", "open_preset",
        "resume_preset_interview", "select_ensemble_chat", "open_ensemble_dm", "ensemble_member_trace", "rename_ensemble_chat", "message", "system_message", "steer",
        "cancel", "branch", "rename", "generate_title", "delete_chat", "delete_preset", "save_prompt", "save_search_mode",
        "reset_world", "memory", "models", "model_effort_levels", "plugins_list", "plugin_locale", "plugin_settings", "plugin_page",
        "plugin_setting", "plugin_action", "plugin_enabled", "plugin_scope", "plugin_uninstall", "plugin_command", "interaction_open"
    };
    private static readonly HashSet<string> Queries = new(StringComparer.Ordinal)
        { "library_page", "memory", "models", "model_effort_levels", "plugin_page", "open_ensemble_dm", "ensemble_member_trace", "message" };

    public static string ContentType(string name) =>
        Types.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    public static async Task Run(WebSession session, bool noOpen, BrowserHost? windowBrowser)
    {
        if (!File.Exists(Path.Combine(WebSession.StaticRoot, "index.html")))
            throw new FileNotFoundException("Build the bundled Web UI with the desktop build script.");
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory, WebRootPath = WebSession.StaticRoot
        });
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 0);
        });
        builder.Host.UseConsoleLifetime();
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 16_000_000);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 17_000_000);
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var host = "";
        var sessionCookie = "";
        app.Use(async (context, next) =>
        {
            if (context.Request.Host.Value != host ||
                context.Request.Headers.Origin is { Count: > 0 } origin && origin.ToString() != "http://" + host)
            {
                context.Response.StatusCode = 403;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = context.Request.Path.StartsWithSegments("/openui")
                ? "frame-ancestors 'self'"
                : "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; media-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Value == "/connect/" + secret)
            {
                context.Response.Cookies.Append(sessionCookie, secret,
                    new CookieOptions { HttpOnly = true, SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict, Path = "/" });
                context.Response.Redirect("/");
                return;
            }
            if (!context.Request.Cookies.TryGetValue(sessionCookie, out var cookie) || cookie != secret)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Open MiniRP using the URL printed in its terminal.");
                return;
            }
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception error) when (!context.Response.HasStarted &&
                error is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException or OperationCanceledException)
            {
                context.Response.Clear();
                context.Response.StatusCode = error is OperationCanceledException ? 408 : 400;
                await WriteJson(context, JsonValues.Error(error is OperationCanceledException ? "The request timed out." : error.Message));
            }
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/events", (HttpContext context) => Events(session, context, app.Lifetime.ApplicationStopping));
        async Task Transcript(HttpContext context, string operation)
        {
            var command = new JsonObject { ["op"] = operation };
            foreach (var key in new[] { "agent", "preset", "room", "conversation", "before", "after", "run" })
                command[key] = context.Request.Query[key].ToString();
            var locale = context.Request.Query["locale"].ToString();
            if (locale.Length > 128 || locale.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new ArgumentException("Choose a display language.");
            if (locale.Length > 0) command["locale"] = locale;
            using var limit = Timeout(context, 30);
            var page = await session.Query(command, limit.Token);
            page.Remove("kind");
            page.Remove("request");
            await WriteJson(context, page);
        }
        app.MapGet("/api/transcript", (HttpContext context) => Transcript(context, "transcript_page"));
        app.MapGet("/api/preset/activity", (HttpContext context) => Transcript(context, "preset_activity"));
        app.MapGet("/api/ensemble/archive", (HttpContext context) => Transcript(context, "ensemble_archive"));
        app.MapGet("/api/settings", (HttpContext context) => WriteJson(context, session.Settings.DesktopForSettings()));
        app.MapPost("/api/settings", async (HttpContext context) =>
        {
            var settings = await ReadJson(context);
            ValidateSettings(settings);
            session.Settings.SaveDesktop(settings);
            session.Send(session.Settings.Configure(web: true));
            await WriteJson(context, new() { ["ok"] = true, ["settings"] = session.Settings.DesktopForSettings() });
        });
        app.MapPost("/api/preferences", async (HttpContext context) =>
        {
            var input = await ReadJson(context);
            var theme = input.GetString("theme");
            if (theme is not ("system_default" or "light" or "dark"))
                throw new ArgumentException("Choose a display theme.");
            var locale = input.GetString("locale");
            if (locale.Length > 20 || locale.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new ArgumentException("Choose a display language.");
            session.Settings.SavePreferences(new()
            {
                ["locale"] = locale, ["theme"] = theme, ["thoughts"] = input.GetBoolean("thoughts")
            });
            await WriteJson(context, new() { ["ok"] = true });
        });
        app.MapPost("/api/command", async (HttpContext context) =>
        {
            var command = await ReadJson(context);
            var operation = command.GetString("op");
            if (!Operations.Contains(operation) || command.Any(pair => pair.Key.StartsWith('_') || pair.Key == "path"))
                throw new ArgumentException("That workspace operation is unavailable.");
            if (Queries.Contains(operation) || operation == "create_preset" && command.GetBoolean("open"))
            {
                using var limit = Timeout(context, operation == "models" ? 90 : 30);
                await WriteJson(context, await session.Query(command, limit.Token));
            }
            else
            {
                session.Send(command);
                await WriteJson(context, new() { ["ok"] = true });
            }
        });
        app.MapPost("/api/attachments", async (HttpContext context) =>
            await WriteJson(context, await session.StageAttachments((await context.Request.ReadFormAsync()).Files, context.RequestAborted)));
        app.MapPost("/api/picker/{id}", async (HttpContext context, string id) =>
        {
            var picker = session.Picker(id) ?? throw new InvalidOperationException("This file request has expired.");
            var files = (await context.Request.ReadFormAsync()).Files;
            var result = await session.StageAttachments(files, context.RequestAborted, picker);
            if (!session.CompleteInteraction(id, result)) throw new InvalidOperationException("This file request has expired.");
            await WriteJson(context, result);
        });
        app.MapPost("/api/interactions/{id}", async (HttpContext context, string id) =>
        {
            if (!session.CompleteInteraction(id, await ReadJson(context)))
                throw new InvalidOperationException("This request has expired.");
            await WriteJson(context, new() { ["ok"] = true });
        });
        app.MapPost("/api/import/{type}", (HttpContext context, string type) => Import(session, context, type));
        app.MapPost("/api/plugin-install/{id}", (HttpContext context, string id) => Install(session, context, id));
        app.MapPost("/api/export", (HttpContext context) => Export(session, context));
        app.MapGet("/api/asset", (HttpContext context) => Asset(session, context));
        app.MapGet("/api/attachment/{id}", (HttpContext context, string id) => Attachment(session, context, id));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        host = new Uri(address).Authority;
        sessionCookie = "MiniRP-" + new Uri(address).Port;
        var url = address + "/connect/" + secret;
        Console.WriteLine("MiniRP WebUI: " + url);
        if (windowBrowser is not null && !noOpen)
        {
            try { await windowBrowser.RunWindow(url, BrowserSetupUi.Desktop, app.Lifetime.ApplicationStopping); }
            finally
            {
                app.Lifetime.StopApplication();
                await app.StopAsync();
            }
            return;
        }
        Console.WriteLine("Press Ctrl+C to stop MiniRP.");
        if (!noOpen) OpenBrowser(url);
        await app.WaitForShutdownAsync();
    }

    private static async Task Events(WebSession session, HttpContext context, CancellationToken stopping)
    {
        context.Response.ContentType = "text/event-stream";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping, context.RequestAborted);
        try
        {
            await foreach (var batch in session.Listen(lifetime.Token))
            {
                var data = string.Join(',', batch.Select(JsonValues.Serialize));
                await context.Response.WriteAsync("data: [" + data + "]\n\n", lifetime.Token);
                await context.Response.Body.FlushAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private static async Task Import(WebSession session, HttpContext context, string type)
    {
        if (type is not ("preset" or "save" or "plugin")) throw new ArgumentException("Choose a supported archive type.");
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(session.Staging.FullName, id + "." + type);
        using var limit = Timeout(context, 300);
        var keep = false;
        try
        {
            await UploadArchive(context, path, limit.Token);
            var request = new JsonObject { ["op"] = type == "plugin" ? "plugin_inspect" : "archive_import",
                ["path"] = path, ["type"] = type };
            var result = (JsonObject)(await session.Query(request, limit.Token)).DeepClone();
            if (type == "plugin")
            {
                result["install"] = id;
                keep = true;
            }
            await WriteJson(context, result);
        }
        finally { if (!keep) File.Delete(path); }
    }

    private static async Task UploadArchive(HttpContext context, string path, CancellationToken ct)
    {
        // The workspace format permits up to 8 GiB of expanded data. Leave room for ZIP and multipart headers.
        const long archiveSize = 8L * 1024 * 1024 * 1024 + 64 * 1024 * 1024;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = archiveSize + 1024 * 1024;
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            contentType.MediaType != "multipart/form-data" ||
            HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 and <= 128 } boundary)
            throw new ArgumentException("Choose an archive.");
        var multipart = new MultipartReader(boundary, context.Request.Body) { BodyLengthLimit = archiveSize };
        var section = await multipart.ReadNextSectionAsync(ct);
        if (section is null || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
            !disposition.IsFileDisposition())
            throw new ArgumentException("Choose an archive.");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            await section.Body.CopyToAsync(output, ct);
        if (await multipart.ReadNextSectionAsync(ct) is not null) throw new ArgumentException("Choose one archive.");
    }

    private static async Task Install(WebSession session, HttpContext context, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Choose a plugin package.");
        var path = Path.Combine(session.Staging.FullName, id + ".plugin");
        if (!File.Exists(path)) throw new ArgumentException("This plugin package is unavailable.");
        using var limit = Timeout(context, 60);
        try
        {
            await WriteJson(context, await session.Query(new() { ["op"] = "plugin_install", ["path"] = path }, limit.Token));
            session.Send(new() { ["op"] = "plugins_list" });
            session.Send(new() { ["op"] = "plugin_settings" });
        }
        finally { File.Delete(path); }
    }

    private static async Task Export(WebSession session, HttpContext context)
    {
        var request = await ReadJson(context);
        var extension = request.GetString("type") switch
        {
            "room" => ".minirp-room", "preset" => ".minirp-preset", "save" => ".minirp-save", "plugin" => ".plugin",
            "transcript" => ".jsonl", _ => throw new ArgumentException("Choose a supported export type.")
        };
        var name = SafeName(request.GetString("name"), "MiniRP") + extension;
        var path = Path.Combine(session.Staging.FullName, Guid.NewGuid().ToString("N") + extension);
        using var limit = Timeout(context, 300);
        try
        {
            var command = new JsonObject { ["op"] = "archive_export", ["path"] = path };
            foreach (var key in new[] { "type", "preset", "room", "chat", "conversation", "id", "agent", "run" })
                if (request[key] is { } value) command[key] = value.DeepClone();
            await session.Query(command, limit.Token);
            await Results.File(path, "application/octet-stream", name).ExecuteAsync(context);
        }
        finally { File.Delete(path); }
    }

    private static async Task Asset(WebSession session, HttpContext context)
    {
        var query = context.Request.Query;
        var mode = query["mode"].ToString();
        var command = new JsonObject { ["op"] = mode switch
        {
            "background" => "preset_background_open", "display" => "display_asset_open", _ => "asset_open"
        } };
        foreach (var key in new[] { "url", "agent", "preset", "room", "conversation", "block" })
            command[key] = query[key].ToString();
        if (mode == "background") command["frame"] = query["frame"].ToString();
        if (query["attachment"] is { Count: > 0 } attachment) command["attachment"] = JsonValues.ParseObject(attachment.ToString());
        using var limit = Timeout(context, 300);
        var info = (await session.Query(command, limit.Token)).GetObject("result");
        var handle = info.GetString("handle");
        try
        {
            var type = info.GetString("mimeType");
            var inline = query["inline"] == "1" && IsMedia(type);
            var first = (await session.Query(new() { ["op"] = "asset_pull", ["handle"] = handle }, limit.Token)).GetObject("result");
            context.Response.ContentType = inline ? type : "application/octet-stream";
            if (!inline)
            {
                var header = new ContentDispositionHeaderValue("attachment");
                header.SetHttpFileName(SafeName(info.GetString("name"), "asset"));
                context.Response.Headers.ContentDisposition = header.ToString();
            }
            var chunk = first;
            while (!chunk.GetBoolean("done"))
            {
                await context.Response.Body.WriteAsync(session.TakeBinary(chunk.GetString("transfer")), limit.Token);
                chunk = (await session.Query(new() { ["op"] = "asset_pull", ["handle"] = handle }, limit.Token)).GetObject("result");
            }
        }
        finally { if (handle.Length > 0) session.Send(new() { ["op"] = "asset_close", ["handle"] = handle }); }
    }

    private static Task Attachment(WebSession session, HttpContext context, string id)
    {
        if (!(id.Length == 12 && id.All(Uri.IsHexDigit)) && !Guid.TryParseExact(id, "D", out _))
            return Results.NotFound().ExecuteAsync(context);
        var name = SafeName(context.Request.Query["name"].ToString(), "attachment");
        var type = ContentType(name);
        if (type == "application/octet-stream" && context.Request.Query["mime"] is { Count: 1 } hint)
            type = hint.ToString();
        var path = Path.Combine(session.Root, "attachments", id);
        if (!File.Exists(path)) return Results.NotFound().ExecuteAsync(context);
        if (context.Request.Query["inline"] == "1")
        {
            if (type == "application/pdf" || IsMedia(type))
                return Results.File(path, type, enableRangeProcessing: true).ExecuteAsync(context);
            if (type.StartsWith("text/", StringComparison.Ordinal) || type is "application/json" or "application/xml")
                return Results.File(path, "text/plain; charset=utf-8", enableRangeProcessing: true).ExecuteAsync(context);
        }
        return Results.File(path, "application/octet-stream", name).ExecuteAsync(context);
    }

    private static bool IsMedia(string mime) => mime is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/avif" or "image/bmp" or "image/x-icon" or "image/vnd.microsoft.icon" ||
        (mime.StartsWith("audio/", StringComparison.Ordinal) || mime.StartsWith("video/", StringComparison.Ordinal)) &&
        mime.AsSpan(mime.IndexOf('/') + 1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789.+-") < 0;

    private static string SafeName(string name, string fallback)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c)).Take(120).ToArray()).Trim('.', ' ');
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static void ValidateSettings(JsonObject settings)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { "environment" };
        foreach (var provider in settings.GetArray("providers"))
        {
            if (provider is not JsonObject || provider.GetString("id") is not { Length: > 0 } id || !ids.Add(id) ||
                !Uri.TryCreate(provider.GetString("url"), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
                throw new ArgumentException("Each provider needs a unique ID and an HTTP or HTTPS base URL.");
        }
        foreach (var agent in settings.GetObject("agents"))
            if (!ids.Contains(agent.Value.GetString("provider")))
                throw new ArgumentException("Choose an existing provider for each model.");
    }

    private static Task WriteJson(HttpContext context, JsonObject json)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonValues.Serialize(json), context.RequestAborted);
    }

    private static async Task<JsonObject> ReadJson(HttpContext context) =>
        await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject
        ?? throw new ArgumentException("Expected a JSON object.");

    private static CancellationTokenSource Timeout(HttpContext context, int seconds)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        result.CancelAfter(TimeSpan.FromSeconds(seconds));
        return result;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null)
            {
                var launch = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "/c", "start", "", url }) launch.ArgumentList.Add(argument);
                using var process = Process.Start(launch);
            }
            else
            {
                using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (System.ComponentModel.Win32Exception) { Console.WriteLine("Open the WebUI URL in your browser."); }
    }
}
