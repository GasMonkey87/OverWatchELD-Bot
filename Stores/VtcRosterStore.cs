using System.Text.Json;
using Discord.WebSocket;

namespace OverWatchELD.VtcBot.Stores;

public sealed class VtcDriver
{
    public string DriverId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? DiscordUserId { get; set; }
    public string? DiscordUsername { get; set; }
    public string? TruckNumber { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class VtcRosterStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonRead;
    private readonly JsonSerializerOptions _jsonWrite;
    private readonly object _lock = new();
    private Dictionary<string, List<VtcDriver>> _byGuild = new();

    public VtcRosterStore(string path, JsonSerializerOptions jsonRead, JsonSerializerOptions jsonWrite)
    {
        _path = path;
        _jsonRead = jsonRead;
        _jsonWrite = jsonWrite;
        Load();
    }

    public List<VtcDriver> List(string guildId)
    {
        guildId = string.IsNullOrWhiteSpace(guildId) ? "0" : guildId.Trim();

        lock (_lock)
        {
            if (!_byGuild.TryGetValue(guildId, out var list))
            {
                list = new();
                _byGuild[guildId] = list;
                Save();
            }
            return list.Select(Clone).ToList();
        }
    }

    public VtcDriver AddOrUpdateByName(string guildId, VtcDriver incoming)
    {
        guildId = string.IsNullOrWhiteSpace(guildId) ? "0" : guildId.Trim();
        incoming.Name = (incoming.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(incoming.Name))
            throw new InvalidOperationException("Name is required.");

        lock (_lock)
        {
            if (!_byGuild.TryGetValue(guildId, out var list))
            {
                list = new();
                _byGuild[guildId] = list;
            }

            VtcDriver? existing = null;

            if (!string.IsNullOrWhiteSpace(incoming.DriverId))
                existing = list.FirstOrDefault(d => d.DriverId == incoming.DriverId);

            existing ??= list.FirstOrDefault(d => string.Equals(d.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                var d = new VtcDriver
                {
                    DriverId = string.IsNullOrWhiteSpace(incoming.DriverId) ? Guid.NewGuid().ToString("N") : incoming.DriverId,
                    Name = incoming.Name,
                    DiscordUserId = Clean(incoming.DiscordUserId),
                    DiscordUsername = Clean(incoming.DiscordUsername),
                    TruckNumber = Clean(incoming.TruckNumber),
                    Role = Clean(incoming.Role),
                    Status = Clean(incoming.Status),
                    Notes = Clean(incoming.Notes),
                    CreatedUtc = DateTimeOffset.UtcNow,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };

                list.Add(d);
                Save();
                return Clone(d);
            }

            existing.DiscordUserId = Clean(incoming.DiscordUserId) ?? existing.DiscordUserId;
            existing.DiscordUsername = Clean(incoming.DiscordUsername) ?? existing.DiscordUsername;
            existing.TruckNumber = Clean(incoming.TruckNumber) ?? existing.TruckNumber;
            existing.Role = Clean(incoming.Role) ?? existing.Role;
            existing.Status = Clean(incoming.Status) ?? existing.Status;
            existing.Notes = Clean(incoming.Notes) ?? existing.Notes;
            existing.Name = incoming.Name;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;

            Save();
            return Clone(existing);
        }
    }

    public bool Delete(string guildId, string driverIdOrName)
    {
        guildId = string.IsNullOrWhiteSpace(guildId) ? "0" : guildId.Trim();
        driverIdOrName = (driverIdOrName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(driverIdOrName)) return false;

        lock (_lock)
        {
            if (!_byGuild.TryGetValue(guildId, out var list)) return false;

            var idx = list.FindIndex(d =>
                d.DriverId.Equals(driverIdOrName, StringComparison.OrdinalIgnoreCase) ||
                d.Name.Equals(driverIdOrName, StringComparison.OrdinalIgnoreCase));

            if (idx < 0) return false;
            list.RemoveAt(idx);
            Save();
            return true;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) { _byGuild = new(); return; }
            var json = File.ReadAllText(_path);
            _byGuild = JsonSerializer.Deserialize<Dictionary<string, List<VtcDriver>>>(json, _jsonRead) ?? new();
        }
        catch
        {
            _byGuild = new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            File.WriteAllText(_path, JsonSerializer.Serialize(_byGuild, _jsonWrite));
        }
        catch { }
    }

    private static string? Clean(string? s)
    {
        s = (s ?? "").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static VtcDriver Clone(VtcDriver d) => new()
    {
        DriverId = d.DriverId,
        Name = d.Name,
        DiscordUserId = d.DiscordUserId,
        DiscordUsername = d.DiscordUsername,
        TruckNumber = d.TruckNumber,
        Role = d.Role,
        Status = d.Status,
        Notes = d.Notes,
        CreatedUtc = d.CreatedUtc,
        UpdatedUtc = d.UpdatedUtc
    };
}

public sealed class MergedRosterDriver
{
    public string DriverId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DiscordUserId { get; set; }
    public string? DiscordUsername { get; set; }
    public string? TruckNumber { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int RoleSort { get; set; }
}

public static class RosterMerge
{
    public static ulong? TryParseUserIdFromMentionOrId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        if (raw.StartsWith("<@") && raw.EndsWith(">"))
        {
            raw = raw.Substring(2, raw.Length - 3);
            if (raw.StartsWith("!")) raw = raw[1..];
        }

        return ulong.TryParse(raw, out var id) ? id : null;
    }

    public static List<MergedRosterDriver> BuildMergedDiscordRoster(SocketGuild guild, List<VtcDriver> manual)
    {
        var result = new List<MergedRosterDriver>();

        var manualByDiscordId = manual
            .Where(x => !string.IsNullOrWhiteSpace(x.DiscordUserId))
            .GroupBy(x => x.DiscordUserId!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var manualByName = manual
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var guildUsers = guild.Users.Where(u => !u.IsBot).ToDictionary(u => u.Id, u => u);

        foreach (var kv in guildUsers.OrderBy(x => GetBestDisplayName(x.Value), StringComparer.OrdinalIgnoreCase))
        {
            var user = kv.Value;
            var discordId = user.Id.ToString();
            var displayName = GetBestDisplayName(user);
            var username = (user.Username ?? "").Trim();

            manualByDiscordId.TryGetValue(discordId, out var manualById);
            manualByName.TryGetValue(displayName, out var manualByDisplay);

            var manualMatch = manualById ?? manualByDisplay;

            result.Add(new MergedRosterDriver
            {
                DriverId = !string.IsNullOrWhiteSpace(manualMatch?.DriverId) ? manualMatch!.DriverId : discordId,
                Name = !string.IsNullOrWhiteSpace(manualMatch?.Name) ? manualMatch!.Name : displayName,
                DiscordUserId = discordId,
                DiscordUsername = !string.IsNullOrWhiteSpace(manualMatch?.DiscordUsername) ? manualMatch!.DiscordUsername : username,
                TruckNumber = CleanOpt(manualMatch?.TruckNumber),
                Role = !string.IsNullOrWhiteSpace(manualMatch?.Role) ? manualMatch!.Role : GetBestRoleName(user),
                Status = !string.IsNullOrWhiteSpace(manualMatch?.Status) ? manualMatch!.Status : GetDiscordPresenceText(user),
                Notes = CleanOpt(manualMatch?.Notes),
                CreatedUtc = manualMatch?.CreatedUtc ?? DateTimeOffset.UtcNow,
                UpdatedUtc = manualMatch?.UpdatedUtc ?? DateTimeOffset.UtcNow,
                RoleSort = user.Id == guild.OwnerId ? 999999 : GetTopRoleWeight(user)
            });
        }

        return result;
    }

    public static string GetBestDisplayName(SocketGuildUser user)
    {
        var display = (user.DisplayName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(display)) return display;

        var nick = (user.Nickname ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(nick)) return nick;

        var global = (user.GlobalName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(global)) return global;

        return (user.Username ?? "Driver").Trim();
    }

    public static string GetBestRoleName(SocketGuildUser user)
    {
        var role = user.Roles.Where(r => !r.IsEveryone).OrderByDescending(r => r.Position).FirstOrDefault();
        return role?.Name ?? "Driver";
    }

    public static int GetTopRoleWeight(SocketGuildUser user)
        => user.Roles.Where(r => !r.IsEveryone).Select(r => r.Position).DefaultIfEmpty(0).Max();

    public static string GetDiscordPresenceText(SocketGuildUser user)
        => user.Status switch
        {
            Discord.UserStatus.Online => "Online",
            Discord.UserStatus.Idle => "Idle",
            Discord.UserStatus.DoNotDisturb => "Busy",
            Discord.UserStatus.AFK => "Idle",
            Discord.UserStatus.Offline => "Offline",
            Discord.UserStatus.Invisible => "Offline",
            _ => "Unknown"
        };

    private static string? CleanOpt(string? s)
    {
        s = (s ?? "").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
