using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace OverWatchELD.VtcBot
{
    internal static class RosterStore
    {
        private static readonly object _lock = new();

        private static string DbPath
            => Path.Combine(AppContext.BaseDirectory, "bot.db");

        private static string ConnString
            => new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();

        public sealed class RosterDriver
        {
            public string Name { get; set; } = "";
            public string Role { get; set; } = "Driver";
            public string DiscordUserId { get; set; } = "";
            public string DiscordUserName { get; set; } = "";
            public DateTimeOffset LinkedUtc { get; set; }
        }

        public static void Init()
        {
            lock (_lock)
            {
                Directory.CreateDirectory(AppContext.BaseDirectory);

                using var con = new SqliteConnection(ConnString);
                con.Open();

                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText =
@"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS linked_drivers (
  discord_user_id   TEXT PRIMARY KEY,
  discord_user_name TEXT NOT NULL,
  driver_name       TEXT NOT NULL,
  role              TEXT NOT NULL DEFAULT 'Driver',
  linked_utc        TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_linked_drivers_name ON linked_drivers(driver_name);
";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Upserts a linked driver. Call this AFTER a successful !link verification.
        /// </summary>
        public static void UpsertLinkedDriver(string discordUserId, string discordUserName, string driverName, string role = "Driver")
        {
            if (string.IsNullOrWhiteSpace(discordUserId)) return;
            if (string.IsNullOrWhiteSpace(driverName)) return;

            discordUserName = (discordUserName ?? "").Trim();
            driverName = (driverName ?? "").Trim();
            role = string.IsNullOrWhiteSpace(role) ? "Driver" : role.Trim();

            lock (_lock)
            {
                using var con = new SqliteConnection(ConnString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText =
@"
INSERT INTO linked_drivers(discord_user_id, discord_user_name, driver_name, role, linked_utc)
VALUES($uid, $uname, $dname, $role, $utc)
ON CONFLICT(discord_user_id) DO UPDATE SET
  discord_user_name = excluded.discord_user_name,
  driver_name       = excluded.driver_name,
  role              = excluded.role,
  linked_utc        = excluded.linked_utc;
";
                cmd.Parameters.AddWithValue("$uid", discordUserId.Trim());
                cmd.Parameters.AddWithValue("$uname", discordUserName);
                cmd.Parameters.AddWithValue("$dname", driverName);
                cmd.Parameters.AddWithValue("$role", role);
                cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        public static List<RosterDriver> GetRosterDrivers()
        {
            var list = new List<RosterDriver>();

            lock (_lock)
            {
                using var con = new SqliteConnection(ConnString);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText =
@"
SELECT discord_user_id, discord_user_name, driver_name, role, linked_utc
FROM linked_drivers
ORDER BY driver_name COLLATE NOCASE ASC;
";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var linkedUtcStr = r.GetString(4);

                    list.Add(new RosterDriver
                    {
                        DiscordUserId = r.GetString(0),
                        DiscordUserName = r.GetString(1),
                        Name = r.GetString(2),
                        Role = r.GetString(3),
                        LinkedUtc = DateTimeOffset.TryParse(linkedUtcStr, out var dto) ? dto : DateTimeOffset.UtcNow
                    });
                }
            }

            return list;
        }
    }
}