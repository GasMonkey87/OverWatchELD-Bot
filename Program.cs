using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

using OverWatchELD.VtcBot.Threads;

internal static class Program
{
    private static DiscordSocketClient? _client;
    private static volatile bool _discordReady = false;

    private static readonly JsonSerializerOptions JsonReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly string DispatchCfgPath = Path.Combine(DataDir, "dispatch_settings.json");

    private static ThreadMapStore? _threadStore;
    private static readonly string ThreadMapPath = Path.Combine(DataDir, "thread_map.json");

    private static DispatchSettingsStore? _dispatchStore;

    private sealed class DispatchSettings
    {
        public string GuildId { get; set; } = "";
        public string? DispatchChannelId { get; set; }
        public string? DispatchWebhookUrl { get; set; }
        public string? AnnouncementChannelId { get; set; }
        public string? AnnouncementWebhookUrl { get; set; }
    }

    private sealed class DispatchSettingsStore
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<string, DispatchSettings> _byGuild = new();

        public DispatchSettingsStore(string path)
        {
            _path = path;
            Load();
        }

        public DispatchSettings Get(string guildId)
        {
            lock (_lock)
            {
                if (!_byGuild.TryGetValue(guildId, out var s))
                {
                    s = new DispatchSettings { GuildId = guildId };
                    _byGuild[guildId] = s;
                    Save();
                }
                return s;
            }
        }

        public void SetDispatchChannel(string guildId, ulong channelId)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.DispatchChannelId = channelId.ToString();
                Save();
            }
        }

        public void SetDispatchWebhook(string guildId, string webhookUrl)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.DispatchWebhookUrl = webhookUrl;
                Save();
            }
        }

        public void SetAnnouncementChannel(string guildId, ulong channelId)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.AnnouncementChannelId = channelId.ToString();
                Save();
            }
        }

        public void SetAnnouncementWebhook(string guildId, string webhookUrl)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.AnnouncementWebhookUrl = webhookUrl;
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) { _byGuild = new(); return; }
                var json = File.ReadAllText(_path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, DispatchSettings>>(json, JsonReadOpts);
                _byGuild = dict ?? new();
            }
            catch { _byGuild = new(); }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? DataDir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_byGuild, JsonWriteOpts));
            }
            catch { }
        }
    }

    private sealed class SendMessageReq
    {
        [JsonPropertyName("driverName")]
        public string? DriverName { get; set; }

        public string? DiscordUserId { get; set; }
        public string? DiscordUsername { get; set; }
        public string Text { get; set; } = "";
        public string? Source { get; set; }
    }

    private sealed class MessagesResponse
    {
        public bool Ok { get; set; }
        public string? GuildId { get; set; }
        public List<MessageDto>? Items { get; set; }
    }

    private sealed class MessageDto
    {
        public string Id { get; set; } = "";
        public string GuildId { get; set; } = "";
        [JsonPropertyName("driverName")]
        public string DriverName { get; set; } = "";
        public string Text { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
    }

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);

        _threadStore = new ThreadMapStore(ThreadMapPath);
        _dispatchStore = new DispatchSettingsStore(DispatchCfgPath);

        var token = (Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN env var.");
            return;
        }

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.MessageContent |
                GatewayIntents.GuildMembers
        });

        _client.Ready += () =>
        {
            _discordReady = true;
            Console.WriteLine("✅ Discord READY");
            return Task.CompletedTask;
        };

        _client.Log += msg =>
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {msg.Severity,-7} {msg.Source,-12} {msg.Message}");
            if (msg.Exception != null) Console.WriteLine(msg.Exception);
            return Task.CompletedTask;
        };

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        var portStr = (Environment.GetEnvironmentVariable("PORT") ?? "8080").Trim();
        if (!int.TryParse(portStr, out var port)) port = 8080;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        // ✅ This is the one you tested
        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        // ✅ REQUIRED BY ELD LOGIN: /api/vtc/servers
        // Must return servers[].id (NOT guildId)
        app.MapGet("/api/vtc/servers", () =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var servers = _client.Guilds.Select(g => new
            {
                id = g.Id.ToString(),          // ✅ ELD expects `id`
                name = g.Name,
                guildId = g.Id.ToString()      // keep extra for compatibility
            }).ToArray();

            return Results.Json(new { ok = true, servers }, JsonWriteOpts);
        });

        // ✅ REQUIRED BY ELD LOGIN: /api/vtc/name
        app.MapGet("/api/vtc/name", (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();

            SocketGuild? g = null;

            if (ulong.TryParse(guildIdStr, out var gid) && gid != 0)
                g = _client.Guilds.FirstOrDefault(x => x.Id == gid);

            g ??= _client.Guilds.FirstOrDefault();

            if (g == null)
                return Results.Json(new { ok = false, error = "NoGuild" }, statusCode: 404);

            // multiple field names so ELD can parse whatever it expects
            return Results.Json(new
            {
                ok = true,
                guildId = g.Id.ToString(),
                name = g.Name,
                vtcName = g.Name
            }, JsonWriteOpts);
        });

        // ✅ This is what you tested in browser
        app.MapGet("/api/messages", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            if (!ulong.TryParse(gidStr, out var gid) || gid == 0)
            {
                gid = _client.Guilds.FirstOrDefault()?.Id ?? 0;
                gidStr = gid.ToString();
            }
            if (gid == 0) return Results.Json(new { ok = false, error = "NoGuild" }, statusCode: 500);

            return Results.Json(new MessagesResponse
            {
                Ok = true,
                GuildId = gidStr,
                Items = new List<MessageDto>() // you can keep your existing implementation here
            }, JsonWriteOpts);
        });

        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        await Task.Delay(-1);
    }
}
