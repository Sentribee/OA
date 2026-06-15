using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class StaffChangePasswordModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmEmployeeSession Staff { get; private set; } = null!;

    public string? AlertMessage { get; private set; }

    [BindProperty]
    [Required]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required]
    [Compare(nameof(NewPassword))]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var staff = await LoadCurrentEmployeeAsync(cancellationToken);
        if (staff is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Staff = staff;
        SetViewData();
        AlertMessage = TempData["CrmStaffPasswordStatus"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var staff = await LoadCurrentEmployeeAsync(cancellationToken);
        if (staff is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Staff = staff;
        SetViewData();
        if (!ModelState.IsValid || NewPassword.Length < 8)
        {
            AlertMessage = "Password must be at least 8 characters and both entries must match.";
            return Page();
        }

        var passwordUser = new CrmEmployeePasswordUser(Staff.EmployeeId, Staff.WorkEmail);
        var passwordHash = CreateEmployeePasswordHasher().HashPassword(passwordUser, NewPassword);
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE bee_CrmEmployee
            SET EmployeePasswordHash = @PasswordHash,
                MustChangePassword = 0,
                PasswordUpdatedAtUtc = UTC_TIMESTAMP(6),
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @EmployeeId AND MerchantId = @MerchantId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 512).Value = passwordHash;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (!Staff.ProfileCompletedAtUtc.HasValue)
        {
            TempData["CrmStaffProfileStatus"] = "Password updated. Complete your staff profile to continue.";
            return RedirectToPage("/Crm/StaffProfile");
        }

        TempData["CrmStaffStatus"] = "Password updated.";
        return RedirectToPage("/Crm/StaffDashboard");
    }

    private void SetViewData()
    {
        ViewData["CrmEmployee"] = Staff;
        ViewData["Title"] = "Change Password";
        ViewData["PageTitle"] = "Change Password";
        ViewData["ActiveMenu"] = "StaffDashboard";
    }
}
