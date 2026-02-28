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
    private static string _state = "starting";
    private static string? _lastError;

    private static string _hubBaseUrl = "";

    public static async Task Main(string[] args)
    {
        var token = Env("DISCORD_TOKEN", "BOT_TOKEN") ?? "";
        _hubBaseUrl = (Env("HUB_BASE_URL") ?? "").Trim().TrimEnd('/');

        Console.WriteLine("OverWatchELD.VtcBot starting...");
        Console.WriteLine($"Token={(string.IsNullOrWhiteSpace(token) ? "MISSING" : "OK")}");
        Console.WriteLine($"Hub={(string.IsNullOrWhiteSpace(_hubBaseUrl) ? "MISSING" : _hubBaseUrl)}");

        await RunDiscordAsync(token);
    }

    private static async Task RunDiscordAsync(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                _state = "no-token";
                Console.WriteLine("No DISCORD_TOKEN/BOT_TOKEN set. Sleeping...");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return;
            }

            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent
            });

            _client.Log += m =>
            {
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {m.Severity} {m.Source}: {m.Message}");
                if (m.Exception != null) Console.WriteLine(m.Exception);
                return Task.CompletedTask;
            };

            _client.Ready += () =>
            {
                _state = "ready";
                Console.WriteLine($"✅ READY as {_client.CurrentUser}. Guilds={_client.Guilds.Count}");
                return Task.CompletedTask;
            };

            _client.MessageReceived += OnMessageAsync;

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            _state = "running";
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            _state = "crashed";
            _lastError = ex.Message;
            Console.WriteLine("🔥 Bot crashed:");
            Console.WriteLine(ex);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
    }

    private static async Task OnMessageAsync(SocketMessage msg)
    {
        try
        {
            if (msg.Author.IsBot) return;

            var text = (msg.Content ?? "").Trim();
            if (!text.StartsWith("!link", StringComparison.OrdinalIgnoreCase)) return;

            if (msg.Channel is IDMChannel)
            {
                await msg.Channel.SendMessageAsync("❌ Run `!link CODE` inside your Discord server (not DMs).");
                return;
            }

            if (msg.Channel is not SocketGuildChannel gch)
            {
                await msg.Channel.SendMessageAsync("❌ Could not detect server.");
                return;
            }

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                await msg.Channel.SendMessageAsync("Usage: `!link WEK6N5`");
                return;
            }

            var code = NormalizeCode(parts[1]);
            if (code.Length != 6)
            {
                await msg.Channel.SendMessageAsync("❌ Invalid code. Example: `!link WEK6N5`");
                return;
            }

            if (string.IsNullOrWhiteSpace(_hubBaseUrl))
            {
                await msg.Channel.SendMessageAsync("❌ Hub not configured. Set `HUB_BASE_URL` in Railway variables.");
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"{_hubBaseUrl}/api/link/confirm";

            var payload = new ConfirmLinkReq
            {
                Code = code,
                GuildId = gch.Guild.Id.ToString(),
                GuildName = gch.Guild.Name,
                LinkedByUserId = msg.Author.Id.ToString()
            };

            var resp = await http.PostAsJsonAsync(url, payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                await msg.Channel.SendMessageAsync($"❌ Hub confirm failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n```{body}```");
                return;
            }

            await msg.Channel.SendMessageAsync($"✅ Linked `{code}` to this server. Now return to ELD and Claim.");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Console.WriteLine("🔥 link handler error:");
            Console.WriteLine(ex);
            try { await msg.Channel.SendMessageAsync("❌ Error while linking. Check bot logs."); } catch { }
        }
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

    private sealed class ConfirmLinkReq
    {
        public string Code { get; set; } = "";
        public string GuildId { get; set; } = "";
        public string? GuildName { get; set; }
        public string? LinkedByUserId { get; set; }
    }
}
