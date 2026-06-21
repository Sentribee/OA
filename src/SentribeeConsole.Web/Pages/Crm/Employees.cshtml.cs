using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Crm;

public class EmployeesModel(
    IConfiguration configuration,
    IConsoleEmailService emailService) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public IReadOnlyList<CrmEmployeeRow> Employees { get; private set; } = [];

    public IReadOnlyList<CrmOfficeOption> Offices { get; private set; } = [];

    public IReadOnlyList<CrmRoleOption> Roles { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    public EmployeeInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(long? employeeId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmEmployeesStatus"] as string;
        await LoadOfficesAsync(cancellationToken);
        await LoadRolesAsync(cancellationToken);
        await LoadEmployeesAsync(cancellationToken);
        if (employeeId.HasValue)
        {
            await LoadEmployeeInputAsync(employeeId.Value, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        await LoadOfficesAsync(cancellationToken);
        await LoadRolesAsync(cancellationToken);
        if (Input.Id == 0)
        {
            if (string.IsNullOrWhiteSpace(Input.WorkEmail))
            {
                ModelState.AddModelError("Input.WorkEmail", "Work email is required.");
            }

            if (!Input.RoleId.HasValue)
            {
                ModelState.AddModelError("Input.RoleId", "Role is required.");
            }

            if (!Input.StartDate.HasValue)
            {
                ModelState.AddModelError("Input.StartDate", "Start date is required.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(Input.InitialPassword) && Input.InitialPassword.Length < 8)
        {
            ModelState.AddModelError("Input.InitialPassword", "Initial password must be at least 8 characters.");
        }

        if (Input.Id > 0 && string.IsNullOrWhiteSpace(Input.RealName))
        {
            ModelState.AddModelError("Input.RealName", "Real name is required.");
        }

        if (!ModelState.IsValid)
        {
            StatusMessage = "Please check the highlighted fields.";
            await LoadEmployeesAsync(cancellationToken);
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var officeId = await NormalizeOfficeIdAsync(connection, Input.OfficeAddressId, cancellationToken);
        var roleId = await NormalizeRoleIdAsync(connection, Input.RoleId, cancellationToken);
        if (Input.Id > 0)
        {
            var passwordHash = CreateInitialPasswordHash(Input.Id, Input.WorkEmail, Input.InitialPassword);
            const string updateSql = """
                UPDATE bee_CrmEmployee
                SET OfficeAddressId = @OfficeAddressId,
                    RoleId = @RoleId,
                    RealName = @RealName,
                    PreferredName = @PreferredName,
                    ResidentialAddress = @ResidentialAddress,
                    WorkEmail = @WorkEmail,
                    PrivateEmail = @PrivateEmail,
                    EmployeePasswordHash = COALESCE(@EmployeePasswordHash, EmployeePasswordHash),
                    MustChangePassword = CASE WHEN @EmployeePasswordHash IS NULL THEN MustChangePassword ELSE 1 END,
                    LoginEnabled = @LoginEnabled,
                    GstNumber = @GstNumber,
                    BankAccountNumber = @BankAccountNumber,
                    StartDate = @StartDate,
                    JobTitle = @JobTitle,
                    EmploymentType = @EmploymentType,
                    PayType = @PayType,
                    HourlyRate = @HourlyRate,
                    AnnualSalary = @AnnualSalary,
                    StandardWeeklyHours = @StandardWeeklyHours,
                    ScheduledStartTime = @ScheduledStartTime,
                    ScheduledEndTime = @ScheduledEndTime,
                    Status = @Status,
                    Notes = @Notes,
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @EmployeeId AND MerchantId = @MerchantId;
                """;
            await using var command = new MySqlCommand(updateSql, connection);
            AddSaveParameters(command, officeId, roleId, passwordHash);
            command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Input.Id;
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmEmployeesStatus"] = "Employee updated.";
        }
        else
        {
            var temporaryPassword = GenerateTemporaryPassword();
            const string insertSql = """
                INSERT INTO bee_CrmEmployee
                    (ProjectId, MerchantId, OfficeAddressId, RoleId, RealName, PreferredName, ResidentialAddress,
                     WorkEmail, PrivateEmail, EmployeePasswordHash, MustChangePassword, LoginEnabled,
                     GstNumber, BankAccountNumber, StartDate,
                     JobTitle, EmploymentType, PayType, HourlyRate, AnnualSalary, StandardWeeklyHours,
                     ScheduledStartTime, ScheduledEndTime,
                     Status, Notes, ProfileCompletedAtUtc, InviteSentAtUtc)
                VALUES
                    (@ProjectId, @MerchantId, @OfficeAddressId, @RoleId, @RealName, @PreferredName, @ResidentialAddress,
                     @WorkEmail, @PrivateEmail, @EmployeePasswordHash, @MustChangePassword, @LoginEnabled,
                     @GstNumber, @BankAccountNumber, @StartDate,
                     @JobTitle, @EmploymentType, @PayType, @HourlyRate, @AnnualSalary, @StandardWeeklyHours,
                     @ScheduledStartTime, @ScheduledEndTime,
                     @Status, @Notes, NULL, UTC_TIMESTAMP(6));
                SELECT LAST_INSERT_ID();
                """;
            await using var command = new MySqlCommand(insertSql, connection);
            AddSaveParameters(command, officeId, roleId, null, temporaryPassword);
            var employeeId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            var emailResult = await emailService.SendEmployeeWelcomeAsync(
                Input.WorkEmail!.Trim(),
                Merchant.BusinessName,
                BuildEmployeeLoginUrl(),
                temporaryPassword,
                cancellationToken);
            TempData["CrmEmployeesStatus"] = emailResult.Success
                ? "Employee added and welcome email sent."
                : $"Employee added, but email delivery failed: {emailResult.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(long employeeId, string status, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        var normalizedStatus = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) ? "Active" : "Inactive";
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE bee_CrmEmployee
            SET Status = @Status,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @EmployeeId AND MerchantId = @MerchantId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = normalizedStatus;
        await command.ExecuteNonQueryAsync(cancellationToken);
        TempData["CrmEmployeesStatus"] = normalizedStatus == "Active" ? "Employee activated." : "Employee deactivated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResendInviteAsync(long employeeId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string selectSql = """
            SELECT id, WorkEmail
            FROM bee_CrmEmployee
            WHERE id = @EmployeeId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var selectCommand = new MySqlCommand(selectSql, connection);
        selectCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        selectCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            TempData["CrmEmployeesStatus"] = "Employee was not found.";
            return RedirectToPage();
        }

        var workEmail = reader["WorkEmail"] as string;
        await reader.DisposeAsync();
        if (string.IsNullOrWhiteSpace(workEmail))
        {
            TempData["CrmEmployeesStatus"] = "Employee does not have a work email.";
            return RedirectToPage();
        }

        var temporaryPassword = GenerateTemporaryPassword();
        var passwordHash = CreateInitialPasswordHash(employeeId, workEmail, temporaryPassword);
        const string updateSql = """
            UPDATE bee_CrmEmployee
            SET EmployeePasswordHash = @EmployeePasswordHash,
                MustChangePassword = 1,
                LoginEnabled = 1,
                InviteSentAtUtc = UTC_TIMESTAMP(6),
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @EmployeeId AND MerchantId = @MerchantId;
            """;
        await using var updateCommand = new MySqlCommand(updateSql, connection);
        updateCommand.Parameters.Add("@EmployeePasswordHash", MySqlDbType.VarChar, 512).Value = passwordHash!;
        updateCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        updateCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        var emailResult = await emailService.SendEmployeeWelcomeAsync(
            workEmail.Trim(),
            Merchant.BusinessName,
            BuildEmployeeLoginUrl(),
            temporaryPassword,
            cancellationToken);
        TempData["CrmEmployeesStatus"] = emailResult.Success
            ? "Employee invite resent."
            : $"Employee password was reset, but email delivery failed: {emailResult.Message}";
        return RedirectToPage();
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Employees";
        ViewData["PageTitle"] = "Employees";
        ViewData["ActiveMenu"] = "Employees";
    }

    private async Task LoadOfficesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, LocationName
            FROM bee_CrmOfficeAddress
            WHERE MerchantId = @MerchantId AND Status = 'Active'
            ORDER BY IsPrimary DESC, LocationName, id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmOfficeOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmOfficeOption(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["LocationName"] as string ?? string.Empty));
        }

        Offices = rows;
    }

    private async Task LoadRolesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, RoleName
            FROM bee_CrmRole
            WHERE MerchantId = @MerchantId AND Status = 'Active'
            ORDER BY RoleName, id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmRoleOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmRoleOption(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["RoleName"] as string ?? string.Empty));
        }

        Roles = rows;
    }

    private async Task LoadEmployeesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT employee.id, employee.RealName, employee.PreferredName, employee.Phone,
                employee.WorkEmail, employee.PrivateEmail, employee.GstNumber, employee.BankAccountNumber,
                employee.ResidentialAddress, employee.StartDate, employee.JobTitle,
                employee.EmploymentType, employee.PayType, employee.HourlyRate, employee.AnnualSalary,
                employee.StandardWeeklyHours, employee.ScheduledStartTime, employee.ScheduledEndTime,
                employee.LoginEnabled, employee.MustChangePassword,
                employee.ProfileCompletedAtUtc, employee.InviteSentAtUtc,
                employee.Status, office.LocationName, role.RoleName
            FROM bee_CrmEmployee AS employee
            LEFT JOIN bee_CrmOfficeAddress AS office ON office.id = employee.OfficeAddressId
            LEFT JOIN bee_CrmRole AS role ON role.id = employee.RoleId
            WHERE employee.MerchantId = @MerchantId
            ORDER BY employee.Status = 'Active' DESC, employee.RealName, employee.id DESC;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmEmployeeRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmEmployeeRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["RealName"] as string ?? string.Empty,
                reader["PreferredName"] as string,
                reader["Phone"] as string,
                reader["WorkEmail"] as string,
                reader["PrivateEmail"] as string,
                reader["GstNumber"] as string,
                reader["BankAccountNumber"] as string,
                reader["ResidentialAddress"] as string,
                reader.IsDBNull(reader.GetOrdinal("StartDate")) ? null : reader.GetDateTime(reader.GetOrdinal("StartDate")),
                reader["JobTitle"] as string,
                reader["EmploymentType"] as string,
                reader["PayType"] as string ?? "Hourly",
                reader.IsDBNull(reader.GetOrdinal("HourlyRate")) ? null : reader.GetDecimal(reader.GetOrdinal("HourlyRate")),
                reader.IsDBNull(reader.GetOrdinal("AnnualSalary")) ? null : reader.GetDecimal(reader.GetOrdinal("AnnualSalary")),
                reader.GetDecimal(reader.GetOrdinal("StandardWeeklyHours")),
                reader.IsDBNull(reader.GetOrdinal("ScheduledStartTime")) ? null : reader.GetTimeSpan(reader.GetOrdinal("ScheduledStartTime")),
                reader.IsDBNull(reader.GetOrdinal("ScheduledEndTime")) ? null : reader.GetTimeSpan(reader.GetOrdinal("ScheduledEndTime")),
                reader.GetBoolean(reader.GetOrdinal("LoginEnabled")),
                reader.GetBoolean(reader.GetOrdinal("MustChangePassword")),
                reader.IsDBNull(reader.GetOrdinal("ProfileCompletedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("ProfileCompletedAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("InviteSentAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("InviteSentAtUtc")),
                reader["Status"] as string ?? string.Empty,
                reader["LocationName"] as string,
                reader["RoleName"] as string));
        }

        Employees = rows;
    }

    private async Task LoadEmployeeInputAsync(long employeeId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, OfficeAddressId, RoleId, RealName, PreferredName, ResidentialAddress,
                WorkEmail, PrivateEmail, LoginEnabled, MustChangePassword, GstNumber, BankAccountNumber,
                StartDate, JobTitle, EmploymentType, PayType, HourlyRate, AnnualSalary,
                StandardWeeklyHours, ScheduledStartTime, ScheduledEndTime, Status, Notes
            FROM bee_CrmEmployee
            WHERE id = @EmployeeId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        Input = new EmployeeInput
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            OfficeAddressId = reader.IsDBNull(reader.GetOrdinal("OfficeAddressId")) ? null : reader.GetInt64(reader.GetOrdinal("OfficeAddressId")),
            RoleId = reader.IsDBNull(reader.GetOrdinal("RoleId")) ? null : reader.GetInt64(reader.GetOrdinal("RoleId")),
            RealName = reader["RealName"] as string ?? string.Empty,
            PreferredName = reader["PreferredName"] as string,
            ResidentialAddress = reader["ResidentialAddress"] as string,
            WorkEmail = reader["WorkEmail"] as string,
            PrivateEmail = reader["PrivateEmail"] as string,
            LoginEnabled = reader.GetBoolean(reader.GetOrdinal("LoginEnabled")),
            MustChangePassword = reader.GetBoolean(reader.GetOrdinal("MustChangePassword")),
            GstNumber = reader["GstNumber"] as string,
            BankAccountNumber = reader["BankAccountNumber"] as string,
            StartDate = reader.IsDBNull(reader.GetOrdinal("StartDate")) ? null : reader.GetDateTime(reader.GetOrdinal("StartDate")),
            JobTitle = reader["JobTitle"] as string,
            EmploymentType = reader["EmploymentType"] as string,
            PayType = reader["PayType"] as string ?? "Hourly",
            HourlyRate = reader.IsDBNull(reader.GetOrdinal("HourlyRate")) ? null : reader.GetDecimal(reader.GetOrdinal("HourlyRate")),
            AnnualSalary = reader.IsDBNull(reader.GetOrdinal("AnnualSalary")) ? null : reader.GetDecimal(reader.GetOrdinal("AnnualSalary")),
            StandardWeeklyHours = reader.IsDBNull(reader.GetOrdinal("StandardWeeklyHours")) ? 40m : reader.GetDecimal(reader.GetOrdinal("StandardWeeklyHours")),
            ScheduledStartTime = reader.IsDBNull(reader.GetOrdinal("ScheduledStartTime")) ? null : reader.GetTimeSpan(reader.GetOrdinal("ScheduledStartTime")),
            ScheduledEndTime = reader.IsDBNull(reader.GetOrdinal("ScheduledEndTime")) ? null : reader.GetTimeSpan(reader.GetOrdinal("ScheduledEndTime")),
            Status = reader["Status"] as string ?? "Active",
            Notes = reader["Notes"] as string
        };
    }

    private async Task<long?> NormalizeOfficeIdAsync(MySqlConnection connection, long? officeId, CancellationToken cancellationToken)
    {
        if (!officeId.HasValue)
        {
            return null;
        }

        const string sql = "SELECT id FROM bee_CrmOfficeAddress WHERE id = @OfficeId AND MerchantId = @MerchantId LIMIT 1;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@OfficeId", MySqlDbType.Int64).Value = officeId.Value;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : officeId.Value;
    }

    private async Task<long?> NormalizeRoleIdAsync(MySqlConnection connection, long? roleId, CancellationToken cancellationToken)
    {
        if (!roleId.HasValue)
        {
            return null;
        }

        const string sql = "SELECT id FROM bee_CrmRole WHERE id = @RoleId AND MerchantId = @MerchantId LIMIT 1;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@RoleId", MySqlDbType.Int64).Value = roleId.Value;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : roleId.Value;
    }

    private void AddSaveParameters(MySqlCommand command, long? officeId, long? roleId, string? passwordHash, string? temporaryPassword = null)
    {
        var workEmail = Input.WorkEmail?.Trim();
        var realName = Input.Id == 0
            ? GuessEmployeeNameFromEmail(workEmail)
            : Input.RealName!.Trim();
        var effectivePasswordHash = Input.Id == 0
            ? CreateInitialPasswordHash(0, workEmail, temporaryPassword)
            : passwordHash;
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@OfficeAddressId", MySqlDbType.Int64).Value = (object?)officeId ?? DBNull.Value;
        command.Parameters.Add("@RoleId", MySqlDbType.Int64).Value = (object?)roleId ?? DBNull.Value;
        command.Parameters.Add("@RealName", MySqlDbType.VarChar, 160).Value = realName;
        command.Parameters.Add("@PreferredName", MySqlDbType.VarChar, 160).Value = DbValue(Input.PreferredName);
        command.Parameters.Add("@ResidentialAddress", MySqlDbType.VarChar, 700).Value = DbValue(Input.ResidentialAddress);
        command.Parameters.Add("@WorkEmail", MySqlDbType.VarChar, 180).Value = DbValue(workEmail);
        command.Parameters.Add("@PrivateEmail", MySqlDbType.VarChar, 180).Value = DbValue(Input.PrivateEmail);
        command.Parameters.Add("@EmployeePasswordHash", MySqlDbType.VarChar, 512).Value = (object?)effectivePasswordHash ?? DBNull.Value;
        command.Parameters.Add("@MustChangePassword", MySqlDbType.Byte).Value = Input.Id == 0 || (Input.LoginEnabled && passwordHash is not null) ? 1 : 0;
        command.Parameters.Add("@LoginEnabled", MySqlDbType.Byte).Value = Input.Id == 0 || Input.LoginEnabled ? 1 : 0;
        command.Parameters.Add("@GstNumber", MySqlDbType.VarChar, 80).Value = DbValue(Input.GstNumber);
        command.Parameters.Add("@BankAccountNumber", MySqlDbType.VarChar, 120).Value = DbValue(Input.BankAccountNumber);
        command.Parameters.Add("@StartDate", MySqlDbType.Date).Value = (object?)Input.StartDate?.Date ?? DBNull.Value;
        command.Parameters.Add("@JobTitle", MySqlDbType.VarChar, 160).Value = DbValue(Input.JobTitle);
        command.Parameters.Add("@EmploymentType", MySqlDbType.VarChar, 80).Value = DbValue(Input.EmploymentType);
        command.Parameters.Add("@PayType", MySqlDbType.VarChar, 40).Value = NormalizePayType(Input.PayType);
        command.Parameters.Add("@HourlyRate", MySqlDbType.Decimal).Value = (object?)Input.HourlyRate ?? DBNull.Value;
        command.Parameters.Add("@AnnualSalary", MySqlDbType.Decimal).Value = (object?)Input.AnnualSalary ?? DBNull.Value;
        command.Parameters.Add("@StandardWeeklyHours", MySqlDbType.Decimal).Value = Input.StandardWeeklyHours <= 0 ? 40m : Input.StandardWeeklyHours;
        command.Parameters.Add("@ScheduledStartTime", MySqlDbType.Time).Value = (object?)Input.ScheduledStartTime ?? DBNull.Value;
        command.Parameters.Add("@ScheduledEndTime", MySqlDbType.Time).Value = (object?)Input.ScheduledEndTime ?? DBNull.Value;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = string.Equals(Input.Status, "Inactive", StringComparison.OrdinalIgnoreCase) ? "Inactive" : "Active";
        command.Parameters.Add("@Notes", MySqlDbType.Text).Value = DbValue(Input.Notes);
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string NormalizePayType(string? value)
    {
        return string.Equals(value, "Salary", StringComparison.OrdinalIgnoreCase) ? "Salary" : "Hourly";
    }

    private static string? CreateInitialPasswordHash(long employeeId, string? workEmail, string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(workEmail))
        {
            return null;
        }

        var passwordUser = new CrmEmployeePasswordUser(employeeId, workEmail.Trim());
        return CreateEmployeePasswordHasher().HashPassword(passwordUser, password);
    }

    private string BuildEmployeeLoginUrl()
    {
        return $"https://{CrmProjectDomain}/oa/login";
    }

    private static string GenerateTemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
        return string.Create(14, alphabet, static (span, chars) =>
        {
            Span<byte> bytes = stackalloc byte[span.Length];
            RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[bytes[i] % chars.Length];
            }
        });
    }

    private static string GuessEmployeeNameFromEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "New employee";
        }

        var local = email.Trim();
        var at = local.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            local = local[..at];
        }

        return string.IsNullOrWhiteSpace(local) ? "New employee" : local;
    }
}

public sealed class EmployeeInput
{
    public long Id { get; set; }

    public long? OfficeAddressId { get; set; }

    public long? RoleId { get; set; }

    [StringLength(160)]
    public string? RealName { get; set; }

    [StringLength(160)]
    public string? PreferredName { get; set; }

    [StringLength(700)]
    public string? ResidentialAddress { get; set; }

    [EmailAddress]
    [StringLength(180)]
    public string? WorkEmail { get; set; }

    [EmailAddress]
    [StringLength(180)]
    public string? PrivateEmail { get; set; }

    public bool LoginEnabled { get; set; }

    public bool MustChangePassword { get; set; }

    [StringLength(120)]
    public string? InitialPassword { get; set; }

    [StringLength(80)]
    public string? GstNumber { get; set; }

    [StringLength(120)]
    public string? BankAccountNumber { get; set; }

    public DateTime? StartDate { get; set; }

    [StringLength(160)]
    public string? JobTitle { get; set; }

    [StringLength(80)]
    public string? EmploymentType { get; set; }

    [Required]
    public string PayType { get; set; } = "Hourly";

    [Range(0, 1000000)]
    public decimal? HourlyRate { get; set; }

    [Range(0, 10000000)]
    public decimal? AnnualSalary { get; set; }

    [Range(1, 168)]
    public decimal StandardWeeklyHours { get; set; } = 40m;

    public TimeSpan? ScheduledStartTime { get; set; }

    public TimeSpan? ScheduledEndTime { get; set; }

    [Required]
    public string Status { get; set; } = "Active";

    [StringLength(3000)]
    public string? Notes { get; set; }
}

public sealed record CrmEmployeeRow(
    long Id,
    string RealName,
    string? PreferredName,
    string? Phone,
    string? WorkEmail,
    string? PrivateEmail,
    string? GstNumber,
    string? BankAccountNumber,
    string? ResidentialAddress,
    DateTime? StartDate,
    string? JobTitle,
    string? EmploymentType,
    string PayType,
    decimal? HourlyRate,
    decimal? AnnualSalary,
    decimal StandardWeeklyHours,
    TimeSpan? ScheduledStartTime,
    TimeSpan? ScheduledEndTime,
    bool LoginEnabled,
    bool MustChangePassword,
    DateTime? ProfileCompletedAtUtc,
    DateTime? InviteSentAtUtc,
    string Status,
    string? OfficeName,
    string? RoleName);

public sealed record CrmOfficeOption(long Id, string LocationName);

public sealed record CrmRoleOption(long Id, string RoleName);
