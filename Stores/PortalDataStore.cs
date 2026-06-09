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
            if (!File.Exists(_path)) return new PortalDataRoot();
            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<PortalDataRoot>(json, ReadOptions) ?? new PortalDataRoot();
            }
            catch { return new PortalDataRoot(); }
        }
    }

    public void Save(PortalDataRoot data)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
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
    public string CompanyName { get; set; } = "";
    public string LogoImageUrl { get; set; } = "";
    public string CompanyPictureUrl { get; set; } = "";
    public string BannerImageUrl { get; set; } = "";
    public string WelcomeText { get; set; } = "";
    public string CompanyInfo { get; set; } = "";
    public string HeroImageUrl { get; set; } = "";
    public string JoinDiscordUrl { get; set; } = "";
    public string LearnMoreUrl { get; set; } = "";
    public bool IsPublicDirectoryListed { get; set; } = true;
    public bool IsAcceptingApplications { get; set; } = true;
    public string PublicRecruitingMessage { get; set; } = "";
    public string PublicRequirements { get; set; } = "";
    public List<PortalApplicationQuestion> ApplicationQuestions { get; set; } = new()
    {
        new PortalApplicationQuestion { Question = "What is your Discord username?", Required = true },
        new PortalApplicationQuestion { Question = "How many hours do you have in ATS/ETS2?", Required = true },
        new PortalApplicationQuestion { Question = "Why do you want to join this VTC?", Required = true }
    };
    public List<PortalApplication> Applications { get; set; } = new();
    public List<PortalLatestInfo> LatestInfo { get; set; } = new();
    public List<PortalDriver> Drivers { get; set; } = new();
    public List<PortalDriver> FeaturedDrivers { get; set; } = new();
    public List<string> SlideshowImages { get; set; } = new();
    public List<PortalDriver> ManagementTeam { get; set; } = new();
    public string SelectedFeaturedDriver { get; set; } = "";
    public List<PortalTruck> Trucks { get; set; } = new();
    public List<PortalGarage> Garages { get; set; } = new();
    public List<PortalDispatchLoad> DispatchLoads { get; set; } = new();
    public List<PortalAuditEntry> AuditLog { get; set; } = new();
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PortalApplicationQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Question { get; set; } = "";
    public string Type { get; set; } = "textarea";
    public bool Required { get; set; } = true;
}

public sealed class PortalApplicationAnswer
{
    public string QuestionId { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
}

public sealed class PortalApplication
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ApplicantName { get; set; } = "";
    public string ApplicantEmail { get; set; } = "";
    public string ApplicantDiscord { get; set; } = "";
    public string ApplicantDiscordUserId { get; set; } = "";
    public List<PortalApplicationAnswer> Answers { get; set; } = new();
    public string Status { get; set; } = "Pending";
    public string ReviewedBy { get; set; } = "";
    public string ReviewNotes { get; set; } = "";
    public DateTimeOffset SubmittedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedUtc { get; set; }
}

public sealed class PortalLatestInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Meta { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PortalAuditEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Actor { get; set; } = "";
    public string ActorDiscordUserId { get; set; } = "";
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
    public string CityToken { get; set; } = "";
    public string CityName { get; set; } = "";
    public string Size { get; set; } = "Small";
    public int TruckCapacity { get; set; } = 3;
    public bool IsOwned { get; set; }
    public double? MapX { get; set; }
    public double? MapY { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public List<string> AssignedTruckNumbers { get; set; } = new();
}

public sealed class PortalDispatchLoad
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string LoadNumber { get; set; } = "";
    public string Status { get; set; } = "Available";
    public string Title { get; set; } = "";
    public string Cargo { get; set; } = "";
    public string Weight { get; set; } = "";
    public string Origin { get; set; } = "";
    public string OriginCompany { get; set; } = "";
    public string Destination { get; set; } = "";
    public string DestinationCompany { get; set; } = "";
    public string Miles { get; set; } = "";
    public string Rate { get; set; } = "";
    public string Revenue { get; set; } = "";
    public string AssignedDriver { get; set; } = "";
    public string AssignedDriverDiscordUserId { get; set; } = "";
    public string AssignedTruck { get; set; } = "";
    public string Dispatcher { get; set; } = "";
    public string Notes { get; set; } = "";
    public string BolUrl { get; set; } = "";
    public string ReceiptUrl { get; set; } = "";
    public string DiscordMessageUrl { get; set; } = "";
    public string MarketType { get; set; } = "freight";
    public string GameMarket { get; set; } = "freight_market";
    public string TargetMarket { get; set; } = "freight_market";
    public string Trailer { get; set; } = "";
    public string TrailerDefinition { get; set; } = "";
    public string TrailerVariant { get; set; } = "";
    public bool IsCompanyLoad { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClaimedUtc { get; set; }
    public DateTimeOffset? PickedUpUtc { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
    public DateTimeOffset? PaidUtc { get; set; }
}