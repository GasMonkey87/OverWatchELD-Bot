using System;
using System.IO;
using System.Text.Json;

namespace OverWatchELD.VtcBot.Stores
{
    public sealed class DriverScoreRecord
    {
        public string GuildId { get; set; } = "";
        public string DiscordUserId { get; set; } = "";
        public string DriverName { get; set; } = "";
        public int Score { get; set; } = 100;
        public int SpeedingEvents { get; set; }
        public int InspectionDefects { get; set; }
        public int HosViolations { get; set; }
        public int MissedPreTrips { get; set; }
        public string Notes { get; set; } = "";
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    public static class DriverScoreStore
    {
        private static readonly object Gate = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static string Folder =>
            Path.Combine(AppContext.BaseDirectory, "data", "driver-scores");

        public static void Save(
            string guildId,
            string discordUserId,
            DriverScoreRecord record)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);

                File.WriteAllText(
                    Path.Combine(Folder, $"{guildId}_{discordUserId}.json"),
                    JsonSerializer.Serialize(record, JsonOptions));
            }
        }

        public static DriverScoreRecord? Load(
            string guildId,
            string discordUserId)
        {
            lock (Gate)
            {
                var path = Path.Combine(
                    Folder,
                    $"{guildId}_{discordUserId}.json");

                if (!File.Exists(path))
                    return null;

                return JsonSerializer.Deserialize<DriverScoreRecord>(
                    File.ReadAllText(path),
                    JsonOptions);
            }
        }
    }
}
