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
using OverWatchELD.VtcBot.Commands;
using OverWatchELD.VtcBot.Models;
using OverWatchELD.VtcBot.Stores;

namespace OverWatchELD.VtcBot;

public static partial class Program
{
    private static async Task HandleBuiltInDispatchCommandsAsync(SocketMessage rawMsg, BotServices services)
    {
        try
        {
            if (rawMsg is not SocketUserMessage msg)
                return;

            if (msg.Author.IsBot)
                return;

            var text = (msg.Content ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!text.Equals("!setdispatchchannel", StringComparison.OrdinalIgnoreCase) &&
                !text.Equals("!setupdispatch", StringComparison.OrdinalIgnoreCase))
                return;

            if (msg.Channel is not SocketGuildChannel guildChannel)
            {
                await msg.Channel.SendMessageAsync("Run that command inside your Discord server.");
                return;
            }

            var guildId = guildChannel.Guild.Id.ToString();
            ulong channelIdToSave = guildChannel.Id;

            if (guildChannel is SocketThreadChannel thread &&
                thread.ParentChannel is SocketTextChannel parentText)
            {
                channelIdToSave = parentText.Id;
            }

            services.DispatchStore?.SetDispatchChannel(guildId, channelIdToSave);

            await msg.Channel.SendMessageAsync($"Dispatch channel saved: <#{channelIdToSave}>");
        }
        catch
        {
        }
    }

    private static async Task<LoadDto?> ReadLoadDtoAsync(HttpRequest req, string logPath, string routeName)
    {
        try
        {
            string rawBody = "";
            JsonElement body = default;
            var hasBody = false;

            if (HttpMethods.IsPost(req.Method))
            {
                req.EnableBuffering();
                using (var reader = new StreamReader(req.Body, leaveOpen: true))
                {
                    rawBody = await reader.ReadToEndAsync();
                }
                req.Body.Position = 0;

                if (!string.IsNullOrWhiteSpace(rawBody))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(rawBody);
                        body = doc.RootElement.Clone();
                        hasBody = true;
                    }
                    catch
                    {
                    }
                }
            }

            string q(string name) => req.Query.TryGetValue(name, out var v) ? (v.ToString() ?? "").Trim() : "";

            string Pick(JsonElement e, params string[] names)
            {
                if (e.ValueKind != JsonValueKind.Object)
                    return "";

                foreach (var name in names)
                {
                    if (e.TryGetProperty(name, out var prop))
                    {
                        var value = (prop.ToString() ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }

                return "";
            }

            var loadNumber = hasBody ? Pick(body, "loadNumber", "currentLoadNumber", "loadNo") : "";
            if (string.IsNullOrWhiteSpace(loadNumber))
                loadNumber = FirstNonBlank(q("loadNumber"), q("currentLoadNumber"), q("loadNo"));

            var driver = hasBody ? Pick(body, "driver", "driverName") : "";
            if (string.IsNullOrWhiteSpace(driver))
                driver = FirstNonBlank(q("driver"), q("driverName"));

            var truck = hasBody ? Pick(body, "truck", "truckName") : "";
            if (string.IsNullOrWhiteSpace(truck))
                truck = FirstNonBlank(q("truck"), q("truckName"));

            var cargo = hasBody ? Pick(body, "cargo", "commodity") : "";
            if (string.IsNullOrWhiteSpace(cargo))
                cargo = FirstNonBlank(q("cargo"), q("commodity"));

            var startLocation = hasBody ? Pick(body, "startLocation", "origin") : "";
            if (string.IsNullOrWhiteSpace(startLocation))
                startLocation = FirstNonBlank(q("startLocation"), q("origin"));

            var endLocation = hasBody ? Pick(body, "endLocation", "destination") : "";
            if (string.IsNullOrWhiteSpace(endLocation))
                endLocation = FirstNonBlank(q("endLocation"), q("destination"));

            var weightText = hasBody ? Pick(body, "weight") : "";
            if (string.IsNullOrWhiteSpace(weightText))
                weightText = q("weight");

            double.TryParse(weightText, out var weight);

            LogLoadApi(logPath,
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] {routeName} method={req.Method} raw={rawBody} parsedLoad={loadNumber} parsedDriver={driver} parsedTruck={truck} parsedCargo={cargo}");

            if (string.IsNullOrWhiteSpace(loadNumber))
                return null;

            return new LoadDto
            {
                LoadNumber = loadNumber,
                Driver = driver,
                Truck = truck,
                Cargo = cargo,
                Weight = weight,
                StartLocation = startLocation,
                EndLocation = endLocation
            };
        }
        catch (Exception ex)
        {
            LogLoadApi(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] {routeName} exception={ex}");
            return null;
        }
    }

    private static void LogLoadApi(string path, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string FirstNonBlank(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v))
            {
                var s = v.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }

        return "";
    }

    private sealed class ThreadResult
    {
        public bool ThreadCreated { get; set; }
        public string ThreadId { get; set; } = "";
        public bool Archived { get; set; }
        public string Reason { get; set; } = "";
        public bool FallbackPosted { get; set; }
    }

    private static async Task<ThreadResult> PostLoadPickup(
        DiscordSocketClient? client,
        DispatchSettingsStore? dispatchStore,
        ProgramLoadThreadStore store,
        LoadDto dto,
        string logPath)
    {
        var result = new ThreadResult();

        if (client == null)
        {
            result.Reason = "ClientNull";
            return result;
        }

        if (dispatchStore == null)
        {
            result.Reason = "DispatchStoreNull";
            return result;
        }

        if (client.Guilds.Count == 0)
        {
            result.Reason = "NoGuilds";
            return result;
        }

        foreach (var guild in client.Guilds)
        {
            try
            {
                var settings = dispatchStore.Get(guild.Id.ToString());
                if (!ulong.TryParse(settings.DispatchChannelId, out var channelId) || channelId == 0)
                {
                    LogLoadApi(logPath, $"pickup no dispatch channel configured for guild={guild.Id}");
                    continue;
                }

                var channel = guild.GetTextChannel(channelId);
                if (channel == null)
                {
                    LogLoadApi(logPath, $"pickup dispatch channel not found guild={guild.Id} channel={channelId}");
                    result.Reason = "DispatchChannelNotFound";
                    continue;
                }

                var currentUser = guild.CurrentUser;
                var perms = currentUser.GetPermissions(channel);
                if (!perms.ViewChannel || !perms.SendMessages)
                {
                    LogLoadApi(logPath, $"pickup missing send perms guild={guild.Id} channel={channelId}");
                    result.Reason = "MissingPermissions";
                    continue;
                }

                var embed = BuildEmbed("📦 Load Picked Up", dto);
                var msg = await channel.SendMessageAsync(embed: embed);

                if (perms.CreatePublicThreads && perms.SendMessagesInThreads)
                {
                    try
                    {
                        var thread = await channel.CreateThreadAsync(
                            $"load-{Sanitize(dto.LoadNumber)}",
                            autoArchiveDuration: ThreadArchiveDuration.OneDay,
                            message: msg);

                        store.Upsert(new ProgramLoadThreadStore.LoadThreadEntry
                        {
                            LoadNumber = dto.LoadNumber ?? "",
                            ThreadId = thread.Id.ToString(),
                            ChannelId = channelId.ToString(),
                            GuildId = guild.Id.ToString()
                        });

                        await thread.SendMessageAsync(embed: BuildEmbed("🚛 In Transit", dto));

                        result.ThreadCreated = true;
                        result.ThreadId = thread.Id.ToString();
                        result.Reason = "OK";
                        LogLoadApi(logPath, $"pickup thread created guild={guild.Id} channel={channelId} thread={thread.Id} load={dto.LoadNumber}");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        LogLoadApi(logPath, $"pickup thread create failed guild={guild.Id} channel={channelId} load={dto.LoadNumber} ex={ex}");
                    }
                }

                result.FallbackPosted = true;
                result.Reason = "PostedWithoutThread";
                LogLoadApi(logPath, $"pickup fallback posted guild={guild.Id} channel={channelId} load={dto.LoadNumber}");
                return result;
            }
            catch (Exception ex)
            {
                result.Reason = "Exception";
                LogLoadApi(logPath, $"pickup exception guild={guild.Id} load={dto.LoadNumber} ex={ex}");
            }
        }

        if (string.IsNullOrWhiteSpace(result.Reason))
            result.Reason = "NoMatchingConfiguredGuild";

        return result;
    }

    private static async Task<ThreadResult> PostLoadComplete(
        DiscordSocketClient? client,
        ProgramLoadThreadStore store,
        LoadDto dto,
        string logPath)
    {
        var result = new ThreadResult();

        if (client == null)
        {
            result.Reason = "ClientNull";
            return result;
        }

        var map = store.GetByLoadNumber(dto.LoadNumber ?? "");
        if (map == null || !ulong.TryParse(map.ThreadId, out var threadId) || threadId == 0)
        {
            result.Reason = "ThreadNotFound";
            result.FallbackPosted = false;
            LogLoadApi(logPath, $"complete thread not found load={dto.LoadNumber}");
            return result;
        }

        try
        {
            var channel = await client.Rest.GetChannelAsync(threadId);
            if (channel is RestThreadChannel thread)
            {
                await thread.SendMessageAsync(embed: BuildEmbed("✅ Completed", dto));
                await thread.ModifyAsync(x => x.Archived = true);
                store.MarkArchived(dto.LoadNumber ?? "");
                result.Archived = true;
                result.Reason = "OK";
                LogLoadApi(logPath, $"complete archived thread={threadId} load={dto.LoadNumber}");
                return result;
            }

            result.Reason = "ThreadChannelLookupFailed";
            return result;
        }
        catch (Exception ex)
        {
            result.Reason = "Exception";
            LogLoadApi(logPath, $"complete exception load={dto.LoadNumber} thread={threadId} ex={ex}");
            return result;
        }
    }

    private static string Sanitize(string? value)
    {
        var s = (value ?? "load").Trim().ToLowerInvariant();
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        var safe = new string(chars);
        return string.IsNullOrWhiteSpace(safe) ? "load" : safe;
    }

    private static Embed BuildEmbed(string title, LoadDto dto)
    {
        return new EmbedBuilder()
            .WithTitle(title)
            .AddField("Load", dto.LoadNumber ?? "", true)
            .AddField("Driver", dto.Driver ?? "", true)
            .AddField("Truck", dto.Truck ?? "", true)
            .AddField("Cargo", dto.Cargo ?? "", false)
            .AddField("Weight", dto.Weight.ToString("N0"), true)
            .AddField("From", dto.StartLocation ?? "", true)
            .AddField("To", string.IsNullOrWhiteSpace(dto.EndLocation) ? "In Transit" : dto.EndLocation, true)
            .WithCurrentTimestamp()
            .Build();
    }

    private sealed class ProgramLoadThreadStore
    {
        private readonly string _path;
        private readonly JsonSerializerOptions _opts;

        public ProgramLoadThreadStore(string path, JsonSerializerOptions opts)
        {
            _path = path;
            _opts = opts;
        }

        public sealed class LoadThreadEntry
        {
            public string LoadNumber { get; set; } = "";
            public string ThreadId { get; set; } = "";
            public string ChannelId { get; set; } = "";
            public string GuildId { get; set; } = "";
            public bool Archived { get; set; }
        }

        public List<LoadThreadEntry> Load()
        {
            if (!File.Exists(_path))
                return new List<LoadThreadEntry>();

            return JsonSerializer.Deserialize<List<LoadThreadEntry>>(File.ReadAllText(_path), _opts)
                   ?? new List<LoadThreadEntry>();
        }

        public void Save(List<LoadThreadEntry> list)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(list, _opts));
        }

        public void Upsert(LoadThreadEntry entry)
        {
            var list = Load();
            var existing = list.FirstOrDefault(x =>
                string.Equals(x.LoadNumber, entry.LoadNumber, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                list.Add(entry);
            }
            else
            {
                existing.ThreadId = entry.ThreadId;
                existing.ChannelId = entry.ChannelId;
                existing.GuildId = entry.GuildId;
                existing.Archived = entry.Archived;
            }

            Save(list);
        }

        public LoadThreadEntry? GetByLoadNumber(string load)
        {
            return Load().FirstOrDefault(x =>
                string.Equals(x.LoadNumber, load, StringComparison.OrdinalIgnoreCase));
        }

        public void MarkArchived(string load)
        {
            var list = Load();
            var existing = list.FirstOrDefault(x =>
                string.Equals(x.LoadNumber, load, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                existing.Archived = true;

            Save(list);
        }
    }

    public sealed class DiscordSelectVtcRequest
    {
        public string GuildId { get; set; } = "";
    }
}
