using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MySqlConnector;
using QRCoder;

namespace SentribeeConsole.Web.Pages.Crm;

public abstract partial class CrmMerchantPageModel(IConfiguration configuration) : PageModel
{
    protected const string MerchantSessionKey = "CrmMerchantId";
    protected const string EmployeeSessionKey = "CrmEmployeeId";
    protected const string CrmProjectDomain = "oa.sentribee.ai";
    protected const string CrmProjectDisplayName = "Sentribee OA";
    protected const string LegacyCrmProjectDomain = "crm.sentribee.ai";
    protected const string LegacyCrmProjectDisplayName = "Sentribee CRM";

    protected string ConnectionString =>
        configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

    protected async Task<int> GetCrmProjectIdAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM bee_Project
            WHERE ProjectName IN (@DisplayName, @Domain, @LegacyDisplayName, @LegacyDomain)
               OR WebsiteUrl IN (@HttpsUrl, @HttpUrl, @Domain, @LegacyHttpsUrl, @LegacyHttpUrl, @LegacyDomain)
            ORDER BY CASE
                WHEN ProjectName = @DisplayName THEN 0
                WHEN WebsiteUrl IN (@HttpsUrl, @HttpUrl, @Domain) THEN 1
                ELSE 2
            END, id
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 150).Value = CrmProjectDisplayName;
        command.Parameters.Add("@Domain", MySqlDbType.VarChar, 150).Value = CrmProjectDomain;
        command.Parameters.Add("@LegacyDisplayName", MySqlDbType.VarChar, 150).Value = LegacyCrmProjectDisplayName;
        command.Parameters.Add("@LegacyDomain", MySqlDbType.VarChar, 150).Value = LegacyCrmProjectDomain;
        command.Parameters.Add("@HttpsUrl", MySqlDbType.VarChar, 500).Value = $"https://{CrmProjectDomain}";
        command.Parameters.Add("@HttpUrl", MySqlDbType.VarChar, 500).Value = $"http://{CrmProjectDomain}";
        command.Parameters.Add("@LegacyHttpsUrl", MySqlDbType.VarChar, 500).Value = $"https://{LegacyCrmProjectDomain}";
        command.Parameters.Add("@LegacyHttpUrl", MySqlDbType.VarChar, 500).Value = $"http://{LegacyCrmProjectDomain}";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null
            ? throw new InvalidOperationException("CRM project is not configured. Run the CRM database migration first.")
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    protected long? CurrentMerchantId => HttpContext.Session.GetString(MerchantSessionKey) is { Length: > 0 } value &&
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var merchantId)
            ? merchantId
            : null;

    protected long? CurrentEmployeeId => HttpContext.Session.GetString(EmployeeSessionKey) is { Length: > 0 } value &&
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var employeeId)
            ? employeeId
            : null;

    protected void SignInMerchant(long merchantId)
    {
        HttpContext.Session.Remove(EmployeeSessionKey);
        HttpContext.Session.SetString(MerchantSessionKey, merchantId.ToString(CultureInfo.InvariantCulture));
    }

    protected void SignInEmployee(long merchantId, long employeeId)
    {
        HttpContext.Session.SetString(MerchantSessionKey, merchantId.ToString(CultureInfo.InvariantCulture));
        HttpContext.Session.SetString(EmployeeSessionKey, employeeId.ToString(CultureInfo.InvariantCulture));
    }

    protected void SignOutMerchant()
    {
        HttpContext.Session.Remove(MerchantSessionKey);
        HttpContext.Session.Remove(EmployeeSessionKey);
    }

    protected async Task<CrmMerchantSession?> LoadCurrentMerchantAsync(CancellationToken cancellationToken)
    {
        var merchantId = CurrentMerchantId;
        if (merchantId is null || CurrentEmployeeId is not null)
        {
            return null;
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            SELECT merchant.id, merchant.ProjectId, merchant.BusinessName, merchant.CorpId,
                merchant.ContactName, merchant.Email, merchant.WebsiteUrl, merchant.AvatarUrl,
                merchant.Status, merchant.PlanName, merchant.TimeZoneId, merchant.ContextInstructions,
                merchant.ProfileGuidanceInstructions, merchant.ProfileDimensionFocus,
                industry.Name AS IndustryName
            FROM bee_CrmMerchant AS merchant
            LEFT JOIN bee_CrmIndustry AS industry ON industry.id = merchant.IndustryId
            WHERE merchant.id = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            SignOutMerchant();
            return null;
        }

        return new CrmMerchantSession(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("ProjectId")),
            reader["BusinessName"] as string ?? string.Empty,
            reader["CorpId"] as string ?? string.Empty,
            reader["ContactName"] as string,
            reader["Email"] as string ?? string.Empty,
            reader["WebsiteUrl"] as string,
            reader["AvatarUrl"] as string,
            reader["Status"] as string ?? string.Empty,
            reader["PlanName"] as string ?? string.Empty,
            reader["TimeZoneId"] as string ?? "Pacific/Auckland",
            reader["ContextInstructions"] as string,
            reader["ProfileGuidanceInstructions"] as string,
            reader["ProfileDimensionFocus"] as string,
            reader["IndustryName"] as string);
    }

    protected async Task<CrmEmployeeSession?> LoadCurrentEmployeeAsync(CancellationToken cancellationToken)
    {
        var merchantId = CurrentMerchantId;
        var employeeId = CurrentEmployeeId;
        if (merchantId is null || employeeId is null)
        {
            return null;
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT merchant.id AS MerchantId, merchant.ProjectId, merchant.BusinessName, merchant.CorpId,
                merchant.PlanName, merchant.Status AS MerchantStatus, merchant.TimeZoneId,
                employee.id AS EmployeeId, employee.RealName, employee.PreferredName, employee.AvatarUrl, employee.WorkEmail,
                employee.JobTitle, employee.MustChangePassword, employee.ProfileCompletedAtUtc, employee.Status AS EmployeeStatus,
                role.id AS RoleId, role.RoleName, role.CanApproveLeave, role.CanManageAttendance, role.CanManageEmployees
            FROM bee_CrmEmployee AS employee
            INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = employee.MerchantId
            LEFT JOIN bee_CrmRole AS role ON role.id = employee.RoleId
            WHERE employee.id = @EmployeeId
              AND employee.MerchantId = @MerchantId
              AND employee.LoginEnabled = 1
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId.Value;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            SignOutMerchant();
            return null;
        }

        return new CrmEmployeeSession(
            reader.GetInt64(reader.GetOrdinal("MerchantId")),
            reader.GetInt32(reader.GetOrdinal("ProjectId")),
            reader["BusinessName"] as string ?? string.Empty,
            reader["CorpId"] as string ?? string.Empty,
            reader["PlanName"] as string ?? string.Empty,
            reader["MerchantStatus"] as string ?? string.Empty,
            reader["TimeZoneId"] as string ?? "Pacific/Auckland",
            reader.GetInt64(reader.GetOrdinal("EmployeeId")),
            reader["RealName"] as string ?? string.Empty,
            reader["PreferredName"] as string,
            reader["AvatarUrl"] as string,
            reader["WorkEmail"] as string ?? string.Empty,
            reader["JobTitle"] as string,
            reader.GetBoolean(reader.GetOrdinal("MustChangePassword")),
            reader.IsDBNull(reader.GetOrdinal("ProfileCompletedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("ProfileCompletedAtUtc")),
            reader["EmployeeStatus"] as string ?? string.Empty,
            reader.IsDBNull(reader.GetOrdinal("RoleId")) ? null : reader.GetInt64(reader.GetOrdinal("RoleId")),
            reader["RoleName"] as string,
            !reader.IsDBNull(reader.GetOrdinal("CanApproveLeave")) && reader.GetBoolean(reader.GetOrdinal("CanApproveLeave")),
            !reader.IsDBNull(reader.GetOrdinal("CanManageAttendance")) && reader.GetBoolean(reader.GetOrdinal("CanManageAttendance")),
            !reader.IsDBNull(reader.GetOrdinal("CanManageEmployees")) && reader.GetBoolean(reader.GetOrdinal("CanManageEmployees")));
    }

    protected static string NormalizeCorpId(string value)
    {
        var normalized = CorpIdUnsafeCharacters().Replace(value.Trim().ToLowerInvariant(), "-");
        normalized = Regex.Replace(normalized, "-{2,}", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? $"corp-{Guid.NewGuid():N}"[..18] : normalized;
    }

    protected static string BuildCorpIdFromName(string name)
    {
        var ascii = new StringBuilder(name.Length);
        foreach (var character in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                ascii.Append(character);
            }
        }

        return NormalizeCorpId(ascii.ToString());
    }

    protected static PasswordHasher<CrmMerchantPasswordUser> CreatePasswordHasher()
    {
        return new PasswordHasher<CrmMerchantPasswordUser>();
    }

    protected static PasswordHasher<CrmEmployeePasswordUser> CreateEmployeePasswordHasher()
    {
        return new PasswordHasher<CrmEmployeePasswordUser>();
    }

    protected static string BuildChatUrl(string publicChatPath)
    {
        return $"https://chat.sentribee.ai/{NormalizeCorpId(publicChatPath)}";
    }

    protected FileContentResult GenerateChatQrCodeFile(string publicChatPath, bool download = false)
    {
        var chatUrl = BuildChatUrl(publicChatPath);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(chatUrl, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(data);
        var png = qrCode.GetGraphic(12);
        Response.Headers.CacheControl = "no-store";
        return download
            ? File(png, "image/png", $"sentribee-chat-{NormalizeCorpId(publicChatPath)}.png")
            : File(png, "image/png");
    }

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex CorpIdUnsafeCharacters();
}

public sealed record CrmMerchantSession(
    long Id,
    int ProjectId,
    string BusinessName,
    string CorpId,
    string? ContactName,
    string Email,
    string? WebsiteUrl,
    string? AvatarUrl,
    string Status,
    string PlanName,
    string TimeZoneId,
    string? ContextInstructions,
    string? ProfileGuidanceInstructions,
    string? ProfileDimensionFocus,
    string? IndustryName);

public sealed record CrmMerchantPasswordUser(long Id, string Email);

public sealed record CrmEmployeePasswordUser(long Id, string Email);

public sealed record CrmEmployeeSession(
    long MerchantId,
    int ProjectId,
    string BusinessName,
    string CorpId,
    string PlanName,
    string MerchantStatus,
    string TimeZoneId,
    long EmployeeId,
    string RealName,
    string? PreferredName,
    string? AvatarUrl,
    string WorkEmail,
    string? JobTitle,
    bool MustChangePassword,
    DateTime? ProfileCompletedAtUtc,
    string EmployeeStatus,
    long? RoleId,
    string? RoleName,
    bool CanApproveLeave,
    bool CanManageAttendance,
    bool CanManageEmployees);
