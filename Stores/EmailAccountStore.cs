using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace OverWatchELD.VtcBot.Stores;

public sealed class EmailAccountStore
{
private readonly string _connectionString;

```
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
        CreatedUtc TEXT NOT NULL,
        LastLoginUtc TEXT
    );
    """;

    cmd.ExecuteNonQuery();
}

public bool EmailExists(string email)
{
    using var conn = new SqliteConnection(_connectionString);
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Accounts WHERE Email=@email";
    cmd.Parameters.AddWithValue("@email", email.Trim().ToLowerInvariant());

    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
}

public bool CreateAccount(string email, string password, string displayName)
{
    email = email.Trim().ToLowerInvariant();

    if (EmailExists(email))
        return false;

    var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    var hash = Convert.ToBase64String(
        Rfc2898DeriveBytes.Pbkdf2(
            password,
            Convert.FromBase64String(salt),
            100000,
            HashAlgorithmName.SHA256,
            32));

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
    )
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
    email = email.Trim().ToLowerInvariant();

    using var conn = new SqliteConnection(_connectionString);
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM Accounts WHERE Email=@email";
    cmd.Parameters.AddWithValue("@email", email);

    using var reader = cmd.ExecuteReader();

    if (!reader.Read())
        return null;

    var salt = reader["PasswordSalt"].ToString() ?? "";
    var storedHash = reader["PasswordHash"].ToString() ?? "";

    var computedHash =
        Convert.ToBase64String(
            Rfc2898DeriveBytes.Pbkdf2(
                password,
                Convert.FromBase64String(salt),
                100000,
                HashAlgorithmName.SHA256,
                32));

    if (storedHash != computedHash)
        return null;

    return new EmailAccount
    {
        Id = reader["Id"].ToString() ?? "",
        Email = reader["Email"].ToString() ?? "",
        DisplayName = reader["DisplayName"].ToString() ?? "",
        DiscordUserId = reader["DiscordUserId"].ToString() ?? "",
        DiscordUsername = reader["DiscordUsername"].ToString() ?? ""
    };
}
```

}

public sealed class EmailAccount
{
public string Id { get; set; } = "";
public string Email { get; set; } = "";
public string DisplayName { get; set; } = "";
public string DiscordUserId { get; set; } = "";
public string DiscordUsername { get; set; } = "";
}
