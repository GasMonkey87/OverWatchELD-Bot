using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class BolDiscordOnlyRoutes
{
    public static void Register(IEndpointRouteBuilder r, BotServices services, JsonSerializerOptions jsonRead, JsonSerializerOptions jsonWrite)
    {
        r.MapGet("/vtc/bol/settings", (HttpRequest req) =>
        {
            try
            {
                var guildId = (req.Query["guildId"].ToString() ?? "").Trim();

                if (string.IsNullOrWhiteSpace(guildId) && services.Client != null)
                    guildId = services.Client.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(guildId))
                {
                    return Results.Json(new
                    {
                        ok = true,
                        guildId = "",
                        channelId = ""
                    }, jsonWrite);
                }

                var settings = services.DispatchStore?.Get(guildId);
                var channelId = ResolveBolChannelId(settings);

                return Results.Json(new
                {
                    ok = true,
                    guildId,
                    channelId
                }, jsonWrite);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "BolSettingsReadFailed",
                    detail = ex.Message
                }, statusCode: 500);
            }
        });

        r.MapMethods("/vtc/bol/settings", new[] { "POST", "GET" }, (HttpRequest req) =>
        {
            try
            {
                var guildId = (req.Query["guildId"].ToString() ?? "").Trim();

                if (string.IsNullOrWhiteSpace(guildId) && services.Client != null)
                    guildId = services.Client.Guilds.FirstOrDefault()?.Id.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(guildId))
                {
                    return Results.Json(new
                    {
                        ok = true,
                        guildId = "",
                        channelId = "",
                        note = "No guild resolved."
                    }, jsonWrite);
                }

                var settings = services.DispatchStore?.Get(guildId);
                var channelId = ResolveBolChannelId(settings);

                return Results.Json(new
                {
                    ok = true,
                    guildId,
                    channelId,
                    note = "This route reads the existing system BOL channel from dispatch settings. Set the BOL channel through your normal webhook/channel setup."
                }, jsonWrite);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "BolSettingsReadFailed",
                    detail = ex.Message
                }, statusCode: 500);
            }
        });

        r.MapMethods("/loads/bol/post", new[] { "POST", "GET" }, async (HttpRequest req) =>
        {
            try
            {
                if (services.Client == null || !services.DiscordReady)
                    return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

                BolMessageDto dto = new();

                if (HttpMethods.IsPost(req.Method))
                {
                    try
                    {
                        if (req.HasFormContentType)
                        {
                            var form = await req.ReadFormAsync();
                            dto.GuildId = (form["guildId"].ToString() ?? "").Trim();
                            dto.LoadNumber = (form["loadNumber"].ToString() ?? "").Trim();
                            dto.Driver = (form["driver"].ToString() ?? "").Trim();
                            dto.Truck = (form["truck"].ToString() ?? "").Trim();
                            dto.Cargo = (form["cargo"].ToString() ?? "").Trim();
                            dto.Weight = ParseDouble(form["weight"].ToString());
                            dto.StartLocation = (form["startLocation"].ToString() ?? "").Trim();
                            dto.EndLocation = (form["endLocation"].ToString() ?? "").Trim();
                            dto.Status = (form["status"].ToString() ?? "").Trim();
                        }
                        else
                        {
                            dto = await JsonSerializer.DeserializeAsync<BolMessageDto>(req.Body, jsonRead) ?? new BolMessageDto();
                        }
                    }
                    catch
                    {
                        dto = new BolMessageDto();
                    }
                }

                dto.GuildId = FirstNonBlank(dto.GuildId, req.Query["guildId"].ToString());
                dto.LoadNumber = FirstNonBlank(dto.LoadNumber, req.Query["loadNumber"].ToString(), req.Query["currentLoadNumber"].ToString(), req.Query["loadNo"].ToString());
                dto.Driver = FirstNonBlank(dto.Driver, req.Query["driver"].ToString(), req.Query["driverName"].ToString());
                dto.Truck = FirstNonBlank(dto.Truck, req.Query["truck"].ToString(), req.Query["truckName"].ToString());
                dto.Cargo = FirstNonBlank(dto.Cargo, req.Query["cargo"].ToString(), req.Query["commodity"].ToString());
                dto.StartLocation = FirstNonBlank(dto.StartLocation, req.Query["startLocation"].ToString(), req.Query["origin"].ToString());
                dto.EndLocation = FirstNonBlank(dto.EndLocation, req.Query["endLocation"].ToString(), req.Query["destination"].ToString());
                dto.Status = FirstNonBlank(dto.Status, req.Query["status"].ToString(), "Picked Up");

                if (dto.Weight <= 0)
                    dto.Weight = ParseDouble(req.Query["weight"].ToString());

                var guild = DiscordThreadService.ResolveGuild(services.Client, dto.GuildId);
                guild ??= services.Client.Guilds.FirstOrDefault();

                if (guild == null)
                    return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

                if (string.IsNullOrWhiteSpace(dto.LoadNumber))
                {
                    return Results.Json(new
                    {
                        ok = false,
                        error = "BadJson",
                        hint = "Send loadNumber/currentLoadNumber plus optional driver, truck, cargo, weight, startLocation, endLocation, status"
                    }, statusCode: 400);
                }

                var settings = services.DispatchStore?.Get(guild.Id.ToString());
                var channelId = ResolveBolChannelId(settings);

                if (!ulong.TryParse(channelId, out var bolChannelId) || bolChannelId == 0)
                    return Results.Json(new { ok = false, error = "BolChannelNotConfigured" }, statusCode: 400);

                var channel = guild.GetTextChannel(bolChannelId);
                if (channel == null)
                    return Results.Json(new { ok = false, error = "BolChannelNotFound" }, statusCode: 404);

                var embed = BuildBolEmbed(dto);
                var sent = await channel.SendMessageAsync(embed: embed);

                return Results.Json(new
                {
                    ok = true,
                    posted = true,
                    guildId = guild.Id.ToString(),
                    channelId = channel.Id.ToString(),
                    messageId = sent.Id.ToString(),
                    loadNumber = dto.LoadNumber
                }, jsonWrite);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "BolPostFailed",
                    detail = ex.Message
                }, statusCode: 500);
            }
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

    private static Embed BuildBolEmbed(BolMessageDto dto)
    {
        var eb = new EmbedBuilder()
            .WithTitle("Bill of Lading")
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(dto.LoadNumber))
            eb.AddField("Load Number", dto.LoadNumber, true);

        if (!string.IsNullOrWhiteSpace(dto.Status))
            eb.AddField("Status", dto.Status, true);

        if (!string.IsNullOrWhiteSpace(dto.Driver))
            eb.AddField("Driver", dto.Driver, true);

        if (!string.IsNullOrWhiteSpace(dto.Truck))
            eb.AddField("Truck", dto.Truck, true);

        if (!string.IsNullOrWhiteSpace(dto.Cargo))
            eb.AddField("Cargo", dto.Cargo, false);

        if (dto.Weight > 0)
            eb.AddField("Weight", dto.Weight.ToString("N0") + " lbs", true);

        if (!string.IsNullOrWhiteSpace(dto.StartLocation))
            eb.AddField("Origin", dto.StartLocation, true);

        if (!string.IsNullOrWhiteSpace(dto.EndLocation))
            eb.AddField("Destination", dto.EndLocation, true);

        return eb.Build();
    }

    private static string FirstNonBlank(params string[] values)
    {
        foreach (var value in values)
        {
            var s = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }

        return "";
    }

    private static double ParseDouble(string? text)
    {
        return double.TryParse((text ?? "").Trim(), out var v) ? v : 0;
    }

    private static string ReadObjString(object? obj, params string[] names)
    {
        try
        {
            if (obj == null)
                return "";

            var t = obj.GetType();
            foreach (var name in names)
            {
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (p == null)
                    continue;

                var v = p.GetValue(obj)?.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
        }
        catch
        {
        }

        return "";
    }

    private static string ReadNestedObjString(object? obj, string nestedProperty, params string[] names)
    {
        try
        {
            if (obj == null)
                return "";

            var t = obj.GetType();
            var p = t.GetProperty(nestedProperty, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (p == null)
                return "";

            var nested = p.GetValue(obj);
            return ReadObjString(nested, names);
        }
        catch
        {
            return "";
        }
    }

    private sealed class BolMessageDto
    {
        public string GuildId { get; set; } = "";
        public string LoadNumber { get; set; } = "";
        public string Driver { get; set; } = "";
        public string Truck { get; set; } = "";
        public string Cargo { get; set; } = "";
        public double Weight { get; set; }
        public string StartLocation { get; set; } = "";
        public string EndLocation { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
