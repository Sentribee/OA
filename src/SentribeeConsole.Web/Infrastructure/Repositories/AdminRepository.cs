using MySqlConnector;
using System.Data;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Infrastructure.Repositories;

public sealed class AdminRepository(IConfiguration configuration) : IAdminRepository
{
    private readonly string _connectionString =
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException(
            "Connection string 'DefaultConnection' is not configured. Set ConnectionStrings__DefaultConnection.");

    public async Task<AdminUser?> FindByLoginIdAsync(
        string loginId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, LoginID, Pwd, Roles, LastLoginTime,
                DisplayName, Email, AvatarUrl
            FROM bee_Admin
            WHERE LoginID = @LoginId
            LIMIT 1;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@LoginId", MySqlDbType.VarChar, 50).Value = loginId;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapAdmin(reader);
    }

    public async Task<AdminUser?> FindByEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, LoginID, Pwd, Roles, LastLoginTime,
                DisplayName, Email, AvatarUrl
            FROM bee_Admin
            WHERE Email = @Email
            LIMIT 1;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAdmin(reader) : null;
    }

    public async Task<AdminUser?> FindByIdAsync(int id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, LoginID, Pwd, Roles, LastLoginTime,
                DisplayName, Email, AvatarUrl
            FROM bee_Admin
            WHERE id = @Id
            LIMIT 1;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@Id", MySqlDbType.Int32).Value = id;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAdmin(reader) : null;
    }

    public async Task UpdateSuccessfulLoginAsync(
        int id,
        string? upgradedPasswordHash,
        DateTime loginTimeUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_Admin
            SET LastLoginTime = @LastLoginTime,
                Pwd = CASE WHEN @PasswordHash IS NULL THEN Pwd ELSE @PasswordHash END
            WHERE id = @Id;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@Id", MySqlDbType.Int32).Value = id;
        command.Parameters.Add("@LastLoginTime", MySqlDbType.DateTime).Value = loginTimeUtc;
        command.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 256).Value =
            (object?)upgradedPasswordHash ?? DBNull.Value;

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateProfileAsync(
        int id,
        string displayName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_Admin
            SET DisplayName = @DisplayName
            WHERE id = @Id;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@Id", MySqlDbType.Int32).Value = id;
        command.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 100).Value = displayName;

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAvatarAsync(int id, string avatarUrl, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_Admin
            SET AvatarUrl = @AvatarUrl
            WHERE id = @Id;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@Id", MySqlDbType.Int32).Value = id;
        command.Parameters.Add("@AvatarUrl", MySqlDbType.VarChar, 500).Value = avatarUrl;

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdatePasswordAsync(int id, string passwordHash, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bee_Admin
            SET Pwd = @PasswordHash
            WHERE id = @Id;
            """;

        await using var connection = new MySqlConnection(_connectionString);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@Id", MySqlDbType.Int32).Value = id;
        command.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 512).Value = passwordHash;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AdminUser MapAdmin(MySqlDataReader reader)
    {
        return new AdminUser
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            LoginId = reader["LoginID"] as string ?? string.Empty,
            StoredPassword = reader["Pwd"] as string ?? string.Empty,
            Roles = reader["Roles"] as string,
            LastLoginTime = reader.IsDBNull(reader.GetOrdinal("LastLoginTime"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("LastLoginTime")),
            DisplayName = reader["DisplayName"] as string,
            Email = reader["Email"] as string,
            AvatarUrl = reader["AvatarUrl"] as string
        };
    }
}
