using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class LeaveModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public IReadOnlyList<CrmAttendanceEmployeeOption> Employees { get; private set; } = [];

    public IReadOnlyList<CrmLeaveRow> Leaves { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    public LeaveInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(long? leaveId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmLeaveStatus"] as string;
        await LoadEmployeesAsync(cancellationToken);
        await LoadLeavesAsync(cancellationToken);
        if (leaveId.HasValue)
        {
            await LoadLeaveInputAsync(leaveId.Value, cancellationToken);
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
        if (Input.EndDate < Input.StartDate)
        {
            ModelState.AddModelError("Input.EndDate", "End date must be after start date.");
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await EmployeeBelongsToMerchantAsync(connection, Input.EmployeeId, cancellationToken))
        {
            ModelState.AddModelError("Input.EmployeeId", "Employee is required.");
        }

        if (!ModelState.IsValid)
        {
            await LoadEmployeesAsync(cancellationToken);
            await LoadLeavesAsync(cancellationToken);
            return Page();
        }

        if (Input.Id > 0)
        {
            const string sql = """
                UPDATE bee_CrmEmployeeLeave
                SET EmployeeId = @EmployeeId,
                    LeaveType = @LeaveType,
                    StartDate = @StartDate,
                    EndDate = @EndDate,
                    Hours = @Hours,
                    IsPaid = @IsPaid,
                    Status = @Status,
                    Reason = @Reason,
                    Notes = @Notes,
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @LeaveId AND MerchantId = @MerchantId;
                """;
            await using var command = new MySqlCommand(sql, connection);
            AddSaveParameters(command);
            command.Parameters.Add("@LeaveId", MySqlDbType.Int64).Value = Input.Id;
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmLeaveStatus"] = "Leave record updated.";
        }
        else
        {
            const string sql = """
                INSERT INTO bee_CrmEmployeeLeave
                    (ProjectId, MerchantId, EmployeeId, LeaveType, StartDate, EndDate,
                     Hours, IsPaid, Status, Reason, Notes)
                VALUES
                    (@ProjectId, @MerchantId, @EmployeeId, @LeaveType, @StartDate, @EndDate,
                     @Hours, @IsPaid, @Status, @Reason, @Notes);
                """;
            await using var command = new MySqlCommand(sql, connection);
            AddSaveParameters(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmLeaveStatus"] = "Leave record added.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(long leaveId, string status, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        var normalizedStatus = NormalizeStatus(status);
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE bee_CrmEmployeeLeave
            SET Status = @Status, UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @LeaveId AND MerchantId = @MerchantId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@LeaveId", MySqlDbType.Int64).Value = leaveId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = normalizedStatus;
        await command.ExecuteNonQueryAsync(cancellationToken);
        TempData["CrmLeaveStatus"] = $"Leave marked as {normalizedStatus}.";
        return RedirectToPage();
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Leave";
        ViewData["PageTitle"] = "Leave";
        ViewData["ActiveMenu"] = "Leave";
    }

    private async Task LoadEmployeesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, RealName, PreferredName, JobTitle
            FROM bee_CrmEmployee
            WHERE MerchantId = @MerchantId AND Status = 'Active'
            ORDER BY RealName, id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmAttendanceEmployeeOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmAttendanceEmployeeOption(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["RealName"] as string ?? string.Empty,
                reader["PreferredName"] as string,
                reader["JobTitle"] as string));
        }

        Employees = rows;
    }

    private async Task LoadLeavesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT leave_record.id, leave_record.LeaveType, leave_record.StartDate, leave_record.EndDate,
                leave_record.Hours, leave_record.IsPaid, leave_record.Status, leave_record.Reason,
                leave_record.Notes, employee.RealName, employee.PreferredName
            FROM bee_CrmEmployeeLeave AS leave_record
            INNER JOIN bee_CrmEmployee AS employee ON employee.id = leave_record.EmployeeId
            WHERE leave_record.MerchantId = @MerchantId
            ORDER BY leave_record.StartDate DESC, leave_record.id DESC
            LIMIT 120;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmLeaveRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmLeaveRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["RealName"] as string ?? string.Empty,
                reader["PreferredName"] as string,
                reader["LeaveType"] as string ?? string.Empty,
                reader.GetDateTime(reader.GetOrdinal("StartDate")),
                reader.GetDateTime(reader.GetOrdinal("EndDate")),
                reader.GetDecimal(reader.GetOrdinal("Hours")),
                reader.GetBoolean(reader.GetOrdinal("IsPaid")),
                reader["Status"] as string ?? string.Empty,
                reader["Reason"] as string,
                reader["Notes"] as string));
        }

        Leaves = rows;
    }

    private async Task LoadLeaveInputAsync(long leaveId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, EmployeeId, LeaveType, StartDate, EndDate, Hours, IsPaid, Status, Reason, Notes
            FROM bee_CrmEmployeeLeave
            WHERE id = @LeaveId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@LeaveId", MySqlDbType.Int64).Value = leaveId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        Input = new LeaveInput
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            EmployeeId = reader.GetInt64(reader.GetOrdinal("EmployeeId")),
            LeaveType = reader["LeaveType"] as string ?? "Annual",
            StartDate = reader.GetDateTime(reader.GetOrdinal("StartDate")),
            EndDate = reader.GetDateTime(reader.GetOrdinal("EndDate")),
            Hours = reader.GetDecimal(reader.GetOrdinal("Hours")),
            IsPaid = reader.GetBoolean(reader.GetOrdinal("IsPaid")),
            Status = reader["Status"] as string ?? "Approved",
            Reason = reader["Reason"] as string,
            Notes = reader["Notes"] as string
        };
    }

    private async Task<bool> EmployeeBelongsToMerchantAsync(MySqlConnection connection, long employeeId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT 1 FROM bee_CrmEmployee WHERE id = @EmployeeId AND MerchantId = @MerchantId LIMIT 1;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null;
    }

    private void AddSaveParameters(MySqlCommand command)
    {
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Input.EmployeeId;
        command.Parameters.Add("@LeaveType", MySqlDbType.VarChar, 60).Value = NormalizeLeaveType(Input.LeaveType);
        command.Parameters.Add("@StartDate", MySqlDbType.Date).Value = Input.StartDate.Date;
        command.Parameters.Add("@EndDate", MySqlDbType.Date).Value = Input.EndDate.Date;
        command.Parameters.Add("@Hours", MySqlDbType.Decimal).Value = Input.Hours;
        command.Parameters.Add("@IsPaid", MySqlDbType.Bit).Value = Input.IsPaid;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = NormalizeStatus(Input.Status);
        command.Parameters.Add("@Reason", MySqlDbType.VarChar, 500).Value = DbValue(Input.Reason);
        command.Parameters.Add("@Notes", MySqlDbType.Text).Value = DbValue(Input.Notes);
    }

    private static string NormalizeLeaveType(string? value)
    {
        return value switch
        {
            "Annual" or "Sick" or "Unpaid" or "Bereavement" or "PublicHoliday" or "Personal" or "Other" => value,
            _ => "Annual"
        };
    }

    private static string NormalizeStatus(string? value)
    {
        return value switch
        {
            "Pending" or "Approved" or "Rejected" or "Cancelled" => value,
            _ => "Approved"
        };
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

public sealed class LeaveInput
{
    public long Id { get; set; }

    [Range(1, long.MaxValue)]
    public long EmployeeId { get; set; }

    [Required]
    public string LeaveType { get; set; } = "Annual";

    [DataType(DataType.Date)]
    public DateTime StartDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    public DateTime EndDate { get; set; } = DateTime.Today;

    [Range(0.25, 1000)]
    public decimal Hours { get; set; } = 8m;

    public bool IsPaid { get; set; } = true;

    [Required]
    public string Status { get; set; } = "Approved";

    [StringLength(500)]
    public string? Reason { get; set; }

    [StringLength(3000)]
    public string? Notes { get; set; }
}

public sealed record CrmLeaveRow(
    long Id,
    string RealName,
    string? PreferredName,
    string LeaveType,
    DateTime StartDate,
    DateTime EndDate,
    decimal Hours,
    bool IsPaid,
    string Status,
    string? Reason,
    string? Notes);
