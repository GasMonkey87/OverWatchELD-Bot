// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// Fixes:
// - Empty server name issue
// - DiscordNotReady race condition
// - CS0176 error
// - Safe multi-guild
// - Admin commands preserved

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using OverWatchELD.VtcBot.Threads;

internal static class Program
{
    private static DiscordSocketClient? _client;
    private static volatile bool _discordReady = false;

    private static readonly JsonSerializerOptions JsonReadOpts =
        new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions JsonWriteOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string DataDir =
        Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly string GuildCfgPath =
        Path.Combine(DataDir, "guild_cfg.json");

    private static readonly string DriversPath =
        Path.Combine(DataDir, "drivers.json");

    private static readonly ConcurrentDictionary<string, GuildCfg> GuildCfgs = new();
    private static readonly ConcurrentDictionary<string, List<DriverItem>> GuildDrivers = new();
    private static readonly object DriversGate = new();

    private static ThreadMapStore? _threadStore;

    private sealed class GuildCfg
    {
        public string GuildId { get; set; } = "";
        public string DispatchChannelId { get; set; } = "";
        public string AnnouncementChannelId { get; set; } = "";
        public string DispatchWebhookUrl { get; set; } = "";
        public Dictionary<string, string> Webhooks { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public string VtcName { get; set; } = "";
    }

    private sealed class DriverItem
    {
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);

        _threadStore = new ThreadMapStore(
            Path.Combine(DataDir, "thread_map.json"));

        var token = (Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN");
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

        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        LoadGuildCfgs();
        LoadDrivers();

        var port = int.TryParse(
            Environment.GetEnvironmentVariable("PORT"),
            out var p) ? p : 8080;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        // FIXED SERVER ENDPOINT
        app.MapGet("/api/vtc/servers", async () =>
        {
            if (_client == null)
                return Results.Json(new { ok = false }, statusCode: 503);

            var start = DateTime.UtcNow;
            while (!_discordReady &&
                   (DateTime.UtcNow - start) < TimeSpan.FromSeconds(6))
            {
                await Task.Delay(200);
            }

            if (!_discordReady)
                return Results.Json(new { ok = false, retry = true }, statusCode: 503);

            var servers = _client.Guilds.Select(g => new
            {
                guildId = g.Id.ToString(),
                name = g.Name,
                Name = g.Name // compatibility
            }).ToArray();

            return Results.Json(new { ok = true, servers }, JsonWriteOpts);
        });

        await app.RunAsync();
    }

    private static async Task HandleMessageAsync(SocketMessage socketMsg)
    {
        if (socketMsg is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;
        if (!msg.Content.StartsWith("!")) return;

        var body = msg.Content[1..].Trim();

        if (body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        if (!IsAdmin(msg.Author))
        {
            await msg.Channel.SendMessageAsync("⛔ Admin only.");
            return;
        }

        if (body.StartsWith("adddriver", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitArgs(body);
            if (parts.Count < 3)
            {
                await msg.Channel.SendMessageAsync("Usage: !adddriver \"Name\" Role");
                return;
            }

            var guildId = GetGuildId(msg);
            var name = parts[1];
            var role = string.Join(' ', parts.Skip(2));

            lock (DriversGate)
            {
                var list = GuildDrivers.GetOrAdd(guildId, _ => new());
                var existing = list.FirstOrDefault(d =>
                    d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                    existing.Role = role;
                else
                    list.Add(new DriverItem { Name = name, Role = role });

                SaveDriversUnsafe();
            }

            await msg.Channel.SendMessageAsync($"✅ Driver saved: {name}");
            return;
        }

        if (body.StartsWith("remove", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitArgs(body);
            if (parts.Count < 2)
            {
                await msg.Channel.SendMessageAsync("Usage: !remove \"Name\"");
                return;
            }

            var guildId = GetGuildId(msg);
            var name = parts[1];

            bool removed = false;

            lock (DriversGate)
            {
                if (GuildDrivers.TryGetValue(guildId, out var list))
                {
                    var before = list.Count;
                    list.RemoveAll(d =>
                        string.Equals(d?.Name, name,
                            StringComparison.OrdinalIgnoreCase));
                    removed = list.Count != before;
                    if (removed) SaveDriversUnsafe();
                }
            }

            await msg.Channel.SendMessageAsync(
                removed ? $"✅ Removed {name}" : "Not found");
        }
    }

    private static string GetGuildId(SocketUserMessage msg)
    {
        if (msg.Channel is SocketGuildChannel gc)
            return gc.Guild.Id.ToString();
        return "unknown";
    }

    private static bool IsAdmin(SocketUser user)
    {
        return user is SocketGuildUser gu &&
               (gu.GuildPermissions.Administrator ||
                gu.GuildPermissions.ManageGuild);
    }

    private static List<string> SplitArgs(string input)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else sb.Append(c);
        }

        if (sb.Length > 0)
            result.Add(sb.ToString());

        return result;
    }

    private static void LoadGuildCfgs()
    {
        if (!File.Exists(GuildCfgPath)) return;
        var json = File.ReadAllText(GuildCfgPath);
        var list = JsonSerializer.Deserialize<List<GuildCfg>>(json, JsonReadOpts) ?? new();
        foreach (var cfg in list)
            GuildCfgs[cfg.GuildId] = cfg;
    }

    private static void LoadDrivers()
    {
        if (!File.Exists(DriversPath)) return;
        var json = File.ReadAllText(DriversPath);
        var data = JsonSerializer.Deserialize<Dictionary<string, List<DriverItem>>>(json, JsonReadOpts);
        if (data != null)
            foreach (var kvp in data)
                GuildDrivers[kvp.Key] = kvp.Value;
    }

    private static void SaveDriversUnsafe()
    {
        var json = JsonSerializer.Serialize(GuildDrivers,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(DriversPath, json);
    }
}
