using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Discord.Rest;
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

        r.MapPost("/loads/bol/upload", async (HttpRequest req) =>
        {
            if (services.Client == null)
                return Results.Json(new { ok = false, error = "ClientNull" }, statusCode: 503);

            if (!req.HasFormContentType)
                return Results.Json(new { ok = false, error = "ExpectedMultipartFormData" }, statusCode: 400);

            var form = await req.ReadFormAsync();
            var loadNumber = (form["loadNumber"].ToString() ?? "").Trim();
            var status = (form["status"].ToString() ?? "").Trim();
            var file = form.Files["file"];

            if (string.IsNullOrWhiteSpace(loadNumber))
                return Results.Json(new { ok = false, error = "MissingLoadNumber" }, statusCode: 400);

            if (file == null || file.Length == 0)
                return Results.Json(new { ok = false, error = "MissingFile" }, statusCode: 400);

            var map = threadStore.GetByLoadNumber(loadNumber);
            if (map == null)
                return Results.Json(new { ok = false, error = "LoadThreadNotFound" }, statusCode: 404);

            var displayName = string.IsNullOrWhiteSpace(status)
                ? $"BOL - {loadNumber}.pdf"
                : $"BOL - {loadNumber} - {status}.pdf";

            if (ulong.TryParse(map.ThreadId, out var threadId) && threadId != 0)
            {
                try
                {
                    var channel = await services.Client.Rest.GetChannelAsync(threadId);
                    if (channel is RestThreadChannel thread)
                    {
                        await using var stream = file.OpenReadStream();
                        await thread.SendFileAsync(stream, displayName, $"📄 BOL attached for load `{loadNumber}`.");
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
                    return Results.Json(new
                    {
                        ok = false,
                        error = "ThreadUploadFailed",
                        detail = ex.Message
                    }, statusCode: 500);
                }
            }

            if (ulong.TryParse(map.GuildId, out var guildId) &&
                ulong.TryParse(map.ChannelId, out var channelId))
            {
                try
                {
                    var guild = services.Client.GetGuild(guildId);
                    var channel = guild?.GetTextChannel(channelId);
                    if (channel == null)
                        return Results.Json(new { ok = false, error = "FallbackChannelNotFound" }, statusCode: 404);

                    await using var stream = file.OpenReadStream();
                    await channel.SendFileAsync(stream, displayName, $"📄 BOL attached for load `{loadNumber}`.");
                    return Results.Json(new
                    {
                        ok = true,
                        uploaded = true,
                        target = "channel",
                        loadNumber,
                        channelId = map.ChannelId
                    }, jsonWrite);
                }
                catch (Exception ex)
                {
                    return Results.Json(new
                    {
                        ok = false,
                        error = "ChannelUploadFailed",
                        detail = ex.Message
                    }, statusCode: 500);
                }
            }

            return Results.Json(new { ok = false, error = "NoUsableThreadOrChannel" }, statusCode: 404);
        });
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
