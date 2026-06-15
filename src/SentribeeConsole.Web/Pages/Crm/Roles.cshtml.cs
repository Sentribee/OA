using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class RolesModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public IReadOnlyList<CrmRoleRow> Roles { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    public RoleInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(long? roleId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmRolesStatus"] as string;
        await LoadRolesAsync(cancellationToken);
        if (roleId.HasValue)
        {
            await LoadRoleInputAsync(roleId.Value, cancellationToken);
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
        if (!ModelState.IsValid)
        {
            await LoadRolesAsync(cancellationToken);
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (Input.Id > 0)
        {
            const string sql = """
                UPDATE bee_CrmRole
                SET RoleName = @RoleName,
                    Description = @Description,
                    CanApproveLeave = @CanApproveLeave,
                    CanManageAttendance = @CanManageAttendance,
                    CanManageEmployees = @CanManageEmployees,
                    Status = @Status,
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @RoleId AND MerchantId = @MerchantId;
                """;
            await using var command = new MySqlCommand(sql, connection);
            AddSaveParameters(command);
            command.Parameters.Add("@RoleId", MySqlDbType.Int64).Value = Input.Id;
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmRolesStatus"] = "Role updated.";
        }
        else
        {
            const string sql = """
                INSERT INTO bee_CrmRole
                    (ProjectId, MerchantId, RoleName, Description, CanApproveLeave,
                     CanManageAttendance, CanManageEmployees, Status)
                VALUES
                    (@ProjectId, @MerchantId, @RoleName, @Description, @CanApproveLeave,
                     @CanManageAttendance, @CanManageEmployees, @Status);
                """;
            await using var command = new MySqlCommand(sql, connection);
            AddSaveParameters(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmRolesStatus"] = "Role added.";
        }

        return RedirectToPage();
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Roles";
        ViewData["PageTitle"] = "Roles";
        ViewData["ActiveMenu"] = "Roles";
    }

    private async Task LoadRolesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, RoleName, Description, CanApproveLeave, CanManageAttendance, CanManageEmployees, Status
            FROM bee_CrmRole
            WHERE MerchantId = @MerchantId
            ORDER BY Status = 'Active' DESC, RoleName, id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmRoleRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmRoleRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["RoleName"] as string ?? string.Empty,
                reader["Description"] as string,
                reader.GetBoolean(reader.GetOrdinal("CanApproveLeave")),
                reader.GetBoolean(reader.GetOrdinal("CanManageAttendance")),
                reader.GetBoolean(reader.GetOrdinal("CanManageEmployees")),
                reader["Status"] as string ?? string.Empty));
        }

        Roles = rows;
    }

    private async Task LoadRoleInputAsync(long roleId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, RoleName, Description, CanApproveLeave, CanManageAttendance, CanManageEmployees, Status
            FROM bee_CrmRole
            WHERE id = @RoleId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@RoleId", MySqlDbType.Int64).Value = roleId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        Input = new RoleInput
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            RoleName = reader["RoleName"] as string ?? string.Empty,
            Description = reader["Description"] as string,
            CanApproveLeave = reader.GetBoolean(reader.GetOrdinal("CanApproveLeave")),
            CanManageAttendance = reader.GetBoolean(reader.GetOrdinal("CanManageAttendance")),
            CanManageEmployees = reader.GetBoolean(reader.GetOrdinal("CanManageEmployees")),
            Status = reader["Status"] as string ?? "Active"
        };
    }

    private void AddSaveParameters(MySqlCommand command)
    {
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@RoleName", MySqlDbType.VarChar, 120).Value = Input.RoleName.Trim();
        command.Parameters.Add("@Description", MySqlDbType.VarChar, 500).Value = DbValue(Input.Description);
        command.Parameters.Add("@CanApproveLeave", MySqlDbType.Bit).Value = Input.CanApproveLeave;
        command.Parameters.Add("@CanManageAttendance", MySqlDbType.Bit).Value = Input.CanManageAttendance;
        command.Parameters.Add("@CanManageEmployees", MySqlDbType.Bit).Value = Input.CanManageEmployees;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = string.Equals(Input.Status, "Inactive", StringComparison.OrdinalIgnoreCase) ? "Inactive" : "Active";
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

public sealed class RoleInput
{
    public long Id { get; set; }

    [Required]
    [StringLength(120)]
    public string RoleName { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public bool CanApproveLeave { get; set; }

    public bool CanManageAttendance { get; set; }

    public bool CanManageEmployees { get; set; }

    [Required]
    public string Status { get; set; } = "Active";
}

public sealed record CrmRoleRow(
    long Id,
    string RoleName,
    string? Description,
    bool CanApproveLeave,
    bool CanManageAttendance,
    bool CanManageEmployees,
    string Status);
