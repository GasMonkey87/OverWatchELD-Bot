using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace OverWatchELD.VtcBot.Stores;

public sealed class EmailAccountStore
{
    private readonly string _connectionString;

    public EmailAccountStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory);
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText =
        """
        CREATE TABLE IF NOT EXISTS Accounts
        (
            Id TEXT PRIMARY KEY,
            Email TEXT NOT NULL UNIQUE,
            DisplayName TEXT NOT NULL,
            PasswordHash TEXT NOT NULL,
            PasswordSalt TEXT NOT NULL,
            DiscordUserId TEXT,
            DiscordUsername TEXT,
            DiscordGlobalName TEXT,
            CreatedUtc TEXT NOT NULL,
            LastLoginUtc TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_Accounts_Email ON Accounts(Email);
        CREATE INDEX IF NOT EXISTS IX_Accounts_DiscordUserId ON Accounts(DiscordUserId);
        """;
        cmd.ExecuteNonQuery();

        EnsureColumn(conn, "DiscordGlobalName", "TEXT");
    }

    private static void EnsureColumn(SqliteConnection conn, string columnName, string columnType)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(Accounts);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
                existing.Add(reader["name"]?.ToString() ?? "");
        }

        if (existing.Contains(columnName))
            return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE Accounts ADD COLUMN {columnName} {columnType};";
        alter.ExecuteNonQuery();
    }

    public bool EmailExists(string email)
    {
        email = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(email))
            return false;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Accounts WHERE Email=@email";
        cmd.Parameters.AddWithValue("@email", email);

        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public bool CreateAccount(string email, string password, string displayName)
    {
        email = NormalizeEmail(email);
        displayName = (displayName ?? "").Trim();

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("EmailRequired");

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new InvalidOperationException("PasswordMustBeAtLeast8Characters");

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = email.Split('@')[0];

        if (EmailExists(email))
            return false;

        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var hash = HashPassword(password, salt);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText =
        """
        INSERT INTO Accounts
        (
            Id,
            Email,
            DisplayName,
            PasswordHash,
            PasswordSalt,
            CreatedUtc
        )
        VALUES
        (
            @id,
            @email,
            @display,
            @hash,
            @salt,
            @created
        );
        """;

        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@email", email);
        cmd.Parameters.AddWithValue("@display", displayName);
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.Parameters.AddWithValue("@salt", salt);
        cmd.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        return true;
    }

    public EmailAccount? ValidateLogin(string email, string password)
    {
        email = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return null;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Accounts WHERE Email=@email LIMIT 1";
        cmd.Parameters.AddWithValue("@email", email);

        using var reader = cmd.ExecuteReader();

        if (!reader.Read())
            return null;

        var salt = reader["PasswordSalt"].ToString() ?? "";
        var storedHash = reader["PasswordHash"].ToString() ?? "";
        var computedHash = HashPassword(password, salt);

        if (!FixedTimeEqualsBase64(storedHash, computedHash))
            return null;

        var account = ReadAccount(reader);

        reader.Close();

        using var update = conn.CreateCommand();
        update.CommandText = "UPDATE Accounts SET LastLoginUtc=@lastLogin WHERE Id=@id";
        update.Parameters.AddWithValue("@lastLogin", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("@id", account.Id);
        update.ExecuteNonQuery();

        return account;
    }

    public EmailAccount? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Accounts WHERE Id=@id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", id.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    public EmailAccount? FindByDiscordUserId(string discordUserId)
    {
        if (string.IsNullOrWhiteSpace(discordUserId))
            return null;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Accounts WHERE DiscordUserId=@discordUserId LIMIT 1";
        cmd.Parameters.AddWithValue("@discordUserId", discordUserId.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    public EmailAccount? LinkDiscord(string accountId, string discordUserId, string discordUsername, string? discordGlobalName)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(discordUserId))
            return null;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText =
        """
        UPDATE Accounts
        SET DiscordUserId=@discordUserId,
            DiscordUsername=@discordUsername,
            DiscordGlobalName=@discordGlobalName
        WHERE Id=@accountId;
        """;

        cmd.Parameters.AddWithValue("@discordUserId", discordUserId.Trim());
        cmd.Parameters.AddWithValue("@discordUsername", (discordUsername ?? "").Trim());
        cmd.Parameters.AddWithValue("@discordGlobalName", (discordGlobalName ?? "").Trim());
        cmd.Parameters.AddWithValue("@accountId", accountId.Trim());
        cmd.ExecuteNonQuery();

        return FindById(accountId);
    }

    private static EmailAccount ReadAccount(SqliteDataReader reader)
    {
        return new EmailAccount
        {
            Id = reader["Id"].ToString() ?? "",
            Email = reader["Email"].ToString() ?? "",
            DisplayName = reader["DisplayName"].ToString() ?? "",
            DiscordUserId = reader["DiscordUserId"].ToString() ?? "",
            DiscordUsername = reader["DiscordUsername"].ToString() ?? "",
            DiscordGlobalName = HasColumn(reader, "DiscordGlobalName") ? reader["DiscordGlobalName"].ToString() ?? "" : "",
            CreatedUtc = TryParseDate(reader["CreatedUtc"].ToString()),
            LastLoginUtc = TryParseNullableDate(reader["LastLoginUtc"].ToString())
        };
    }

    private static bool HasColumn(SqliteDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizeEmail(string email) => (email ?? "").Trim().ToLowerInvariant();

    private static string HashPassword(string password, string saltBase64)
    {
        var salt = Convert.FromBase64String(saltBase64);

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            210_000,
            HashAlgorithmName.SHA256,
            32);

        return Convert.ToBase64String(hash);
    }

    private static bool FixedTimeEqualsBase64(string a, string b)
    {
        try
        {
            var left = Convert.FromBase64String(a);
            var right = Convert.FromBase64String(b);
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset TryParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }

    private static DateTimeOffset? TryParseNullableDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}

public sealed class EmailAccount
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public string DiscordUsername { get; set; } = "";
    public string DiscordGlobalName { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
}
