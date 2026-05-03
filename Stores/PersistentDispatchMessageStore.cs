using Npgsql;

namespace OverWatchELD.VtcBot.Stores;

public sealed class PersistentDispatchMessage
{
    public string Id { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string DriverDiscordUserId { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string FromDiscordUserId { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PersistentDispatchMessageStore
{
    private readonly string _connectionString;

    public PersistentDispatchMessageStore(IConfiguration config)
    {
        _connectionString =
            config["DATABASE_URL"]
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "";
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_connectionString);

    private string ConnectionString => ConvertRailwayDatabaseUrl(_connectionString);

    public async Task EnsureCreatedAsync()
    {
        if (!Enabled) return;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sql = """
        CREATE TABLE IF NOT EXISTS dispatch_messages (
            id TEXT PRIMARY KEY,
            guild_id TEXT NOT NULL,
            thread_id TEXT DEFAULT '',
            driver_discord_user_id TEXT DEFAULT '',
            driver_name TEXT DEFAULT '',
            from_discord_user_id TEXT DEFAULT '',
            from_name TEXT DEFAULT '',
            direction TEXT DEFAULT '',
            text TEXT DEFAULT '',
            created_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE INDEX IF NOT EXISTS idx_dispatch_messages_guild_created
        ON dispatch_messages (guild_id, created_utc);

        CREATE INDEX IF NOT EXISTS idx_dispatch_messages_thread
        ON dispatch_messages (thread_id);
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<PersistentDispatchMessage> SaveAsync(PersistentDispatchMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.Id))
            m.Id = Guid.NewGuid().ToString("N");

        await EnsureCreatedAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sql = """
        INSERT INTO dispatch_messages (
            id, guild_id, thread_id, driver_discord_user_id, driver_name,
            from_discord_user_id, from_name, direction, text, created_utc
        )
        VALUES (
            @id, @guild_id, @thread_id, @driver_discord_user_id, @driver_name,
            @from_discord_user_id, @from_name, @direction, @text, @created_utc
        )
        ON CONFLICT (id) DO NOTHING;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", m.Id ?? "");
        cmd.Parameters.AddWithValue("guild_id", m.GuildId ?? "");
        cmd.Parameters.AddWithValue("thread_id", m.ThreadId ?? "");
        cmd.Parameters.AddWithValue("driver_discord_user_id", m.DriverDiscordUserId ?? "");
        cmd.Parameters.AddWithValue("driver_name", m.DriverName ?? "");
        cmd.Parameters.AddWithValue("from_discord_user_id", m.FromDiscordUserId ?? "");
        cmd.Parameters.AddWithValue("from_name", m.FromName ?? "");
        cmd.Parameters.AddWithValue("direction", m.Direction ?? "");
        cmd.Parameters.AddWithValue("text", m.Text ?? "");
        cmd.Parameters.AddWithValue("created_utc", m.CreatedUtc);

        await cmd.ExecuteNonQueryAsync();
        return m;
    }

    public async Task<List<PersistentDispatchMessage>> ListAsync(string guildId, int take = 500)
    {
        await EnsureCreatedAsync();

        var list = new List<PersistentDispatchMessage>();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sql = """
        SELECT id, guild_id, thread_id, driver_discord_user_id, driver_name,
               from_discord_user_id, from_name, direction, text, created_utc
        FROM dispatch_messages
        WHERE guild_id = @guild_id
        ORDER BY created_utc ASC
        LIMIT @take;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("guild_id", guildId ?? "");
        cmd.Parameters.AddWithValue("take", take);

        await using var r = await cmd.ExecuteReaderAsync();

        while (await r.ReadAsync())
        {
            list.Add(new PersistentDispatchMessage
            {
                Id = r.GetString(0),
                GuildId = r.GetString(1),
                ThreadId = r.GetString(2),
                DriverDiscordUserId = r.GetString(3),
                DriverName = r.GetString(4),
                FromDiscordUserId = r.GetString(5),
                FromName = r.GetString(6),
                Direction = r.GetString(7),
                Text = r.GetString(8),
                CreatedUtc = r.GetFieldValue<DateTimeOffset>(9)
            });
        }

        return list;
    }

    private static string ConvertRailwayDatabaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return url;

        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Require
        };

        return builder.ConnectionString;
    }
}
