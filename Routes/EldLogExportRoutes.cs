using System.Text;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OverWatchELD.VtcBot.Services;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot.Routes;

public static class EldLogExportRoutes
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void Register(WebApplication app, BotServices services)
    {
        app.MapGet("/api/logs/export/health", () => Results.Json(new
        {
            ok = true,
            route = "/api/logs/export",
            supports = new[] { "application/json", "multipart/form-data" },
            utc = DateTimeOffset.UtcNow
        }));

        app.MapPost("/api/logs/export", async (HttpContext ctx) =>
{
    Console.WriteLine("=== ELD EXPORT ROUTE HIT ===");
    Console.WriteLine($"ContentType={ctx.Request.ContentType}");

    throw new Exception("ROUTE ENTERED TEST");

    try
    {
                Console.WriteLine("[EldLogExportRoutes] POST /api/logs/export");
                Console.WriteLine($"[EldLogExportRoutes] Content-Type: {ctx.Request.ContentType}");

                if (services.Client == null)
                    return Results.Json(new { ok = false, error = "DiscordClientMissing" }, statusCode: 503);

                LogExportRequest req;
                IFormFile? graphFile = null;
                IFormFile? textFile = null;

                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync();
                    req = ReadMultipartRequest(form);
                    graphFile = FirstFile(form.Files, "graph", "image", "eld-log-graph", "eld-log-graph.png", "graph.png");
                    textFile = FirstFile(form.Files, "log", "txt", "report", "eld-log", "eld-log.txt", "log.txt");
                }
                else
                {
                    req = await ctx.Request.ReadFromJsonAsync<LogExportRequest>(JsonOpts) ?? new LogExportRequest();
                }

                if (string.IsNullOrWhiteSpace(req.GuildId))
                    return Results.Json(new { ok = false, error = "MissingGuildId" }, statusCode: 400);

                if (!ulong.TryParse(req.GuildId.Trim(), out var guildId))
                    return Results.Json(new { ok = false, error = "BadGuildId", guildId = req.GuildId }, statusCode: 400);

                var guild = services.Client.GetGuild(guildId);
                if (guild == null)
                    return Results.Json(new { ok = false, error = "GuildNotFound", guildId = req.GuildId }, statusCode: 404);

                var channel = await ResolveLogsChannelAsync(guild, services.GuildSettingsStore, req.GuildId);
                if (channel == null)
                {
                    return Results.Json(new
                    {
                        ok = false,
                        error = "LogsChannelNotConfigured",
                        hint = "Configure a logs channel for this guild, or create a text channel containing eld-logs, logs, logbook, or dot."
                    }, statusCode: 400);
                }

                var safeGraphName = "eld-log-graph.png";
                var hasGraph = graphFile != null && graphFile.Length > 0;
                var embed = BuildEmbed(req, hasGraph ? safeGraphName : null);
                var textAttachment = BuildTextAttachment(req);

                try
                {
                    if (hasGraph && graphFile != null)
                    {
                        await using var graphStream = graphFile.OpenReadStream();
                        await channel.SendFileAsync(
                            graphStream,
                            safeGraphName,
                            text: "OverWatch ELD DOT log export",
                            embed: embed);
                    }
                    else
                    {
                        await channel.SendMessageAsync("OverWatch ELD DOT log export", embed: embed);
                    }

                    if (textFile != null && textFile.Length > 0)
                    {
                        await using var txtStream = textFile.OpenReadStream();
                        await channel.SendFileAsync(txtStream, "eld-log-export.txt", text: "Attached DOT log text export.");
                    }
                    else if (!string.IsNullOrWhiteSpace(textAttachment))
                    {
                        var bytes = Encoding.UTF8.GetBytes(textAttachment);
                        await using var ms = new MemoryStream(bytes);
                        await channel.SendFileAsync(ms, "eld-log-export.txt", text: "Attached DOT log text export.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[EldLogExportRoutes] Discord send failed:");
                    Console.WriteLine(ex.ToString());

                    return Results.Json(new
                    {
                        ok = false,
                        error = "DiscordSendFailed",
                        message = ex.Message,
                        type = ex.GetType().FullName,
                        channelId = channel.Id.ToString(),
                        channelName = channel.Name
                    }, statusCode: 500);
                }

                return Results.Json(new
                {
                    ok = true,
                    channelId = channel.Id.ToString(),
                    channelName = channel.Name,
                    imageAttached = hasGraph
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("=== ELD LOG EXPORT ROUTE CRASH ===");
                Console.WriteLine(ex.ToString());

                return Results.Json(new
                {
                    ok = false,
                    error = "ExportRouteException",
                    message = ex.Message,
                    type = ex.GetType().FullName,
                    stack = ex.StackTrace
                }, statusCode: 500);
            }
        });
    }

    private static LogExportRequest ReadMultipartRequest(IFormCollection form)
    {
        string Field(params string[] names)
        {
            foreach (var name in names)
            {
                var value = form[name].ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "";
        }

        return new LogExportRequest
        {
            GuildId = Field("guildId", "guild_id"),
            DriverName = Field("driverName", "driver", "name"),
            DiscordUserId = Field("discordUserId", "discordId", "discord_user_id"),
            DiscordUsername = Field("discordUsername", "discordName"),
            TruckersMpId = Field("truckersMpId", "tmpId", "truckersmp_id"),
            IdentityHash = Field("identityHash"),
            PermanentDriverKey = Field("permanentDriverKey", "driverKey"),
            Truck = Field("truck", "truckName"),
            UnitNumber = Field("unitNumber", "unit", "truckNumber"),
            DateRange = Field("dateRange", "range"),
            Certified = Field("certified"),
            Violations = Field("violations"),
            HosRemaining = Field("hosRemaining", "hos"),
            Summary = Field("summary"),
            ReportText = Field("reportText", "text")
        };
    }

    private static IFormFile? FirstFile(IFormFileCollection files, params string[] names)
    {
        foreach (var name in names)
        {
            var byName = files.GetFile(name);
            if (byName != null)
                return byName;

            var byFileName = files.FirstOrDefault(f =>
                string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase));
            if (byFileName != null)
                return byFileName;
        }

        return files.FirstOrDefault(f =>
            (f.ContentType ?? "").Contains("image", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<SocketTextChannel?> ResolveLogsChannelAsync(
        SocketGuild guild,
        GuildSettingsStore? settingsStore,
        string guildId)
    {
        try
        {
            if (settingsStore != null)
            {
                var settings = await settingsStore.GetAsync(guildId);
                var logsChannelId = settings?.LogsChannelId;

                if (!string.IsNullOrWhiteSpace(logsChannelId) &&
                    ulong.TryParse(logsChannelId, out var configuredId))
                {
                    var configured = guild.GetTextChannel(configuredId);
                    if (configured != null)
                        return configured;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[EldLogExportRoutes] GuildSettings lookup failed:");
            Console.WriteLine(ex.ToString());
        }

        return guild.TextChannels.FirstOrDefault(c =>
            c.Name.Contains("eld-logs", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains("logs", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains("logbook", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains("dot", StringComparison.OrdinalIgnoreCase));
    }

    private static Embed BuildEmbed(LogExportRequest req, string? graphAttachmentName)
    {
        var driver = FirstNonBlank(req.DriverName, req.DiscordUsername, "Unknown Driver");

        var embed = new EmbedBuilder()
            .WithTitle("OverWatch ELD DOT Log Export")
            .WithColor(Color.Blue)
            .AddField("Driver", Clip(driver, 256), true)
            .AddField("Date Range", Clip(FirstNonBlank(req.DateRange, "N/A"), 256), true)
            .AddField("Certified", Clip(FirstNonBlank(req.Certified, "N/A"), 256), true)
            .AddField("Truck", Clip(FirstNonBlank(req.Truck, "Unknown Truck"), 256), true)
            .AddField("Unit #", Clip(FirstNonBlank(req.UnitNumber, "N/A"), 256), true)
            .AddField("Violations", Clip(FirstNonBlank(req.Violations, "None"), 256), true)
            .AddField("HOS Remaining", Clip(FirstNonBlank(req.HosRemaining, "N/A"), 1024), false)
            .AddField("Discord ID", Clip(FirstNonBlank(req.DiscordUserId, "Not linked"), 256), true)
            .AddField("TruckersMP ID", Clip(FirstNonBlank(req.TruckersMpId, "Not linked"), 256), true)
            .AddField("Permanent Key", Clip(FirstNonBlank(req.PermanentDriverKey, "N/A"), 1024), false)
            .WithDescription(Clip(FirstNonBlank(req.Summary, "See attached log export."), 1000))
            .WithFooter("OverWatch ELD • DOT Compliance Export")
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(graphAttachmentName))
            embed.WithImageUrl("attachment://" + graphAttachmentName);

        return embed.Build();
    }

    private static string BuildTextAttachment(LogExportRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.ReportText))
            return req.ReportText;

        var sb = new StringBuilder();
        sb.AppendLine("OverWatch ELD DOT Log Export");
        sb.AppendLine($"Generated UTC: {DateTimeOffset.UtcNow:u}");
        sb.AppendLine($"Driver: {FirstNonBlank(req.DriverName, req.DiscordUsername, "Unknown Driver")}");
        sb.AppendLine($"Discord ID: {FirstNonBlank(req.DiscordUserId, "Not linked")}");
        sb.AppendLine($"TruckersMP ID: {FirstNonBlank(req.TruckersMpId, "Not linked")}");
        sb.AppendLine($"Permanent Key: {FirstNonBlank(req.PermanentDriverKey, "N/A")}");
        sb.AppendLine($"Truck: {FirstNonBlank(req.Truck, "Unknown Truck")}");
        sb.AppendLine($"Unit #: {FirstNonBlank(req.UnitNumber, "N/A")}");
        sb.AppendLine($"Date Range: {FirstNonBlank(req.DateRange, "N/A")}");
        sb.AppendLine($"Certified: {FirstNonBlank(req.Certified, "N/A")}");
        sb.AppendLine($"Violations: {FirstNonBlank(req.Violations, "None")}");
        sb.AppendLine($"HOS Remaining: {FirstNonBlank(req.HosRemaining, "N/A")}");
        sb.AppendLine();
        sb.AppendLine(FirstNonBlank(req.Summary, "No summary provided."));
        return sb.ToString();
    }

    private static string Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "N/A";

        value = value.Trim();
        if (value.Length <= max)
            return value;

        return value[..Math.Max(0, max - 12)] + "… truncated";
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

    private sealed class LogExportRequest
    {
        public string GuildId { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string DiscordUserId { get; set; } = "";
        public string DiscordUsername { get; set; } = "";
        public string TruckersMpId { get; set; } = "";
        public string IdentityHash { get; set; } = "";
        public string PermanentDriverKey { get; set; } = "";
        public string Truck { get; set; } = "";
        public string UnitNumber { get; set; } = "";
        public string DateRange { get; set; } = "";
        public string Certified { get; set; } = "";
        public string Violations { get; set; } = "";
        public string HosRemaining { get; set; } = "";
        public string Summary { get; set; } = "";
        public string ReportText { get; set; } = "";
    }
}
