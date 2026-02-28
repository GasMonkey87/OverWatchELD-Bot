using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

internal static class Program
{
    private static DiscordSocketClient? _client;
    private static string? _hubBaseUrl;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task Main(string[] args)
    {
        var token = Env("DISCORD_TOKEN", "BOT_TOKEN");
        _hubBaseUrl = (Env("HUB_BASE_URL") ?? "").Trim().TrimEnd('/');

        Log("OverWatchELD.VtcBot starting (worker bot, SaaS-style)...");
        Log($"Token={(string.IsNullOrWhiteSpace(token) ? "MISSING" : "OK")}");
        Log($"HUB_BASE_URL={(string.IsNullOrWhiteSpace(_hubBaseUrl) ? "MISSING" : _hubBaseUrl)}");

        if (string.IsNullOrWhiteSpace(token))
        {
            Log("❌ DISCORD_TOKEN/BOT_TOKEN is missing. Set it in Railway Variables.");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        if (string.IsNullOrWhiteSpace(_hubBaseUrl))
        {
            Log("❌ HUB_BASE_URL is missing. Set it in Railway Variables.");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent
        });

        _client.Log += m =>
        {
            Log($"{m.Severity} {m.Source}: {m.Message}");
            if (m.Exception != null) Log(m.Exception.ToString());
            return Task.CompletedTask;
        };

        _client.Ready += () =>
        {
            Log($"✅ Discord READY as {_client.CurrentUser}. Guilds={_client.Guilds.Count}");
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Keep alive
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task HandleMessageAsync(SocketMessage msg)
    {
        try
        {
            if (msg.Author.IsBot) return;

            var content = (msg.Content ?? "").Trim();
            if (!content.StartsWith("!link", StringComparison.OrdinalIgnoreCase)) return;

            // Block DMs (SaaS requirement)
            if (msg.Channel is IDMChannel)
            {
                await msg.Channel.SendMessageAsync("❌ Run `!link CODE` inside your Discord server (not DMs).");
                return;
            }

            if (msg.Channel is not SocketGuildChannel gch)
            {
                await msg.Channel.SendMessageAsync("❌ Could not detect server context.");
                return;
            }

            var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                await msg.Channel.SendMessageAsync("Usage: `!link WEK6N5`");
                return;
            }

            var code = NormalizeCode(parts[1]);
            if (!LooksLikeLinkCode(code))
            {
                await msg.Channel.SendMessageAsync("❌ Invalid code. Example: `!link WEK6N5` (6 chars).");
                return;
            }

            var url = $"{_hubBaseUrl}/api/link/confirm";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var payload = new ConfirmLinkReq
            {
                Code = code,
                GuildId = gch.Guild.Id.ToString(),
                GuildName = gch.Guild.Name,
                LinkedByUserId = msg.Author.Id.ToString()
            };

            var resp = await http.PostAsJsonAsync(url, payload, JsonOpts);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                await msg.Channel.SendMessageAsync($"❌ Hub confirm failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n```{body}```");
                return;
            }

            await msg.Channel.SendMessageAsync($"✅ Linked `{code}` to this server.\nNow go back to the ELD and finish linking (Claim).");
        }
        catch (Exception ex)
        {
            Log("🔥 link handler error:");
            Log(ex.ToString());
            try { await msg.Channel.SendMessageAsync("❌ Error while linking. Check bot logs."); } catch { }
        }
    }

    private static bool LooksLikeLinkCode(string code)
    {
        // Your setup uses 6-char codes like WEK6N5
        return code.Length == 6 && code.All(char.IsLetterOrDigit);
    }

    private static string NormalizeCode(string s)
    {
        s = (s ?? "").Trim().ToUpperInvariant();
        return new string(s.Where(char.IsLetterOrDigit).ToArray());
    }

    private static string? Env(params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = Environment.GetEnvironmentVariable(k);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static void Log(string s) => Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {s}");

    private sealed class ConfirmLinkReq
    {
        public string Code { get; set; } = "";
        public string GuildId { get; set; } = "";
        public string? GuildName { get; set; }
        public string? LinkedByUserId { get; set; }
    }
}
