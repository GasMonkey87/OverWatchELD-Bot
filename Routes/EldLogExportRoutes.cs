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
            try
            {
                if (services.Client == null)
                    return Results.Json(new { ok = false, error = "DiscordClientMissing" }, statusCode: 503);

                LogExportRequest req;
                IFormFile? graphFile = null;
                IFormFile? textFile = null;

                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync();
                    req = ReadMultipartRequest(form);
                    graphFile = FirstFile(form.Files, "graph", "image", "file", "eld-log-graph.png", "graph.png");
                    textFile = FirstFile(form.Files, "log", "txt", "report", "eld-log.txt", "log.txt");
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
                        hint = "Run the bot setup again or configure the logs channel. The route looked for GuildSettings.LogsChannelId and channel names containing logs/logbook/dot."
                    }, statusCode: 400);
                }

                var safeGraphName = "eld-log-graph.png";
                var embed = BuildEmbed(req, graphFile != null ? safeGraphName : null);
                var textAttachment = BuildTextAttachment(req);

                if (graphFile != null && graphFile.Length > 0)
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

                return Results.Json(new
                {
                    ok = true,
                    channelId = channel.Id.ToString(),
                    channelName = channel.Name,
                    imageAttached = graphFile != null && graphFile.Length > 0
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("[EldLogExportRoutes] /api/logs/export failed: " + ex);
                return Results.Json(new
                {
                    ok = false,
                    error = "ExportRouteException",
                    message = ex.Message,
                    type = ex.GetType().Name
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
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
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
            if (byName != null) return byName;

            var byFileName = files.FirstOrDefault(f =>
                string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase));
            if (byFileName != null) return byFileName;
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
                if (ulong.TryParse(settings.LogsChannelId, out var configuredId))
                {
                    var configured = guild.GetTextChannel(configuredId);
                    if (configured != null) return configured;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[EldLogExportRoutes] GuildSettings lookup failed: " + ex.Message);
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
            .AddField("Driver", driver, true)
            .AddField("Date Range", FirstNonBlank(req.DateRange, "N/A"), true)
            .AddField("Certified", FirstNonBlank(req.Certified, "N/A"), true)
            .AddField("Truck", FirstNonBlank(req.Truck, "Unknown Truck"), true)
            .AddField("Unit #", FirstNonBlank(req.UnitNumber, "N/A"), true)
            .AddField("Violations", FirstNonBlank(req.Violations, "None"), true)
            .AddField("HOS Remaining", FirstNonBlank(req.HosRemaining, "N/A"), false)
            .AddField("Discord ID", FirstNonBlank(req.DiscordUserId, "Not linked"), true)
            .AddField("TruckersMP ID", FirstNonBlank(req.TruckersMpId, "Not linked"), true)
            .AddField("Permanent Key", FirstNonBlank(req.PermanentDriverKey, "N/A"), false)
            .WithFooter("OverWatch ELD • DOT Compliance Export")
            .WithCurrentTimestamp();

        var summary = FirstNonBlank(req.Summary, "See attached log export.");
        if (summary.Length > 1000) summary = summary[..1000] + "…";
        embed.WithDescription(summary);

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

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
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
