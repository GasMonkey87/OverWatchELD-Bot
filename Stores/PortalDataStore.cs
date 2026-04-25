using System.Text.Json;

namespace OverWatchELD.VtcBot.Stores;

public sealed class PortalDataStore
{
    private readonly string _path;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public PortalDataStore(IWebHostEnvironment env)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "data");
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "portal_data.json");
    }

    public PortalDataRoot Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return new PortalDataRoot();

            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<PortalDataRoot>(json, ReadOptions) ?? new PortalDataRoot();
            }
            catch
            {
                return new PortalDataRoot();
            }
        }
    }

    public void Save(PortalDataRoot data)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_path, JsonSerializer.Serialize(data, WriteOptions));
        }
    }

    public PortalGuildData GetGuild(string guildId)
    {
        var root = Load();
        if (!root.Guilds.TryGetValue(guildId, out var guild))
        {
            guild = new PortalGuildData { GuildId = guildId };
            root.Guilds[guildId] = guild;
            Save(root);
        }

        return guild;
    }

    public PortalGuildData UpdateGuild(string guildId, Action<PortalGuildData> update)
    {
        var root = Load();
        if (!root.Guilds.TryGetValue(guildId, out var guild))
        {
            guild = new PortalGuildData { GuildId = guildId };
            root.Guilds[guildId] = guild;
        }

        update(guild);
        guild.GuildId = guildId;
        guild.UpdatedUtc = DateTimeOffset.UtcNow;
        Save(root);
        return guild;
    }
}

public sealed class PortalDataRoot
{
    public Dictionary<string, PortalGuildData> Guilds { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PortalGuildData
{
    public string GuildId { get; set; } = "";
    public string SiteTitle { get; set; } = "";
    public string WelcomeText { get; set; } = "";
    public string CompanyInfo { get; set; } = "";
    public string HeroImageUrl { get; set; } = "";
    public string JoinDiscordUrl { get; set; } = "";
    public string LearnMoreUrl { get; set; } = "";
    public List<PortalLatestInfo> LatestInfo { get; set; } = new();
    public List<PortalDriver> Drivers { get; set; } = new();
    public List<PortalDriver> FeaturedDrivers { get; set; } = new();
    public string SelectedFeaturedDriver { get; set; } = "";
    public List<PortalTruck> Trucks { get; set; } = new();
    public List<PortalGarage> Garages { get; set; } = new();
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PortalLatestInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Meta { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PortalDriver
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Role { get; set; } = "Driver";
    public string Bio { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public string DiscordUsername { get; set; } = "";
    public string DiscordAvatarUrl { get; set; } = "";
    public string FavoriteTruck { get; set; } = "";
    public string AssignedTruck { get; set; } = "";
    public string Mileage { get; set; } = "";
    public string TotalMiles { get; set; } = "";
    public string MonthlyMiles { get; set; } = "";
    public string Achievement { get; set; } = "";
    public string Status { get; set; } = "Member";
    public string YearsInVtc { get; set; } = "";
}

public sealed class PortalTruck
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TruckNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Driver { get; set; } = "";
    public string DriverDiscordUserId { get; set; } = "";
    public string Plate { get; set; } = "";
    public string Odometer { get; set; } = "";
    public string Location { get; set; } = "";
    public string Status { get; set; } = "Available";
    public string Condition { get; set; } = "";
    public string Fuel { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class PortalGarage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Country { get; set; } = "";
    public string Slots { get; set; } = "";
    public string Cost { get; set; } = "";
    public string PurchasedBy { get; set; } = "";
    public string PurchasedUtc { get; set; } = "";
    public string Notes { get; set; } = "";
}
