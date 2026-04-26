using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OverWatchELD.Models;

namespace OverWatchELD.Services
{
    public sealed class TelemetrySnapshot
    {
        public bool EngineOn { get; init; }
        public double SpeedMps { get; init; }

        public string? City { get; init; }
        public string? State { get; init; }
        public string? TruckMakeModel { get; init; }

        public string? DriverId { get; init; }
        public string? DriverName { get; init; }
        public string? TruckId { get; init; }
        public string? TruckName { get; init; }

        public double? OdometerMiles { get; init; }
        public double? FuelGallons { get; init; }
        public double? FuelCapacityGallons { get; init; }
        public double? FuelPct { get; init; }
        public double? DamagePct { get; init; }

        public double? CargoWeightLbs { get; init; }
        public double? TrailerWeightLbs { get; init; }
        public double? GrossWeightLbs { get; init; }

        public DateTimeOffset? GameTimeUtc { get; init; }
        public double? GameTimeScale { get; init; }

        public bool Connected { get; init; }
        public string Source { get; init; } = "None";
        public DateTimeOffset SeenUtc { get; init; }

        public string? SourceCity { get; init; }
        public string? SourceCompany { get; init; }
        public string? DestinationCity { get; init; }
        public string? DestinationCompany { get; init; }

        public double? WorldX { get; init; }
        public double? WorldZ { get; init; }
        public double? HeadingDeg { get; init; }

        public double? GpsLatitude { get; init; }
        public double? GpsLongitude { get; init; }

        public double? MarkerX => GpsLongitude ?? WorldX;
        public double? MarkerY => GpsLatitude ?? WorldZ;
        public bool HasMarkerCoordinates => MarkerX.HasValue && MarkerY.HasValue;
    }

    public sealed class TelemetryService
    {
        private readonly TelemetryDutyAutoService _autoDuty = new();

        private bool? _lastEngineOn;
        private DateTimeOffset _lastLiveTelemetryPostUtc = DateTimeOffset.MinValue;

        public bool AutoPostTripOnEngineOff { get; set; } = true;

        private readonly System.Timers.Timer _timer;
        private readonly HttpClient _http = new HttpClient();

        public event Action<TelemetrySnapshot>? Updated;

        public TelemetrySnapshot? LastSnapshot { get; private set; }

        public string? LastRawJson { get; private set; }

        public int PollMs { get; set; } = 250;

        public string NavCity { get; private set; } = "";
        public string NavState { get; private set; } = "";

        public string LocationText =>
            string.IsNullOrWhiteSpace(NavCity) && string.IsNullOrWhiteSpace(NavState)
                ? ""
                : $"{NavCity}, {NavState}".Trim().Trim(',');

        public string EndpointUrl { get; set; } = "http://localhost:25555/api/ats/telemetry";

        private volatile bool _polling;

        public TelemetryService()
        {
            _timer = new System.Timers.Timer(PollMs);
            _timer.AutoReset = true;
            _timer.Elapsed += async (_, __) => await PollAsync();
        }

        public void Start()
        {
            _timer.Interval = PollMs;
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();

            try
            {
                FleetAutoLoggerService.FlushNow();
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task PollAsync()
        {
            if (_polling) return;
            _polling = true;

            try
            {
                string BuildUrl(string baseUrl)
                {
                    return baseUrl.Contains("?")
                        ? $"{baseUrl}&t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
                        : $"{baseUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                }

                string? json = null;
                string usedEndpoint = EndpointUrl;

                try
                {
                    using var resp = await _http.GetAsync(BuildUrl(EndpointUrl));
                    if (resp.IsSuccessStatusCode)
                        json = await resp.Content.ReadAsStringAsync();
                }
                catch
                {
                    json = null;
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    var alt = TrySwapAtsEtsEndpoint(EndpointUrl);

                    if (!string.IsNullOrWhiteSpace(alt) &&
                        !string.Equals(alt, EndpointUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var resp2 = await _http.GetAsync(BuildUrl(alt));
                            if (resp2.IsSuccessStatusCode)
                            {
                                json = await resp2.Content.ReadAsStringAsync();
                                usedEndpoint = alt;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                var app0 = System.Windows.Application.Current as OverWatchELD.App;
                var driverName0 = (app0?.Session?.DriverName ?? "Driver").Trim();

                if (string.IsNullOrWhiteSpace(driverName0))
                    driverName0 = "Driver";

                var driverId0 = MakeStableIdFromName(driverName0);

                if (string.IsNullOrWhiteSpace(json))
                {
                    LastRawJson = null;

                    LastSnapshot = new TelemetrySnapshot
                    {
                        Connected = false,
                        Source = "Funbit:25555",
                        EngineOn = false,
                        SpeedMps = 0,
                        City = null,
                        State = null,
                        WorldX = null,
                        WorldZ = null,
                        HeadingDeg = null,
                        GpsLatitude = null,
                        GpsLongitude = null,
                        TruckMakeModel = null,
                        DriverId = driverId0,
                        DriverName = driverName0,
                        TruckId = null,
                        TruckName = null,
                        GameTimeUtc = null,
                        GameTimeScale = null,
                        OdometerMiles = null,
                        FuelGallons = null,
                        FuelCapacityGallons = null,
                        FuelPct = null,
                        DamagePct = null,
                        CargoWeightLbs = null,
                        TrailerWeightLbs = null,
                        GrossWeightLbs = null,
                        SourceCity = null,
                        SourceCompany = null,
                        DestinationCity = null,
                        DestinationCompany = null,
                        SeenUtc = DateTimeOffset.UtcNow
                    };

                    Updated?.Invoke(LastSnapshot);
                    return;
                }

                if (!string.Equals(usedEndpoint, EndpointUrl, StringComparison.OrdinalIgnoreCase))
                    EndpointUrl = usedEndpoint;

                LastRawJson = json;

                try
                {
                    using var probeDoc = JsonDocument.Parse(json);

                    if (!HasUsefulTruckData(probeDoc.RootElement))
                    {
                        var alt2 = TrySwapAtsEtsEndpoint(usedEndpoint);

                        if (!string.IsNullOrWhiteSpace(alt2) &&
                            !string.Equals(alt2, usedEndpoint, StringComparison.OrdinalIgnoreCase))
                        {
                            using var resp3 = await _http.GetAsync(BuildUrl(alt2));

                            if (resp3.IsSuccessStatusCode)
                            {
                                var json2 = await resp3.Content.ReadAsStringAsync();

                                if (!string.IsNullOrWhiteSpace(json2))
                                {
                                    using var probeDoc2 = JsonDocument.Parse(json2);

                                    if (HasUsefulTruckData(probeDoc2.RootElement))
                                    {
                                        json = json2;
                                        usedEndpoint = alt2;
                                        EndpointUrl = alt2;
                                        LastRawJson = json2;
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                bool connected = TryGetBool(root, "game", "connected") ?? false;

                DateTimeOffset? gameTime = null;
                var gameTimeStr = TryGetString(root, "game", "time");

                if (!string.IsNullOrWhiteSpace(gameTimeStr) &&
                    DateTimeOffset.TryParse(gameTimeStr, out var dto))
                {
                    gameTime = dto.ToUniversalTime();
                }

                if (gameTime.HasValue)
                    EldClock.SetGameTime(gameTime.Value);

                double? timeScale = TryGetDouble(root, "game", "timeScale");

                bool engineOn =
                    TryGetBool(root, "truck", "engineOn")
                    ?? TryGetBool(root, "truck", "engine", "on")
                    ?? TryGetBool(root, "truck", "engine", "enabled")
                    ?? TryGetBool(root, "truck", "engineRunning")
                    ?? TryGetBool(root, "truck", "engine", "running")
                    ?? false;

                try
                {
                    if (AutoPostTripOnEngineOff && _lastEngineOn == true && engineOn == false && connected)
                    {
                        var day = DateOnly.FromDateTime(DateTime.Now);
                        var existing = InspectionStore.LoadMostRecent(day);
                        InspectionLog? existingLog = existing.HasValue ? existing.Value.Log : null;

                        if (existingLog == null || !existingLog.PostTripCompleted)
                        {
                            var log = existingLog ?? InspectionLog.CreateDefault(day, loadId: null);
                            log.PostTripCompleted = true;
                            log.UpdatedUtc = DateTimeOffset.UtcNow;
                            log.PostTripSignedAtUtc ??= DateTimeOffset.UtcNow;
                            log.PostTripDriverSignatureName ??=
                                (System.Windows.Application.Current as OverWatchELD.App)?.Session?.DriverName;

                            InspectionStore.Save(log);
                        }
                    }

                    _lastEngineOn = engineOn;
                }
                catch
                {
                }

                double rawSpeed =
                    TryGetDouble(root, "truck", "speed")
                    ?? TryGetDouble(root, "truck", "speedKmh")
                    ?? TryGetDouble(root, "truck", "speed_kmh")
                    ?? TryGetDouble(root, "truck", "speedMps")
                    ?? TryGetDouble(root, "truck", "speed_mps")
                    ?? 0.0;

                double speedMps = rawSpeed > 70.0 ? rawSpeed / 3.6 : rawSpeed;
                double speedMph = speedMps * 2.23694;
                var gameNow = EldClock.UtcNow;

                try
                {
                    var app = System.Windows.Application.Current as OverWatchELD.App;
                    var duty = app?.DutyMachine as DutyStateMachine;

                    if (duty != null)
                    {
                        _autoDuty.OnTelemetryTick(
                            speedMph: speedMph,
                            engineOn: engineOn,
                            parkingBrake: false,
                            gameNowUtc: gameNow,
                            duty: duty
                        );
                    }
                }
                catch
                {
                }

                string? curCity =
                    TryGetString(root, "truck", "navigation", "currentCity")
                    ?? TryGetString(root, "truck", "navigation", "nearestCity")
                    ?? TryGetString(root, "navigation", "currentCity")
                    ?? TryGetString(root, "navigation", "nearestCity")
                    ?? TryGetString(root, "navigation", "city")
                    ?? TryGetString(root, "job", "sourceCity")
                    ?? TryGetString(root, "job", "destinationCity");

                string? curState =
                    TryGetString(root, "truck", "navigation", "currentState")
                    ?? TryGetString(root, "navigation", "currentState")
                    ?? TryGetString(root, "navigation", "state");

                if (!string.IsNullOrWhiteSpace(curCity))
                    NavCity = curCity.Trim();

                if (!string.IsNullOrWhiteSpace(curState))
                    NavState = curState.Trim();

                string? sourceCity =
                    TryGetString(root, "job", "sourceCity")
                    ?? TryGetString(root, "job", "source", "city");

                string? sourceCompany =
                    TryGetString(root, "job", "sourceCompany")
                    ?? TryGetString(root, "job", "source", "company")
                    ?? TryGetString(root, "job", "sourceCompanyId")
                    ?? TryGetString(root, "job", "source_company");

                string? destinationCity =
                    TryGetString(root, "job", "destinationCity")
                    ?? TryGetString(root, "job", "destination", "city");

                string? destinationCompany =
                    TryGetString(root, "job", "destinationCompany")
                    ?? TryGetString(root, "job", "destination", "company")
                    ?? TryGetString(root, "job", "destinationCompanyId")
                    ?? TryGetString(root, "job", "destination_company");

                double? worldX =
                    TryGetDouble(root, "truck", "worldPlacement", "position", "x")
                    ?? TryGetDouble(root, "truck", "worldPlacement", "x")
                    ?? TryGetDouble(root, "truck", "placement", "position", "x")
                    ?? TryGetDouble(root, "truck", "placement", "x")
                    ?? TryGetDouble(root, "truck", "position", "x")
                    ?? TryGetDouble(root, "truck", "coordinateX")
                    ?? TryGetDouble(root, "truck", "coordinate", "x")
                    ?? TryGetDouble(root, "truck", "coordinates", "x")
                    ?? TryGetDouble(root, "truck", "x");

                double? worldZ =
                    TryGetDouble(root, "truck", "worldPlacement", "position", "z")
                    ?? TryGetDouble(root, "truck", "worldPlacement", "z")
                    ?? TryGetDouble(root, "truck", "placement", "position", "z")
                    ?? TryGetDouble(root, "truck", "placement", "z")
                    ?? TryGetDouble(root, "truck", "position", "z")
                    ?? TryGetDouble(root, "truck", "coordinateZ")
                    ?? TryGetDouble(root, "truck", "coordinate", "z")
                    ?? TryGetDouble(root, "truck", "coordinates", "z")
                    ?? TryGetDouble(root, "truck", "z");

                double? headingDeg =
                    TryGetDouble(root, "truck", "worldPlacement", "orientation", "heading")
                    ?? TryGetDouble(root, "truck", "worldPlacement", "heading")
                    ?? TryGetDouble(root, "truck", "placement", "orientation", "heading")
                    ?? TryGetDouble(root, "truck", "placement", "heading")
                    ?? TryGetDouble(root, "navigation", "gps", "heading")
                    ?? TryGetDouble(root, "navigation", "heading")
                    ?? TryGetDouble(root, "truck", "heading");

                double? gpsLat =
                    TryGetDouble(root, "navigation", "gps", "latitude")
                    ?? TryGetDouble(root, "navigation", "latitude")
                    ?? TryGetDouble(root, "gps", "latitude")
                    ?? TryGetDouble(root, "truck", "gps", "latitude")
                    ?? TryGetDouble(root, "truck", "latitude")
                    ?? TryGetDouble(root, "truck", "lat")
                    ?? TryGetDouble(root, "latitude")
                    ?? TryGetDouble(root, "lat");

                double? gpsLon =
                    TryGetDouble(root, "navigation", "gps", "longitude")
                    ?? TryGetDouble(root, "navigation", "longitude")
                    ?? TryGetDouble(root, "gps", "longitude")
                    ?? TryGetDouble(root, "truck", "gps", "longitude")
                    ?? TryGetDouble(root, "truck", "longitude")
                    ?? TryGetDouble(root, "truck", "lon")
                    ?? TryGetDouble(root, "truck", "lng")
                    ?? TryGetDouble(root, "longitude")
                    ?? TryGetDouble(root, "lon")
                    ?? TryGetDouble(root, "lng");

                string? truckMake =
                    TryGetString(root, "truck", "make")
                    ?? TryGetString(root, "truck", "brand");

                string? truckModel =
                    TryGetString(root, "truck", "model")
                    ?? TryGetString(root, "truck", "name");

                string? truckMakeModel = CombineClean(truckMake, truckModel);

                string? truckId =
                    TryGetString(root, "truck", "id")
                    ?? TryGetString(root, "truck", "truckId")
                    ?? truckModel;

                string? truckName =
                    TryGetString(root, "truck", "name")
                    ?? truckMakeModel
                    ?? truckId;

                double? odometerMiles = ConvertKmToMilesIfNeeded(
                    TryGetDouble(root, "truck", "odometer")
                    ?? TryGetDouble(root, "truck", "odometerKm")
                    ?? TryGetDouble(root, "truck", "odometer_km")
                    ?? TryGetDouble(root, "truck", "odometerMiles")
                    ?? TryGetDouble(root, "truck", "odometer_miles")
                );

                double? fuelGallons = ConvertLitersToGallonsIfNeeded(
                    TryGetDouble(root, "truck", "fuel")
                    ?? TryGetDouble(root, "truck", "fuelLiters")
                    ?? TryGetDouble(root, "truck", "fuel_liters")
                    ?? TryGetDouble(root, "truck", "fuelGallons")
                    ?? TryGetDouble(root, "truck", "fuel_gallons")
                );

                double? fuelCapacityGallons = ConvertLitersToGallonsIfNeeded(
                    TryGetDouble(root, "truck", "fuelCapacity")
                    ?? TryGetDouble(root, "truck", "fuelCapacityLiters")
                    ?? TryGetDouble(root, "truck", "fuel_capacity_liters")
                    ?? TryGetDouble(root, "truck", "fuelCapacityGallons")
                    ?? TryGetDouble(root, "truck", "fuel_capacity_gallons")
                );

                double? fuelPct = null;

                if (fuelGallons.HasValue && fuelCapacityGallons.HasValue && fuelCapacityGallons.Value > 0)
                    fuelPct = Math.Clamp((fuelGallons.Value / fuelCapacityGallons.Value) * 100.0, 0.0, 100.0);

                double? damagePct =
                    NormalizeDamagePct(
                        TryGetDouble(root, "truck", "damage")
                        ?? TryGetDouble(root, "truck", "wear")
                        ?? TryGetDouble(root, "truck", "damagePct")
                        ?? TryGetDouble(root, "truck", "damage_pct")
                    );

                double? cargoWeightLbs = ConvertKgToLbsIfNeeded(
                    TryGetDouble(root, "job", "cargoWeight")
                    ?? TryGetDouble(root, "job", "cargoWeightKg")
                    ?? TryGetDouble(root, "job", "cargo_weight_kg")
                    ?? TryGetDouble(root, "job", "cargoWeightLbs")
                    ?? TryGetDouble(root, "job", "cargo_weight_lbs")
                );

                double? trailerWeightLbs = ConvertKgToLbsIfNeeded(
                    TryGetDouble(root, "trailer", "mass")
                    ?? TryGetDouble(root, "trailer", "weight")
                    ?? TryGetDouble(root, "trailer", "weightKg")
                    ?? TryGetDouble(root, "trailer", "weight_kg")
                    ?? TryGetDouble(root, "trailer", "weightLbs")
                    ?? TryGetDouble(root, "trailer", "weight_lbs")
                );

                double? grossWeightLbs = null;

                if (cargoWeightLbs.HasValue || trailerWeightLbs.HasValue)
                    grossWeightLbs = (cargoWeightLbs ?? 0) + (trailerWeightLbs ?? 0);

                var snapshot = new TelemetrySnapshot
                {
                    Connected = connected,
                    Source = usedEndpoint,
                    EngineOn = engineOn,
                    SpeedMps = speedMps,

                    City = string.IsNullOrWhiteSpace(NavCity) ? null : NavCity,
                    State = string.IsNullOrWhiteSpace(NavState) ? null : NavState,

                    WorldX = worldX,
                    WorldZ = worldZ,
                    HeadingDeg = headingDeg,
                    GpsLatitude = gpsLat,
                    GpsLongitude = gpsLon,

                    TruckMakeModel = truckMakeModel,
                    DriverId = driverId0,
                    DriverName = driverName0,
                    TruckId = truckId,
                    TruckName = truckName,

                    GameTimeUtc = gameTime,
                    GameTimeScale = timeScale,

                    OdometerMiles = odometerMiles,
                    FuelGallons = fuelGallons,
                    FuelCapacityGallons = fuelCapacityGallons,
                    FuelPct = fuelPct,
                    DamagePct = damagePct,

                    CargoWeightLbs = cargoWeightLbs,
                    TrailerWeightLbs = trailerWeightLbs,
                    GrossWeightLbs = grossWeightLbs,

                    SourceCity = CleanOrNull(sourceCity),
                    SourceCompany = CleanOrNull(sourceCompany),
                    DestinationCity = CleanOrNull(destinationCity),
                    DestinationCompany = CleanOrNull(destinationCompany),

                    SeenUtc = DateTimeOffset.UtcNow
                };

                LastSnapshot = snapshot;

                Updated?.Invoke(snapshot);

                try
                {
                    await TryPostLiveTelemetryToBotAsync(snapshot);
                }
                catch
                {
                }
            }
            catch
            {
            }
            finally
            {
                _polling = false;
            }
        }

        private async System.Threading.Tasks.Task TryPostLiveTelemetryToBotAsync(TelemetrySnapshot snapshot)
        {
            if ((DateTimeOffset.UtcNow - _lastLiveTelemetryPostUtc).TotalSeconds < 3)
                return;

            if (snapshot == null || !snapshot.HasMarkerCoordinates)
                return;

            var botBaseUrl = GetBotApiBaseUrl();
            var guildId = GetGuildId();

            if (string.IsNullOrWhiteSpace(botBaseUrl) || string.IsNullOrWhiteSpace(guildId))
                return;

            _lastLiveTelemetryPostUtc = DateTimeOffset.UtcNow;

            botBaseUrl = botBaseUrl.Trim().TrimEnd('/');

            var driverDiscordUserId = GetDriverDiscordUserId();

            if (string.IsNullOrWhiteSpace(driverDiscordUserId))
                driverDiscordUserId = snapshot.DriverId ?? snapshot.DriverName ?? "driver";

            var body = new
            {
                guildId = guildId,
                driverDiscordUserId = driverDiscordUserId,
                driverName = snapshot.DriverName ?? "Driver",
                truckName = snapshot.TruckName ?? snapshot.TruckMakeModel ?? "Truck",

                markerX = snapshot.MarkerX,
                markerY = snapshot.MarkerY,
                worldX = snapshot.WorldX,
                worldZ = snapshot.WorldZ,
                longitude = snapshot.GpsLongitude,
                latitude = snapshot.GpsLatitude,

                city = snapshot.City,
                state = snapshot.State,
                status = snapshot.EngineOn ? "Driving" : "Stopped",

                sourceCity = snapshot.SourceCity,
                sourceCompany = snapshot.SourceCompany,
                destinationCity = snapshot.DestinationCity,
                destinationCompany = snapshot.DestinationCompany
            };

            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            await _http.PostAsync(
                $"{botBaseUrl}/api/telemetry?guildId={Uri.EscapeDataString(guildId)}",
                content
            );
        }

        private static string GetBotApiBaseUrl()
        {
            var fromSession = GetSessionValue("BotApiBaseUrl", "BotBaseUrl", "ApiBaseUrl");

            if (!string.IsNullOrWhiteSpace(fromSession))
                return fromSession;

            return "https://overwatcheld-bot-5.up.railway.app";
        }

        private static string GetGuildId()
        {
            return GetSessionValue("GuildId", "DiscordGuildId", "VtcGuildId") ?? "";
        }

        private static string GetDriverDiscordUserId()
        {
            return GetSessionValue("DiscordUserId", "DriverDiscordUserId", "UserId") ?? "";
        }

        private static string? GetSessionValue(params string[] names)
        {
            try
            {
                var app = System.Windows.Application.Current as OverWatchELD.App;
                var session = app?.Session;

                if (session == null)
                    return null;

                var type = session.GetType();

                foreach (var name in names)
                {
                    var prop = type.GetProperty(name);

                    if (prop == null)
                        continue;

                    var value = prop.GetValue(session)?.ToString();

                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }
            catch
            {
            }

            return null;
        }

        private static string? TrySwapAtsEtsEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return null;

            if (endpoint.Contains("/api/ats/telemetry", StringComparison.OrdinalIgnoreCase))
                return endpoint.Replace("/api/ats/telemetry", "/api/ets2/telemetry", StringComparison.OrdinalIgnoreCase);

            if (endpoint.Contains("/api/ets2/telemetry", StringComparison.OrdinalIgnoreCase))
                return endpoint.Replace("/api/ets2/telemetry", "/api/ats/telemetry", StringComparison.OrdinalIgnoreCase);

            return null;
        }

        private static bool HasUsefulTruckData(JsonElement root)
        {
            return TryGetString(root, "truck", "make") != null
                || TryGetString(root, "truck", "model") != null
                || TryGetDouble(root, "truck", "speed") != null
                || TryGetDouble(root, "truck", "odometer") != null
                || TryGetDouble(root, "navigation", "gps", "latitude") != null
                || TryGetDouble(root, "truck", "worldPlacement", "position", "x") != null
                || TryGetDouble(root, "truck", "placement", "x") != null;
        }

        private static string MakeStableIdFromName(string name)
        {
            var clean = (name ?? "driver").Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(clean))
                clean = "driver";

            var sb = new StringBuilder();

            foreach (var c in clean)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else if (c == ' ' || c == '_' || c == '-')
                    sb.Append('-');
            }

            var result = sb.ToString().Trim('-');

            return string.IsNullOrWhiteSpace(result)
                ? "driver"
                : result;
        }

        private static string? CombineClean(string? a, string? b)
        {
            a = CleanOrNull(a);
            b = CleanOrNull(b);

            if (a == null && b == null) return null;
            if (a == null) return b;
            if (b == null) return a;

            if (b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                return b;

            return $"{a} {b}".Trim();
        }

        private static string? CleanOrNull(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();

            return string.IsNullOrWhiteSpace(value)
                ? null
                : value;
        }

        private static bool? TryGetBool(JsonElement root, params string[] path)
        {
            if (!TryGetElement(root, out var el, path))
                return null;

            try
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;

                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();

                    if (bool.TryParse(s, out var b))
                        return b;

                    if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var i))
                        return i != 0;
                }

                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
                    return n != 0;
            }
            catch
            {
            }

            return null;
        }

        private static double? TryGetDouble(JsonElement root, params string[] path)
        {
            if (!TryGetElement(root, out var el, path))
                return null;

            try
            {
                if (el.ValueKind == JsonValueKind.Number)
                    return el.GetDouble();

                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();

                    if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                        return d;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string? TryGetString(JsonElement root, params string[] path)
        {
            if (!TryGetElement(root, out var el, path))
                return null;

            try
            {
                if (el.ValueKind == JsonValueKind.String)
                    return CleanOrNull(el.GetString());

                if (el.ValueKind == JsonValueKind.Number)
                    return el.ToString();

                if (el.ValueKind == JsonValueKind.True)
                    return "true";

                if (el.ValueKind == JsonValueKind.False)
                    return "false";
            }
            catch
            {
            }

            return null;
        }

        private static bool TryGetElement(JsonElement root, out JsonElement element, params string[] path)
        {
            element = root;

            foreach (var part in path)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    return false;

                if (!element.TryGetProperty(part, out element))
                    return false;
            }

            return true;
        }

        private static double? ConvertKmToMilesIfNeeded(double? value)
        {
            if (!value.HasValue)
                return null;

            return value.Value * 0.621371;
        }

        private static double? ConvertLitersToGallonsIfNeeded(double? value)
        {
            if (!value.HasValue)
                return null;

            return value.Value * 0.264172;
        }

        private static double? ConvertKgToLbsIfNeeded(double? value)
        {
            if (!value.HasValue)
                return null;

            return value.Value * 2.20462;
        }

        private static double? NormalizeDamagePct(double? value)
        {
            if (!value.HasValue)
                return null;

            var v = value.Value;

            if (v <= 1.0)
                return Math.Clamp(v * 100.0, 0.0, 100.0);

            return Math.Clamp(v, 0.0, 100.0);
        }
    }
}
