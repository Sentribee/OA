using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class WorkflowsModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public long WorkflowId { get; private set; }

    public IReadOnlyList<CrmWorkflowStepRow> Steps { get; private set; } = [];

    public IReadOnlyList<CrmRoleOption> Roles { get; private set; } = [];

    public IReadOnlyList<CrmAttendanceEmployeeOption> Employees { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    public WorkflowStepInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(long? stepId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmWorkflowsStatus"] as string;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        WorkflowId = await EnsureLeaveWorkflowAsync(connection, cancellationToken);
        await LoadOptionsAsync(connection, cancellationToken);
        await LoadStepsAsync(connection, cancellationToken);
        if (stepId.HasValue)
        {
            await LoadStepInputAsync(connection, stepId.Value, cancellationToken);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveStepAsync(CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        if (!Input.ApproverRoleId.HasValue && !Input.ApproverEmployeeId.HasValue)
        {
            ModelState.AddModelError("Input.ApproverRoleId", "Select an approver role or employee.");
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        WorkflowId = await EnsureLeaveWorkflowAsync(connection, cancellationToken);
        if (!ModelState.IsValid)
        {
            await LoadOptionsAsync(connection, cancellationToken);
            await LoadStepsAsync(connection, cancellationToken);
            return Page();
        }

        var roleId = await NormalizeRoleIdAsync(connection, Input.ApproverRoleId, cancellationToken);
        var employeeId = await NormalizeEmployeeIdAsync(connection, Input.ApproverEmployeeId, cancellationToken);
        if (Input.Id > 0)
        {
            const string sql = """
                UPDATE bee_CrmWorkflowStep
                SET StepOrder = @StepOrder,
                    StepName = @StepName,
                    ApproverRoleId = @ApproverRoleId,
                    ApproverEmployeeId = @ApproverEmployeeId,
                    IsFinalApproval = @IsFinalApproval,
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @StepId AND WorkflowDefinitionId = @WorkflowDefinitionId AND MerchantId = @MerchantId;
                """;
            await using var command = new MySqlCommand(sql, connection);
            AddStepParameters(command, roleId, employeeId);
            command.Parameters.Add("@StepId", MySqlDbType.Int64).Value = Input.Id;
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmWorkflowsStatus"] = "Workflow step updated.";
        }
        else
        {
            const string sql = """
                INSERT INTO bee_CrmWorkflowStep
                    (WorkflowDefinitionId, MerchantId, StepOrder, StepName, ApproverRoleId, ApproverEmployeeId, IsFinalApproval)
                VALUES
                    (@WorkflowDefinitionId, @MerchantId, @StepOrder, @StepName, @ApproverRoleId, @ApproverEmployeeId, @IsFinalApproval);
                """;
            await using var command = new MySqlCommand(sql, connection);
            AddStepParameters(command, roleId, employeeId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmWorkflowsStatus"] = "Workflow step added.";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteStepAsync(long stepId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var workflowId = await EnsureLeaveWorkflowAsync(connection, cancellationToken);
        const string sql = "DELETE FROM bee_CrmWorkflowStep WHERE id = @StepId AND WorkflowDefinitionId = @WorkflowDefinitionId AND MerchantId = @MerchantId;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@StepId", MySqlDbType.Int64).Value = stepId;
        command.Parameters.Add("@WorkflowDefinitionId", MySqlDbType.Int64).Value = workflowId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await command.ExecuteNonQueryAsync(cancellationToken);
        TempData["CrmWorkflowsStatus"] = "Workflow step deleted.";
        return RedirectToPage();
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Workflows";
        ViewData["PageTitle"] = "Workflows";
        ViewData["ActiveMenu"] = "Workflows";
    }

    private async Task<long> EnsureLeaveWorkflowAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT id
            FROM bee_CrmWorkflowDefinition
            WHERE MerchantId = @MerchantId AND WorkflowKey = @WorkflowKey
            LIMIT 1;
            """;
        await using (var command = new MySqlCommand(selectSql, connection))
        {
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            command.Parameters.Add("@WorkflowKey", MySqlDbType.VarChar, 80).Value = CrmWorkflowEngine.LeaveWorkflowKey;
            var existing = await command.ExecuteScalarAsync(cancellationToken);
            if (existing is not null)
            {
                return Convert.ToInt64(existing);
            }
        }

        const string insertSql = """
            INSERT INTO bee_CrmWorkflowDefinition
                (ProjectId, MerchantId, WorkflowKey, WorkflowName, Status)
            VALUES
                (@ProjectId, @MerchantId, @WorkflowKey, 'Leave approval', 'Active');
            SELECT LAST_INSERT_ID();
            """;
        await using (var command = new MySqlCommand(insertSql, connection))
        {
            command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            command.Parameters.Add("@WorkflowKey", MySqlDbType.VarChar, 80).Value = CrmWorkflowEngine.LeaveWorkflowKey;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }
    }

    private async Task LoadOptionsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string roleSql = "SELECT id, RoleName FROM bee_CrmRole WHERE MerchantId = @MerchantId AND Status = 'Active' ORDER BY RoleName, id;";
        await using (var command = new MySqlCommand(roleSql, connection))
        {
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<CrmRoleOption>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new CrmRoleOption(reader.GetInt64(reader.GetOrdinal("id")), reader["RoleName"] as string ?? string.Empty));
            }

            Roles = rows;
        }

        const string employeeSql = "SELECT id, RealName, PreferredName, JobTitle FROM bee_CrmEmployee WHERE MerchantId = @MerchantId AND Status = 'Active' AND LoginEnabled = 1 ORDER BY RealName, id;";
        await using (var command = new MySqlCommand(employeeSql, connection))
        {
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
    }

    private async Task LoadStepsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT step.id, step.StepOrder, step.StepName, step.ApproverRoleId, step.ApproverEmployeeId,
                step.IsFinalApproval, role.RoleName, employee.RealName AS EmployeeName
            FROM bee_CrmWorkflowStep AS step
            LEFT JOIN bee_CrmRole AS role ON role.id = step.ApproverRoleId
            LEFT JOIN bee_CrmEmployee AS employee ON employee.id = step.ApproverEmployeeId
            WHERE step.WorkflowDefinitionId = @WorkflowDefinitionId AND step.MerchantId = @MerchantId
            ORDER BY step.StepOrder, step.id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@WorkflowDefinitionId", MySqlDbType.Int64).Value = WorkflowId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmWorkflowStepRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmWorkflowStepRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetInt32(reader.GetOrdinal("StepOrder")),
                reader["StepName"] as string ?? string.Empty,
                reader.IsDBNull(reader.GetOrdinal("ApproverRoleId")) ? null : reader.GetInt64(reader.GetOrdinal("ApproverRoleId")),
                reader.IsDBNull(reader.GetOrdinal("ApproverEmployeeId")) ? null : reader.GetInt64(reader.GetOrdinal("ApproverEmployeeId")),
                reader["RoleName"] as string,
                reader["EmployeeName"] as string,
                reader.GetBoolean(reader.GetOrdinal("IsFinalApproval"))));
        }

        Steps = rows;
    }

    private async Task LoadStepInputAsync(MySqlConnection connection, long stepId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, StepOrder, StepName, ApproverRoleId, ApproverEmployeeId, IsFinalApproval
            FROM bee_CrmWorkflowStep
            WHERE id = @StepId AND WorkflowDefinitionId = @WorkflowDefinitionId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@StepId", MySqlDbType.Int64).Value = stepId;
        command.Parameters.Add("@WorkflowDefinitionId", MySqlDbType.Int64).Value = WorkflowId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        Input = new WorkflowStepInput
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            StepOrder = reader.GetInt32(reader.GetOrdinal("StepOrder")),
            StepName = reader["StepName"] as string ?? string.Empty,
            ApproverRoleId = reader.IsDBNull(reader.GetOrdinal("ApproverRoleId")) ? null : reader.GetInt64(reader.GetOrdinal("ApproverRoleId")),
            ApproverEmployeeId = reader.IsDBNull(reader.GetOrdinal("ApproverEmployeeId")) ? null : reader.GetInt64(reader.GetOrdinal("ApproverEmployeeId")),
            IsFinalApproval = reader.GetBoolean(reader.GetOrdinal("IsFinalApproval"))
        };
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

    private async Task<long?> NormalizeEmployeeIdAsync(MySqlConnection connection, long? employeeId, CancellationToken cancellationToken)
    {
        if (!employeeId.HasValue)
        {
            return null;
        }

        const string sql = "SELECT id FROM bee_CrmEmployee WHERE id = @EmployeeId AND MerchantId = @MerchantId LIMIT 1;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId.Value;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : employeeId.Value;
    }

    private void AddStepParameters(MySqlCommand command, long? roleId, long? employeeId)
    {
        command.Parameters.Add("@WorkflowDefinitionId", MySqlDbType.Int64).Value = WorkflowId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@StepOrder", MySqlDbType.Int32).Value = Input.StepOrder;
        command.Parameters.Add("@StepName", MySqlDbType.VarChar, 160).Value = Input.StepName.Trim();
        command.Parameters.Add("@ApproverRoleId", MySqlDbType.Int64).Value = (object?)roleId ?? DBNull.Value;
        command.Parameters.Add("@ApproverEmployeeId", MySqlDbType.Int64).Value = (object?)employeeId ?? DBNull.Value;
        command.Parameters.Add("@IsFinalApproval", MySqlDbType.Bit).Value = Input.IsFinalApproval;
    }
}

public sealed class WorkflowStepInput
{
    public long Id { get; set; }

    [Range(1, 100)]
    public int StepOrder { get; set; } = 1;

    [Required]
    [StringLength(160)]
    public string StepName { get; set; } = string.Empty;

    public long? ApproverRoleId { get; set; }

    public long? ApproverEmployeeId { get; set; }

    public bool IsFinalApproval { get; set; }
}

public sealed record CrmWorkflowStepRow(
    long Id,
    int StepOrder,
    string StepName,
    long? ApproverRoleId,
    long? ApproverEmployeeId,
    string? RoleName,
    string? EmployeeName,
    bool IsFinalApproval);
