using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OverWatchELD.VtcBot.Services;

namespace OverWatchELD.VtcBot.Routes;

public static class AutoSetupRoutes
{
    public static void Register(IEndpointRouteBuilder app, BotServices services, JsonSerializerOptions jsonWrite)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);

       
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = ex.Message
                }, statusCode: 500);
            }
        });
    }

    private static async Task<ICategoryChannel?> EnsureCategoryAsync(SocketGuild guild, string name)
    {
        var existing = guild.CategoryChannels
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
            return existing;

        return await guild.CreateCategoryChannelAsync(name);
    }

    private static async Task<ITextChannel> EnsureTextChannelAsync(
        SocketGuild guild,
        ulong? categoryId,
        string name,
        string topic)
    {
        var existing = guild.TextChannels.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) &&
            x.CategoryId == categoryId);

        if (existing != null)
            return existing;

        return await guild.CreateTextChannelAsync(name, props =>
        {
            props.CategoryId = categoryId;
            props.Topic = topic;
        });
    }

    private static async Task<string?> EnsureWebhookUrlAsync(ITextChannel channel, string webhookName)
    {
        try
        {
            var hooks = await channel.GetWebhooksAsync();
            var existing = hooks.FirstOrDefault(x =>
                string.Equals(x.Name, webhookName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return BuildWebhookUrl(existing.Id, existing.Token);

            var created = await channel.CreateWebhookAsync(webhookName);
            return BuildWebhookUrl(created.Id, created.Token);
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildWebhookUrl(ulong id, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return $"https://discord.com/api/webhooks/{id}/{token}";
    }

    private static async Task<JsonObject?> LoadJsonObjectAsync(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var raw = await File.ReadAllTextAsync(path);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return JsonNode.Parse(raw) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var p))
            {
                var s = p.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return "";
    }

    private static string DefaultIfBlank(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
