using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OverWatchELD.VtcBot.Stores
{
    public sealed class DriverDisciplineStore
    {
        private readonly string _path;
        private readonly JsonSerializerOptions _readOpts;
        private readonly JsonSerializerOptions _writeOpts;
        private readonly object _gate = new();

        public DriverDisciplineStore(string path, JsonSerializerOptions readOpts, JsonSerializerOptions writeOpts)
        {
            _path = path;
            _readOpts = readOpts;
            _writeOpts = writeOpts;

            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_path))
                SaveInternal(new List<DriverDisciplineEntry>());
        }

        public sealed class DriverDisciplineEntry
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string GuildId { get; set; } = "";
            public string DriverDiscordUserId { get; set; } = "";
            public string DriverName { get; set; } = "";
            public string Level { get; set; } = "warning";
            public string Category { get; set; } = "";
            public string Reason { get; set; } = "";
            public string ActionTaken { get; set; } = "";
            public string CreatedBy { get; set; } = "";
            public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
            public bool IsActive { get; set; } = true;
            public string ResolutionNotes { get; set; } = "";
            public DateTimeOffset? ResolvedUtc { get; set; }
        }

        public List<DriverDisciplineEntry> List(string guildId)
        {
            lock (_gate)
            {
                return LoadInternal()
                    .Where(x => string.Equals((x.GuildId ?? "").Trim(), (guildId ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CreatedUtc)
                    .ToList();
            }
        }

        public List<DriverDisciplineEntry> ListForDriver(string guildId, string driverDiscordUserId)
        {
            lock (_gate)
            {
                return LoadInternal()
                    .Where(x =>
                        string.Equals((x.GuildId ?? "").Trim(), (guildId ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals((x.DriverDiscordUserId ?? "").Trim(), (driverDiscordUserId ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CreatedUtc)
                    .ToList();
            }
        }

        public DriverDisciplineEntry Add(DriverDisciplineEntry entry)
        {
            lock (_gate)
            {
                var rows = LoadInternal();

                entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id.Trim();
                entry.GuildId = (entry.GuildId ?? "").Trim();
                entry.DriverDiscordUserId = (entry.DriverDiscordUserId ?? "").Trim();
                entry.DriverName = (entry.DriverName ?? "").Trim();
                entry.Level = string.IsNullOrWhiteSpace(entry.Level) ? "warning" : entry.Level.Trim();
                entry.Category = (entry.Category ?? "").Trim();
                entry.Reason = (entry.Reason ?? "").Trim();
                entry.ActionTaken = (entry.ActionTaken ?? "").Trim();
                entry.CreatedBy = (entry.CreatedBy ?? "").Trim();
                entry.CreatedUtc = entry.CreatedUtc == default ? DateTimeOffset.UtcNow : entry.CreatedUtc;

                rows.Add(entry);
                SaveInternal(rows);
                return entry;
            }
        }

        public DriverDisciplineEntry? Resolve(string guildId, string id, string resolutionNotes)
        {
            lock (_gate)
            {
                var rows = LoadInternal();

                var row = rows.FirstOrDefault(x =>
                    string.Equals((x.GuildId ?? "").Trim(), (guildId ?? "").Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((x.Id ?? "").Trim(), (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

                if (row == null)
                    return null;

                row.IsActive = false;
                row.ResolutionNotes = (resolutionNotes ?? "").Trim();
                row.ResolvedUtc = DateTimeOffset.UtcNow;

                SaveInternal(rows);
                return row;
            }
        }

        private List<DriverDisciplineEntry> LoadInternal()
        {
            try
            {
                if (!File.Exists(_path))
                    return new List<DriverDisciplineEntry>();

                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<DriverDisciplineEntry>();

                return JsonSerializer.Deserialize<List<DriverDisciplineEntry>>(json, _readOpts)
                       ?? new List<DriverDisciplineEntry>();
            }
            catch
            {
                return new List<DriverDisciplineEntry>();
            }
        }

        private void SaveInternal(List<DriverDisciplineEntry> rows)
        {
            var json = JsonSerializer.Serialize(rows, _writeOpts);
            File.WriteAllText(_path, json);
        }
    }
}
