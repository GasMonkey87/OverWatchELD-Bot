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
            var traceId = Guid.NewGuid().ToString("N")[..8];

            try
            {
                Console.WriteLine($"=== BOL UPLOAD ENTERED trace={traceId} ===");
                Console.WriteLine($"ContentType={req.ContentType}");
                Console.WriteLine($"ContentLength={req.ContentLength}");
                Console.WriteLine($"HasFormContentType={req.HasFormContentType}");

                if (services.Client == null)
                    return JsonError("ClientNull", "Discord client is null.", traceId, 503);

                if (!services.DiscordReady)
                    Console.WriteLine($"[BOL {traceId}] DiscordReady=false; will still try using cached guilds.");

                if (!req.HasFormContentType)
                    return JsonError("ExpectedMultipartFormData", "POST must be multipart/form-data.", traceId, 400);

                IFormCollection form;
                try
                {
                    form = await req.ReadFormAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BOL {traceId}] ReadFormAsync failed: {ex}");
                    return JsonError("ReadFormFailed", ex.Message, traceId, 400, ex.GetType().FullName);
                }

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

                Console.WriteLine($"[BOL {traceId}] guildId={guildIdFromForm}; loadNumber={loadNumber}; status={status}; files={form.Files.Count}");

                if (string.IsNullOrWhiteSpace(loadNumber))
                    return JsonError("MissingLoadNumber", "Multipart form is missing loadNumber/currentLoadNumber/bolNumber.", traceId, 400);

                if (file == null || file.Length == 0)
                    return JsonError("MissingFile", "Multipart form is missing file/bol/bolFile PDF upload.", traceId, 400);

                Console.WriteLine($"[BOL {traceId}] fileName={file.FileName}; fileLength={file.Length}; fileContentType={file.ContentType}");

                byte[] fileBytes;
                try
                {
                    await using var input = file.OpenReadStream();
                    using var ms = new MemoryStream();
                    await input.CopyToAsync(ms);
                    fileBytes = ms.ToArray();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BOL {traceId}] Copy uploaded file failed: {ex}");
                    return JsonError("CopyFileFailed", ex.Message, traceId, 500, ex.GetType().FullName);
                }

                if (fileBytes.Length == 0)
                    return JsonError("EmptyCopiedFile", "Uploaded file copied to 0 bytes.", traceId, 400);

                var ext = Path.GetExtension(file.FileName);
                if (string.IsNullOrWhiteSpace(ext))
                    ext = ".pdf";

                var safeStatus = string.IsNullOrWhiteSpace(status) ? "" : $" - {status}";
                var displayName = $"BOL - {SanitizeFileName(loadNumber)}{SanitizeFileName(safeStatus)}{ext}";
                var message = $"📄 BOL PDF attached for load `{loadNumber}`.";

                var map = threadStore.GetByLoadNumber(loadNumber);
                Console.WriteLine($"[BOL {traceId}] mapFound={map != null}");

                if (map != null && ulong.TryParse(map.ThreadId, out var threadId) && threadId != 0)
                {
                    try
                    {
                        Console.WriteLine($"[BOL {traceId}] Trying threadId={map.ThreadId}");
                        var channel = await services.Client.Rest.GetChannelAsync(threadId);

                        if (channel is RestThreadChannel thread)
                        {
                            await using var stream = new MemoryStream(fileBytes);
                            await thread.SendFileAsync(stream, displayName, message);

                            return Results.Json(new
                            {
                                ok = true,
                                uploaded = true,
                                target = "thread",
                                traceId,
                                loadNumber,
                                threadId = map.ThreadId
                            }, jsonWrite);
                        }

                        Console.WriteLine($"[BOL {traceId}] Channel for threadId was not RestThreadChannel: {channel?.GetType().FullName ?? "null"}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BOL {traceId}] BOL THREAD UPLOAD ERROR: {ex}");
                    }
                }

                if (map != null && ulong.TryParse(map.GuildId, out var mappedGuildId) &&
                    ulong.TryParse(map.ChannelId, out var mappedChannelId))
                {
                    try
                    {
                        Console.WriteLine($"[BOL {traceId}] Trying mapped guild={mappedGuildId}, channel={mappedChannelId}");
                        var guild = services.Client.GetGuild(mappedGuildId);
                        var channel = guild?.GetTextChannel(mappedChannelId);

                        if (channel != null)
                        {
                            await using var stream = new MemoryStream(fileBytes);
                            await channel.SendFileAsync(stream, displayName, message);

                            return Results.Json(new
                            {
                                ok = true,
                                uploaded = true,
                                target = "mapped-channel",
                                traceId,
                                loadNumber,
                                channelId = map.ChannelId
                            }, jsonWrite);
                        }

                        Console.WriteLine($"[BOL {traceId}] Mapped channel not found in cached guild.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BOL {traceId}] BOL MAPPED CHANNEL UPLOAD ERROR: {ex}");
                    }
                }

                var fallbackChannel = await ResolveBolFallbackChannelAsync(services, guildIdFromForm, traceId);
                if (fallbackChannel != null)
                {
                    try
                    {
                        Console.WriteLine($"[BOL {traceId}] Trying fallback channel #{fallbackChannel.Name} ({fallbackChannel.Id})");

                        var embed = new EmbedBuilder()
                            .WithTitle("Bill of Lading PDF")
                            .WithColor(Color.Blue)
                            .AddField("Load Number", loadNumber, true)
                            .AddField("Status", string.IsNullOrWhiteSpace(status) ? "BOL PDF Export" : status, true)
                            .WithFooter("OverWatch ELD • BOL Export")
                            .WithCurrentTimestamp()
                            .Build();

                        await using var stream = new MemoryStream(fileBytes);
                        await fallbackChannel.SendFileAsync(stream, displayName, text: message, embed: embed);

                        return Results.Json(new
                        {
                            ok = true,
                            uploaded = true,
                            target = "bol-channel",
                            traceId,
                            loadNumber,
                            channelId = fallbackChannel.Id.ToString(),
                            channelName = fallbackChannel.Name
                        }, jsonWrite);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BOL {traceId}] BOL FALLBACK CHANNEL SEND ERROR: {ex}");
                        return JsonError("BolDiscordSendFailed", ex.Message, traceId, 500, ex.GetType().FullName);
                    }
                }

                return Results.Json(new
                {
                    ok = false,
                    error = "NoBolThreadOrChannel",
                    traceId,
                    guildId = guildIdFromForm,
                    loadNumber,
                    hint = "No load thread was found and no BOL channel is configured. Create a channel named bol, bills-of-lading, documents, or paperwork, or configure the BOL channel ID."
                }, statusCode: 404);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"=== BOL UPLOAD CRASH trace={traceId} ===");
                Console.WriteLine(ex.ToString());

                return JsonError("BolUploadFailed", ex.Message, traceId, 500, ex.GetType().FullName);
            }
        }

        r.MapPost("/loads/bol/upload", HandleBolUpload);

        r.MapGet("/loads/bol/upload/version", () => Results.Json(new
        {
            ok = true,
            route = "BolUploadRoutes",
            version = "bol-upload-full-fix-2026-06-14",
            utc = DateTimeOffset.UtcNow
        }, jsonWrite));
    }

    private static IResult JsonError(string error, string message, string traceId, int statusCode, string? type = null)
    {
        return Results.Json(new
        {
            ok = false,
            error,
            message,
            traceId,
            type
        }, statusCode: statusCode);
    }

    private static async Task<SocketTextChannel?> ResolveBolFallbackChannelAsync(BotServices services, string guildIdText, string traceId)
    {
        if (services.Client == null)
            return null;

        SocketGuild? guild = null;

        if (ulong.TryParse(guildIdText, out var guildId))
            guild = services.Client.GetGuild(guildId);

        guild ??= services.Client.Guilds.FirstOrDefault();
        if (guild == null)
        {
            Console.WriteLine($"[BOL {traceId}] No guild found. client guild count={services.Client.Guilds.Count}");
            return null;
        }

        Console.WriteLine($"[BOL {traceId}] Resolving fallback in guild={guild.Name} ({guild.Id})");

        try
        {
            var settings = services.GuildSettingsStore != null
                ? await services.GuildSettingsStore.GetAsync(guild.Id.ToString())
                : null;

            var channelId = ResolveBolChannelId(settings);
            Console.WriteLine($"[BOL {traceId}] Configured BOL channel ID={channelId}");

            if (ulong.TryParse(channelId, out var configuredId))
            {
                var configured = guild.GetTextChannel(configuredId);
                if (configured != null)
                    return configured;

                Console.WriteLine($"[BOL {traceId}] Configured channel ID was not found in guild cache.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BOL {traceId}] BOL UPLOAD SETTINGS LOOKUP ERROR: {ex}");
        }

        var found = guild.TextChannels.FirstOrDefault(c =>
        {
            var name = Normalize(c.Name);
            return name == "bol" ||
                   name.Contains("bill-of-lading") ||
                   name.Contains("bills-of-lading") ||
                   name.Contains("lading") ||
                   name.Contains("documents") ||
                   name.Contains("paperwork");
        });

        Console.WriteLine(found == null
            ? $"[BOL {traceId}] No fallback channel by name found."
            : $"[BOL {traceId}] Fallback channel by name found: #{found.Name} ({found.Id})");

        return found;
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

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '-');

        return value.Trim();
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
            catch (Exception ex)
            {
                Console.WriteLine("[BOL LOAD THREAD MAP READ ERROR] " + ex);
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
