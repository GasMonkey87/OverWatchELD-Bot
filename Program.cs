using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static class Program
{
    private static DiscordSocketClient? _client;
    private static string _discordState = "starting";
    private static string _discordUser = "";
    private static string? _lastError;

    private static string? _hubBaseUrl;

    public static async Task Main(string[] args)
    {
        var discordToken = Env("DISCORD_TOKEN", "BOT_TOKEN");
        _hubBaseUrl = (Env("HUB_BASE_URL") ?? "").Trim().TrimEnd('/');

        var port = 8080;
        if (int.TryParse(Env("PORT"), out var p) && p > 0) port = p;

        // ---- Start the HTTP host (keeps Railway container alive + /health endpoint)
        var webTask = RunWebAsync(port);

        // ---- Start Discord bot (optional if no token)
        var botTask = RunDiscordAsync(discordToken);

        await Task.WhenAll(webTask, botTask);
    }

    private static async Task RunWebAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        });

        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        var app = builder.Build();
        app.UseForwardedHeaders();

        app.MapGet("/", () => Results.Ok(new { ok = true, service = "OverWatchELD.VtcBot", hint = "Use /health" }));

        app.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            service = "OverWatchELD.VtcBot",
            discord = new
            {
                state = _discordState,
                user = _discordUser,
                guilds = _client?.Guilds.Count ?? 0
            },
            hub = new
            {
                baseUrl = _hubBaseUrl ?? "",
                configured = !string.IsNullOrWhiteSpace(_hubBaseUrl)
            },
            lastError = _lastError ?? ""
        }));

        await app.RunAsync();
    }

    private static async Task RunDiscordAsync(string? token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                _discordState = "no-token";
                Console.WriteLine("⚠️ No DISCORD_TOKEN/BOT_TOKEN set; skipping Discord login.");
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return;
            }

            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents =
                    GatewayIntents.Guilds |
                    GatewayIntents.GuildMessages |
                    GatewayIntents.MessageContent
            });

            _client.Log += m =>
            {
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {m.Severity} {m.Source}: {m.Message}");
                if (m.Exception != null) Console.WriteLine(m.Exception);
                return Task.CompletedTask;
            };

            _client.Ready += () =>
            {
                _discordState = "ready";
                _discordUser = _client.CurrentUser?.ToString() ?? "";
                Console.WriteLine($"✅ Discord READY as {_discordUser}. Guilds={_client.Guilds.Count}");
                return Task.CompletedTask;
            };

            _client.MessageReceived += HandleMessageAsync;

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            _discordState = "running";
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            _discordState = "crashed";
            _lastError = ex.Message;
            Console.WriteLine("🔥 Discord bot crashed:");
            Console.WriteLine(ex);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
    }

    private static string NormalizeCode(string s)
    {
        s = (s ?? "").Trim().ToUpperInvariant();
        var chars = s.Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    private static bool LooksLikeLinkCode(string code)
    {
        return code.Length == 6 && code.All(char.IsLetterOrDigit);
    }

    private static async Task HandleMessageAsync(SocketMessage msg)
    {
        try
        {
            if (msg.Author.IsBot) return;

            var content = (msg.Content ?? "").Trim();
            if (!content.StartsWith("!link", StringComparison.OrdinalIgnoreCase)) return;

            if (msg.Channel is IDMChannel)
            {
                await msg.Channel.SendMessageAsync("❌ Linking must be run inside your Discord server (not DMs).");
                return;
            }

            if (msg.Channel is not SocketGuildChannel gch)
            {
                await msg.Channel.SendMessageAsync("❌ Could not resolve server context for linking.");
                return;
            }

            var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                await msg.Channel.SendMessageAsync("Usage: `!link CODE`");
                return;
            }

            var code = NormalizeCode(parts[1]);
            if (!LooksLikeLinkCode(code))
            {
                await msg.Channel.SendMessageAsync("❌ Invalid code. Example: `!link WEK6N5`");
                return;
            }

            if (string.IsNullOrWhiteSpace(_hubBaseUrl))
            {
                await msg.Channel.SendMessageAsync("❌ Hub not configured. Set `HUB_BASE_URL` in Railway variables.");
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

            var resp = await http.PostAsJsonAsync(url, payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                await msg.Channel.SendMessageAsync($"❌ Hub confirm failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n```{body}```");
                return;
            }

            await msg.Channel.SendMessageAsync($"✅ Linked code `{code}` to this server.\nNow return to the ELD and finish linking (Claim).");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Console.WriteLine("🔥 Message handler error:");
            Console.WriteLine(ex);
            try { await msg.Channel.SendMessageAsync("❌ Error while linking. Check bot logs."); } catch { }
        }
    }

 

