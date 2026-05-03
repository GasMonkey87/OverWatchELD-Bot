using Npgsql;

namespace OverWatchELD.VtcBot.Stores;

public sealed class GuildSettings
{
    public string GuildId { get; set; } = "";
    public string VtcName { get; set; } = "";
    public string DispatchChannelId { get; set; } = "";
    public string LogsChannelId { get; set; } = "";
    public string InspectionsChannelId { get; set; } = "";
    public string AnnouncementsChannelId { get; set; } = "";
    public string BolChannelId { get; set; } = "";
    public string LoadboardChannelId { get; set; } = "";
    public bool UseLoadThreads { get; set; } = true;
    public bool AutoArchiveCompletedLoads { get; set; } = true;
}

public sealed class GuildSettingsStore
{
    private readonly string _connectionString;

    public GuildSettingsStore(IConfiguration config)
    {
        _connectionString =
            config["DATABASE_URL"]
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "";
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_connectionString);

    private string ConnectionString
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
                throw new InvalidOperationException("DATABASE_URL is not configured.");

            return ConvertRailwayDatabaseUrl(_connectionString);
        }
    }

    public async Task EnsureCreatedAsync()
    {
        if (!Enabled) return;

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sql = """
        CREATE TABLE IF NOT EXISTS guild_settings (
            guild_id TEXT PRIMARY KEY,
            vtc_name TEXT DEFAULT '',
            dispatch_channel_id TEXT DEFAULT '',
            logs_channel_id TEXT DEFAULT '',
            inspections_channel_id TEXT DEFAULT '',
            announcements_channel_id TEXT DEFAULT '',
            bol_channel_id TEXT DEFAULT '',
            loadboard_channel_id TEXT DEFAULT '',
            use_load_threads BOOLEAN DEFAULT TRUE,
            auto_archive_completed_loads BOOLEAN DEFAULT TRUE,
            updated_utc TIMESTAMPTZ DEFAULT NOW()
        );
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<GuildSettings> GetAsync(string guildId)
    {
        guildId = (guildId ?? "").Trim();

        if (string.IsNullOrWhiteSpace(guildId))
            return new GuildSettings();

        await EnsureCreatedAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sql = """
        SELECT guild_id, vtc_name, dispatch_channel_id, logs_channel_id,
               inspections_channel_id, announcements_channel_id, bol_channel_id,
               loadboard_channel_id, use_load_threads, auto_archive_completed_loads
        FROM guild_settings
        WHERE guild_id = @guild_id;
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("guild_id", guildId);

        await using var r = await cmd.ExecuteReaderAsync();

        if (!await r.ReadAsync())
            return new GuildSettings { GuildId = guildId };

        return new GuildSettings
        {
            GuildId = r.GetString(0),
            VtcName = r.GetString(1),
            DispatchChannelId = r.GetString(2),
            LogsChannelId = r.GetString(3),
            InspectionsChannelId = r.GetString(4),
            AnnouncementsChannelId = r.GetString(5),
            BolChannelId = r.GetString(6),
            LoadboardChannelId = r.GetString(7),
            UseLoadThreads = r.GetBoolean(8),
            AutoArchiveCompletedLoads = r.GetBoolean(9)
        };
    }

    public async Task UpsertAsync(GuildSettings s)
    {
        if (s == null || string.IsNullOrWhiteSpace(s.GuildId))
            return;

        await EnsureCreatedAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sql = """
        INSERT INTO guild_settings (
            guild_id, vtc_name, dispatch_channel_id, logs_channel_id,
            inspections_channel_id, announcements_channel_id, bol_channel_id,
            loadboard_channel_id, use_load_threads, auto_archive_completed_loads,
            updated_utc
        )
        VALUES (
            @guild_id, @vtc_name, @dispatch_channel_id, @logs_channel_id,
            @inspections_channel_id, @announcements_channel_id, @bol_channel_id,
            @loadboard_channel_id, @use_load_threads, @auto_archive_completed_loads,
            NOW()
        )
        ON CONFLICT (guild_id)
        DO UPDATE SET
            vtc_name = EXCLUDED.vtc_name,
            dispatch_channel_id = EXCLUDED.dispatch_channel_id,
            logs_channel_id = EXCLUDED.logs_channel_id,
            inspections_channel_id = EXCLUDED.inspections_channel_id,
            announcements_channel_id = EXCLUDED.announcements_channel_id,
            bol_channel_id = EXCLUDED.bol_channel_id,
            loadboard_channel_id = EXCLUDED.loadboard_channel_id,
            use_load_threads = EXCLUDED.use_load_threads,
            auto_archive_completed_loads = EXCLUDED.auto_archive_completed_loads,
            updated_utc = NOW();
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("guild_id", s.GuildId);
        cmd.Parameters.AddWithValue("vtc_name", s.VtcName ?? "");
        cmd.Parameters.AddWithValue("dispatch_channel_id", s.DispatchChannelId ?? "");
        cmd.Parameters.AddWithValue("logs_channel_id", s.LogsChannelId ?? "");
        cmd.Parameters.AddWithValue("inspections_channel_id", s.InspectionsChannelId ?? "");
        cmd.Parameters.AddWithValue("announcements_channel_id", s.AnnouncementsChannelId ?? "");
        cmd.Parameters.AddWithValue("bol_channel_id", s.BolChannelId ?? "");
        cmd.Parameters.AddWithValue("loadboard_channel_id", s.LoadboardChannelId ?? "");
        cmd.Parameters.AddWithValue("use_load_threads", s.UseLoadThreads);
        cmd.Parameters.AddWithValue("auto_archive_completed_loads", s.AutoArchiveCompletedLoads);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PatchAsync(string guildId, Action<GuildSettings> patch)
    {
        var s = await GetAsync(guildId);
        s.GuildId = guildId;
        patch(s);
        await UpsertAsync(s);
    }

    private static string ConvertRailwayDatabaseUrl(string url)
    {
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
            SslMode = SslMode.Require,
            TrustServerCertificate = true
        };

        return builder.ConnectionString;
    }
}
