using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public static class CrmWorkflowEngine
{
    public const string LeaveWorkflowKey = "leave-request";

    public static async Task StartLeaveWorkflowAsync(
        MySqlConnection connection,
        CrmEmployeeSession staff,
        long leaveId,
        CancellationToken cancellationToken)
    {
        var workflowId = await GetActiveWorkflowIdAsync(connection, staff.MerchantId, LeaveWorkflowKey, cancellationToken);
        if (!workflowId.HasValue)
        {
            return;
        }

        var firstStep = await GetNextStepAsync(connection, workflowId.Value, null, cancellationToken);
        if (firstStep is null)
        {
            return;
        }

        const string requestSql = """
            INSERT INTO bee_CrmWorkflowRequest
                (ProjectId, MerchantId, WorkflowDefinitionId, EntityType, EntityId,
                 RequestedByEmployeeId, CurrentStepId, Status)
            VALUES
                (@ProjectId, @MerchantId, @WorkflowDefinitionId, 'Leave', @LeaveId,
                 @RequestedByEmployeeId, @CurrentStepId, 'Pending');
            SELECT LAST_INSERT_ID();
            """;
        await using var requestCommand = new MySqlCommand(requestSql, connection);
        requestCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = staff.ProjectId;
        requestCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = staff.MerchantId;
        requestCommand.Parameters.Add("@WorkflowDefinitionId", MySqlDbType.Int64).Value = workflowId.Value;
        requestCommand.Parameters.Add("@LeaveId", MySqlDbType.Int64).Value = leaveId;
        requestCommand.Parameters.Add("@RequestedByEmployeeId", MySqlDbType.Int64).Value = staff.EmployeeId;
        requestCommand.Parameters.Add("@CurrentStepId", MySqlDbType.Int64).Value = firstStep.Id;
        var requestId = Convert.ToInt64(await requestCommand.ExecuteScalarAsync(cancellationToken));
        await CreateApprovalAsync(connection, requestId, staff.MerchantId, firstStep, cancellationToken);
    }

    public static async Task<bool> DecideApprovalAsync(
        MySqlConnection connection,
        CrmEmployeeSession staff,
        long approvalId,
        string decision,
        string? note,
        CancellationToken cancellationToken)
    {
        var approval = await LoadPendingApprovalAsync(connection, staff, approvalId, cancellationToken);
        if (approval is null)
        {
            return false;
        }

        var approved = string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase);
        var finalStatus = approved ? "Approved" : "Rejected";
        const string approvalSql = """
            UPDATE bee_CrmWorkflowApproval
            SET Status = @Status,
                DecisionByEmployeeId = @DecisionByEmployeeId,
                DecisionAtUtc = UTC_TIMESTAMP(6),
                DecisionNote = @DecisionNote
            WHERE id = @ApprovalId AND MerchantId = @MerchantId AND Status = 'Pending';
            """;
        await using (var command = new MySqlCommand(approvalSql, connection))
        {
            command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = finalStatus;
            command.Parameters.Add("@DecisionByEmployeeId", MySqlDbType.Int64).Value = staff.EmployeeId;
            command.Parameters.Add("@DecisionNote", MySqlDbType.VarChar, 1000).Value = DbValue(note);
            command.Parameters.Add("@ApprovalId", MySqlDbType.Int64).Value = approvalId;
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = staff.MerchantId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!approved)
        {
            await FinishRequestAsync(connection, staff.MerchantId, approval.RequestId, approval.LeaveId, "Rejected", cancellationToken);
            return true;
        }

        var nextStep = await GetNextStepAsync(connection, approval.WorkflowDefinitionId, approval.StepOrder, cancellationToken);
        if (nextStep is null)
        {
            await FinishRequestAsync(connection, staff.MerchantId, approval.RequestId, approval.LeaveId, "Approved", cancellationToken);
            return true;
        }

        const string requestSql = """
            UPDATE bee_CrmWorkflowRequest
            SET CurrentStepId = @CurrentStepId
            WHERE id = @RequestId AND MerchantId = @MerchantId;
            """;
        await using (var command = new MySqlCommand(requestSql, connection))
        {
            command.Parameters.Add("@CurrentStepId", MySqlDbType.Int64).Value = nextStep.Id;
            command.Parameters.Add("@RequestId", MySqlDbType.Int64).Value = approval.RequestId;
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = staff.MerchantId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await CreateApprovalAsync(connection, approval.RequestId, staff.MerchantId, nextStep, cancellationToken);
        return true;
    }

    private static async Task<long?> GetActiveWorkflowIdAsync(
        MySqlConnection connection,
        long merchantId,
        string workflowKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM bee_CrmWorkflowDefinition
            WHERE MerchantId = @MerchantId AND WorkflowKey = @WorkflowKey AND Status = 'Active'
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        command.Parameters.Add("@WorkflowKey", MySqlDbType.VarChar, 80).Value = workflowKey;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    private static async Task<WorkflowStepRef?> GetNextStepAsync(
        MySqlConnection connection,
        long workflowId,
        int? currentStepOrder,
        CancellationToken cancellationToken)
    {
        var sql = currentStepOrder.HasValue
            ? """
                SELECT id, StepOrder, ApproverRoleId, ApproverEmployeeId
                FROM bee_CrmWorkflowStep
                WHERE WorkflowDefinitionId = @WorkflowDefinitionId AND StepOrder > @StepOrder
                ORDER BY StepOrder, id
                LIMIT 1;
                """
            : """
                SELECT id, StepOrder, ApproverRoleId, ApproverEmployeeId
                FROM bee_CrmWorkflowStep
                WHERE WorkflowDefinitionId = @WorkflowDefinitionId
                ORDER BY StepOrder, id
                LIMIT 1;
                """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@WorkflowDefinitionId", MySqlDbType.Int64).Value = workflowId;
        if (currentStepOrder.HasValue)
        {
            command.Parameters.Add("@StepOrder", MySqlDbType.Int32).Value = currentStepOrder.Value;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WorkflowStepRef(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("StepOrder")),
            reader.IsDBNull(reader.GetOrdinal("ApproverRoleId")) ? null : reader.GetInt64(reader.GetOrdinal("ApproverRoleId")),
            reader.IsDBNull(reader.GetOrdinal("ApproverEmployeeId")) ? null : reader.GetInt64(reader.GetOrdinal("ApproverEmployeeId")));
    }

    private static async Task CreateApprovalAsync(
        MySqlConnection connection,
        long requestId,
        long merchantId,
        WorkflowStepRef step,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_CrmWorkflowApproval
                (WorkflowRequestId, MerchantId, StepId, ApproverRoleId, ApproverEmployeeId, Status)
            VALUES
                (@WorkflowRequestId, @MerchantId, @StepId, @ApproverRoleId, @ApproverEmployeeId, 'Pending');
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@WorkflowRequestId", MySqlDbType.Int64).Value = requestId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        command.Parameters.Add("@StepId", MySqlDbType.Int64).Value = step.Id;
        command.Parameters.Add("@ApproverRoleId", MySqlDbType.Int64).Value = (object?)step.ApproverRoleId ?? DBNull.Value;
        command.Parameters.Add("@ApproverEmployeeId", MySqlDbType.Int64).Value = (object?)step.ApproverEmployeeId ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PendingApprovalRef?> LoadPendingApprovalAsync(
        MySqlConnection connection,
        CrmEmployeeSession staff,
        long approvalId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT approval.WorkflowRequestId, request.WorkflowDefinitionId, request.EntityId,
                step.StepOrder, approval.ApproverRoleId, approval.ApproverEmployeeId
            FROM bee_CrmWorkflowApproval AS approval
            INNER JOIN bee_CrmWorkflowRequest AS request ON request.id = approval.WorkflowRequestId
            INNER JOIN bee_CrmWorkflowStep AS step ON step.id = approval.StepId
            WHERE approval.id = @ApprovalId
              AND approval.MerchantId = @MerchantId
              AND approval.Status = 'Pending'
              AND request.EntityType = 'Leave'
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ApprovalId", MySqlDbType.Int64).Value = approvalId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = staff.MerchantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        long? approverRoleId = reader.IsDBNull(reader.GetOrdinal("ApproverRoleId")) ? null : reader.GetInt64(reader.GetOrdinal("ApproverRoleId"));
        long? approverEmployeeId = reader.IsDBNull(reader.GetOrdinal("ApproverEmployeeId")) ? null : reader.GetInt64(reader.GetOrdinal("ApproverEmployeeId"));
        var roleMatches = approverRoleId.HasValue && staff.RoleId == approverRoleId.Value;
        var employeeMatches = approverEmployeeId.HasValue && staff.EmployeeId == approverEmployeeId.Value;
        if (!staff.CanApproveLeave || (!roleMatches && !employeeMatches))
        {
            return null;
        }

        return new PendingApprovalRef(
            reader.GetInt64(reader.GetOrdinal("WorkflowRequestId")),
            reader.GetInt64(reader.GetOrdinal("WorkflowDefinitionId")),
            reader.GetInt64(reader.GetOrdinal("EntityId")),
            reader.GetInt32(reader.GetOrdinal("StepOrder")));
    }

    private static async Task FinishRequestAsync(
        MySqlConnection connection,
        long merchantId,
        long requestId,
        long leaveId,
        string status,
        CancellationToken cancellationToken)
    {
        const string requestSql = """
            UPDATE bee_CrmWorkflowRequest
            SET Status = @Status, CompletedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @RequestId AND MerchantId = @MerchantId;
            """;
        await using (var command = new MySqlCommand(requestSql, connection))
        {
            command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = status;
            command.Parameters.Add("@RequestId", MySqlDbType.Int64).Value = requestId;
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string leaveSql = """
            UPDATE bee_CrmEmployeeLeave
            SET Status = @Status, UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @LeaveId AND MerchantId = @MerchantId;
            """;
        await using (var command = new MySqlCommand(leaveSql, connection))
        {
            command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = status;
            command.Parameters.Add("@LeaveId", MySqlDbType.Int64).Value = leaveId;
            command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private sealed record WorkflowStepRef(long Id, int StepOrder, long? ApproverRoleId, long? ApproverEmployeeId);

    private sealed record PendingApprovalRef(long RequestId, long WorkflowDefinitionId, long LeaveId, int StepOrder);
}
