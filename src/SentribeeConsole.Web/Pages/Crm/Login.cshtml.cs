using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class LoginModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    [BindProperty]
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public string? AlertMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (CurrentMerchantId is not null)
        {
            return CurrentEmployeeId is not null
                ? RedirectToPage("/Crm/StaffDashboard")
                : RedirectToPage("/Crm/Dashboard");
        }

        AlertMessage = TempData["CrmAuthStatus"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            AlertMessage = "Enter your email and password.";
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var projectId = await GetCrmProjectIdAsync(connection, cancellationToken);

        const string sql = """
            SELECT id, Email, PasswordHash
            FROM bee_CrmMerchant
            WHERE ProjectId = @ProjectId AND Email = @Email
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = Email.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.CloseAsync();
            return await TryEmployeeLoginAsync(connection, projectId, cancellationToken);
        }

        var merchantId = reader.GetInt64(reader.GetOrdinal("id"));
        var storedHash = reader["PasswordHash"] as string;
        await reader.CloseAsync();

        if (string.IsNullOrWhiteSpace(storedHash))
        {
            AlertMessage = "This account has no password yet. Register again or contact SentriBee support.";
            return Page();
        }

        var passwordUser = new CrmMerchantPasswordUser(merchantId, Email.Trim());
        var verified = CreatePasswordHasher().VerifyHashedPassword(passwordUser, storedHash, Password);
        if (verified == PasswordVerificationResult.Failed)
        {
            AlertMessage = "Invalid email or password.";
            return Page();
        }

        const string updateSql = "UPDATE bee_CrmMerchant SET LastLoginAtUtc = UTC_TIMESTAMP(6), UpdatedAtUtc = UTC_TIMESTAMP(6) WHERE id = @MerchantId;";
        await using var updateCommand = new MySqlCommand(updateSql, connection);
        updateCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        SignInMerchant(merchantId);
        return RedirectToPage("/Crm/Dashboard");
    }

    private async Task<IActionResult> TryEmployeeLoginAsync(
        MySqlConnection connection,
        int projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT employee.id, employee.MerchantId, employee.WorkEmail, employee.EmployeePasswordHash,
                employee.MustChangePassword, employee.Status
            FROM bee_CrmEmployee AS employee
            INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = employee.MerchantId
            WHERE merchant.ProjectId = @ProjectId
              AND employee.WorkEmail = @Email
              AND employee.LoginEnabled = 1
            ORDER BY employee.Status = 'Active' DESC, employee.id
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 180).Value = Email.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            AlertMessage = "Invalid email or password.";
            return Page();
        }

        var employeeId = reader.GetInt64(reader.GetOrdinal("id"));
        var merchantId = reader.GetInt64(reader.GetOrdinal("MerchantId"));
        var workEmail = reader["WorkEmail"] as string ?? Email.Trim();
        var storedHash = reader["EmployeePasswordHash"] as string;
        var mustChangePassword = reader.GetBoolean(reader.GetOrdinal("MustChangePassword"));
        var status = reader["Status"] as string ?? string.Empty;
        await reader.CloseAsync();

        if (!string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(storedHash))
        {
            AlertMessage = "Invalid email or password.";
            return Page();
        }

        var passwordUser = new CrmEmployeePasswordUser(employeeId, workEmail);
        var verified = CreateEmployeePasswordHasher().VerifyHashedPassword(passwordUser, storedHash, Password);
        if (verified == PasswordVerificationResult.Failed)
        {
            AlertMessage = "Invalid email or password.";
            return Page();
        }

        const string updateSql = """
            UPDATE bee_CrmEmployee
            SET LastEmployeeLoginAtUtc = UTC_TIMESTAMP(6), UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @EmployeeId AND MerchantId = @MerchantId;
            """;
        await using var updateCommand = new MySqlCommand(updateSql, connection);
        updateCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        updateCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        SignInEmployee(merchantId, employeeId);
        return mustChangePassword
            ? RedirectToPage("/Crm/StaffChangePassword")
            : RedirectToPage("/Crm/StaffDashboard");
    }
}
