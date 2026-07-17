using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

internal static class EmployeePasswordResetSupport
{
    public static async Task EnsureTableAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS bee_CrmEmployeePasswordReset (
                id BIGINT NOT NULL AUTO_INCREMENT,
                ProjectId INT NOT NULL,
                MerchantId BIGINT NOT NULL,
                EmployeeId BIGINT NOT NULL,
                TokenHash CHAR(64) NOT NULL,
                ExpiresAtUtc DATETIME(6) NOT NULL,
                UsedAtUtc DATETIME(6) NULL,
                RequestedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
                RequestIp VARCHAR(80) NULL,
                PRIMARY KEY (id),
                UNIQUE KEY UX_bee_CrmEmployeePasswordReset_TokenHash (TokenHash),
                KEY IX_bee_CrmEmployeePasswordReset_Employee (EmployeeId, UsedAtUtc, ExpiresAtUtc),
                KEY IX_bee_CrmEmployeePasswordReset_Merchant (MerchantId, RequestedAtUtc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
            """;
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed record EmployeePasswordResetTarget(
    long EmployeeId,
    long MerchantId,
    int ProjectId,
    string WorkEmail,
    string DisplayName,
    string BusinessName);
