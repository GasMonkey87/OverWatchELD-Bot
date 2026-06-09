using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OverWatchELD.Services;

/// <summary>
/// Phase 7 ELD ↔ Website Sync client.
/// Copy this file into the WPF ELD project under Services/.
/// It uses the same portal_data.json backend that powers the website portal.
/// </summary>
public sealed class EldWebsiteSyncClient
{
    private readonly HttpClient _http;
    private readonly string _apiBaseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public EldWebsiteSyncClient(HttpClient? http = null, string? apiBaseUrl = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _apiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? "https://api.overwatcheld.com"
            : apiBaseUrl.TrimEnd('/');
    }

    public async Task<PortalSyncPayload?> PullAsync(string guildId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(guildId)) return null;

        var url = $"{_apiBaseUrl}/api/vtc/portal/settings?guildId={Uri.EscapeDataString(guildId)}";
        using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;

        var wrapper = await res.Content.ReadFromJsonAsync<PortalSyncResponse>(JsonOptions, ct).ConfigureAwait(false);
        return wrapper?.Data;
    }

    public async Task<bool> PushAsync(PortalSyncPayload payload, CancellationToken ct = default)
    {
        if (payload == null || string.IsNullOrWhiteSpace(payload.GuildId)) return false;

        var url = $"{_apiBaseUrl}/api/vtc/portal/settings";
        using var res = await _http.PostAsJsonAsync(url, payload, JsonOptions, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode;
    }

    public async Task<bool> PushDriverAsync(string guildId, PortalDriverSync driver, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(guildId) || driver == null) return false;

        var url = $"{_apiBaseUrl}/api/vtc/portal/drivers?guildId={Uri.EscapeDataString(guildId)}";
        using var res = await _http.PostAsJsonAsync(url, driver, JsonOptions, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode;
    }

    public async Task<bool> PushTruckAsync(string guildId, PortalTruckSync truck, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(guildId) || truck == null) return false;

        var url = $"{_apiBaseUrl}/api/vtc/portal/fleet?guildId={Uri.EscapeDataString(guildId)}";
        using var res = await _http.PostAsJsonAsync(url, truck, JsonOptions, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode;
    }

    public async Task<bool> PushGarageAsync(string guildId, PortalGarageSync garage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(guildId) || garage == null) return false;

        var url = $"{_apiBaseUrl}/api/vtc/portal/garages?guildId={Uri.EscapeDataString(guildId)}";
        using var res = await _http.PostAsJsonAsync(url, garage, JsonOptions, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode;
    }

    public static PortalSyncPayload MergeLocalWithRemote(PortalSyncPayload local, PortalSyncPayload? remote)
    {
        if (remote == null) return local;
        local.GuildId = First(local.GuildId, remote.GuildId);
        local.CompanyName = First(local.CompanyName, remote.CompanyName);
        local.SiteTitle = First(local.SiteTitle, remote.SiteTitle, local.CompanyName);
        local.WelcomeText = First(local.WelcomeText, remote.WelcomeText);
        local.CompanyInfo = First(local.CompanyInfo, remote.CompanyInfo);
        local.LogoImageUrl = First(local.LogoImageUrl, remote.LogoImageUrl);
        local.BannerImageUrl = First(local.BannerImageUrl, remote.BannerImageUrl);
        local.HeroImageUrl = First(local.HeroImageUrl, remote.HeroImageUrl, local.BannerImageUrl);
        local.JoinDiscordUrl = First(local.JoinDiscordUrl, remote.JoinDiscordUrl);
        local.LearnMoreUrl = First(local.LearnMoreUrl, remote.LearnMoreUrl);
        local.IsPublicDirectoryListed = remote.IsPublicDirectoryListed;
        local.IsAcceptingApplications = remote.IsAcceptingApplications;
        local.PublicRecruitingMessage = First(local.PublicRecruitingMessage, remote.PublicRecruitingMessage);
        local.PublicRequirements = First(local.PublicRequirements, remote.PublicRequirements);
        local.Drivers = MergeById(local.Drivers, remote.Drivers, x => x.Id, x => x.DiscordUserId, x => x.Name);
        local.Trucks = MergeById(local.Trucks, remote.Trucks, x => x.Id, x => x.TruckNumber, x => x.Name);
        local.Garages = MergeById(local.Garages, remote.Garages, x => x.Id, x => x.CityToken, x => x.CityName);
        local.FeaturedDrivers = MergeById(local.FeaturedDrivers, remote.FeaturedDrivers, x => x.Id, x => x.DiscordUserId, x => x.Name + "|" + x.Achievement);
        return local;
    }

    private static List<T> MergeById<T>(List<T>? local, List<T>? remote, params Func<T, string?>[] keys)
    {
        var rows = new List<T>();
        void Add(T item)
        {
            foreach (var keyFn in keys)
            {
                var key = keyFn(item);
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (rows.Exists(x => string.Equals(keyFn(x), key, StringComparison.OrdinalIgnoreCase))) return;
            }
            rows.Add(item);
        }
        foreach (var item in remote ?? new List<T>()) Add(item);
        foreach (var item in local ?? new List<T>()) Add(item);
        return rows;
    }

    private static string First(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }
}

public sealed class PortalSyncResponse
{
    public bool Ok { get; set; }
    public PortalSyncPayload? Data { get; set; }
}

public sealed class PortalSyncPayload
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
    public List<PortalApplicationQuestionSync> ApplicationQuestions { get; set; } = new();
    public List<PortalDriverSync> Drivers { get; set; } = new();
    public List<PortalDriverSync> FeaturedDrivers { get; set; } = new();
    public List<string> SlideshowImages { get; set; } = new();
    public List<PortalDriverSync> ManagementTeam { get; set; } = new();
    public string SelectedFeaturedDriver { get; set; } = "";
    public List<PortalTruckSync> Trucks { get; set; } = new();
    public List<PortalGarageSync> Garages { get; set; } = new();
}

public sealed class PortalApplicationQuestionSync
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Type { get; set; } = "textarea";
    public bool Required { get; set; } = true;
}

public sealed class PortalDriverSync
{
    public string Id { get; set; } = "";
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

public sealed class PortalTruckSync
{
    public string Id { get; set; } = "";
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

public sealed class PortalGarageSync
{
    public string Id { get; set; } = "";
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