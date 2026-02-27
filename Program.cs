using System.Net.Http.Json;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// --- Forwarded headers (Railway/proxy safe) ---
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// --- Config from environment ---
static string? Env(params string[] keys)
{
    foreach (var k in keys)
    {
        var v = Environment.GetEnvironmentVariable(k);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
    }
    return null;
}

var discordToken = Env("DISCORD_TOKEN", "BOT_TOKEN");
var hubBaseUrl = Env("HUB_BASE_URL")?.TrimEnd('/');

// Railway provides PORT, but we’ll default safely
var portStr = Env("PORT");
var port = 8080;
if (!string.IsNullOrWhiteSpace(portStr) && int.TryParse(portStr, out var p) && p > 0) port = p;

if (string.IsNullOrWhiteSpace(hubBaseUrl))
{
    Console.WriteLine("❌ HUB_BASE_URL is missing. Set HUB_BASE_URL=https://<your-hub-domain>");
}

if (string.IsNullOrWhiteSpace(discordToken))
{
    Console.WriteLine("❌ DISCORD_TOKEN (or BOT_TOKEN) is missing. Bot will NOT connect to Discord.");
}

// --- Discord client ---
var discordConfig = new DiscordSocketConfig
{
    // You MUST enable Message Content Intent in the Discord Developer Portal for this bot
    GatewayIntents =
        GatewayIntents.Guilds |
        GatewayIntents.GuildMessages |
        GatewayIntents.MessageContent
};

var client = new DiscordSocketClient(discordConfig);

// --- Http client for calling Hub ---
var http = new HttpClient(new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All
})
{
    Timeout = TimeSpan.FromSeconds(10)
};

// Basic JSON options (just in case)
var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

// --- Track basic status ---
var startedUtc = DateTimeOffset.UtcNow;
string lastDiscordState = "starting";
string lastDiscordUser = "";
string? lastError = null;

// --- Discord event wiring ---
client.Log += msg =>
{
    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {msg.Severity} {msg.Source}: {msg.Message}");
    if (msg.Exception != null) Console.WriteLine(msg.Exception);
    return Task.CompletedTask;
};

client.Ready += () =>
{
    lastDiscordState = "ready";
    lastDiscordUser = client.CurrentUser?.ToString() ?? "";
    Console.WriteLine($"✅ Discord READY as {lastDiscordUser}. Guilds={client.Guilds.Count}");
    return Task.CompletedTask;
};

client.Disconnected += ex =>
{
    lastDiscordState = "disconnected";
    lastError = ex?.Message;
    Console.WriteLine($"⚠️ Discord disconnected: {ex?.Message ?? "(no exception)"}");
    return Task.CompletedTask;
};

static string NormalizeCode(string s)
{
    s = (s ?? "").Trim().ToUpperInvariant();
    // Keep alnum only
    var chars = s.Where(char.IsLetterOrDigit).ToArray();
    return new string(chars);
}

static bool LooksLikeLinkCode(string code)
{
    // You said your code is 6 chars (WEK6N5). Keep it strict.
    if (code.Length != 6) return false;
    return code.All(char.IsLetterOrDigit);
}

// This matches your Hub ConfirmLinkReq:
// sealed class ConfirmLinkReq { public string Code; public string GuildId; public string? GuildName; public string? LinkedByUserId; }
record ConfirmLinkReq(string Code, string GuildId, string? GuildName, string? LinkedByUserId);

client.MessageReceived += async (msg) =>
{
    try
    {
        // Ignore system/bot messages
        if (msg.Author.IsBot) return;

        // Only handle "!link CODE"
        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!link", StringComparison.OrdinalIgnoreCase)) return;

        // Block DMs (SaaS rule)
        if (msg.Channel is IDMChannel)
        {
            await msg.Channel.SendMessageAsync("❌ Linking must be run inside your Discord server (not DMs).");
            return;
        }

        // We need a guild context
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

        if (string.IsNullOrWhiteSpace(hubBaseUrl))
        {
            await msg.Channel.SendMessageAsync("❌ HUB is not configured. Set `HUB_BASE_URL` in Railway variables.");
            return;
        }

        var guildId = gch.Guild.Id.ToString();
        var guildName = gch.Guild.Name;
        var userId = msg.Author.Id.ToString();

        var url = $"{hubBaseUrl}/api/link/confirm";

        var payload = new ConfirmLinkReq(code, guildId, guildName, userId);

        var resp = await http.PostAsJsonAsync(url, payload, jsonOpts);
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
        lastError = ex.Message;
        try { await msg.Channel.SendMessageAsync("❌ Error while linking. Check bot logs."); } catch { }
        Console.WriteLine("🔥 MessageReceived handler exception:");
        Console.WriteLine(ex);
    }
};

// --- Minimal Web API for health (so Railway has something to hit) ---
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();
app.UseForwardedHeaders();

app.MapGet("/", () => Results.Ok(new
{
    ok = true,
    service = "OverWatchELD.VtcBot",
    hint = "Use /health"
}));

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    service = "OverWatchELD.VtcBot",
    startedUtc,
    discord = new
    {
        state = lastDiscordState,
        user = lastDiscordUser,
        guilds = client.Guilds.Count
    },
    hub = new
    {
        baseUrl = hubBaseUrl ?? "",
        configured = !string.IsNullOrWhiteSpace(hubBaseUrl)
    },
    lastError = lastError ?? ""
}));

// --- Start Discord client in background ---
_ = Task.Run(async () =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(discordToken))
        {
            Console.WriteLine("⚠️ No DISCORD_TOKEN provided; skipping Discord login.");
            lastDiscordState = "no-token";
            return;
        }

        await client.LoginAsync(TokenType.Bot, discordToken);
        await client.StartAsync();
        lastDiscordState = "running";

        // Keep alive forever
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    catch (Exception ex)
    {
        lastDiscordState = "crashed";
        lastError = ex.Message;
        Console.WriteLine("🔥 Discord background runner crashed:");
        Console.WriteLine(ex);
    }
});

// --- Run web host (keeps container alive) ---
await app.RunAsync();
