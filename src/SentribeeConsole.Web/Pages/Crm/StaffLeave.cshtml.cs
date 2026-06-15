using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class StaffLeaveModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmEmployeeSession Staff { get; private set; } = null!;

    public IReadOnlyList<CrmLeaveRow> Leaves { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    public StaffLeaveInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var staff = await RequireStaffAsync(cancellationToken);
        if (staff.Result is not null)
        {
            return staff.Result;
        }

        Staff = staff.Staff!;
        SetViewData();
        StatusMessage = TempData["CrmStaffLeaveStatus"] as string;
        await LoadLeavesAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitAsync(CancellationToken cancellationToken)
    {
        var staff = await RequireStaffAsync(cancellationToken);
        if (staff.Result is not null)
        {
            return staff.Result;
        }

        Staff = staff.Staff!;
        SetViewData();
        if (Input.EndDate < Input.StartDate)
        {
            ModelState.AddModelError("Input.EndDate", "End date must be after start date.");
        }

        if (!ModelState.IsValid)
        {
            await LoadLeavesAsync(cancellationToken);
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            INSERT INTO bee_CrmEmployeeLeave
                (ProjectId, MerchantId, EmployeeId, LeaveType, StartDate, EndDate,
                 Hours, IsPaid, Status, Reason, Notes)
            VALUES
                (@ProjectId, @MerchantId, @EmployeeId, @LeaveType, @StartDate, @EndDate,
                 @Hours, @IsPaid, 'Pending', @Reason, @Notes);
            SELECT LAST_INSERT_ID();
            """;
        await using (var command = new MySqlCommand(sql, connection))
        {
            command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Staff.ProjectId;
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
            command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
            command.Parameters.Add("@LeaveType", MySqlDbType.VarChar, 60).Value = NormalizeLeaveType(Input.LeaveType);
            command.Parameters.Add("@StartDate", MySqlDbType.Date).Value = Input.StartDate.Date;
            command.Parameters.Add("@EndDate", MySqlDbType.Date).Value = Input.EndDate.Date;
            command.Parameters.Add("@Hours", MySqlDbType.Decimal).Value = Input.Hours;
            command.Parameters.Add("@IsPaid", MySqlDbType.Bit).Value = Input.IsPaid;
            command.Parameters.Add("@Reason", MySqlDbType.VarChar, 500).Value = DbValue(Input.Reason);
            command.Parameters.Add("@Notes", MySqlDbType.Text).Value = DbValue(Input.Notes);
            var leaveId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            await CrmWorkflowEngine.StartLeaveWorkflowAsync(connection, Staff, leaveId, cancellationToken);
        }

        TempData["CrmStaffLeaveStatus"] = "Leave submitted.";
        return RedirectToPage();
    }

    private async Task<(CrmEmployeeSession? Staff, IActionResult? Result)> RequireStaffAsync(CancellationToken cancellationToken)
    {
        var staff = await LoadCurrentEmployeeAsync(cancellationToken);
        if (staff is null)
        {
            return (null, RedirectToPage("/Crm/Login"));
        }

        if (staff.MustChangePassword)
        {
            return (null, RedirectToPage("/Crm/StaffChangePassword"));
        }

        if (!staff.ProfileCompletedAtUtc.HasValue)
        {
            return (null, RedirectToPage("/Crm/StaffProfile"));
        }

        return (staff, null);
    }

    private void SetViewData()
    {
        ViewData["CrmEmployee"] = Staff;
        ViewData["Title"] = "My Leave";
        ViewData["PageTitle"] = "My Leave";
        ViewData["ActiveMenu"] = "StaffLeave";
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
            WHERE leave_record.MerchantId = @MerchantId AND leave_record.EmployeeId = @EmployeeId
            ORDER BY leave_record.StartDate DESC, leave_record.id DESC
            LIMIT 80;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
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

    private static string NormalizeLeaveType(string? value)
    {
        return value switch
        {
            "Annual" or "Sick" or "Unpaid" or "Bereavement" or "PublicHoliday" or "Personal" or "Other" => value,
            _ => "Annual"
        };
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

public sealed class StaffLeaveInput
{
    [Required]
    public string LeaveType { get; set; } = "Annual";

    [DataType(DataType.Date)]
    public DateTime StartDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    public DateTime EndDate { get; set; } = DateTime.Today;

    [Range(0.25, 1000)]
    public decimal Hours { get; set; } = 8m;

    public bool IsPaid { get; set; } = true;

    [StringLength(500)]
    public string? Reason { get; set; }

    [StringLength(3000)]
    public string? Notes { get; set; }
}
