using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

internal static class Program
{
    private static DiscordSocketClient? _client;

    // ✅ Public release safe: Hub is environment-configurable (no guild locked)
    private const string DefaultHubBase = "https://overwatcheld-saas-production.up.railway.app";

    private static readonly HttpClient HubHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static async Task Main(string[] args)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
                    ?? Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN (or BOT_TOKEN).");
            return;
        }

        // -----------------------------
        // HTTP API (Railway)
        // -----------------------------
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        // ✅ Resolve hub base URL (no guild pin)
        var hubBase = (Environment.GetEnvironmentVariable("HUB_BASE_URL") ?? DefaultHubBase).Trim();
        hubBase = NormalizeAbsoluteBaseUrl(hubBase);

        // ✅ Critical: BaseAddress must be set so proxy can use relative URIs
        HubHttp.BaseAddress = new Uri(hubBase + "/", UriKind.Absolute);

        var app = builder.Build();

        // Root + health
        app.MapGet("/", () => Results.Ok(new { ok = true, service = "OverWatchELD.VtcBot" }));
        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        static bool HasGuildId(HttpRequest req)
        {
            if (req.Query.ContainsKey("guildId")) return true;
            if (req.Headers.ContainsKey("X-Guild-Id")) return true;
            return false;
        }

        static IResult MissingGuildId()
        {
            return Results.Json(new
            {
                error = "MissingGuildId",
                hint = "Provide ?guildId=YOUR_SERVER_ID or header X-Guild-Id."
            }, statusCode: 400);
        }

        async Task<IResult> ProxyToHub(HttpRequest req)
        {
            var path = req.Path.HasValue ? req.Path.Value! : "/";
            var qs = req.QueryString.HasValue ? req.QueryString.Value! : "";

            // ✅ Enforce guildId for multi-tenant endpoints (public release safe)
            if (path.StartsWith("/api/messages", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/vtc", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasGuildId(req))
                    return MissingGuildId();
            }

            // Build RELATIVE uri for HubHttp (BaseAddress already set)
            var rel = path.TrimStart('/') + qs;

            HttpResponseMessage resp;

            if (HttpMethods.IsPost(req.Method) || HttpMethods.IsPut(req.Method) || HttpMethods.IsPatch(req.Method))
            {
                using var reader = new StreamReader(req.Body);
                var body = await reader.ReadToEndAsync();

                using var content = new StringContent(body, Encoding.UTF8, req.ContentType ?? "application/json");
                resp = await HubHttp.SendAsync(new HttpRequestMessage(new HttpMethod(req.Method), rel) { Content = content });
            }
            else
            {
                resp = await HubHttp.SendAsync(new HttpRequestMessage(new HttpMethod(req.Method), rel));
            }

            var txt = await resp.Content.ReadAsStringAsync();

            return Results.Content(
                txt,
                resp.Content.Headers.ContentType?.ToString() ?? "application/json",
                Encoding.UTF8,
                (int)resp.StatusCode);
        }

        // ✅ Bulletproof route matching (prevents 404 drift)
        app.MapMethods("/api/messages", new[] { "GET", "POST" }, (HttpRequest req) => ProxyToHub(req));
        app.MapMethods("/api/messages/{**rest}", new[] { "GET", "POST" }, (HttpRequest req) => ProxyToHub(req));

        app.MapMethods("/api/vtc", new[] { "GET", "POST" }, (HttpRequest req) => ProxyToHub(req));
        app.MapMethods("/api/vtc/{**rest}", new[] { "GET", "POST" }, (HttpRequest req) => ProxyToHub(req));

        // Start HTTP server
        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        Console.WriteLine($"🔁 Proxying to hub: {hubBase}");

        // -----------------------------
        // Discord bot
        // -----------------------------
        var socketConfig = new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Info,
            MessageCacheSize = 50,
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.DirectMessages |
                GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(socketConfig);
        _client.Log += m => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        _client.Ready += () =>
        {
            // ✅ Public release safe: do not print usernames
            Console.WriteLine("✅ READY");
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        Console.WriteLine("🤖 Discord bot started.");
        await Task.Delay(-1);
    }

    private static async Task HandleMessageAsync(SocketMessage raw)
    {
        if (raw is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;

        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!")) return;

        var body = content.Substring(1).Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        if (body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        if (body.StartsWith("link", StringComparison.OrdinalIgnoreCase))
        {
            var code = body.Length > 4 ? body.Substring(4).Trim() : "";
            if (string.IsNullOrWhiteSpace(code))
            {
                await msg.Channel.SendMessageAsync("Usage: `!link YOURCODE`");
                return;
            }

            await msg.Channel.SendMessageAsync("✅ Link code received. Paste it into the ELD to complete pairing.");
            return;
        }
    }

    private static string NormalizeAbsoluteBaseUrl(string url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) url = DefaultHubBase;

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        while (url.EndsWith("/")) url = url[..^1];

        _ = new Uri(url, UriKind.Absolute);
        return url;
    }
}
