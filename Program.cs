using System;
using System.IO;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

internal static class Program
{
    private static DiscordSocketClient? _client;

    public static async Task Main()
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN")
                    ?? Environment.GetEnvironmentVariable("BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN (or BOT_TOKEN) env var.");
            return;
        }

        var cfg = new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Info,
            MessageCacheSize = 50,
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.DirectMessages |
                GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(cfg);

        _client.Log += (m) => { Console.WriteLine(m.ToString()); return Task.CompletedTask; };
        _client.Ready += () =>
        {
            Console.WriteLine($"✅ READY as {_client.CurrentUser} (id: {_client.CurrentUser.Id})");
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        Console.WriteLine("🚀 Bot started. Waiting…");
        await Task.Delay(-1);
    }

    private static async Task HandleMessageAsync(SocketMessage raw)
    {
        // Basic guards
        if (raw is not SocketUserMessage msg) return;
        if (msg.Author.IsBot) return;

        int pos = 0;
        if (!msg.HasCharPrefix('!', ref pos)) return;

        var body = msg.Content.Substring(pos).Trim();
        Console.WriteLine($"📩 CMD from {msg.Author.Username}: {body}");

        // !ping
        if (body.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        // !link CODE
        if (body.StartsWith("link ", StringComparison.OrdinalIgnoreCase))
        {
            var code = body.Substring("link ".Length).Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                await msg.Channel.SendMessageAsync("Usage: `!link YOURCODE`");
                return;
            }

            // TODO: save (msg.Author.Id <-> code) to DB here
            await msg.Channel.SendMessageAsync($"✅ Link received for **{msg.Author.Username}**: `{code}`");
            return;
        }

        // Fallback so you KNOW it is receiving commands
        await msg.Channel.SendMessageAsync($"✅ I saw: `{body}`");
    }
}
