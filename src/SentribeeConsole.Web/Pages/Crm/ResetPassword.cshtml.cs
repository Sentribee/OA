using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class ResetPasswordModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Compare(nameof(NewPassword), ErrorMessage = "Confirm password must match the new password.")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    public bool TokenIsValid { get; private set; }

    public string DisplayEmail { get; private set; } = string.Empty;

    public string? AlertMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadTokenAsync(cancellationToken);
        AlertMessage = TempData["CrmPasswordResetStatus"] as string;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var reset = await LoadTokenAsync(cancellationToken);
        if (reset is null)
        {
            AlertMessage = "This reset link is invalid or expired.";
            return Page();
        }

        if (!ModelState.IsValid)
        {
            TokenIsValid = true;
            DisplayEmail = reset.WorkEmail;
            AlertMessage = "Password must be at least 8 characters and both entries must match.";
            return Page();
        }

        var passwordUser = new CrmEmployeePasswordUser(reset.EmployeeId, reset.WorkEmail);
        var passwordHash = CreateEmployeePasswordHasher().HashPassword(passwordUser, NewPassword);

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string updateEmployeeSql = """
            UPDATE bee_CrmEmployee
            SET EmployeePasswordHash = @PasswordHash,
                MustChangePassword = 0,
                PasswordUpdatedAtUtc = UTC_TIMESTAMP(6),
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @EmployeeId
              AND MerchantId = @MerchantId
              AND LoginEnabled = 1
              AND Status = 'Active';
            """;
        await using (var employeeCommand = new MySqlCommand(updateEmployeeSql, connection, transaction))
        {
            employeeCommand.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 512).Value = passwordHash;
            employeeCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = reset.EmployeeId;
            employeeCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = reset.MerchantId;
            var updated = await employeeCommand.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                AlertMessage = "This reset link is invalid or expired.";
                return Page();
            }
        }

        const string useTokenSql = """
            UPDATE bee_CrmEmployeePasswordReset
            SET UsedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @ResetId
              AND UsedAtUtc IS NULL
              AND ExpiresAtUtc > UTC_TIMESTAMP(6);
            """;
        await using (var tokenCommand = new MySqlCommand(useTokenSql, connection, transaction))
        {
            tokenCommand.Parameters.Add("@ResetId", MySqlDbType.Int64).Value = reset.ResetId;
            var used = await tokenCommand.ExecuteNonQueryAsync(cancellationToken);
            if (used == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                AlertMessage = "This reset link is invalid or expired.";
                return Page();
            }
        }

        const string invalidateSql = """
            UPDATE bee_CrmEmployeePasswordReset
            SET UsedAtUtc = UTC_TIMESTAMP(6)
            WHERE EmployeeId = @EmployeeId
              AND UsedAtUtc IS NULL;
            """;
        await using (var invalidateCommand = new MySqlCommand(invalidateSql, connection, transaction))
        {
            invalidateCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = reset.EmployeeId;
            await invalidateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        TempData["CrmAuthStatus"] = "Password reset. Sign in with your new password.";
        return RedirectToPage("/Crm/Login");
    }

    private async Task<EmployeePasswordResetRecord?> LoadTokenAsync(CancellationToken cancellationToken)
    {
        TokenIsValid = false;
        DisplayEmail = string.Empty;
        if (string.IsNullOrWhiteSpace(Token))
        {
            return null;
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EmployeePasswordResetSupport.EnsureTableAsync(connection, cancellationToken);
        const string sql = """
            SELECT reset.id AS ResetId, reset.EmployeeId, reset.MerchantId,
                employee.WorkEmail,
                reset.ExpiresAtUtc
            FROM bee_CrmEmployeePasswordReset AS reset
            INNER JOIN bee_CrmEmployee AS employee
              ON employee.id = reset.EmployeeId AND employee.MerchantId = reset.MerchantId
            WHERE reset.TokenHash = @TokenHash
              AND reset.UsedAtUtc IS NULL
              AND reset.ExpiresAtUtc > UTC_TIMESTAMP(6)
              AND employee.LoginEnabled = 1
              AND employee.Status = 'Active'
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 64).Value = EmployeePasswordResetSupport.HashToken(Token.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var record = new EmployeePasswordResetRecord(
            reader.GetInt64(reader.GetOrdinal("ResetId")),
            reader.GetInt64(reader.GetOrdinal("EmployeeId")),
            reader.GetInt64(reader.GetOrdinal("MerchantId")),
            reader["WorkEmail"] as string ?? string.Empty);
        TokenIsValid = true;
        DisplayEmail = record.WorkEmail;
        return record;
    }

    private sealed record EmployeePasswordResetRecord(
        long ResetId,
        long EmployeeId,
        long MerchantId,
        string WorkEmail);
}
