using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class ProfileRequestsModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public IReadOnlyList<EmployeeProfileChangeRequestRow> Requests { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    [StringLength(1000)]
    public string? DecisionNote { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmProfileRequestsStatus"] as string;
        await LoadRequestsAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostDecideAsync(long requestId, string decision, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        var approved = string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase);
        var normalizedDecision = approved ? "Approved" : "Rejected";

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            SELECT id, EmployeeId, Status, RequestedProfileJson
            FROM bee_CrmEmployeeProfileChangeRequest
            WHERE id = @RequestId AND MerchantId = @MerchantId
            FOR UPDATE;
            """;
        await using var selectCommand = new MySqlCommand(selectSql, connection, transaction);
        selectCommand.Parameters.Add("@RequestId", MySqlDbType.Int64).Value = requestId;
        selectCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["CrmProfileRequestsStatus"] = "Profile request was not found.";
            return RedirectToPage();
        }

        var employeeId = reader.GetInt64(reader.GetOrdinal("EmployeeId"));
        var status = reader["Status"] as string ?? string.Empty;
        var requestedJson = reader["RequestedProfileJson"] as string;
        await reader.CloseAsync();

        if (!string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["CrmProfileRequestsStatus"] = "Profile request has already been decided.";
            return RedirectToPage();
        }

        if (approved)
        {
            var requestedProfile = EmployeeProfileSupport.DeserializeSnapshot(requestedJson);
            if (requestedProfile is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["CrmProfileRequestsStatus"] = "Profile request data is invalid.";
                return RedirectToPage();
            }

            const string updateEmployeeSql = """
                UPDATE bee_CrmEmployee
                SET RealName = @RealName,
                    PreferredName = @PreferredName,
                    AvatarUrl = @AvatarUrl,
                    ResidentialAddress = @ResidentialAddress,
                    Phone = @Phone,
                    WorkEmail = @WorkEmail,
                    PrivateEmail = @PrivateEmail,
                    GstNumber = @GstNumber,
                    BankAccountNumber = @BankAccountNumber,
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @EmployeeId AND MerchantId = @MerchantId;
                """;
            await using var updateEmployeeCommand = new MySqlCommand(updateEmployeeSql, connection, transaction);
            EmployeeProfileSupport.AddSnapshotParameters(updateEmployeeCommand, requestedProfile);
            updateEmployeeCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
            updateEmployeeCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            await updateEmployeeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string updateRequestSql = """
            UPDATE bee_CrmEmployeeProfileChangeRequest
            SET Status = @Status,
                DecisionByMerchantId = @DecisionByMerchantId,
                DecisionAtUtc = UTC_TIMESTAMP(6),
                DecisionNote = @DecisionNote,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @RequestId AND MerchantId = @MerchantId;
            """;
        await using var updateRequestCommand = new MySqlCommand(updateRequestSql, connection, transaction);
        updateRequestCommand.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = normalizedDecision;
        updateRequestCommand.Parameters.Add("@DecisionByMerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        updateRequestCommand.Parameters.Add("@DecisionNote", MySqlDbType.VarChar, 1000).Value = EmployeeProfileSupport.DbValue(DecisionNote);
        updateRequestCommand.Parameters.Add("@RequestId", MySqlDbType.Int64).Value = requestId;
        updateRequestCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await updateRequestCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        TempData["CrmProfileRequestsStatus"] = approved ? "Profile request approved and employee profile updated." : "Profile request rejected.";
        return RedirectToPage();
    }

    private async Task LoadRequestsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        Requests = await EmployeeProfileSupport.LoadProfileRequestsAsync(connection, Merchant.Id, null, 50, cancellationToken);
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Profile Requests";
        ViewData["PageTitle"] = "Profile Requests";
        ViewData["ActiveMenu"] = "ProfileRequests";
    }
}
