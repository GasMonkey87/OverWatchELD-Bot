using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class BolUploadRoutes
{
    public static void Register(IEndpointRouteBuilder r, BotServices services, JsonSerializerOptions jsonWrite)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

        var readOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var threadStore = new LoadThreadMapStore(
            Path.Combine(dataDir, "load_threads.json"),
            readOpts,
            jsonWrite);

        async Task<IResult> HandleBolUpload(HttpRequest req)
        {
            try
            {
                if (services.Client == null)
                    return Results.Json(new { ok = false, error = "ClientNull" }, statusCode: 503);

                if (!req.HasFormContentType)
                    return Results.Json(new { ok = false, error = "ExpectedMultipartFormData" }, statusCode: 400);

                var form = await req.ReadFormAsync();

                var guildIdFromForm = FirstNonBlank(
                    form["guildId"].ToString(),
                    form["GuildId"].ToString(),
                    form["guild_id"].ToString()).Trim();

                var loadNumber = FirstNonBlank(
                    form["loadNumber"].ToString(),
                    form["currentLoadNumber"].ToString(),
                    form["bolNumber"].ToString()).Trim();

                var status = FirstNonBlank(
                    form["status"].ToString(),
                    form["bolStatus"].ToString()).Trim();

                var file = form.Files["file"] ??
                           form.Files["bol"] ??
                           form.Files["bolFile"] ??
                           form.Files.FirstOrDefault(f =>
                               string.Equals(Path.GetExtension(f.FileName), ".pdf", StringComparison.OrdinalIgnoreCase)) ??
                           form.Files.FirstOrDefault();

                if (string.IsNullOrWhiteSpace(loadNumber))
                    return Results.Json(new { ok = false, error = "MissingLoadNumber" }, statusCode: 400);

                if (file == null || file.Length == 0)
                    return Results.Json(new { ok = false, error = "MissingFile" }, statusCode: 400);

                var ext = Path.GetExtension(file.FileName);
                if (string.IsNullOrWhiteSpace(ext))
                    ext = ".pdf";

                var safeStatus = string.IsNullOrWhiteSpace(status) ? "" : $" - {status}";
                var displayName = $"BOL - {loadNumber}{safeStatus}{ext}";
                var message = $"📄 BOL PDF attached for load `{loadNumber}`.";

                var map = threadStore.GetByLoadNumber(loadNumber);

                if (map != null && ulong.TryParse(map.ThreadId, out var threadId) && threadId != 0)
                {
                    try
                    {
                        var channel = await services.Client.Rest.GetChannelAsync(threadId);

                        if (channel is RestThreadChannel thread)
                        {
                            await using var stream = file.OpenReadStream();

                            await thread.SendFileAsync(stream, displayName, message);

                            return Results.Json(new
                            {
                                ok = true,
                                uploaded = true,
                                target = "thread",
                                loadNumber,
                                threadId = map.ThreadId
                            }, jsonWrite);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[BOL THREAD UPLOAD ERROR] " + ex);
                    }
                }

                if (map != null && ulong.TryParse(map.GuildId, out var mappedGuildId) &&
                    ulong.TryParse(map.ChannelId, out var mappedChannelId))
                {
                    try
                    {
                        var guild = services.Client.GetGuild(mappedGuildId);
                        var channel = guild?.GetTextChannel(mappedChannelId);

                        if (channel != null)
                        {
                            await using var stream = file.OpenReadStream();

                            await channel.SendFileAsync(stream, displayName, message);

                            return Results.Json(new
                            {
                                ok = true,
                                uploaded = true,
                                target = "mapped-channel",
                                loadNumber,
                                channelId = map.ChannelId
                            }, jsonWrite);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[BOL MAPPED CHANNEL UPLOAD ERROR] " + ex);
                    }
                }

                var fallbackChannel = await ResolveBolFallbackChannelAsync(services, guildIdFromForm);
                if (fallbackChannel != null)
                {
                    await using var stream = file.OpenReadStream();
                    var embed = new EmbedBuilder()
                        .WithTitle("Bill of Lading PDF")
                        .WithColor(Color.Blue)
                        .AddField("Load Number", loadNumber, true)
                        .AddField("Status", string.IsNullOrWhiteSpace(status) ? "BOL PDF Export" : status, true)
                        .WithFooter("OverWatch ELD • BOL Export")
                        .WithCurrentTimestamp()
                        .Build();

                    await fallbackChannel.SendFileAsync(stream, displayName, text: message, embed: embed);

                    return Results.Json(new
                    {
                        ok = true,
                        uploaded = true,
                        target = "bol-channel",
                        loadNumber,
                        channelId = fallbackChannel.Id.ToString(),
                        channelName = fallbackChannel.Name
                    }, jsonWrite);
                }

                return Results.Json(new
                {
                    ok = false,
                    error = "NoBolThreadOrChannel",
                    hint = "No load thread was found and no BOL channel is configured. Set the BOL channel or create a channel named bol, bills-of-lading, or documents."
                }, statusCode: 404);
            }
            catch (Exception ex)
{
    Console.WriteLine("=== BOL UPLOAD CRASH ===");
    Console.WriteLine(ex.ToString());

    return Results.Json(new
    {
        ok = false,
        error = "BolUploadFailed",
        message = ex.Message,
        type = ex.GetType().FullName,
        stack = ex.ToString()
    }, statusCode: 500);
}
        }

        r.MapPost("/loads/bol/upload", HandleBolUpload);

        // Do not map /loads/bol/post here. BolDiscordOnlyRoutes already owns the JSON BOL post route.
        // Keeping this upload endpoint separate avoids ASP.NET ambiguous endpoint matches.
    }

    private static async Task<SocketTextChannel?> ResolveBolFallbackChannelAsync(BotServices services, string guildIdText)
    {
        if (services.Client == null)
            return null;

        SocketGuild? guild = null;

        if (ulong.TryParse(guildIdText, out var guildId))
            guild = services.Client.GetGuild(guildId);

        guild ??= services.Client.Guilds.FirstOrDefault();
        if (guild == null)
            return null;

        try
        {
            var settings = services.GuildSettingsStore != null
                ? await services.GuildSettingsStore.GetAsync(guild.Id.ToString())
                : null;

            var channelId = ResolveBolChannelId(settings);
            if (ulong.TryParse(channelId, out var configuredId))
            {
                var configured = guild.GetTextChannel(configuredId);
                if (configured != null)
                    return configured;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[BOL UPLOAD SETTINGS LOOKUP ERROR] " + ex.Message);
        }

        return guild.TextChannels.FirstOrDefault(c =>
        {
            var name = Normalize(c.Name);
            return name == "bol" ||
                   name.Contains("bill-of-lading") ||
                   name.Contains("bills-of-lading") ||
                   name.Contains("lading") ||
                   name.Contains("documents") ||
                   name.Contains("paperwork");
        });
    }

    private static string ResolveBolChannelId(object? settings)
    {
        if (settings == null)
            return "";

        var nested = ReadNestedObjString(settings, "Bols", "ChannelId");
        if (!string.IsNullOrWhiteSpace(nested))
            return nested;

        nested = ReadNestedObjString(settings, "BOLs", "ChannelId");
        if (!string.IsNullOrWhiteSpace(nested))
            return nested;

        return FirstNonBlank(
            ReadObjString(settings, "BolsChannelId"),
            ReadObjString(settings, "BolChannelId"),
            ReadObjString(settings, "BOLChannelId"),
            ReadObjString(settings, "BolsWebhookChannelId"),
            ReadObjString(settings, "BOLWebhookChannelId"),
            ReadObjString(settings, "BolsChannel"),
            ReadObjString(settings, "BolChannel")
        );
    }

    private static string ReadNestedObjString(object? obj, string propertyName, string childName)
    {
        var nested = ReadObj(obj, propertyName);
        return ReadObjString(nested, childName);
    }

    private static object? ReadObj(object? obj, string propertyName)
    {
        try
        {
            return obj?.GetType().GetProperty(propertyName)?.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadObjString(object? obj, string propertyName)
    {
        try
        {
            return obj?.GetType().GetProperty(propertyName)?.GetValue(obj)?.ToString()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string Normalize(string? value)
    {
        return (value ?? "")
            .Trim()
            .ToLowerInvariant()
            .Replace("_", "-")
            .Replace(" ", "-");
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private sealed class LoadThreadMapStore
    {
        private readonly string _path;
        private readonly JsonSerializerOptions _readOpts;
        private readonly JsonSerializerOptions _writeOpts;

        public LoadThreadMapStore(string path, JsonSerializerOptions readOpts, JsonSerializerOptions writeOpts)
        {
            _path = path;
            _readOpts = readOpts;
            _writeOpts = writeOpts;
        }

        public LoadThreadEntry? GetByLoadNumber(string loadNumber)
        {
            var all = LoadAll();

            return all.FirstOrDefault(x =>
                string.Equals(x.LoadNumber, loadNumber, StringComparison.OrdinalIgnoreCase));
        }

        private List<LoadThreadEntry> LoadAll()
        {
            try
            {
                if (!File.Exists(_path))
                    return new List<LoadThreadEntry>();

                var raw = File.ReadAllText(_path);

                if (string.IsNullOrWhiteSpace(raw))
                    return new List<LoadThreadEntry>();

                return JsonSerializer.Deserialize<List<LoadThreadEntry>>(raw, _readOpts)
                       ?? new List<LoadThreadEntry>();
            }
            catch
            {
                return new List<LoadThreadEntry>();
            }
        }

        public void SaveAll(List<LoadThreadEntry> items)
        {
            var dir = Path.GetDirectoryName(_path);

            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_path, JsonSerializer.Serialize(items, _writeOpts));
        }
    }

    private sealed class LoadThreadEntry
    {
        public string LoadNumber { get; set; } = "";
        public string ThreadId { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string GuildId { get; set; } = "";
        public bool Archived { get; set; }
    }
}
