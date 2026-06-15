using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Crm;

public class StaffProfileModel(
    IConfiguration configuration,
    IFileStorageService storageService) : CrmMerchantPageModel(configuration)
{
    private const long MaxAvatarLength = 3 * 1024 * 1024;

    public CrmEmployeeSession Staff { get; private set; } = null!;

    public EmployeeProfileDetails? ProfileDetails { get; private set; }

    public IReadOnlyList<EmployeeProfileChangeRequestRow> Requests { get; private set; } = [];

    public bool IsFirstProfileCompletion => !Staff.ProfileCompletedAtUtc.HasValue;

    public string? StatusMessage { get; private set; }

    [BindProperty]
    public EmployeeProfileInput Input { get; set; } = new();

    [BindProperty]
    public PasswordChangeInput PasswordInput { get; set; } = new();

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

        Staff = staff;
        SetViewData();
        StatusMessage = TempData["CrmStaffProfileStatus"] as string;
        await LoadProfileAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveProfileAsync(IFormFile? avatar, CancellationToken cancellationToken)
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

        Staff = staff;
        SetViewData();
        RemoveModelStatePrefix(nameof(PasswordInput));
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        ProfileDetails = await EmployeeProfileSupport.LoadEmployeeProfileDetailsAsync(connection, Staff.EmployeeId, Staff.MerchantId, cancellationToken);
        if (ProfileDetails is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        if (!ModelState.IsValid)
        {
            StatusMessage = "Please check the highlighted fields.";
            Requests = await EmployeeProfileSupport.LoadProfileRequestsAsync(connection, Staff.MerchantId, Staff.EmployeeId, 10, cancellationToken);
            return Page();
        }

        var avatarUrl = Input.AvatarUrl;
        if (avatar is { Length: > 0 })
        {
            avatarUrl = await UploadAvatarAsync(avatar, cancellationToken);
            if (avatarUrl is null)
            {
                StatusMessage = "Please check the highlighted fields.";
                Requests = await EmployeeProfileSupport.LoadProfileRequestsAsync(connection, Staff.MerchantId, Staff.EmployeeId, 10, cancellationToken);
                return Page();
            }
        }

        var requestedProfile = EmployeeProfileSupport.ToSnapshot(Input, avatarUrl);
        if (!Staff.ProfileCompletedAtUtc.HasValue)
        {
            const string updateSql = """
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
                    ProfileCompletedAtUtc = UTC_TIMESTAMP(6),
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @EmployeeId AND MerchantId = @MerchantId;
                """;
            await using var updateCommand = new MySqlCommand(updateSql, connection);
            EmployeeProfileSupport.AddSnapshotParameters(updateCommand, requestedProfile);
            updateCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
            updateCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            TempData["CrmStaffStatus"] = "Profile completed.";
            return RedirectToPage("/Crm/StaffDashboard");
        }

        await EmployeeProfileSupport.InsertProfileRequestAsync(
            connection,
            Staff.ProjectId,
            Staff.MerchantId,
            Staff.EmployeeId,
            Staff.EmployeeId,
            null,
            ProfileDetails.Profile,
            requestedProfile,
            cancellationToken);

        TempData["CrmStaffProfileStatus"] = "Profile changes submitted for highest admin approval.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken cancellationToken)
    {
        var staff = await LoadCurrentEmployeeAsync(cancellationToken);
        if (staff is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Staff = staff;
        SetViewData();
        RemoveModelStatePrefix(nameof(Input));
        if (!IsPasswordInputValid())
        {
            StatusMessage = "Password must be at least 8 characters and both entries must match.";
            await LoadProfileAsync(cancellationToken);
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string findSql = "SELECT WorkEmail, EmployeePasswordHash FROM bee_CrmEmployee WHERE id = @EmployeeId AND MerchantId = @MerchantId LIMIT 1;";
        await using var findCommand = new MySqlCommand(findSql, connection);
        findCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        findCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        await using var reader = await findCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return RedirectToPage("/Crm/Login");
        }

        var email = reader["WorkEmail"] as string ?? Staff.WorkEmail;
        var storedHash = reader["EmployeePasswordHash"] as string;
        await reader.CloseAsync();
        var passwordUser = new CrmEmployeePasswordUser(Staff.EmployeeId, email);
        if (string.IsNullOrWhiteSpace(storedHash) ||
            CreateEmployeePasswordHasher().VerifyHashedPassword(passwordUser, storedHash, PasswordInput.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            StatusMessage = "Current password is incorrect.";
            await LoadProfileAsync(cancellationToken);
            return Page();
        }

        var nextHash = CreateEmployeePasswordHasher().HashPassword(passwordUser, PasswordInput.NewPassword);
        const string updateSql = """
            UPDATE bee_CrmEmployee
            SET EmployeePasswordHash = @PasswordHash,
                MustChangePassword = 0,
                PasswordUpdatedAtUtc = UTC_TIMESTAMP(6),
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @EmployeeId AND MerchantId = @MerchantId;
            """;
        await using var updateCommand = new MySqlCommand(updateSql, connection);
        updateCommand.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 512).Value = nextHash;
        updateCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        updateCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        TempData["CrmStaffProfileStatus"] = "Password changed.";
        return RedirectToPage();
    }

    private async Task LoadProfileAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        ProfileDetails = await EmployeeProfileSupport.LoadEmployeeProfileDetailsAsync(connection, Staff.EmployeeId, Staff.MerchantId, cancellationToken);
        if (ProfileDetails is not null)
        {
            Input = EmployeeProfileSupport.ToInput(ProfileDetails.Profile);
            Requests = await EmployeeProfileSupport.LoadProfileRequestsAsync(connection, Staff.MerchantId, Staff.EmployeeId, 10, cancellationToken);
        }
    }

    private async Task<string?> UploadAvatarAsync(IFormFile avatar, CancellationToken cancellationToken)
    {
        if (avatar.Length > MaxAvatarLength || !avatar.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(avatar), "Upload an image under 3 MB.");
            return null;
        }

        var extension = Path.GetExtension(avatar.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = avatar.ContentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        }

        await using var stream = avatar.OpenReadStream();
        var stored = await storageService.UploadAsync(
            stream,
            avatar.ContentType,
            extension,
            $"oa/{Staff.ProjectId}/{Staff.MerchantId}/staff/{Staff.EmployeeId}/avatar",
            cancellationToken);
        return stored.PublicUrl;
    }

    private void SetViewData()
    {
        ViewData["CrmEmployee"] = Staff;
        ViewData["Title"] = "My Profile";
        ViewData["PageTitle"] = "My Profile";
        ViewData["ActiveMenu"] = "StaffProfile";
    }

    private void RemoveModelStatePrefix(string prefix)
    {
        foreach (var key in ModelState.Keys.Where(key => key.StartsWith($"{prefix}.", StringComparison.Ordinal)).ToList())
        {
            ModelState.Remove(key);
        }
    }

    private bool IsPasswordInputValid()
    {
        return !string.IsNullOrWhiteSpace(PasswordInput.CurrentPassword) &&
            PasswordInput.NewPassword.Length >= 8 &&
            string.Equals(PasswordInput.NewPassword, PasswordInput.ConfirmPassword, StringComparison.Ordinal);
    }
}
