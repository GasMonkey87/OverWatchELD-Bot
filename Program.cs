// Program.cs ✅ FULL COPY/REPLACE (OverWatchELD.VtcBot)
// ✅ Merged build: ELD Login + Messaging + !Commands + Announcements
// ✅ CRITICAL FIX: GET /api/messages returns ROOT ARRAY (ELD ParseMessages requires array)
// ✅ Keeps /api/vtc/servers (servers[].id) and /api/vtc/name (fix login 404)
// ✅ Keeps ELD -> Discord send: POST /api/messages/send
// ✅ Keeps thread by-user endpoints + bulk mark/delete (thread helper kept)
// ✅ Keeps !setupdispatch + !announcement working (MessageReceived wired)
// ✅ No RestWebhook.Url (build webhook URL from Id+Token)
// ✅ Keeps VTC Roster API + !rosterLink + !rosterlist (manual drivers)
// ✅ ✅ FIXED: Discord command !link now generates/accepts a pairing code that ELD can claim
// ✅ ✅ FIXED: /api/vtc/pair/claim?code=... now returns guildId/vtcName/discordUserId/discordUsername (like ELD expects)
//
// ✅ NEW UPGRADE: API webhook creator for ELD Settings:
//    POST /api/vtc/webhook/create?guildId=...&channelId=...&type=logs|inspections|bols|announcement
//    - logs/inspections/bols: returns webhookUrl (ELD stores it)
//    - announcement: ALSO saves AnnouncementChannelId + AnnouncementWebhookUrl into dispatch_settings.json
//
// ✅ NEW UPGRADE: Performance tracking + Leaderboard
//    - Slash commands: /performance, /leaderboard
//    - Push endpoint: POST /api/performance/update?guildId=...
//    - Top endpoint:  GET  /api/performance/top?guildId=...&take=10
//    - Optional pull-refresh failsafe from ELD: set env ELD_BASE_URL, bot will periodically pull /api/performance?guildId=...
//
// ⚠️ Nothing else changed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;

using OverWatchELD.VtcBot.Threads;

internal static class Program
{
    private static DiscordSocketClient? _client;
    private static volatile bool _discordReady = false;

    // ✅ 2-line guard support (prevents duplicate MessageReceived handlers)
    private static bool _messageHandlerAttached = false;

    // ✅ Slash command guard (prevents duplicate registration)
    private static bool _slashWired = false;
    private static bool _slashRegisteredOnce = false;

    private static readonly JsonSerializerOptions JsonReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonWriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");

    private static ThreadMapStore? _threadStore;
    private static readonly string ThreadMapPath = Path.Combine(DataDir, "thread_map.json");

    private static DispatchSettingsStore? _dispatchStore;
    private static readonly string DispatchCfgPath = Path.Combine(DataDir, "dispatch_settings.json");

    // -----------------------------
    // VTC Roster (manual drivers) - persistent per guild
    // Stored at: data/vtc_roster.json
    // -----------------------------
    private static VtcRosterStore? _rosterStore;
    private static readonly string RosterPath = Path.Combine(DataDir, "vtc_roster.json");

    // -----------------------------
    // ✅ Pairing store (ELD pairing codes)
    // Bot generates code (via !link) -> ELD claims it at /api/vtc/pair/claim?code=...
    // Stored at: data/link_codes.json
    // -----------------------------
    private static LinkCodeStore? _linkCodeStore;
    private static readonly string LinkCodesPath = Path.Combine(DataDir, "link_codes.json");

    // (Optional) linked history, useful later
    private static LinkedDriversStore? _linkedDriversStore;
    private static readonly string LinkedDriversPath = Path.Combine(DataDir, "linked_drivers.json");

    // -----------------------------
    // ✅ Performance Store (Miles + Loads + Performance%)
    // Stored at: data/performance_<guildId>.json
    // -----------------------------
    private static PerformanceStore? _perfStore;
    private static readonly string PerfDir = Path.Combine(DataDir, "performance");

    // -----------------------------
    // Models
    // -----------------------------
    private sealed class VtcDriver
    {
        public string DriverId { get; set; } = Guid.NewGuid().ToString("N"); // stable id
        public string Name { get; set; } = "";
        public string? DiscordUserId { get; set; } // optional
        public string? TruckNumber { get; set; }
        public string? Role { get; set; }          // Driver/Dispatcher/Admin/etc
        public string? Status { get; set; }        // Active/Inactive
        public string? Notes { get; set; }
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    // -----------------------------
    // ✅ Performance models
    // -----------------------------
    private sealed class DriverPerformance
    {
        public string DiscordUserId { get; set; } = "";

        public double MilesWeek { get; set; }
        public double MilesMonth { get; set; }
        public double MilesTotal { get; set; }

        public int LoadsWeek { get; set; }
        public int LoadsMonth { get; set; }
        public int LoadsTotal { get; set; }

        public double PerformancePct { get; set; } // 0..100 (from ELD)
        public double Score { get; set; }          // computed here
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

        public string? Source { get; set; }        // optional "eld"
    }

    private static class PerformanceScoring
    {
        // Miles + Loads + Performance %
        // You can tune these later without changing payload/storage.
        public static double ComputeScore(DriverPerformance p)
        {
            var miles = p.MilesWeek * 1.0;
            var loads = p.LoadsWeek * 250.0;
            var pct = p.PerformancePct * 500.0;
            return miles + loads + pct;
        }
    }

    private sealed class PerformanceStore
    {
        private readonly string _dir;
        private readonly object _lock = new();

        public PerformanceStore(string dir)
        {
            _dir = dir;
            Directory.CreateDirectory(_dir);
        }

        private string PathForGuild(string guildId)
        {
            guildId = (guildId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";
            return System.IO.Path.Combine(_dir, $"performance_{guildId}.json");
        }

        // guild file: discordUserId -> performance
        public Dictionary<string, DriverPerformance> Load(string guildId)
        {
            var path = PathForGuild(guildId);
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(path)) return new Dictionary<string, DriverPerformance>(StringComparer.OrdinalIgnoreCase);
                    var json = File.ReadAllText(path);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, DriverPerformance>>(json, JsonReadOpts);
                    return dict != null
                        ? new Dictionary<string, DriverPerformance>(dict, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, DriverPerformance>(StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    return new Dictionary<string, DriverPerformance>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public void Save(string guildId, Dictionary<string, DriverPerformance> dict)
        {
            var path = PathForGuild(guildId);
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? _dir);
                    File.WriteAllText(path, JsonSerializer.Serialize(dict, JsonWriteOpts));
                }
                catch { }
            }
        }

        public void Upsert(string guildId, DriverPerformance perf)
        {
            guildId = (guildId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";

            var uid = (perf?.DiscordUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(uid)) return;

            var dict = Load(guildId);

            perf.DiscordUserId = uid;
            perf.UpdatedUtc = DateTimeOffset.UtcNow;
            perf.Score = PerformanceScoring.ComputeScore(perf);

            dict[uid] = perf;
            Save(guildId, dict);
        }

        public (DriverPerformance? perf, int rank, int total) GetWithRank(string guildId, string discordUserId)
        {
            guildId = (guildId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";
            discordUserId = (discordUserId ?? "").Trim();

            var dict = Load(guildId);
            if (!dict.TryGetValue(discordUserId, out var me))
                return (null, 0, dict.Count);

            // compute scores + rank
            var list = dict.Values
                .Select(p =>
                {
                    p.Score = PerformanceScoring.ComputeScore(p);
                    return p;
                })
                .OrderByDescending(p => p.Score)
                .ThenByDescending(p => p.PerformancePct)
                .ThenByDescending(p => p.MilesWeek)
                .ThenByDescending(p => p.LoadsWeek)
                .ToList();

            var idx = list.FindIndex(x => string.Equals(x.DiscordUserId, discordUserId, StringComparison.OrdinalIgnoreCase));
            var rank = idx >= 0 ? idx + 1 : 0;
            return (me, rank, list.Count);
        }

        public List<DriverPerformance> GetTop(string guildId, int take)
        {
            if (take <= 0) take = 10;
            if (take > 50) take = 50;

            var dict = Load(guildId);
            return dict.Values
                .Select(p =>
                {
                    p.Score = PerformanceScoring.ComputeScore(p);
                    return p;
                })
                .OrderByDescending(p => p.Score)
                .ThenByDescending(p => p.PerformancePct)
                .ThenByDescending(p => p.MilesWeek)
                .ThenByDescending(p => p.LoadsWeek)
                .Take(take)
                .ToList();
        }
    }

    private sealed class VtcRosterStore
    {
        private readonly string _path;
        private readonly object _lock = new();

        // guildId -> drivers[]
        private Dictionary<string, List<VtcDriver>> _byGuild = new();

        public VtcRosterStore(string path)
        {
            _path = path;
            Load();
        }

        public List<VtcDriver> List(string guildId)
        {
            guildId = (guildId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";

            lock (_lock)
            {
                if (!_byGuild.TryGetValue(guildId, out var list))
                {
                    list = new List<VtcDriver>();
                    _byGuild[guildId] = list;
                    Save();
                }
                return list.Select(Clone).ToList();
            }
        }

        public VtcDriver AddOrUpdateByName(string guildId, VtcDriver incoming)
        {
            guildId = (guildId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";

            incoming.Name = (incoming.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(incoming.Name))
                throw new InvalidOperationException("Name is required.");

            lock (_lock)
            {
                if (!_byGuild.TryGetValue(guildId, out var list))
                {
                    list = new List<VtcDriver>();
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

                // update existing
                existing.DiscordUserId = Clean(incoming.DiscordUserId) ?? existing.DiscordUserId;
                existing.TruckNumber = Clean(incoming.TruckNumber) ?? existing.TruckNumber;
                existing.Role = Clean(incoming.Role) ?? existing.Role;
                existing.Status = Clean(incoming.Status) ?? existing.Status;
                existing.Notes = Clean(incoming.Notes) ?? existing.Notes;

                if (!string.IsNullOrWhiteSpace(incoming.Name))
                    existing.Name = incoming.Name;

                existing.UpdatedUtc = DateTimeOffset.UtcNow;

                Save();
                return Clone(existing);
            }
        }

        public bool Delete(string guildId, string driverIdOrName)
        {
            guildId = (guildId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";

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
                var dict = JsonSerializer.Deserialize<Dictionary<string, List<VtcDriver>>>(json, JsonReadOpts);
                _byGuild = dict ?? new();
            }
            catch { _byGuild = new(); }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? DataDir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_byGuild, JsonWriteOpts));
            }
            catch { }
        }

        private static string? Clean(string? s)
        {
            s = (s ?? "").Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static VtcDriver Clone(VtcDriver d) => new VtcDriver
        {
            DriverId = d.DriverId,
            Name = d.Name,
            DiscordUserId = d.DiscordUserId,
            TruckNumber = d.TruckNumber,
            Role = d.Role,
            Status = d.Status,
            Notes = d.Notes,
            CreatedUtc = d.CreatedUtc,
            UpdatedUtc = d.UpdatedUtc
        };
    }

    private sealed class RosterUpsertReq
    {
        public string? DriverId { get; set; }
        public string? Name { get; set; }
        public string? DiscordUserId { get; set; }
        public string? TruckNumber { get; set; }
        public string? Role { get; set; }
        public string? Status { get; set; }
        public string? Notes { get; set; }
    }

    // ✅ helper for !rosterLink
    private static ulong? TryParseUserIdFromMentionOrId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        // <@123> or <@!123>
        if (raw.StartsWith("<@") && raw.EndsWith(">"))
        {
            raw = raw.Substring(2, raw.Length - 3);
            if (raw.StartsWith("!")) raw = raw.Substring(1);
        }

        return ulong.TryParse(raw, out var id) ? id : null;
    }

    // -----------------------------
    // ✅ Pairing Code Store (Bot -> ELD)
    // -----------------------------
    private sealed class LinkCodeEntry
    {
        public string Code { get; set; } = "";
        public string GuildId { get; set; } = "0";
        public string GuildName { get; set; } = "";
        public string DiscordUserId { get; set; } = "";
        public string DiscordUsername { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(30);
    }

    private sealed class LinkCodeStore
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<string, LinkCodeEntry> _byCode = new(StringComparer.OrdinalIgnoreCase);

        public LinkCodeStore(string path)
        {
            _path = path;
            Load();
        }

        public void Put(LinkCodeEntry entry)
        {
            if (entry == null) throw new InvalidOperationException("Entry required.");
            var code = (entry.Code ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Code required.");

            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                entry.Code = code;
                if (entry.CreatedUtc == default) entry.CreatedUtc = now;
                if (entry.ExpiresUtc <= now) entry.ExpiresUtc = now.AddMinutes(30);

                _byCode[code] = entry;
                Prune_NoLock(now);
                Save_NoLock();
            }
        }

        public bool Consume(string code, out LinkCodeEntry entry)
        {
            entry = new LinkCodeEntry();
            code = (code ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) return false;

            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                Prune_NoLock(now);

                if (!_byCode.TryGetValue(code, out var e)) return false;
                if (e.ExpiresUtc <= now)
                {
                    _byCode.Remove(code);
                    Save_NoLock();
                    return false;
                }

                _byCode.Remove(code);
                Save_NoLock();
                entry = Clone(e);
                return true;
            }
        }

        private void Prune_NoLock(DateTimeOffset now)
        {
            var expired = _byCode
                .Where(kvp => kvp.Value.ExpiresUtc <= now)
                .Select(kvp => kvp.Key)
                .ToList();

            if (expired.Count == 0) return;
            foreach (var k in expired) _byCode.Remove(k);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) { _byCode = new(StringComparer.OrdinalIgnoreCase); return; }
                var json = File.ReadAllText(_path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, LinkCodeEntry>>(json, JsonReadOpts)
                           ?? new Dictionary<string, LinkCodeEntry>();
                _byCode = new Dictionary<string, LinkCodeEntry>(dict, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                _byCode = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save_NoLock()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? DataDir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_byCode, JsonWriteOpts));
            }
            catch { }
        }

        private static LinkCodeEntry Clone(LinkCodeEntry e) => new LinkCodeEntry
        {
            Code = e.Code,
            GuildId = e.GuildId,
            GuildName = e.GuildName,
            DiscordUserId = e.DiscordUserId,
            DiscordUsername = e.DiscordUsername,
            CreatedUtc = e.CreatedUtc,
            ExpiresUtc = e.ExpiresUtc
        };
    }

    private sealed class LinkedDriverEntry
    {
        public string GuildId { get; set; } = "0";
        public string DiscordUserId { get; set; } = "";
        public string DiscordUserName { get; set; } = "";
        public DateTimeOffset LinkedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string? LastCode { get; set; }
    }

    private sealed class LinkedDriversStore
    {
        private readonly string _path;
        private readonly object _lock = new();

        // guildId -> (discordUserId -> entry)
        private Dictionary<string, Dictionary<string, LinkedDriverEntry>> _byGuild =
            new(StringComparer.OrdinalIgnoreCase);

        public LinkedDriversStore(string path)
        {
            _path = path;
            Load();
        }

        public void Link(string guildId, string discordUserId, string discordUserName, string? code)
        {
            guildId = (guildId ?? "").Trim();
            discordUserId = (discordUserId ?? "").Trim();
            discordUserName = (discordUserName ?? "").Trim();
            code = (code ?? "").Trim();

            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";
            if (string.IsNullOrWhiteSpace(discordUserId)) return;

            lock (_lock)
            {
                if (!_byGuild.TryGetValue(guildId, out var byUser))
                {
                    byUser = new Dictionary<string, LinkedDriverEntry>(StringComparer.OrdinalIgnoreCase);
                    _byGuild[guildId] = byUser;
                }

                byUser[discordUserId] = new LinkedDriverEntry
                {
                    GuildId = guildId,
                    DiscordUserId = discordUserId,
                    DiscordUserName = discordUserName,
                    LinkedUtc = DateTimeOffset.UtcNow,
                    LastCode = string.IsNullOrWhiteSpace(code) ? null : code
                };

                Save_NoLock();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) { _byGuild = new(StringComparer.OrdinalIgnoreCase); return; }
                var json = File.ReadAllText(_path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, LinkedDriverEntry>>>(json, JsonReadOpts);
                _byGuild = dict ?? new(StringComparer.OrdinalIgnoreCase);
            }
            catch { _byGuild = new(StringComparer.OrdinalIgnoreCase); }
        }

        private void Save_NoLock()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? DataDir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_byGuild, JsonWriteOpts));
            }
            catch { }
        }
    }

    // -----------------------------
    // Dispatch settings store (unchanged)
    // -----------------------------
    private sealed class DispatchSettings
    {
        public string GuildId { get; set; } = "";
        public string? DispatchChannelId { get; set; }
        public string? DispatchWebhookUrl { get; set; }
        public string? AnnouncementChannelId { get; set; }
        public string? AnnouncementWebhookUrl { get; set; }
    }

    private sealed class DispatchSettingsStore
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<string, DispatchSettings> _byGuild = new();

        public DispatchSettingsStore(string path)
        {
            _path = path;
            Load();
        }

        public DispatchSettings Get(string guildId)
        {
            guildId = (guildId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(guildId)) guildId = "0";

            lock (_lock)
            {
                if (!_byGuild.TryGetValue(guildId, out var s))
                {
                    s = new DispatchSettings { GuildId = guildId };
                    _byGuild[guildId] = s;
                    Save();
                }
                return s;
            }
        }

        public void SetDispatchChannel(string guildId, ulong channelId)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.DispatchChannelId = channelId.ToString();
                Save();
            }
        }

        public void SetDispatchWebhook(string guildId, string url)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.DispatchWebhookUrl = (url ?? "").Trim();
                Save();
            }
        }

        public void SetAnnouncementChannel(string guildId, ulong channelId)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.AnnouncementChannelId = channelId.ToString();
                Save();
            }
        }

        public void SetAnnouncementWebhook(string guildId, string url)
        {
            lock (_lock)
            {
                var s = Get(guildId);
                s.AnnouncementWebhookUrl = (url ?? "").Trim();
                Save();
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) { _byGuild = new(); return; }
                var json = File.ReadAllText(_path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, DispatchSettings>>(json, JsonReadOpts);
                _byGuild = dict ?? new();
            }
            catch { _byGuild = new(); }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? DataDir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_byGuild, JsonWriteOpts));
            }
            catch { }
        }
    }

    private sealed class SendMessageReq
    {
        [JsonPropertyName("driverName")]
        public string? DisplayName { get; set; }
        public string? DiscordUserId { get; set; }
        public string? DiscordUsername { get; set; }
        public string Text { get; set; } = "";
        public string? Source { get; set; }
    }

    private sealed class MarkBulkReq
    {
        public string? ChannelId { get; set; }
        public List<string>? MessageIds { get; set; }
    }

    private sealed class DeleteBulkReq
    {
        public string? ChannelId { get; set; }
        public List<string>? MessageIds { get; set; }
    }

    private sealed class AnnouncementPostReq
    {
        public string? GuildId { get; set; }
        public string? Text { get; set; }
        public string? Author { get; set; }
    }

    public static async Task Main()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(PerfDir);

        _threadStore = new ThreadMapStore(ThreadMapPath);
        _dispatchStore = new DispatchSettingsStore(DispatchCfgPath);
        _rosterStore = new VtcRosterStore(RosterPath);

        _linkCodeStore = new LinkCodeStore(LinkCodesPath);
        _linkedDriversStore = new LinkedDriversStore(LinkedDriversPath);

        _perfStore = new PerformanceStore(PerfDir);

        var token = (Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN env var.");
            return;
        }

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents =
                GatewayIntents.Guilds |
                GatewayIntents.GuildMessages |
                GatewayIntents.MessageContent | // ✅ REQUIRED for prefix commands
                GatewayIntents.GuildMembers
        });

        _client.Ready += async () =>
        {
            _discordReady = true;
            Console.WriteLine("✅ Discord client READY");

            // ✅ Wire slash interactions exactly once
            if (!_slashWired)
            {
                _client.InteractionCreated += HandleInteractionAsync;
                _client.GuildAvailable += async g =>
                {
                    try { await RegisterSlashCommandsForGuildAsync(g); } catch { }
                };
                _slashWired = true;
            }

            // ✅ Register slash commands once on startup for all current guilds
            if (!_slashRegisteredOnce)
            {
                _slashRegisteredOnce = true;
                try
                {
                    foreach (var g in _client.Guilds)
                    {
                        try { await RegisterSlashCommandsForGuildAsync(g); } catch { }
                    }
                    Console.WriteLine("✅ Slash commands registered (guild-scoped).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("⚠️ Slash command register failed: " + ex.Message);
                }
            }

            return;
        };

        _client.Log += msg =>
        {
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {msg.Severity,-7} {msg.Source,-12} {msg.Message}");
            if (msg.Exception != null) Console.WriteLine(msg.Exception);
            return Task.CompletedTask;
        };

        // ✅ 2-line guard (prevents duplicate MessageReceived attaches)
        if (!_messageHandlerAttached)
        {
            _client.MessageReceived += HandleMessageAsync;
            _messageHandlerAttached = true;
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // ✅ Optional: background pull refresh loop (failsafe)
        _ = Task.Run(() => PerformancePullLoopAsync());

        var portStr = (Environment.GetEnvironmentVariable("PORT") ?? "8080").Trim();
        if (!int.TryParse(portStr, out var port)) port = 8080;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/build", () => Results.Ok(new { ok = true, name = "OverWatchELD.VtcBot", version = "link-eld-claim+webhook-create+performance-slash" }));

        var api = app.MapGroup("/api");
        var api2 = app.MapGroup("/api/api"); // double-api safety
        RegisterRoutes(api);
        RegisterRoutes(api2);

        _ = Task.Run(() => app.RunAsync());
        Console.WriteLine($"🌐 HTTP API listening on 0.0.0.0:{port}");
        await Task.Delay(-1);
    }

    private static void RegisterRoutes(IEndpointRouteBuilder r)
    {
        // -----------------------------
        // ✅ ELD LOGIN endpoints
        // -----------------------------
        r.MapGet("/vtc/servers", () =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var servers = _client.Guilds.Select(g => new
            {
                id = g.Id.ToString(), // ✅ ELD expects `id`
                name = g.Name,
                guildId = g.Id.ToString()
            }).ToArray();

            return Results.Json(new { ok = true, servers, serverCount = servers.Length }, JsonWriteOpts);
        });

        r.MapGet("/vtc/name", (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();

            SocketGuild? g = null;
            if (ulong.TryParse(guildIdStr, out var gid) && gid != 0)
                g = _client.Guilds.FirstOrDefault(x => x.Id == gid);

            g ??= _client.Guilds.FirstOrDefault();
            if (g == null)
                return Results.Json(new { ok = false, error = "NoGuild" }, statusCode: 404);

            return Results.Json(new
            {
                ok = true,
                guildId = g.Id.ToString(),
                name = g.Name,
                vtcName = g.Name
            }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ NEW: ELD Settings Webhook Creator (Logs / Inspections / BOLs / Announcement)
        // POST /api/vtc/webhook/create?guildId=...&channelId=...&type=logs|inspections|bols|announcement
        // - logs/inspections/bols: returns webhookUrl (ELD stores it)
        // - announcement: ALSO saves in dispatch_settings.json for announcements API usage
        // -----------------------------
        r.MapPost("/vtc/webhook/create", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var guildIdStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var channelIdStr = (req.Query["channelId"].ToString() ?? "").Trim();
            var type = (req.Query["type"].ToString() ?? "").Trim().ToLowerInvariant();

            if (!ulong.TryParse(guildIdStr, out var gid) || gid == 0)
                return Results.Json(new { ok = false, error = "InvalidGuildId" }, statusCode: 400);

            if (!ulong.TryParse(channelIdStr, out var cid) || cid == 0)
                return Results.Json(new { ok = false, error = "InvalidChannelId" }, statusCode: 400);

            var guild = _client.Guilds.FirstOrDefault(x => x.Id == gid);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var ch = guild.GetTextChannel(cid);
            if (ch == null)
                return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            var hookName = type switch
            {
                "logs" => "OverWatchELD Logs",
                "inspections" => "OverWatchELD Inspections",
                "bols" => "OverWatchELD BOLs",
                "announcement" => "OverWatchELD Announcements",
                "announcements" => "OverWatchELD Announcements",
                _ => "OverWatchELD"
            };

            try
            {
                var hook = await ch.CreateWebhookAsync(hookName);
                var token = (hook.Token ?? "").Trim();
                var url = string.IsNullOrWhiteSpace(token) ? "" : $"https://discord.com/api/webhooks/{hook.Id}/{token}";

                // If this is announcement, persist into dispatch settings (so /vtc/announcements works)
                if (_dispatchStore != null && (type == "announcement" || type == "announcements"))
                {
                    _dispatchStore.SetAnnouncementChannel(gid.ToString(), ch.Id);
                    if (!string.IsNullOrWhiteSpace(url))
                        _dispatchStore.SetAnnouncementWebhook(gid.ToString(), url);
                }

                return Results.Json(new
                {
                    ok = true,
                    type,
                    guildId = gid.ToString(),
                    channelId = ch.Id.ToString(),
                    webhookUrl = url,
                    saved = (type == "announcement" || type == "announcements")
                }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "WebhookCreateFailed", message = ex.Message }, statusCode: 500);
            }
        });

        // -----------------------------
        // ✅ NEW: Performance PUSH (ELD -> Bot)
        // POST /api/performance/update?guildId=...
        // Body: DriverPerformance JSON
        // -----------------------------
        r.MapPost("/performance/update", async (HttpRequest req) =>
        {
            if (_perfStore == null)
                return Results.Json(new { ok = false, error = "PerfStoreNotReady" }, statusCode: 503);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var guild = ResolveGuild(gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            DriverPerformance? payload;
            try { payload = await JsonSerializer.DeserializeAsync<DriverPerformance>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.DiscordUserId))
                return Results.Json(new { ok = false, error = "BadJsonOrMissingDiscordUserId" }, statusCode: 400);

            payload.DiscordUserId = (payload.DiscordUserId ?? "").Trim();
            payload.PerformancePct = Math.Clamp(payload.PerformancePct, 0, 100);
            payload.Score = PerformanceScoring.ComputeScore(payload);

            _perfStore.Upsert(guild.Id.ToString(), payload);

            var (_, rank, total) = _perfStore.GetWithRank(guild.Id.ToString(), payload.DiscordUserId);

            return Results.Json(new
            {
                ok = true,
                guildId = guild.Id.ToString(),
                discordUserId = payload.DiscordUserId,
                score = payload.Score,
                rank,
                total
            }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ NEW: Performance TOP (for UI/debug)
        // GET /api/performance/top?guildId=...&take=10
        // -----------------------------
        r.MapGet("/performance/top", (HttpRequest req) =>
        {
            if (_perfStore == null)
                return Results.Json(new { ok = false, error = "PerfStoreNotReady" }, statusCode: 503);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var guild = ResolveGuild(gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var takeStr = (req.Query["take"].ToString() ?? "10").Trim();
            if (!int.TryParse(takeStr, out var take)) take = 10;
            take = Math.Clamp(take, 1, 50);

            var top = _perfStore.GetTop(guild.Id.ToString(), take);
            return Results.Json(new { ok = true, guildId = guild.Id.ToString(), top }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ ELD PAIRING CLAIM (classic flow)
        // GET /api/vtc/pair/claim?code=ABC123
        // Bot consumes code created by !link and returns identity + guild
        // -----------------------------
        r.MapGet("/vtc/pair/claim", (HttpRequest req) =>
        {
            var code = (req.Query["code"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                return Results.Json(new { ok = false, error = "MissingCode" }, statusCode: 400);

            if (_linkCodeStore == null)
                return Results.Json(new { ok = false, error = "LinkStoreNotReady" }, statusCode: 503);

            if (!_linkCodeStore.Consume(code, out var entry))
                return Results.Json(new { ok = false, error = "InvalidOrExpiredCode" }, statusCode: 404);

            // Validate guild still exists, but don't block pairing if bot cache not ready
            var vtcName = (entry.GuildName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(vtcName) && _client != null)
            {
                try
                {
                    if (ulong.TryParse(entry.GuildId, out var gid2) && gid2 != 0)
                    {
                        var g = _client.Guilds.FirstOrDefault(x => x.Id == gid2);
                        if (g != null) vtcName = g.Name;
                    }
                }
                catch { }
            }

            // Save linked history (optional)
            try
            {
                _linkedDriversStore?.Link(entry.GuildId, entry.DiscordUserId, entry.DiscordUsername, entry.Code);
            }
            catch { }

            return Results.Json(new
            {
                ok = true,
                code = entry.Code,
                guildId = entry.GuildId,
                vtcName = string.IsNullOrWhiteSpace(vtcName) ? "VTC" : vtcName,
                discordUserId = entry.DiscordUserId,
                discordUsername = entry.DiscordUsername
            }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ VTC Roster API (manual drivers)
        // -----------------------------
        r.MapGet("/vtc/roster", (HttpRequest req) =>
        {
            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var guild = ResolveGuild(gidStr);
            if (guild == null) return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            if (_rosterStore == null) return Results.Json(new { ok = false, error = "RosterNotReady" }, statusCode: 503);

            var list = _rosterStore.List(guild.Id.ToString())
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Results.Json(new { ok = true, guildId = guild.Id.ToString(), drivers = list }, JsonWriteOpts);
        });

        r.MapPost("/vtc/roster/add", async (HttpRequest req) =>
        {
            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var guild = ResolveGuild(gidStr);
            if (guild == null) return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            if (_rosterStore == null) return Results.Json(new { ok = false, error = "RosterNotReady" }, statusCode: 503);

            RosterUpsertReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<RosterUpsertReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Name))
                return Results.Json(new { ok = false, error = "BadJsonOrMissingName" }, statusCode: 400);

            try
            {
                var saved = _rosterStore.AddOrUpdateByName(guild.Id.ToString(), new VtcDriver
                {
                    DriverId = (payload.DriverId ?? "").Trim(),
                    Name = (payload.Name ?? "").Trim(),
                    DiscordUserId = (payload.DiscordUserId ?? "").Trim(),
                    TruckNumber = (payload.TruckNumber ?? "").Trim(),
                    Role = (payload.Role ?? "").Trim(),
                    Status = (payload.Status ?? "").Trim(),
                    Notes = (payload.Notes ?? "").Trim()
                });

                return Results.Json(new { ok = true, driver = saved }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "RosterSaveFailed", message = ex.Message }, statusCode: 500);
            }
        });

        r.MapPost("/vtc/roster/update", async (HttpRequest req) =>
        {
            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var guild = ResolveGuild(gidStr);
            if (guild == null) return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            if (_rosterStore == null) return Results.Json(new { ok = false, error = "RosterNotReady" }, statusCode: 503);

            RosterUpsertReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<RosterUpsertReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null || (string.IsNullOrWhiteSpace(payload.DriverId) && string.IsNullOrWhiteSpace(payload.Name)))
                return Results.Json(new { ok = false, error = "BadJsonMissingDriverIdOrName" }, statusCode: 400);

            try
            {
                var saved = _rosterStore.AddOrUpdateByName(guild.Id.ToString(), new VtcDriver
                {
                    DriverId = (payload.DriverId ?? "").Trim(),
                    Name = (payload.Name ?? "").Trim(),
                    DiscordUserId = (payload.DiscordUserId ?? "").Trim(),
                    TruckNumber = (payload.TruckNumber ?? "").Trim(),
                    Role = (payload.Role ?? "").Trim(),
                    Status = (payload.Status ?? "").Trim(),
                    Notes = (payload.Notes ?? "").Trim()
                });

                return Results.Json(new { ok = true, driver = saved }, JsonWriteOpts);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "RosterUpdateFailed", message = ex.Message }, statusCode: 500);
            }
        });

        r.MapDelete("/vtc/roster/delete", (HttpRequest req) =>
        {
            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var driverId = (req.Query["driverId"].ToString() ?? "").Trim();
            var name = (req.Query["name"].ToString() ?? "").Trim();

            var guild = ResolveGuild(gidStr);
            if (guild == null) return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            if (_rosterStore == null) return Results.Json(new { ok = false, error = "RosterNotReady" }, statusCode: 503);

            var key = !string.IsNullOrWhiteSpace(driverId) ? driverId : name;
            if (string.IsNullOrWhiteSpace(key))
                return Results.Json(new { ok = false, error = "MissingDriverIdOrName" }, statusCode: 400);

            var ok = _rosterStore.Delete(guild.Id.ToString(), key);
            return Results.Json(new { ok = ok }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Announcements feed + post
        // -----------------------------
        r.MapGet("/vtc/announcements", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var limitStr = (req.Query["limit"].ToString() ?? "25").Trim();
            if (!int.TryParse(limitStr, out var limit)) limit = 25;
            limit = Math.Clamp(limit, 1, 100);

            var guild = ResolveGuild(gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var settings = _dispatchStore?.Get(guild.Id.ToString());
            if (settings == null || !ulong.TryParse(settings.AnnouncementChannelId, out var annChId) || annChId == 0)
                return Results.Json(new { ok = true, guildId = guild.Id.ToString(), announcements = Array.Empty<object>() }, JsonWriteOpts);

            var ch = guild.GetTextChannel(annChId);
            if (ch == null)
                return Results.Json(new { ok = true, guildId = guild.Id.ToString(), announcements = Array.Empty<object>() }, JsonWriteOpts);

            var msgs = await ch.GetMessagesAsync(limit: limit).FlattenAsync();

            var announcements = msgs
                .OrderByDescending(m => m.Timestamp)
                .Select(m =>
                {
                    var atts = new List<string>();
                    try
                    {
                        foreach (var a in m.Attachments) if (!string.IsNullOrWhiteSpace(a?.Url)) atts.Add(a.Url);
                        foreach (var e in m.Embeds) if (!string.IsNullOrWhiteSpace(e?.Url)) atts.Add(e.Url);
                    }
                    catch { }

                    return new
                    {
                        text = (m.Content ?? "").Trim(),
                        author = (m.Author?.Username ?? "Announcements").Trim(),
                        createdUtc = m.Timestamp.UtcDateTime,
                        attachments = atts
                    };
                })
                .ToArray();

            return Results.Json(new { ok = true, guildId = guild.Id.ToString(), announcements }, JsonWriteOpts);
        });

        r.MapPost("/vtc/announcements/post", async (HttpRequest req) =>
        {
            AnnouncementPostReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<AnnouncementPostReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            var gidStr = (payload?.GuildId ?? "").Trim();
            var text = (payload?.Text ?? "").Trim();
            var author = (payload?.Author ?? "").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return Results.Json(new { ok = false, error = "EmptyText" }, statusCode: 400);

            var guild = ResolveGuild(gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var settings = _dispatchStore?.Get(guild.Id.ToString());
            var hookUrl = (settings?.AnnouncementWebhookUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(hookUrl))
                return Results.Json(new { ok = false, error = "AnnouncementWebhookNotSet" }, statusCode: 400);

            var content = string.IsNullOrWhiteSpace(author) ? text : $"**{author}:** {text}";
            var json = JsonSerializer.Serialize(new { username = "OverWatch ELD", content }, JsonWriteOpts);

            using var resp = await _http.PostAsync(hookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return Results.Json(new { ok = false, error = "WebhookSendFailed", status = (int)resp.StatusCode, body }, statusCode: 502);

            return Results.Json(new { ok = true }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ CRITICAL: GET /api/messages MUST return ROOT ARRAY (ELD ParseMessages requires array)
        // -----------------------------
        r.MapGet("/messages", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(Array.Empty<object>(), JsonWriteOpts);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();
            var guild = ResolveGuild(gidStr);
            if (guild == null) return Results.Json(Array.Empty<object>(), JsonWriteOpts);

            var settings = _dispatchStore?.Get(guild.Id.ToString());
            if (settings == null || !ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return Results.Json(Array.Empty<object>(), JsonWriteOpts);

            var ch = guild.GetTextChannel(dispatchChId);
            if (ch == null) return Results.Json(Array.Empty<object>(), JsonWriteOpts);

            var msgs = await ch.GetMessagesAsync(limit: 50).FlattenAsync();

            // Return oldest -> newest
            var arr = msgs
                .Where(m =>
                {
                    var txt = (m.Content ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(txt)) return false;
                    if (txt.StartsWith("!", StringComparison.OrdinalIgnoreCase)) return false; // don't feed commands into ELD
                    return true;
                })
                .OrderBy(m => m.Timestamp)
                .Select(m =>
                {
                    long createdUnix = 0;
                    try { createdUnix = m.Timestamp.ToUnixTimeSeconds(); } catch { }

                    var author = (m.Author?.Username ?? "User").Trim();
                    var content = (m.Content ?? "").Trim();

                    // ✅ Make it appear as "Dispatch" to the ELD
                    var eldText = $"[{author}] {content}";

                    return new
                    {
                        id = m.Id.ToString(),
                        createdUnix = createdUnix.ToString(),
                        sentUtc = m.Timestamp.UtcDateTime.ToString("o"),
                        fromName = "Dispatch",
                        senderName = "Dispatch",
                        text = eldText,
                        isDispatcher = true,
                        avatarUrl = ""
                    };
                })
                .ToArray();

            return Results.Json(arr, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ POST /api/messages/send (ELD -> Discord)
        // -----------------------------
        r.MapPost("/messages/send", async (HttpRequest req) =>
        {
            if (_client == null || !_discordReady)
                return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            var gidStr = (req.Query["guildId"].ToString() ?? "").Trim();

            SendMessageReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<SendMessageReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Text))
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var guild = ResolveGuild(gidStr);
            if (guild == null)
                return Results.Json(new { ok = false, error = "GuildNotFound" }, statusCode: 404);

            var who = NormalizeDisplayName(payload.DisplayName, payload.DiscordUsername);
            var text = payload.Text.Trim();

            // Prefer thread if discordUserId is known
            var discordUserIdStr = (payload.DiscordUserId ?? "").Trim();
            if (ulong.TryParse(discordUserIdStr, out var duid) && duid != 0)
            {
                var threadId = ThreadStoreTryGet(guild.Id, duid);
                if (threadId == 0)
                {
                    var created = await EnsureDriverThreadAsync(guild, duid, who);
                    if (created == 0)
                        return Results.Json(new { ok = false, error = "ThreadCreateFailedOrDispatchNotSet" }, statusCode: 500);
                    threadId = created;
                }

                var chan = await ResolveChannelAsync(threadId);
                if (chan == null)
                    return Results.Json(new { ok = false, error = "ThreadChannelNotFound" }, statusCode: 404);

                await EnsureThreadOpenAsync(chan);

                var sent = await chan.SendMessageAsync($"**{who}:** {text}");
                return Results.Json(new { ok = true, mode = "thread", threadId = threadId.ToString(), messageId = sent.Id.ToString() }, JsonWriteOpts);
            }

            // Fallback: dispatch webhook if set
            var settings = _dispatchStore?.Get(guild.Id.ToString());
            var hookUrl = (settings?.DispatchWebhookUrl ?? "").Trim();

            if (!string.IsNullOrWhiteSpace(hookUrl))
            {
                var json = JsonSerializer.Serialize(new { username = who, content = text }, JsonWriteOpts);
                using var resp = await _http.PostAsync(hookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return Results.Json(new { ok = false, error = "DispatchWebhookSendFailed", status = (int)resp.StatusCode, body }, statusCode: 502);

                return Results.Json(new { ok = true, mode = "dispatchWebhook" }, JsonWriteOpts);
            }

            // Final fallback: dispatch channel send
            if (settings == null || !ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return Results.Json(new { ok = false, error = "DispatchNotConfigured" }, statusCode: 400);

            var dispatchCh = guild.GetTextChannel(dispatchChId);
            if (dispatchCh == null)
                return Results.Json(new { ok = false, error = "DispatchChannelNotFound" }, statusCode: 404);

            var sent2 = await dispatchCh.SendMessageAsync($"**{who}:** {text}");
            return Results.Json(new { ok = true, mode = "dispatchChannel", messageId = sent2.Id.ToString() }, JsonWriteOpts);
        });

        // -----------------------------
        // ✅ Bulk mark/delete
        // -----------------------------
        r.MapPost("/messages/markread/bulk", async (HttpRequest req) =>
        {
            if (_client == null) return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            MarkBulkReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<MarkBulkReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null ||
                !ulong.TryParse(payload.ChannelId, out var channelId) ||
                payload.MessageIds == null || payload.MessageIds.Count == 0)
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await ResolveChannelAsync(channelId);
            if (chan == null) return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            int okCount = 0;
            foreach (var idStr in payload.MessageIds)
            {
                if (!ulong.TryParse(idStr, out var mid)) continue;
                try
                {
                    var msg = await chan.GetMessageAsync(mid);
                    if (msg == null) continue;
                    await msg.AddReactionAsync(new Emoji("✅"));
                    okCount++;
                }
                catch { }
            }

            return Results.Json(new { ok = true, marked = okCount }, JsonWriteOpts);
        });

        r.MapDelete("/messages/delete/bulk", async (HttpRequest req) =>
        {
            if (_client == null) return Results.Json(new { ok = false, error = "DiscordNotReady" }, statusCode: 503);

            DeleteBulkReq? payload;
            try { payload = await JsonSerializer.DeserializeAsync<DeleteBulkReq>(req.Body, JsonReadOpts); }
            catch { payload = null; }

            if (payload == null ||
                !ulong.TryParse(payload.ChannelId, out var channelId) ||
                payload.MessageIds == null || payload.MessageIds.Count == 0)
                return Results.Json(new { ok = false, error = "BadJson" }, statusCode: 400);

            var chan = await ResolveChannelAsync(channelId);
            if (chan == null) return Results.Json(new { ok = false, error = "ChannelNotFound" }, statusCode: 404);

            int okCount = 0;
            foreach (var idStr in payload.MessageIds)
            {
                if (!ulong.TryParse(idStr, out var mid)) continue;
                try { await chan.DeleteMessageAsync(mid); okCount++; } catch { }
            }

            return Results.Json(new { ok = true, deleted = okCount }, JsonWriteOpts);
        });
    }

    // -----------------------------
    // ✅ Slash Commands: /performance, /leaderboard
    // -----------------------------
    private static async Task RegisterSlashCommandsForGuildAsync(SocketGuild guild)
    {
        if (_client == null) return;

        // Guild-scoped commands = instant updates (no global propagation delay).
        // If missing permissions, this will fail silently and you can rely on prefix commands.
        try
        {
            var perf = new SlashCommandBuilder()
                .WithName("performance")
                .WithDescription("Show your current performance and rank in this VTC.");

            var leaderboard = new SlashCommandBuilder()
                .WithName("leaderboard")
                .WithDescription("Show the top drivers leaderboard for this VTC.")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("take")
                    .WithDescription("How many drivers to show (default 10, max 25).")
                    .WithType(ApplicationCommandOptionType.Integer)
                    .WithRequired(false));

            // Upsert by name (delete+create is simplest & reliable)
            // We attempt delete first; ignore failures.
            try
            {
                var existing = await guild.GetApplicationCommandsAsync();
                foreach (var cmd in existing)
                {
                    if (cmd.Name.Equals("performance", StringComparison.OrdinalIgnoreCase) ||
                        cmd.Name.Equals("leaderboard", StringComparison.OrdinalIgnoreCase))
                    {
                        try { await cmd.DeleteAsync(); } catch { }
                    }
                }
            }
            catch { }

            await guild.CreateApplicationCommandAsync(perf.Build());
            await guild.CreateApplicationCommandAsync(leaderboard.Build());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Slash register failed for guild {guild.Id}: {ex.Message}");
        }
    }

    private static async Task HandleInteractionAsync(SocketInteraction inter)
    {
        try
        {
            if (inter is SocketSlashCommand slash)
            {
                var name = (slash.Data?.Name ?? "").Trim().ToLowerInvariant();
                if (name == "performance")
                {
                    await HandleSlashPerformanceAsync(slash);
                    return;
                }
                if (name == "leaderboard")
                {
                    await HandleSlashLeaderboardAsync(slash);
                    return;
                }
            }
        }
        catch { }

        try
        {
            // If we didn't handle it, ack so Discord doesn't show "interaction failed"
            if (!inter.HasResponded)
                await inter.RespondAsync("⚠️ Command not handled.", ephemeral: true);
        }
        catch { }
    }

    private static async Task HandleSlashPerformanceAsync(SocketSlashCommand cmd)
    {
        if (_perfStore == null)
        {
            await cmd.RespondAsync("❌ Performance store not ready.", ephemeral: true);
            return;
        }

        if (cmd.GuildId == null || cmd.GuildId.Value == 0)
        {
            await cmd.RespondAsync("❌ Run this command inside your VTC server (not DM).", ephemeral: true);
            return;
        }

        var guildIdStr = cmd.GuildId.Value.ToString();
        var userIdStr = cmd.User.Id.ToString();

        var (perf, rank, total) = _perfStore.GetWithRank(guildIdStr, userIdStr);
        if (perf == null)
        {
            await cmd.RespondAsync("📊 No performance data yet for you.\nMake sure your ELD is paired and sending performance updates.", ephemeral: true);
            return;
        }

        // small “top 5” preview
        var top5 = _perfStore.GetTop(guildIdStr, 5);
        var topLines = new List<string>();
        for (int i = 0; i < top5.Count; i++)
        {
            var p = top5[i];
            var tag = p.DiscordUserId == userIdStr ? "**(you)**" : "";
            topLines.Add($"`#{i + 1}` <@{p.DiscordUserId}> — **{p.Score:n0}** {tag}");
        }

        var eb = new EmbedBuilder()
            .WithTitle("🏁 Your Performance")
            .WithDescription($"Rank **#{rank}** of **{total}** drivers\n\n**Top 5 (Score):**\n{string.Join("\n", topLines)}")
            .AddField("Week Miles", $"{perf.MilesWeek:n0}", true)
            .AddField("Week Loads", $"{perf.LoadsWeek:n0}", true)
            .AddField("Performance %", $"{perf.PerformancePct:0.0}%", true)
            .AddField("Month Miles", $"{perf.MilesMonth:n0}", true)
            .AddField("Month Loads", $"{perf.LoadsMonth:n0}", true)
            .AddField("Score", $"{perf.Score:n0}", true)
            .WithFooter($"Last updated: {perf.UpdatedUtc:yyyy-MM-dd HH:mm} UTC");

        await cmd.RespondAsync(embed: eb.Build(), ephemeral: false);
    }

    private static async Task HandleSlashLeaderboardAsync(SocketSlashCommand cmd)
    {
        if (_perfStore == null)
        {
            await cmd.RespondAsync("❌ Performance store not ready.", ephemeral: true);
            return;
        }

        if (cmd.GuildId == null || cmd.GuildId.Value == 0)
        {
            await cmd.RespondAsync("❌ Run this command inside your VTC server (not DM).", ephemeral: true);
            return;
        }

        int take = 10;
        try
        {
            var opt = cmd.Data.Options.FirstOrDefault(x => x.Name == "take");
            if (opt?.Value != null)
            {
                if (int.TryParse(opt.Value.ToString(), out var v)) take = v;
            }
        }
        catch { }

        take = Math.Clamp(take, 1, 25);

        var guildIdStr = cmd.GuildId.Value.ToString();
        var top = _perfStore.GetTop(guildIdStr, take);

        if (top.Count == 0)
        {
            await cmd.RespondAsync("📊 No performance data yet.\nOnce ELD starts sending updates, the leaderboard will populate.", ephemeral: true);
            return;
        }

        var lines = new List<string>();
        for (int i = 0; i < top.Count; i++)
        {
            var p = top[i];
            lines.Add($"`#{i + 1}` <@{p.DiscordUserId}> — **{p.Score:n0}** | {p.MilesWeek:n0} mi | {p.LoadsWeek} loads | {p.PerformancePct:0.0}%");
        }

        var eb = new EmbedBuilder()
            .WithTitle("🏆 VTC Leaderboard")
            .WithDescription(string.Join("\n", lines))
            .WithFooter("Ranking = Miles (week) + Loads (week) + Performance% (overall)");

        await cmd.RespondAsync(embed: eb.Build(), ephemeral: false);
    }

    // -----------------------------
    // ✅ Optional Pull Refresh Loop (failsafe)
    // Bot will call: GET {ELD_BASE_URL}/api/performance?guildId=...
    // Expected response: { ok:true, drivers:[ DriverPerformance ... ] } OR plain array of DriverPerformance
    // Safe no-op if ELD_BASE_URL not set.
    // -----------------------------
    private static async Task PerformancePullLoopAsync()
    {
        var baseUrl = (Environment.GetEnvironmentVariable("ELD_BASE_URL") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return; // no-op unless configured

        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return;

        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2));

                if (_client == null || !_discordReady || _perfStore == null) continue;

                foreach (var g in _client.Guilds)
                {
                    try
                    {
                        var url = $"{baseUrl.TrimEnd('/')}/api/performance?guildId={g.Id}";
                        using var resp = await _http.GetAsync(url);
                        if (!resp.IsSuccessStatusCode) continue;

                        var json = await resp.Content.ReadAsStringAsync();
                        if (string.IsNullOrWhiteSpace(json)) continue;

                        List<DriverPerformance>? drivers = null;

                        // Try wrapped object first: { ok, drivers:[...] }
                        try
                        {
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                                doc.RootElement.TryGetProperty("drivers", out var dEl) &&
                                dEl.ValueKind == JsonValueKind.Array)
                            {
                                drivers = JsonSerializer.Deserialize<List<DriverPerformance>>(dEl.GetRawText(), JsonReadOpts);
                            }
                        }
                        catch { }

                        // Try raw array fallback
                        if (drivers == null)
                        {
                            try
                            {
                                if (json.TrimStart().StartsWith("["))
                                    drivers = JsonSerializer.Deserialize<List<DriverPerformance>>(json, JsonReadOpts);
                            }
                            catch { }
                        }

                        if (drivers == null || drivers.Count == 0) continue;

                        foreach (var p in drivers)
                        {
                            if (p == null) continue;
                            p.DiscordUserId = (p.DiscordUserId ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(p.DiscordUserId)) continue;
                            p.PerformancePct = Math.Clamp(p.PerformancePct, 0, 100);
                            p.Score = PerformanceScoring.ComputeScore(p);
                            p.Source = "eld-pull";
                            _perfStore.Upsert(g.Id.ToString(), p);
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                // swallow loop errors
            }
        }
    }

    // -----------------------------
    // Discord prefix commands
    // -----------------------------
    private static async Task HandleMessageAsync(SocketMessage socketMsg)
    {
        if (_client == null) return;
        if (socketMsg is not SocketUserMessage msg) return;

        try { if (_client.CurrentUser != null && msg.Author.Id == _client.CurrentUser.Id) return; } catch { }

        if (_threadStore != null)
        {
            try
            {
                var handled = await LinkThreadCommand.TryHandleAsync(msg, _client, _threadStore);
                if (handled) return;
            }
            catch { }
        }

        var content = (msg.Content ?? "").Trim();
        if (!content.StartsWith("!")) return;

        if (content.Equals("!ping", StringComparison.OrdinalIgnoreCase))
        {
            await msg.Channel.SendMessageAsync("pong ✅");
            return;
        }

        var body0 = content.Length > 1 ? content[1..].Trim() : "";
        var parts0 = body0.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd0 = (parts0.Length > 0 ? parts0[0] : "").Trim().ToLowerInvariant();
        var arg0 = (parts0.Length > 1 ? parts0[1] : "").Trim();

        // ✅ FIXED: !link [CODE]
        // - If CODE provided: uses that code
        // - If no CODE: generates one
        // Code is stored for ELD to claim at /api/vtc/pair/claim?code=...
        if (cmd0 == "link")
        {
            if (_linkCodeStore == null)
            {
                await msg.Channel.SendMessageAsync("❌ Link store not ready.");
                return;
            }

            // Must be in a server channel so we know which guild to link
            if (msg.Channel is not SocketGuildChannel gch)
            {
                await msg.Channel.SendMessageAsync("❌ Run `!link` inside your VTC server (not DM), then paste the code into the ELD.");
                return;
            }

            var guild = gch.Guild;
            var code = (arg0 ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                code = GenerateLinkCode(6);

            var entry = new LinkCodeEntry
            {
                Code = code,
                GuildId = guild.Id.ToString(),
                GuildName = (guild.Name ?? "").Trim(),
                DiscordUserId = msg.Author.Id.ToString(),
                DiscordUsername = (msg.Author.Username ?? "Driver").Trim(),
                CreatedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30)
            };

            _linkCodeStore.Put(entry);

            await msg.Channel.SendMessageAsync(
                "✅ **ELD Pair Code:** `" + code + "`\n" +
                "Open the ELD → VTC → paste the code → click **Pair**.\n" +
                "_Code expires in 30 minutes._"
            );
            return;
        }

        if (msg.Channel is not SocketGuildChannel guildChan)
        {
            if (cmd0 == "help")
                await msg.Channel.SendMessageAsync("Use `!link` inside your server to get a pairing code for the ELD.\nAdmins: !setupdispatch / !announcement / !rosterlink");
            return;
        }

        var guild2 = guildChan.Guild;
        var guildIdStr = guild2.Id.ToString();

        var gu = msg.Author as SocketGuildUser;
        var isAdmin = gu != null && (gu.GuildPermissions.Administrator || gu.GuildPermissions.ManageGuild);

        var body = content[1..].Trim();
        var parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = (parts.Length > 0 ? parts[0] : "").Trim().ToLowerInvariant();
        var arg = (parts.Length > 1 ? parts[1] : "").Trim();

        if (cmd == "help")
        {
            await msg.Channel.SendMessageAsync(
                "Commands:\n" +
                "• !link [CODE] (driver)\n" +
                "• !setupdispatch #channel (admin)\n" +
                "• !setdispatchwebhook <url> (admin)\n" +
                "• !announcement #channel (admin)\n" +
                "• !setannouncementwebhook <url> (admin)\n" +
                "• !rosterlink @user | DriverName (admin)\n" +
                "• !rosterlist (admin)\n" +
                "• !ping\n" +
                "\nSlash Commands:\n" +
                "• /performance\n" +
                "• /leaderboard\n"
            );
            return;
        }

        if (!isAdmin)
        {
            await msg.Channel.SendMessageAsync("❌ Admin only (Manage Server/Admin required).");
            return;
        }

        if (_dispatchStore == null)
        {
            await msg.Channel.SendMessageAsync("❌ Dispatch store not initialized.");
            return;
        }

        if (cmd == "setupdispatch")
        {
            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!setupdispatch #dispatch`"); return; }

            var ch = guild2.GetTextChannel(cid.Value);
            if (ch == null) { await msg.Channel.SendMessageAsync("❌ Must be a text channel."); return; }

            try
            {
                var hook = await ch.CreateWebhookAsync("OverWatchELD Dispatch");
                var url = BuildWebhookUrl(hook);

                _dispatchStore.SetDispatchChannel(guildIdStr, ch.Id);

                if (string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync("✅ Channel set. Webhook token missing; copy URL in Discord and run `!setdispatchwebhook <url>`");
                    return;
                }

                _dispatchStore.SetDispatchWebhook(guildIdStr, url);
                await msg.Channel.SendMessageAsync($"✅ Dispatch linked: <#{ch.Id}>");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Webhook create failed (need Manage Webhooks). {ex.Message}");
            }
            return;
        }

        if (cmd == "setdispatchwebhook")
        {
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            { await msg.Channel.SendMessageAsync("Usage: `!setdispatchwebhook https://discord.com/api/webhooks/...`"); return; }

            _dispatchStore.SetDispatchWebhook(guildIdStr, arg.Trim());
            await msg.Channel.SendMessageAsync("✅ Dispatch webhook saved.");
            return;
        }

        // ✅ ROSTER: !rosterlink @user | DriverName
        if (cmd == "rosterlink")
        {
            if (_rosterStore == null)
            {
                await msg.Channel.SendMessageAsync("❌ Roster store not initialized.");
                return;
            }

            var parts2 = (arg ?? "").Split('|', 2, StringSplitOptions.RemoveEmptyEntries);
            var left = (parts2.Length > 0 ? parts2[0] : "").Trim();
            var right = (parts2.Length > 1 ? parts2[1] : "").Trim();

            var uid = TryParseUserIdFromMentionOrId(left);
            if (uid == null || uid.Value == 0)
            {
                await msg.Channel.SendMessageAsync("Usage: `!rosterLink @user | DriverName`");
                return;
            }

            var u = guild2.GetUser(uid.Value);
            var driverName = !string.IsNullOrWhiteSpace(right)
                ? right
                : ((u?.DisplayName ?? u?.Username ?? "Driver").Trim());

            if (string.IsNullOrWhiteSpace(driverName))
            {
                await msg.Channel.SendMessageAsync("❌ DriverName is required.");
                return;
            }

            try
            {
                var saved = _rosterStore.AddOrUpdateByName(guildIdStr, new VtcDriver
                {
                    Name = driverName.Trim(),
                    DiscordUserId = uid.Value.ToString()
                });

                await msg.Channel.SendMessageAsync($"✅ Roster linked: **{saved.Name}** ↔ <@{uid.Value}>");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Roster link failed: {ex.Message}");
            }
            return;
        }

        if (cmd == "rosterlist")
        {
            if (_rosterStore == null)
            {
                await msg.Channel.SendMessageAsync("❌ Roster store not initialized.");
                return;
            }

            var list = _rosterStore.List(guildIdStr)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();

            if (list.Count == 0)
            {
                await msg.Channel.SendMessageAsync("📋 Roster is empty. Use `!rosterLink @user | DriverName`");
                return;
            }

            var lines = new List<string> { "📋 **VTC Roster (top 30)**" };
            foreach (var d in list)
            {
                var link = !string.IsNullOrWhiteSpace(d.DiscordUserId) && ulong.TryParse(d.DiscordUserId, out var id) ? $"<@{id}>" : "(unlinked)";
                var extra = string.Join(" • ", new[]
                {
                    string.IsNullOrWhiteSpace(d.TruckNumber) ? null : $"Truck {d.TruckNumber}",
                    string.IsNullOrWhiteSpace(d.Role) ? null : d.Role,
                    string.IsNullOrWhiteSpace(d.Status) ? null : d.Status
                }.Where(x => !string.IsNullOrWhiteSpace(x)));

                lines.Add($"• **{d.Name}** — {link}" + (string.IsNullOrWhiteSpace(extra) ? "" : $" — {extra}"));
            }

            var text = string.Join("\n", lines);
            await msg.Channel.SendMessageAsync(text.Length > 1800 ? text.Substring(0, 1800) + "\n..." : text);
            return;
        }

        if (cmd == "announcement" || cmd == "announcements")
        {
            var cid = TryParseChannelIdFromMention(arg);
            if (cid == null) { await msg.Channel.SendMessageAsync("Usage: `!announcement #announcements`"); return; }

            var ch = guild2.GetTextChannel(cid.Value);
            if (ch == null) { await msg.Channel.SendMessageAsync("❌ Must be a text channel."); return; }

            try
            {
                var hook = await ch.CreateWebhookAsync("OverWatchELD Announcements");
                var url = BuildWebhookUrl(hook);

                _dispatchStore.SetAnnouncementChannel(guildIdStr, ch.Id);

                if (string.IsNullOrWhiteSpace(url))
                {
                    await msg.Channel.SendMessageAsync("✅ Channel set. Webhook token missing; copy URL in Discord and run `!setannouncementwebhook <url>`");
                    return;
                }

                _dispatchStore.SetAnnouncementWebhook(guildIdStr, url);
                await msg.Channel.SendMessageAsync($"✅ Announcements linked: <#{ch.Id}>");
            }
            catch (Exception ex)
            {
                await msg.Channel.SendMessageAsync($"❌ Webhook create failed (need Manage Webhooks). {ex.Message}");
            }
            return;
        }

        if (cmd == "setannouncementwebhook")
        {
            if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            { await msg.Channel.SendMessageAsync("Usage: `!setannouncementwebhook https://discord.com/api/webhooks/...`"); return; }

            _dispatchStore.SetAnnouncementWebhook(guildIdStr, arg.Trim());
            await msg.Channel.SendMessageAsync("✅ Announcement webhook saved.");
            return;
        }

        await msg.Channel.SendMessageAsync("Unknown command. Use `!help`.");
    }

    private static string GenerateLinkCode(int len)
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        len = Math.Clamp(len, 4, 12);

        var bytes = new byte[len];
        RandomNumberGenerator.Fill(bytes);

        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];

        return new string(chars);
    }

    private static SocketGuild? ResolveGuild(string gidStr)
    {
        if (_client == null) return null;

        if (ulong.TryParse((gidStr ?? "").Trim(), out var gid) && gid != 0)
        {
            var g = _client.Guilds.FirstOrDefault(x => x.Id == gid);
            if (g != null) return g;
        }

        return _client.Guilds.FirstOrDefault();
    }

    private static string NormalizeDisplayName(string? requested, string? discordUsername)
    {
        var dn = (requested ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(dn) && !dn.Equals("User", StringComparison.OrdinalIgnoreCase))
            return dn;

        var du = (discordUsername ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(du))
            return du;

        return string.IsNullOrWhiteSpace(dn) ? "User" : dn;
    }

    private static ulong? TryParseChannelIdFromMention(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("<#") && raw.EndsWith(">"))
            raw = raw.Substring(2, raw.Length - 3);
        return ulong.TryParse(raw, out var id) ? id : null;
    }

    private static string? BuildWebhookUrl(RestWebhook hook)
    {
        try
        {
            var token = (hook.Token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(token)) return null;
            return $"https://discord.com/api/webhooks/{hook.Id}/{token}";
        }
        catch { return null; }
    }

    // Thread mapping helpers (reflection-safe)
    private static ulong ThreadStoreTryGet(ulong guildId, ulong userId)
    {
        try
        {
            if (_threadStore == null) return 0;
            var t = _threadStore.GetType();

            foreach (var name in new[] { "TryGetThreadId", "GetThreadId", "TryGet", "Get" })
            {
                var mi = t.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(ulong), typeof(ulong) },
                    modifiers: null);

                if (mi == null) continue;

                var val = mi.Invoke(_threadStore, new object[] { guildId, userId });
                if (val is ulong u && u != 0) return u;
                if (val is string s && ulong.TryParse(s, out var us) && us != 0) return us;
            }
        }
        catch { }
        return 0;
    }

    private static void ThreadStoreSet(ulong guildId, ulong userId, ulong threadId)
    {
        try
        {
            if (_threadStore == null) return;
            var t = _threadStore.GetType();

            foreach (var name in new[] { "SetThreadId", "Set", "Put", "Upsert" })
            {
                var mi = t.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(ulong), typeof(ulong), typeof(ulong) },
                    modifiers: null);

                if (mi == null) continue;
                mi.Invoke(_threadStore, new object[] { guildId, userId, threadId });
                return;
            }
        }
        catch { }
    }

    private static async Task<ulong> EnsureDriverThreadAsync(SocketGuild guild, ulong discordUserId, string label)
    {
        try
        {
            if (_dispatchStore == null) return 0;

            var settings = _dispatchStore.Get(guild.Id.ToString());
            if (!ulong.TryParse(settings.DispatchChannelId, out var dispatchChId) || dispatchChId == 0)
                return 0;

            var dispatchChannel = guild.GetTextChannel(dispatchChId);
            if (dispatchChannel == null) return 0;

            var existing = ThreadStoreTryGet(guild.Id, discordUserId);
            if (existing != 0) return existing;

            var starter = await dispatchChannel.SendMessageAsync($"📌 Dispatch thread created for **{label}**.");

            var thread = await dispatchChannel.CreateThreadAsync(
                name: $"dispatch-{SanitizeThreadName(label)}",
                autoArchiveDuration: ThreadArchiveDuration.OneWeek,
                type: ThreadType.PrivateThread,
                invitable: false,
                message: starter
            );

            try
            {
                var u = guild.GetUser(discordUserId);
                if (u != null) await thread.AddUserAsync(u);
            }
            catch { }

            ThreadStoreSet(guild.Id, discordUserId, thread.Id);
            return thread.Id;
        }
        catch { return 0; }
    }

    private static string SanitizeThreadName(string s)
    {
        s = (s ?? "driver").Trim().ToLowerInvariant();
        if (s.Length > 32) s = s.Substring(0, 32);
        var safe = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "driver" : safe;
    }

    private static async Task<IMessageChannel?> ResolveChannelAsync(ulong channelId)
    {
        if (_client == null) return null;

        if (_client.GetChannel(channelId) is IMessageChannel cached)
            return cached;

        var rest = await _client.Rest.GetChannelAsync(channelId);

        if (rest is RestThreadChannel rt) return rt;
        if (rest is RestTextChannel rtxt) return rtxt;
        return rest as IMessageChannel;
    }

    private static async Task EnsureThreadOpenAsync(IMessageChannel chan)
    {
        if (chan is SocketThreadChannel st && st.IsArchived)
            await st.ModifyAsync(p => p.Archived = false);

        if (chan is RestThreadChannel rt && rt.IsArchived)
            await rt.ModifyAsync(p => p.Archived = false);
    }
}
