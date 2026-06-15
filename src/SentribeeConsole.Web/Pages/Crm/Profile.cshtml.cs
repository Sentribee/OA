using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Pages.Crm;

public class ProfileModel(
    IConfiguration configuration,
    IFileStorageService storageService) : CrmMerchantPageModel(configuration)
{
    private const long MaxAvatarLength = 3 * 1024 * 1024;

    public CrmMerchantSession Merchant { get; private set; } = null!;

    public EmployeeProfileDetails? ProfileDetails { get; private set; }

    public IReadOnlyList<EmployeeProfileChangeRequestRow> Requests { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    public EmployeeProfileInput Input { get; set; } = new();

    [BindProperty]
    public PasswordChangeInput PasswordInput { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmProfileStatus"] as string;
        await LoadProfileAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveProfileAsync(IFormFile? avatar, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        RemoveModelStatePrefix(nameof(PasswordInput));
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var employeeId = await EnsureAdminEmployeeProfileAsync(connection, cancellationToken);
        ProfileDetails = await EmployeeProfileSupport.LoadEmployeeProfileDetailsAsync(connection, employeeId, Merchant.Id, cancellationToken);
        if (ProfileDetails is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        if (!ModelState.IsValid)
        {
            StatusMessage = "Please check the highlighted fields.";
            Requests = await EmployeeProfileSupport.LoadProfileRequestsAsync(connection, Merchant.Id, employeeId, 10, cancellationToken);
            return Page();
        }

        var avatarUrl = Input.AvatarUrl;
        if (avatar is { Length: > 0 })
        {
            avatarUrl = await UploadAvatarAsync(avatar, employeeId, cancellationToken);
            if (avatarUrl is null)
            {
                StatusMessage = "Please check the highlighted fields.";
                Requests = await EmployeeProfileSupport.LoadProfileRequestsAsync(connection, Merchant.Id, employeeId, 10, cancellationToken);
                return Page();
            }
        }

        var requestedProfile = EmployeeProfileSupport.ToSnapshot(Input, avatarUrl);
        await EmployeeProfileSupport.InsertProfileRequestAsync(
            connection,
            Merchant.ProjectId,
            Merchant.Id,
            employeeId,
            null,
            Merchant.Id,
            ProfileDetails.Profile,
            requestedProfile,
            cancellationToken);

        TempData["CrmProfileStatus"] = "Profile changes submitted for highest admin approval.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangePasswordAsync(CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
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
        const string findSql = "SELECT Email, PasswordHash FROM bee_CrmMerchant WHERE id = @MerchantId LIMIT 1;";
        await using var findCommand = new MySqlCommand(findSql, connection);
        findCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await findCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return RedirectToPage("/Crm/Login");
        }

        var email = reader["Email"] as string ?? Merchant.Email;
        var storedHash = reader["PasswordHash"] as string;
        await reader.CloseAsync();
        var passwordUser = new CrmMerchantPasswordUser(Merchant.Id, email);
        if (string.IsNullOrWhiteSpace(storedHash) ||
            CreatePasswordHasher().VerifyHashedPassword(passwordUser, storedHash, PasswordInput.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            StatusMessage = "Current password is incorrect.";
            await LoadProfileAsync(cancellationToken);
            return Page();
        }

        var nextHash = CreatePasswordHasher().HashPassword(passwordUser, PasswordInput.NewPassword);
        const string updateSql = """
            UPDATE bee_CrmMerchant
            SET PasswordHash = @PasswordHash, UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @MerchantId;
            """;
        await using var updateCommand = new MySqlCommand(updateSql, connection);
        updateCommand.Parameters.Add("@PasswordHash", MySqlDbType.VarChar, 512).Value = nextHash;
        updateCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        TempData["CrmProfileStatus"] = "Password changed.";
        return RedirectToPage();
    }

    private async Task LoadProfileAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var employeeId = await EnsureAdminEmployeeProfileAsync(connection, cancellationToken);
        ProfileDetails = await EmployeeProfileSupport.LoadEmployeeProfileDetailsAsync(connection, employeeId, Merchant.Id, cancellationToken);
        if (ProfileDetails is not null)
        {
            Input = EmployeeProfileSupport.ToInput(ProfileDetails.Profile);
            Requests = await EmployeeProfileSupport.LoadProfileRequestsAsync(connection, Merchant.Id, employeeId, 10, cancellationToken);
        }
    }

    private async Task<long> EnsureAdminEmployeeProfileAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT employee.id
            FROM bee_CrmEmployee AS employee
            LEFT JOIN bee_CrmRole AS role ON role.id = employee.RoleId
            WHERE employee.MerchantId = @MerchantId
              AND (
                employee.WorkEmail = @Email
                OR employee.PrivateEmail = @Email
                OR role.RoleName IN ('最高管理员', 'Super Admin', 'Owner')
              )
            ORDER BY
                (employee.WorkEmail = @Email OR employee.PrivateEmail = @Email) DESC,
                (role.RoleName IN ('最高管理员', 'Super Admin', 'Owner')) DESC,
                employee.Status = 'Active' DESC,
                employee.id
            LIMIT 1;
            """;
        await using (var findCommand = new MySqlCommand(findSql, connection))
        {
            findCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            findCommand.Parameters.Add("@Email", MySqlDbType.VarChar, 180).Value = Merchant.Email;
            var existing = await findCommand.ExecuteScalarAsync(cancellationToken);
            if (existing is not null && existing != DBNull.Value)
            {
                return Convert.ToInt64(existing);
            }
        }

        const string insertSql = """
            INSERT INTO bee_CrmEmployee
                (ProjectId, MerchantId, RealName, PreferredName, WorkEmail, PrivateEmail, LoginEnabled, Status)
            VALUES
                (@ProjectId, @MerchantId, @RealName, @PreferredName, @WorkEmail, @PrivateEmail, 0, 'Active');
            SELECT LAST_INSERT_ID();
            """;
        await using var insertCommand = new MySqlCommand(insertSql, connection);
        insertCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        insertCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        insertCommand.Parameters.Add("@RealName", MySqlDbType.VarChar, 160).Value = GuessAdminName();
        insertCommand.Parameters.Add("@PreferredName", MySqlDbType.VarChar, 160).Value = EmployeeProfileSupport.DbValue(Merchant.ContactName);
        insertCommand.Parameters.Add("@WorkEmail", MySqlDbType.VarChar, 180).Value = Merchant.Email;
        insertCommand.Parameters.Add("@PrivateEmail", MySqlDbType.VarChar, 180).Value = Merchant.Email;
        var created = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(created);
    }

    private string GuessAdminName()
    {
        if (!string.IsNullOrWhiteSpace(Merchant.ContactName))
        {
            return Merchant.ContactName.Trim();
        }

        var at = Merchant.Email.IndexOf('@', StringComparison.Ordinal);
        return at > 0 ? Merchant.Email[..at] : Merchant.Email;
    }

    private async Task<string?> UploadAvatarAsync(IFormFile avatar, long employeeId, CancellationToken cancellationToken)
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
            $"oa/{Merchant.ProjectId}/{Merchant.CorpId}/staff/{employeeId}/avatar",
            cancellationToken);
        return stored.PublicUrl;
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "My Profile";
        ViewData["PageTitle"] = "My Profile";
        ViewData["ActiveMenu"] = "MyProfile";
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

public sealed class PasswordChangeInput
{
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
