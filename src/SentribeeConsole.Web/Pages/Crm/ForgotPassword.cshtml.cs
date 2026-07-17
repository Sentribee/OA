using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Crm;

public class ForgotPasswordModel(
    IConfiguration configuration,
    IConsoleEmailService emailService) : CrmMerchantPageModel(configuration)
{
    private const string GenericSuccessMessage = "If this staff email exists, a password reset link has been sent.";

    [BindProperty]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    public string? AlertMessage { get; private set; }

    public void OnGet()
    {
        AlertMessage = TempData["CrmPasswordResetStatus"] as string;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            AlertMessage = "Enter your staff work email.";
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EmployeePasswordResetSupport.EnsureTableAsync(connection, cancellationToken);
        var projectId = await GetCrmProjectIdAsync(connection, cancellationToken);
        var target = await FindEmployeeAsync(connection, projectId, Email.Trim(), cancellationToken);
        if (target is not null)
        {
            var token = EmployeePasswordResetSupport.CreateToken();
            await SaveResetTokenAsync(connection, target, token, cancellationToken);
            var resetUrl = BuildResetUrl(token);
            await emailService.SendEmployeePasswordResetAsync(
                target.WorkEmail,
                target.BusinessName,
                target.DisplayName,
                resetUrl,
                cancellationToken);
        }

        TempData["CrmPasswordResetStatus"] = GenericSuccessMessage;
        return RedirectToPage("/Crm/ForgotPassword");
    }

    private static async Task<EmployeePasswordResetTarget?> FindEmployeeAsync(
        MySqlConnection connection,
        int projectId,
        string email,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT employee.id AS EmployeeId, employee.MerchantId, merchant.ProjectId,
                employee.WorkEmail,
                COALESCE(NULLIF(employee.PreferredName, ''), NULLIF(employee.RealName, ''), employee.WorkEmail) AS DisplayName,
                merchant.BusinessName
            FROM bee_CrmEmployee AS employee
            INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = employee.MerchantId
            WHERE merchant.ProjectId = @ProjectId
              AND LOWER(employee.WorkEmail) = LOWER(@Email)
              AND employee.LoginEnabled = 1
              AND employee.Status = 'Active'
              AND employee.WorkEmail IS NOT NULL
              AND employee.WorkEmail <> ''
            ORDER BY employee.id
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 180).Value = email;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EmployeePasswordResetTarget(
            reader.GetInt64(reader.GetOrdinal("EmployeeId")),
            reader.GetInt64(reader.GetOrdinal("MerchantId")),
            reader.GetInt32(reader.GetOrdinal("ProjectId")),
            reader["WorkEmail"] as string ?? email,
            reader["DisplayName"] as string ?? email,
            reader["BusinessName"] as string ?? "Sentribee OA");
    }

    private async Task SaveResetTokenAsync(
        MySqlConnection connection,
        EmployeePasswordResetTarget target,
        string token,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string invalidateSql = """
            UPDATE bee_CrmEmployeePasswordReset
            SET UsedAtUtc = UTC_TIMESTAMP(6)
            WHERE EmployeeId = @EmployeeId
              AND UsedAtUtc IS NULL
              AND ExpiresAtUtc > UTC_TIMESTAMP(6);
            """;
        await using (var invalidateCommand = new MySqlCommand(invalidateSql, connection, transaction))
        {
            invalidateCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = target.EmployeeId;
            await invalidateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = """
            INSERT INTO bee_CrmEmployeePasswordReset
                (ProjectId, MerchantId, EmployeeId, TokenHash, ExpiresAtUtc, RequestIp)
            VALUES
                (@ProjectId, @MerchantId, @EmployeeId, @TokenHash, UTC_TIMESTAMP(6) + INTERVAL 1 HOUR, @RequestIp);
            """;
        await using (var insertCommand = new MySqlCommand(insertSql, connection, transaction))
        {
            insertCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = target.ProjectId;
            insertCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = target.MerchantId;
            insertCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = target.EmployeeId;
            insertCommand.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 64).Value = EmployeePasswordResetSupport.HashToken(token);
            insertCommand.Parameters.Add("@RequestIp", MySqlDbType.VarChar, 80).Value =
                (object?)HttpContext.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value;
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private string BuildResetUrl(string token)
    {
        var host = Request.Host.Value ?? string.Empty;
        var scheme = Request.Scheme;
        if (host.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{scheme}://{host}/oa/reset-password?token={Uri.EscapeDataString(token)}";
        }

        return $"https://oa.sentribee.ai/oa/reset-password?token={Uri.EscapeDataString(token)}";
    }
}
