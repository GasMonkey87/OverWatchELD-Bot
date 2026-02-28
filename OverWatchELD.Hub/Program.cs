using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

internal static class Program
{
    private static DiscordSocketClient? _client;

    // ✅ Your working SaaS API
    private const string SaaSBase = "https://overwatcheld-saas-production.up.railway.app";

    private static readonly HttpClient ProxyHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
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

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok("ok"));

        // ✅ Proxy GET /api/messages -> SaaS
        app.MapGet("/api/messages", async (HttpRequest req) =>
        {
            var qs = req.QueryString.HasValue ? req.QueryString.Value : "";
            var target = $"{SaaSBase}/api/messages{qs}";

            using var resp = await ProxyHttp.GetAsync(target);
            var body = await resp.Content.ReadAsStringAsync();

            return Results.Content(body, resp.Content.Headers.ContentType?.ToString() ?? "application/json",
                Encoding.UTF8, (int)resp.StatusCode);
        });

        // ✅ Proxy POST /api/messages/send -> SaaS
        app.MapPost("/api/messages/send", async (HttpRequest req) =>
        {
            var qs = req.QueryString.HasValue ? req.QueryString.Value : "";
            var target = $"{SaaSBase}/api/messages/send{qs}";

            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await ProxyHttp.PostAsync(target, content);
            var body = await resp.Content.ReadAsStringAsync();

            return Results.Content(body, resp.Content.Headers.ContentType?.ToString() ?? "application/json",
                Encoding.UTF8, (int)resp.StatusCode);
        });

        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");

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
            Console.WriteLine($"✅ READY as {_client.CurrentUser} (id: {_client.CurrentUser.Id})");
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

            await msg.Channel.SendMessageAsync($"✅ Link received for **{msg.Author.Username}**: `{code}`");
            return;
        }
    }
}
