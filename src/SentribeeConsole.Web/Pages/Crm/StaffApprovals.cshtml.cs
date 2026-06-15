using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class StaffApprovalsModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmEmployeeSession Staff { get; private set; } = null!;

    public IReadOnlyList<CrmApprovalRow> Approvals { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    [StringLength(1000)]
    public string? DecisionNote { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var staff = await RequireStaffAsync(cancellationToken);
        if (staff.Result is not null)
        {
            return staff.Result;
        }

        Staff = staff.Staff!;
        SetViewData();
        StatusMessage = TempData["CrmStaffApprovalStatus"] as string;
        await LoadApprovalsAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostDecideAsync(long approvalId, string decision, CancellationToken cancellationToken)
    {
        var staff = await RequireStaffAsync(cancellationToken);
        if (staff.Result is not null)
        {
            return staff.Result;
        }

        Staff = staff.Staff!;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var accepted = await CrmWorkflowEngine.DecideApprovalAsync(connection, Staff, approvalId, decision, DecisionNote, cancellationToken);
        TempData["CrmStaffApprovalStatus"] = accepted ? $"Leave {decision.ToLowerInvariant()}." : "Approval task was not found.";
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

        if (!staff.CanApproveLeave)
        {
            return (null, RedirectToPage("/Crm/StaffDashboard"));
        }

        return (staff, null);
    }

    private void SetViewData()
    {
        ViewData["CrmEmployee"] = Staff;
        ViewData["Title"] = "Approvals";
        ViewData["PageTitle"] = "Approvals";
        ViewData["ActiveMenu"] = "StaffApprovals";
    }

    private async Task LoadApprovalsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT approval.id, step.StepName, leave_record.LeaveType, leave_record.StartDate,
                leave_record.EndDate, leave_record.Hours, leave_record.IsPaid, leave_record.Reason,
                requester.RealName AS RequesterName
            FROM bee_CrmWorkflowApproval AS approval
            INNER JOIN bee_CrmWorkflowRequest AS request ON request.id = approval.WorkflowRequestId
            INNER JOIN bee_CrmWorkflowStep AS step ON step.id = approval.StepId
            INNER JOIN bee_CrmEmployeeLeave AS leave_record ON leave_record.id = request.EntityId
            INNER JOIN bee_CrmEmployee AS requester ON requester.id = leave_record.EmployeeId
            WHERE approval.MerchantId = @MerchantId
              AND approval.Status = 'Pending'
              AND request.EntityType = 'Leave'
              AND (approval.ApproverEmployeeId = @EmployeeId OR approval.ApproverRoleId = @RoleId)
            ORDER BY approval.CreatedAtUtc, approval.id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        command.Parameters.Add("@RoleId", MySqlDbType.Int64).Value = (object?)Staff.RoleId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmApprovalRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmApprovalRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["StepName"] as string ?? string.Empty,
                reader["RequesterName"] as string ?? string.Empty,
                reader["LeaveType"] as string ?? string.Empty,
                reader.GetDateTime(reader.GetOrdinal("StartDate")),
                reader.GetDateTime(reader.GetOrdinal("EndDate")),
                reader.GetDecimal(reader.GetOrdinal("Hours")),
                reader.GetBoolean(reader.GetOrdinal("IsPaid")),
                reader["Reason"] as string));
        }

        Approvals = rows;
    }
}

public sealed record CrmApprovalRow(
    long Id,
    string StepName,
    string RequesterName,
    string LeaveType,
    DateTime StartDate,
    DateTime EndDate,
    decimal Hours,
    bool IsPaid,
    string? Reason);
