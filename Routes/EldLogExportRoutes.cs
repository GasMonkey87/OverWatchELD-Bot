using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class EldLogExportRoutes
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Register(WebApplication app, BotServices services)
    {
        app.MapPost("/api/logs/export", async (HttpContext ctx) =>
        {
            try
            {
                EldLogExportRequest? req;
                IFormFile? graphFile = null;
                IFormFile? textFile = null;

                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync();
                    var payloadJson = form["payload"].FirstOrDefault()
                                   ?? form["json"].FirstOrDefault()
                                   ?? form["metadata"].FirstOrDefault();

                    if (string.IsNullOrWhiteSpace(payloadJson))
                        return Results.Json(new { ok = false, error = "MissingPayload" }, statusCode: 400);

                    req = JsonSerializer.Deserialize<EldLogExportRequest>(payloadJson, JsonOpts);
                    graphFile = form.Files.FirstOrDefault(f =>
                        string.Equals(f.Name, "graph", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Name, "image", StringComparison.OrdinalIgnoreCase) ||
                        f.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

                    textFile = form.Files.FirstOrDefault(f =>
                        string.Equals(f.Name, "report", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Name, "log", StringComparison.OrdinalIgnoreCase) ||
                        f.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    req = await ctx.Request.ReadFromJsonAsync<EldLogExportRequest>(JsonOpts);
                }

                if (req == null)
                    return Results.Json(new { ok = false, error = "BadPayload" }, statusCode: 400);

                req.GuildId = (req.GuildId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(req.GuildId) || !ulong.TryParse(req.GuildId, out var guildId))
                    return Results.Json(new { ok = false, error = "MissingOrBadGuildId" }, statusCode: 400);

                var client = services.Client;
                var guild = client?.GetGuild(guildId);
                if (guild == null)
                    return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

                var channel = ResolveLogExportChannel(guild, services, req.GuildId);
                if (channel == null)
                    return Results.Json(new { ok = false, error = "LogChannelNotConfigured" }, statusCode: 404);

                var embed = BuildLogExportEmbed(req, graphFile != null);

                if (graphFile != null)
                {
                    await using var graphStream = graphFile.OpenReadStream();
                    var graphAttachment = new FileAttachment(
                        graphStream,
                        string.IsNullOrWhiteSpace(graphFile.FileName) ? "eld-log-graph.png" : graphFile.FileName,
                        description: "OverWatch ELD duty status graph");

                    if (textFile != null)
                    {
                        await using var textStream = textFile.OpenReadStream();
                        var textAttachment = new FileAttachment(
                            textStream,
                            string.IsNullOrWhiteSpace(textFile.FileName) ? "eld-log-export.txt" : textFile.FileName,
                            description: "OverWatch ELD text log export");

                        await channel.SendFilesAsync(
                            new[] { graphAttachment, textAttachment },
                            text: "",
                            embed: embed.Build());
                    }
                    else
                    {
                        await channel.SendFileAsync(graphAttachment, text: "", embed: embed.Build());
                    }
                }
                else
                {
                    await channel.SendMessageAsync(embed: embed.Build());
                }

                SaveRecentExport(ctx, req, graphFile != null, channel.Id.ToString());

                return Results.Json(new
                {
                    ok = true,
                    channelId = channel.Id.ToString(),
                    channelName = channel.Name,
                    graphAttached = graphFile != null
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ELD LOG EXPORT ERROR] " + ex);
                return Results.Json(new { ok = false, error = "Exception", message = ex.Message }, statusCode: 500);
            }
        });

        app.MapGet("/api/logs/exports/recent", (string? guildId) =>
        {
            try
            {
                var dataPath = Path.Combine(AppContext.BaseDirectory, "data", "log_exports.json");
                if (!File.Exists(dataPath))
                    return Results.Json(new { ok = true, exports = Array.Empty<EldLogExportHistoryRow>() });

                var rows = JsonSerializer.Deserialize<List<EldLogExportHistoryRow>>(File.ReadAllText(dataPath), JsonOpts)
                           ?? new List<EldLogExportHistoryRow>();

                if (!string.IsNullOrWhiteSpace(guildId))
                    rows = rows.Where(x => string.Equals(x.GuildId, guildId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

                return Results.Json(new { ok = true, exports = rows.OrderByDescending(x => x.CreatedUtc).Take(50) }, JsonOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
            }
        });
    }

    private static SocketTextChannel? ResolveLogExportChannel(SocketGuild guild, BotServices services, string guildId)
    {
        try
        {
            var settings = services.DispatchStore?.Get(guildId);
            var candidates = new[]
            {
                GetString(settings, "LogsChannelId"),
                GetString(settings, "LogChannelId"),
                GetString(settings, "DotLogsChannelId"),
                GetString(settings, "InspectionChannelId"),
                GetString(settings, "AnnouncementsChannelId"),
                GetString(settings, "DispatchChannelId")
            };

            foreach (var idText in candidates)
            {
                if (ulong.TryParse(idText, out var id))
                {
                    var byId = guild.GetTextChannel(id);
                    if (byId != null) return byId;
                }
            }
        }
        catch { }

        var names = new[]
        {
            "eld-logs", "dot-logs", "driver-logs", "log-exports", "logs", "compliance", "dot-inspections", "dispatch"
        };

        foreach (var wanted in names)
        {
            var match = guild.TextChannels.FirstOrDefault(c =>
                string.Equals(Normalize(c.Name), Normalize(wanted), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return guild.TextChannels.FirstOrDefault(c =>
        {
            var name = Normalize(c.Name);
            return name.Contains("log") || name.Contains("compliance") || name.Contains("dispatch");
        });
    }

    private static EmbedBuilder BuildLogExportEmbed(EldLogExportRequest req, bool hasGraph)
    {
        var certified = string.Equals(req.Certified, "YES", StringComparison.OrdinalIgnoreCase) || req.IsCertified == true;
        var title = certified ? "DOT Log Export — Certified" : "DOT Log Export";
        var color = certified ? Color.Green : new Color(255, 193, 7);

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithDescription("OverWatch ELD driver log export received from the desktop ELD.")
            .AddField("Driver", First(req.DriverName, req.DiscordUsername, "Unknown Driver"), true)
            .AddField("Date Range", First(req.DateRange, req.Date, "N/A"), true)
            .AddField("Certified", certified ? "YES" : First(req.Certified, "NO"), true)
            .AddField("Truck", First(req.Truck, req.TruckName, "Unknown Truck"), true)
            .AddField("Unit #", First(req.UnitNumber, req.TruckNumber, "N/A"), true)
            .AddField("Violations", First(req.Violations, "None"), true)
            .AddField("HOS Remaining", First(req.HosRemaining, "N/A"), true)
            .AddField("Discord ID", First(req.DiscordUserId, "Not linked"), true)
            .AddField("TruckersMP ID", First(req.TruckersMpId, "Not linked"), true)
            .WithFooter("OverWatch ELD • FMCSA/DOT Export")
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(req.Summary))
            embed.AddField("Summary", TrimForDiscord(req.Summary!, 900), false);

        if (!string.IsNullOrWhiteSpace(req.PermanentDriverKey))
            embed.AddField("Permanent Driver Key", TrimForDiscord(req.PermanentDriverKey!, 250), false);

        if (hasGraph)
            embed.WithImageUrl("attachment://eld-log-graph.png");

        return embed;
    }

    private static void SaveRecentExport(HttpContext ctx, EldLogExportRequest req, bool hasGraph, string channelId)
    {
        try
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);
            var path = Path.Combine(dataDir, "log_exports.json");

            var rows = File.Exists(path)
                ? JsonSerializer.Deserialize<List<EldLogExportHistoryRow>>(File.ReadAllText(path), JsonOpts) ?? new List<EldLogExportHistoryRow>()
                : new List<EldLogExportHistoryRow>();

            rows.Add(new EldLogExportHistoryRow
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                GuildId = req.GuildId ?? "",
                DriverName = First(req.DriverName, req.DiscordUsername, "Unknown Driver"),
                DiscordUserId = req.DiscordUserId ?? "",
                DateRange = First(req.DateRange, req.Date, "N/A"),
                Certified = First(req.Certified, req.IsCertified == true ? "YES" : "NO"),
                Truck = First(req.Truck, req.TruckName, "Unknown Truck"),
                UnitNumber = First(req.UnitNumber, req.TruckNumber, "N/A"),
                Violations = First(req.Violations, "None"),
                HasGraph = hasGraph,
                ChannelId = channelId
            });

            rows = rows.OrderByDescending(x => x.CreatedUtc).Take(200).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(rows, JsonOpts));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ELD LOG EXPORT HISTORY ERROR] " + ex.Message);
        }
    }

    private static string GetString(object? obj, string name)
    {
        try
        {
            return obj?.GetType().GetProperty(name)?.GetValue(obj)?.ToString()?.Trim() ?? "";
        }
        catch { return ""; }
    }

    private static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");

    private static string First(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return "";
    }

    private static string TrimForDiscord(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "N/A";
        value = value.Trim();
        return value.Length <= max ? value : value[..Math.Max(0, max - 3)] + "...";
    }

    private sealed class EldLogExportRequest
    {
        public string? GuildId { get; set; }
        public string? DriverName { get; set; }
        public string? DiscordUserId { get; set; }
        public string? DiscordUsername { get; set; }
        public string? TruckersMpId { get; set; }
        public string? IdentityHash { get; set; }
        public string? PermanentDriverKey { get; set; }
        public string? Truck { get; set; }
        public string? TruckName { get; set; }
        public string? UnitNumber { get; set; }
        public string? TruckNumber { get; set; }
        public string? DateRange { get; set; }
        public string? Date { get; set; }
        public string? Certified { get; set; }
        public bool? IsCertified { get; set; }
        public string? Violations { get; set; }
        public string? HosRemaining { get; set; }
        public string? Summary { get; set; }
        public string? ReportText { get; set; }
    }

    private sealed class EldLogExportHistoryRow
    {
        public DateTimeOffset CreatedUtc { get; set; }
        public string GuildId { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string DiscordUserId { get; set; } = "";
        public string DateRange { get; set; } = "";
        public string Certified { get; set; } = "";
        public string Truck { get; set; } = "";
        public string UnitNumber { get; set; } = "";
        public string Violations { get; set; } = "";
        public bool HasGraph { get; set; }
        public string ChannelId { get; set; } = "";
    }
}
