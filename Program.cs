// Program.cs  ✅ FULL COPY/REPLACE
// OverWatchELD.VtcBot
//
// Fixes: Prefix commands not responding + compile error on HasCharPrefix (older Discord.Net)
// Adds: Hard "it WILL reply" handlers for !ping and !link CODE
//
// REQUIREMENT (Discord Developer Portal):
// Bot -> Privileged Gateway Intents -> ENABLE "MESSAGE CONTENT INTENT"
// Then restart Railway service.

using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

internal static class Program
{
    private static DiscordSocketClient? _client;

    public static async Task Main()
    {
        // Railway/env tokens: prefer DISCORD_TOKEN, fallback BOT_TOKEN
        var token =
            Environment.GetEnvironmentVariable("DISCORD_TOKEN") ??
            Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN (or BOT_TOKEN) environment variable.");
            return;
        }

        // ✅ Intents: This is REQUIRED for prefix commands like !link
        // Also enable Message Content Intent in Developer Portal.
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

        _client.Log += OnLogAsync;
        _client.Ready += OnReadyAsync;
        _client.Connected += () =>
        {
            Console.WriteLine("✅ Connected to Discord gateway.");
            return Task.CompletedTask;
        };
        _client.Disconnected += (ex) =>
        {
            Console.WriteLine("⚠️ Disconnected from Discord gateway.");
            if (ex != null) Console.WriteLine(ex.ToString());
            return Task.CompletedTask;
        };

        // ✅ Prefix commands handler (no HasCharPrefix dependency)
        _client.MessageReceived += HandleMessageAsync;

        Console.WriteLine("🔐 Logging in...");
        await _client.LoginAsync(TokenType.Bot, token);

        Console.WriteLine("🚀 Starting client...");
        await _client.StartAsync();

        Console.WriteLine("🟢 Bot is running. Waiting forever…");
        await Task.Delay(-1);
    }

    private static Task OnReadyAsync()
    {
        if (_client?.CurrentUser != null)
            Console.WriteLine($"✅ READY as {_client.CurrentUser} (id: {_client.CurrentUser.Id})");
        else
            Console.WriteLine("✅ READY (CurrentUser unknown)");

        Console.WriteLine("👉 Test in Discord: !ping");
        Console.WriteLine("👉 Then test: !link 12345");
        return Task.CompletedTask;
    }

    private static Task OnLogAsync(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }

    private static async Task HandleMessageAsync(SocketMessage raw)
    {
        try
        {
            if (raw is not SocketUserMessage msg) return;
            if (msg.Author.IsBot) return;

            var content = (msg.Content ?? "").Trim();

            // Prefix check that works across Discord.Net versions
            if (!content.StartsWith("!")) return;

            var body = content.Substring(1).Trim(); // everything after "!"
            if (string.IsNullOrWhiteSpace(body)) return;

            Console.WriteLine($"📩 CMD from {msg.Author.Username}#{msg.Author.Discriminator}: {body}");

            // !ping
            if (body.Equals("ping", StringComparison.OrdinalIgnoreCase))
            {
                await msg.Channel.SendMessageAsync("pong ✅");
                return;
            }

            // !link CODE
            if (body.StartsWith("link", StringComparison.OrdinalIgnoreCase))
            {
                var rest = body.Length > 4 ? body.Substring(4).Trim() : "";

                if (string.IsNullOrWhiteSpace(rest))
                {
                    await msg.Channel.SendMessageAsync("Usage: `!link YOURCODE`");
                    return;
                }

                // TODO: Persist the mapping: msg.Author.Id <-> rest (code)
                await msg.Channel.SendMessageAsync($"✅ Link received for **{msg.Author.Username}**: `{rest}`");
                return;
            }

            // Fallback: prove we are receiving commands
            await msg.Channel.SendMessageAsync($"✅ I saw: `{body}`");
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ HandleMessageAsync error:");
            Console.WriteLine(ex.ToString());
        }
    }
}
