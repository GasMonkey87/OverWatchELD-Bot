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
                    .Where(x => string.Equals(
                        (x.GuildId ?? "").Trim(),
                        (guildId ?? "").Trim(),
                        StringComparison.OrdinalIgnoreCase))
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
                entry.Level = string.IsNullOrWhiteSpace(entry
