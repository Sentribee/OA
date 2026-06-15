using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class StaffDashboardModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmEmployeeSession Staff { get; private set; } = null!;

    public StaffDashboardSummary Summary { get; private set; } = new();

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var staff = await LoadCurrentEmployeeAsync(cancellationToken);
        if (staff is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        if (staff.MustChangePassword)
        {
            return RedirectToPage("/Crm/StaffChangePassword");
        }

        if (!staff.ProfileCompletedAtUtc.HasValue)
        {
            return RedirectToPage("/Crm/StaffProfile");
        }

        Staff = staff;
        SetViewData();
        StatusMessage = TempData["CrmStaffStatus"] as string;
        await LoadSummaryAsync(cancellationToken);
        return Page();
    }

    private void SetViewData()
    {
        ViewData["CrmEmployee"] = Staff;
        ViewData["Title"] = "Staff Dashboard";
        ViewData["PageTitle"] = "My Dashboard";
        ViewData["ActiveMenu"] = "StaffDashboard";
    }

    private async Task LoadSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM bee_CrmEmployeeAttendance WHERE MerchantId = @MerchantId AND EmployeeId = @EmployeeId AND AttendanceDate = @Today) AS TodayClockRecords,
                (SELECT COUNT(*) FROM bee_CrmEmployeeLeave WHERE MerchantId = @MerchantId AND EmployeeId = @EmployeeId AND Status = 'Pending') AS PendingLeaveCount,
                (SELECT COUNT(*) FROM bee_CrmWorkflowApproval
                    WHERE MerchantId = @MerchantId AND Status = 'Pending'
                      AND (@CanApproveLeave = 1)
                      AND (ApproverEmployeeId = @EmployeeId OR ApproverRoleId = @RoleId)) AS PendingApprovalCount;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        command.Parameters.Add("@RoleId", MySqlDbType.Int64).Value = (object?)Staff.RoleId ?? DBNull.Value;
        command.Parameters.Add("@CanApproveLeave", MySqlDbType.Bit).Value = Staff.CanApproveLeave;
        command.Parameters.Add("@Today", MySqlDbType.Date).Value = GetMerchantToday();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            Summary = new StaffDashboardSummary(
                Convert.ToInt32(reader["TodayClockRecords"]),
                Convert.ToInt32(reader["PendingLeaveCount"]),
                Convert.ToInt32(reader["PendingApprovalCount"]));
        }
    }

    private DateTime GetMerchantToday()
    {
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Staff.TimeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone).Date;
        }
        catch
        {
            return DateTime.UtcNow.Date;
        }
    }
}

public sealed record StaffDashboardSummary(
    int TodayClockRecords = 0,
    int PendingLeaveCount = 0,
    int PendingApprovalCount = 0);
