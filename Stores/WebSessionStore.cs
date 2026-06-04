using Microsoft.Data.Sqlite;
using OverWatchELD.VtcBot.Models;

namespace OverWatchELD.VtcBot.Stores;

public sealed class WebSessionStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public WebSessionStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory);
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS WebSessions
(
    SessionId TEXT PRIMARY KEY,
    AccountId TEXT NOT NULL DEFAULT '',
    Email TEXT NOT NULL DEFAULT '',
    IsEmailAccount INTEGER NOT NULL DEFAULT 0,
    DiscordUserId TEXT NOT NULL DEFAULT '',
    Username TEXT NOT NULL DEFAULT '',
    GlobalName TEXT NOT NULL DEFAULT '',
    AccessToken TEXT NOT NULL DEFAULT '',
    ExpiresUtc TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_WebSessions_ExpiresUtc ON WebSessions(ExpiresUtc);
";
            cmd.ExecuteNonQuery();
        }
    }

    public void Save(string sessionId, WebSessionUser user)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || user == null)
            return;

        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO WebSessions
(
    SessionId,
    AccountId,
    Email,
    IsEmailAccount,
    DiscordUserId,
    Username,
    GlobalName,
    AccessToken,
    ExpiresUtc,
    CreatedUtc,
    UpdatedUtc
)
VALUES
(
    @sessionId,
    @accountId,
    @email,
    @isEmailAccount,
    @discordUserId,
    @username,
    @globalName,
    @accessToken,
    @expiresUtc,
    @createdUtc,
    @updatedUtc
)
ON CONFLICT(SessionId) DO UPDATE SET
    AccountId = excluded.AccountId,
    Email = excluded.Email,
    IsEmailAccount = excluded.IsEmailAccount,
    DiscordUserId = excluded.DiscordUserId,
    Username = excluded.Username,
    GlobalName = excluded.GlobalName,
    AccessToken = excluded.AccessToken,
    ExpiresUtc = excluded.ExpiresUtc,
    UpdatedUtc = excluded.UpdatedUtc;
";

            var now = DateTimeOffset.UtcNow.ToString("O");
            cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());
            cmd.Parameters.AddWithValue("@accountId", user.AccountId ?? "");
            cmd.Parameters.AddWithValue("@email", user.Email ?? "");
            cmd.Parameters.AddWithValue("@isEmailAccount", user.IsEmailAccount ? 1 : 0);
            cmd.Parameters.AddWithValue("@discordUserId", user.DiscordUserId ?? "");
            cmd.Parameters.AddWithValue("@username", user.Username ?? "");
            cmd.Parameters.AddWithValue("@globalName", user.GlobalName ?? "");
            cmd.Parameters.AddWithValue("@accessToken", user.AccessToken ?? "");
            cmd.Parameters.AddWithValue("@expiresUtc", user.ExpiresUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@createdUtc", now);
            cmd.Parameters.AddWithValue("@updatedUtc", now);

            cmd.ExecuteNonQuery();
        }
    }

    public bool TryGet(string sessionId, out WebSessionUser? user)
    {
        user = null;

        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    SessionId,
    AccountId,
    Email,
    IsEmailAccount,
    DiscordUserId,
    Username,
    GlobalName,
    AccessToken,
    ExpiresUtc
FROM WebSessions
WHERE SessionId = @sessionId
LIMIT 1;
";
            cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return false;

            var expiresText = reader["ExpiresUtc"].ToString() ?? "";
            if (!DateTimeOffset.TryParse(expiresText, out var expiresUtc) || expiresUtc <= DateTimeOffset.UtcNow)
            {
                Remove(sessionId);
                return false;
            }

            user = new WebSessionUser
            {
                AccountId = reader["AccountId"].ToString() ?? "",
                Email = reader["Email"].ToString() ?? "",
                IsEmailAccount = Convert.ToInt32(reader["IsEmailAccount"]) == 1,
                DiscordUserId = reader["DiscordUserId"].ToString() ?? "",
                Username = reader["Username"].ToString() ?? "",
                GlobalName = reader["GlobalName"].ToString() ?? "",
                AccessToken = reader["AccessToken"].ToString() ?? "",
                ExpiresUtc = expiresUtc
            };

            return true;
        }
    }

    public void Remove(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WebSessions WHERE SessionId = @sessionId;";
            cmd.Parameters.AddWithValue("@sessionId", sessionId.Trim());
            cmd.ExecuteNonQuery();
        }
    }

    public int CleanupExpired()
    {
        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM WebSessions WHERE ExpiresUtc <= @now;";
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            return cmd.ExecuteNonQuery();
        }
    }
}
