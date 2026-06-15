using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public sealed class EmployeeProfileInput
{
    [Required]
    [StringLength(160)]
    public string RealName { get; set; } = string.Empty;

    [StringLength(160)]
    public string? PreferredName { get; set; }

    public string? AvatarUrl { get; set; }

    [StringLength(700)]
    public string? ResidentialAddress { get; set; }

    [StringLength(80)]
    public string? Phone { get; set; }

    [EmailAddress]
    [StringLength(180)]
    public string? WorkEmail { get; set; }

    [EmailAddress]
    [StringLength(180)]
    public string? PrivateEmail { get; set; }

    [StringLength(80)]
    public string? GstNumber { get; set; }

    [StringLength(120)]
    public string? BankAccountNumber { get; set; }
}

public sealed record EmployeeProfileSnapshot(
    string RealName,
    string? PreferredName,
    string? AvatarUrl,
    string? ResidentialAddress,
    string? Phone,
    string? WorkEmail,
    string? PrivateEmail,
    string? GstNumber,
    string? BankAccountNumber);

public sealed record EmployeeProfileDetails(
    long Id,
    EmployeeProfileSnapshot Profile,
    DateTime? StartDate,
    DateTime? EndDate,
    string? JobTitle,
    string? EmploymentType,
    string? PayType,
    decimal? HourlyRate,
    decimal? AnnualSalary,
    decimal? StandardWeeklyHours);

public sealed record EmployeeProfileChangeRequestRow(
    long Id,
    string EmployeeName,
    string? EmployeeEmail,
    string Status,
    string ChangeSummary,
    string? DecisionNote,
    DateTime CreatedAtUtc,
    DateTime? DecisionAtUtc,
    string? CurrentProfileJson,
    string RequestedProfileJson);

public static class EmployeeProfileSupport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<EmployeeProfileDetails?> LoadEmployeeProfileDetailsAsync(
        MySqlConnection connection,
        long employeeId,
        long merchantId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, RealName, PreferredName, AvatarUrl, ResidentialAddress, Phone, WorkEmail, PrivateEmail,
                GstNumber, BankAccountNumber, StartDate, EndDate, JobTitle, EmploymentType, PayType,
                HourlyRate, AnnualSalary, StandardWeeklyHours
            FROM bee_CrmEmployee
            WHERE id = @EmployeeId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadDetails(reader);
    }

    public static async Task<IReadOnlyList<EmployeeProfileChangeRequestRow>> LoadProfileRequestsAsync(
        MySqlConnection connection,
        long merchantId,
        long? employeeId,
        int limit,
        CancellationToken cancellationToken)
    {
        var employeeFilter = employeeId.HasValue ? "AND request.EmployeeId = @EmployeeId" : string.Empty;
        var sql = $"""
            SELECT request.id, request.Status, request.DecisionNote, request.CreatedAtUtc, request.DecisionAtUtc,
                request.CurrentProfileJson, request.RequestedProfileJson,
                employee.RealName, employee.WorkEmail
            FROM bee_CrmEmployeeProfileChangeRequest AS request
            INNER JOIN bee_CrmEmployee AS employee ON employee.id = request.EmployeeId
            WHERE request.MerchantId = @MerchantId
              {employeeFilter}
            ORDER BY request.Status = 'Pending' DESC, request.CreatedAtUtc DESC, request.id DESC
            LIMIT @Limit;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        command.Parameters.Add("@Limit", MySqlDbType.Int32).Value = Math.Clamp(limit, 1, 100);
        if (employeeId.HasValue)
        {
            command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId.Value;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<EmployeeProfileChangeRequestRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var currentJson = reader["CurrentProfileJson"] as string;
            var requestedJson = reader["RequestedProfileJson"] as string ?? "{}";
            rows.Add(new EmployeeProfileChangeRequestRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["RealName"] as string ?? string.Empty,
                reader["WorkEmail"] as string,
                reader["Status"] as string ?? string.Empty,
                BuildChangeSummary(currentJson, requestedJson),
                reader["DecisionNote"] as string,
                reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("DecisionAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("DecisionAtUtc")),
                currentJson,
                requestedJson));
        }

        return rows;
    }

    public static async Task InsertProfileRequestAsync(
        MySqlConnection connection,
        int projectId,
        long merchantId,
        long employeeId,
        long? requestedByEmployeeId,
        long? requestedByMerchantId,
        EmployeeProfileSnapshot currentProfile,
        EmployeeProfileSnapshot requestedProfile,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bee_CrmEmployeeProfileChangeRequest
                (ProjectId, MerchantId, EmployeeId, RequestedByEmployeeId, RequestedByMerchantId,
                 CurrentProfileJson, RequestedProfileJson, Status)
            VALUES
                (@ProjectId, @MerchantId, @EmployeeId, @RequestedByEmployeeId, @RequestedByMerchantId,
                 @CurrentProfileJson, @RequestedProfileJson, 'Pending');
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        command.Parameters.Add("@RequestedByEmployeeId", MySqlDbType.Int64).Value = (object?)requestedByEmployeeId ?? DBNull.Value;
        command.Parameters.Add("@RequestedByMerchantId", MySqlDbType.Int64).Value = (object?)requestedByMerchantId ?? DBNull.Value;
        command.Parameters.Add("@CurrentProfileJson", MySqlDbType.LongText).Value = SerializeSnapshot(currentProfile);
        command.Parameters.Add("@RequestedProfileJson", MySqlDbType.LongText).Value = SerializeSnapshot(requestedProfile);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static EmployeeProfileInput ToInput(EmployeeProfileSnapshot profile)
    {
        return new EmployeeProfileInput
        {
            RealName = profile.RealName,
            PreferredName = profile.PreferredName,
            AvatarUrl = profile.AvatarUrl,
            ResidentialAddress = profile.ResidentialAddress,
            Phone = profile.Phone,
            WorkEmail = profile.WorkEmail,
            PrivateEmail = profile.PrivateEmail,
            GstNumber = profile.GstNumber,
            BankAccountNumber = profile.BankAccountNumber
        };
    }

    public static EmployeeProfileSnapshot ToSnapshot(EmployeeProfileInput input, string? avatarUrl)
    {
        return new EmployeeProfileSnapshot(
            NormalizeRequired(input.RealName),
            NormalizeOptional(input.PreferredName),
            NormalizeOptional(avatarUrl),
            NormalizeOptional(input.ResidentialAddress),
            NormalizeOptional(input.Phone),
            NormalizeOptional(input.WorkEmail),
            NormalizeOptional(input.PrivateEmail),
            NormalizeOptional(input.GstNumber),
            NormalizeOptional(input.BankAccountNumber));
    }

    public static EmployeeProfileSnapshot? DeserializeSnapshot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EmployeeProfileSnapshot>(value, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeSnapshot(EmployeeProfileSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    public static void AddSnapshotParameters(MySqlCommand command, EmployeeProfileSnapshot profile)
    {
        command.Parameters.Add("@RealName", MySqlDbType.VarChar, 160).Value = profile.RealName;
        command.Parameters.Add("@PreferredName", MySqlDbType.VarChar, 160).Value = DbValue(profile.PreferredName);
        command.Parameters.Add("@AvatarUrl", MySqlDbType.VarChar, 800).Value = DbValue(profile.AvatarUrl);
        command.Parameters.Add("@ResidentialAddress", MySqlDbType.VarChar, 700).Value = DbValue(profile.ResidentialAddress);
        command.Parameters.Add("@Phone", MySqlDbType.VarChar, 80).Value = DbValue(profile.Phone);
        command.Parameters.Add("@WorkEmail", MySqlDbType.VarChar, 180).Value = DbValue(profile.WorkEmail);
        command.Parameters.Add("@PrivateEmail", MySqlDbType.VarChar, 180).Value = DbValue(profile.PrivateEmail);
        command.Parameters.Add("@GstNumber", MySqlDbType.VarChar, 80).Value = DbValue(profile.GstNumber);
        command.Parameters.Add("@BankAccountNumber", MySqlDbType.VarChar, 120).Value = DbValue(profile.BankAccountNumber);
    }

    private static EmployeeProfileDetails ReadDetails(MySqlDataReader reader)
    {
        var profile = new EmployeeProfileSnapshot(
            reader["RealName"] as string ?? string.Empty,
            reader["PreferredName"] as string,
            reader["AvatarUrl"] as string,
            reader["ResidentialAddress"] as string,
            reader["Phone"] as string,
            reader["WorkEmail"] as string,
            reader["PrivateEmail"] as string,
            reader["GstNumber"] as string,
            reader["BankAccountNumber"] as string);

        return new EmployeeProfileDetails(
            reader.GetInt64(reader.GetOrdinal("id")),
            profile,
            reader.IsDBNull(reader.GetOrdinal("StartDate")) ? null : reader.GetDateTime(reader.GetOrdinal("StartDate")),
            reader.IsDBNull(reader.GetOrdinal("EndDate")) ? null : reader.GetDateTime(reader.GetOrdinal("EndDate")),
            reader["JobTitle"] as string,
            reader["EmploymentType"] as string,
            reader["PayType"] as string,
            reader.IsDBNull(reader.GetOrdinal("HourlyRate")) ? null : reader.GetDecimal(reader.GetOrdinal("HourlyRate")),
            reader.IsDBNull(reader.GetOrdinal("AnnualSalary")) ? null : reader.GetDecimal(reader.GetOrdinal("AnnualSalary")),
            reader.IsDBNull(reader.GetOrdinal("StandardWeeklyHours")) ? null : reader.GetDecimal(reader.GetOrdinal("StandardWeeklyHours")));
    }

    private static string BuildChangeSummary(string? currentJson, string requestedJson)
    {
        var current = DeserializeSnapshot(currentJson);
        var requested = DeserializeSnapshot(requestedJson);
        if (requested is null)
        {
            return "Profile details";
        }

        var labels = new List<string>();
        AddIfChanged(labels, "Real name", current?.RealName, requested.RealName);
        AddIfChanged(labels, "Preferred name", current?.PreferredName, requested.PreferredName);
        AddIfChanged(labels, "Avatar", current?.AvatarUrl, requested.AvatarUrl);
        AddIfChanged(labels, "Address", current?.ResidentialAddress, requested.ResidentialAddress);
        AddIfChanged(labels, "Phone", current?.Phone, requested.Phone);
        AddIfChanged(labels, "Work email", current?.WorkEmail, requested.WorkEmail);
        AddIfChanged(labels, "Private email", current?.PrivateEmail, requested.PrivateEmail);
        AddIfChanged(labels, "IRD", current?.GstNumber, requested.GstNumber);
        AddIfChanged(labels, "Bank account", current?.BankAccountNumber, requested.BankAccountNumber);
        return labels.Count == 0 ? "No visible changes" : string.Join(", ", labels);
    }

    private static void AddIfChanged(List<string> labels, string label, string? current, string? requested)
    {
        if (!string.Equals(NormalizeOptional(current), NormalizeOptional(requested), StringComparison.Ordinal))
        {
            labels.Add(label);
        }
    }

    private static string NormalizeRequired(string value) => value.Trim();

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
