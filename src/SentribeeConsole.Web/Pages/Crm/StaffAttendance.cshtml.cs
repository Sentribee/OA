using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SentribeeConsole.Web.Infrastructure.OpenAI;

namespace SentribeeConsole.Web.Pages.Crm;

public class StaffAttendanceModel(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAIOptions> openAIOptions) : CrmMerchantPageModel(configuration)
{
    private const int DefaultGraceMinutes = 10;

    public CrmEmployeeSession Staff { get; private set; } = null!;

    public IReadOnlyList<CrmOfficeOption> Offices { get; private set; } = [];

    public IReadOnlyList<CrmAttendanceRow> Records { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    [BindProperty]
    public StaffAttendanceInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var staff = await RequireStaffAsync(cancellationToken);
        if (staff.Result is not null)
        {
            return staff.Result;
        }

        Staff = staff.Staff!;
        SetViewData();
        StatusMessage = TempData["CrmStaffAttendanceStatus"] as string;
        await LoadOfficesAsync(cancellationToken);
        await LoadRecordsAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostClockInAsync(CancellationToken cancellationToken)
    {
        var staff = await RequireStaffAsync(cancellationToken);
        if (staff.Result is not null)
        {
            return staff.Result;
        }

        Staff = staff.Staff!;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var openRecordId = await GetOpenAttendanceRecordIdAsync(connection, cancellationToken);
        if (openRecordId.HasValue)
        {
            TempData["CrmStaffAttendanceStatus"] = "You already have an open clock-in.";
            return RedirectToPage();
        }

        var officeId = await NormalizeOfficeIdAsync(connection, Input.OfficeAddressId, cancellationToken);
        var schedule = await LoadEmployeeScheduleAsync(connection, Staff.EmployeeId, cancellationToken);
        var localNow = GetMerchantLocalNow();
        var isLate = schedule.ScheduledStartTime.HasValue &&
            localNow.TimeOfDay > schedule.ScheduledStartTime.Value.Add(TimeSpan.FromMinutes(DefaultGraceMinutes));
        const string sql = """
            INSERT INTO bee_CrmEmployeeAttendance
                (ProjectId, MerchantId, EmployeeId, OfficeAddressId, AttendanceDate,
                 ClockInAtUtc, ClockInLocalTime, ClockInIp, ClockInNote,
                 GraceMinutes, IsLateBeyondGrace, Status)
            VALUES
                (@ProjectId, @MerchantId, @EmployeeId, @OfficeAddressId, @AttendanceDate,
                 UTC_TIMESTAMP(6), @ClockInLocalTime, @ClockInIp, @ClockInNote,
                 @GraceMinutes, @IsLateBeyondGrace, 'Open');
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Staff.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        command.Parameters.Add("@OfficeAddressId", MySqlDbType.Int64).Value = (object?)officeId ?? DBNull.Value;
        command.Parameters.Add("@AttendanceDate", MySqlDbType.Date).Value = localNow.Date;
        command.Parameters.Add("@ClockInLocalTime", MySqlDbType.Time).Value = localNow.TimeOfDay;
        command.Parameters.Add("@ClockInIp", MySqlDbType.VarChar, 80).Value = DbValue(GetClientIp());
        command.Parameters.Add("@ClockInNote", MySqlDbType.Text).Value = DbValue(Input.ClockInNote);
        command.Parameters.Add("@GraceMinutes", MySqlDbType.Int32).Value = DefaultGraceMinutes;
        command.Parameters.Add("@IsLateBeyondGrace", MySqlDbType.Byte).Value = isLate ? 1 : 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
        TempData["CrmStaffAttendanceStatus"] = "Clock-in recorded.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostClockOutAsync(CancellationToken cancellationToken)
    {
        var staff = await RequireStaffAsync(cancellationToken);
        if (staff.Result is not null)
        {
            return staff.Result;
        }

        Staff = staff.Staff!;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var openRecordId = await GetOpenAttendanceRecordIdAsync(connection, cancellationToken);
        if (!openRecordId.HasValue)
        {
            TempData["CrmStaffAttendanceStatus"] = "No open clock-in was found.";
            return RedirectToPage();
        }

        var schedule = await LoadEmployeeScheduleAsync(connection, Staff.EmployeeId, cancellationToken);
        var localNow = GetMerchantLocalNow();
        var analysis = await CrmAttendanceWorkLogAnalyzer.AnalyzeAsync(
            httpClientFactory,
            openAIOptions.Value,
            schedule.RealName,
            schedule.JobTitle,
            Input.ClockOutNote,
            cancellationToken);

        const string sql = """
            UPDATE bee_CrmEmployeeAttendance
            SET ClockOutAtUtc = UTC_TIMESTAMP(6),
                ClockOutLocalTime = @ClockOutLocalTime,
                ClockOutIp = @ClockOutIp,
                ClockOutNote = @ClockOutNote,
                IsCompleteDay = 1,
                WorkLogSummary = @WorkLogSummary,
                WorkloadLevel = @WorkloadLevel,
                WorkloadReason = @WorkloadReason,
                Status = 'Closed',
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @AttendanceId AND MerchantId = @MerchantId AND EmployeeId = @EmployeeId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@AttendanceId", MySqlDbType.Int64).Value = openRecordId.Value;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        command.Parameters.Add("@ClockOutLocalTime", MySqlDbType.Time).Value = localNow.TimeOfDay;
        command.Parameters.Add("@ClockOutIp", MySqlDbType.VarChar, 80).Value = DbValue(GetClientIp());
        command.Parameters.Add("@ClockOutNote", MySqlDbType.Text).Value = DbValue(Input.ClockOutNote);
        command.Parameters.Add("@WorkLogSummary", MySqlDbType.Text).Value = DbValue(analysis.Summary);
        command.Parameters.Add("@WorkloadLevel", MySqlDbType.VarChar, 40).Value = DbValue(analysis.WorkloadLevel);
        command.Parameters.Add("@WorkloadReason", MySqlDbType.VarChar, 700).Value = DbValue(analysis.Reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
        TempData["CrmStaffAttendanceStatus"] = "Clock-out recorded.";
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

        return (staff, null);
    }

    private void SetViewData()
    {
        ViewData["CrmEmployee"] = Staff;
        ViewData["Title"] = "My Attendance";
        ViewData["PageTitle"] = "My Attendance";
        ViewData["ActiveMenu"] = "StaffAttendance";
    }

    private async Task LoadOfficesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = "SELECT id, LocationName FROM bee_CrmOfficeAddress WHERE MerchantId = @MerchantId AND Status = 'Active' ORDER BY IsPrimary DESC, LocationName, id;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmOfficeOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmOfficeOption(reader.GetInt64(reader.GetOrdinal("id")), reader["LocationName"] as string ?? string.Empty));
        }

        Offices = rows;
    }

    private async Task LoadRecordsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT attendance.id, attendance.AttendanceDate, attendance.ClockInAtUtc, attendance.ClockOutAtUtc,
                attendance.ClockInIp, attendance.ClockOutIp, attendance.ClockInNote, attendance.ClockOutNote,
                attendance.WorkLogSummary, attendance.WorkloadLevel, attendance.WorkloadReason,
                attendance.IsCompleteDay, attendance.IsLateBeyondGrace,
                attendance.Status, employee.RealName, employee.PreferredName, office.LocationName
            FROM bee_CrmEmployeeAttendance AS attendance
            INNER JOIN bee_CrmEmployee AS employee ON employee.id = attendance.EmployeeId
            LEFT JOIN bee_CrmOfficeAddress AS office ON office.id = attendance.OfficeAddressId
            WHERE attendance.MerchantId = @MerchantId AND attendance.EmployeeId = @EmployeeId
            ORDER BY attendance.ClockInAtUtc DESC, attendance.id DESC
            LIMIT 60;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmAttendanceRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmAttendanceRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["RealName"] as string ?? string.Empty,
                reader["PreferredName"] as string,
                reader["LocationName"] as string,
                reader.GetDateTime(reader.GetOrdinal("AttendanceDate")),
                reader.GetDateTime(reader.GetOrdinal("ClockInAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("ClockOutAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("ClockOutAtUtc")),
                reader["ClockInIp"] as string,
                reader["ClockOutIp"] as string,
                reader["ClockInNote"] as string,
                reader["ClockOutNote"] as string,
                reader["WorkLogSummary"] as string,
                reader["WorkloadLevel"] as string,
                reader["WorkloadReason"] as string,
                reader.GetBoolean(reader.GetOrdinal("IsCompleteDay")),
                reader.GetBoolean(reader.GetOrdinal("IsLateBeyondGrace")),
                reader["Status"] as string ?? string.Empty));
        }

        Records = rows;
    }

    private async Task<long?> NormalizeOfficeIdAsync(MySqlConnection connection, long? officeId, CancellationToken cancellationToken)
    {
        if (!officeId.HasValue)
        {
            return null;
        }

        const string sql = "SELECT id FROM bee_CrmOfficeAddress WHERE id = @OfficeId AND MerchantId = @MerchantId AND Status = 'Active' LIMIT 1;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@OfficeId", MySqlDbType.Int64).Value = officeId.Value;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : officeId.Value;
    }

    private async Task<long?> GetOpenAttendanceRecordIdAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM bee_CrmEmployeeAttendance
            WHERE MerchantId = @MerchantId AND EmployeeId = @EmployeeId AND ClockOutAtUtc IS NULL
            ORDER BY ClockInAtUtc DESC, id DESC
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = Staff.EmployeeId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    private async Task<CrmEmployeeAttendanceSchedule> LoadEmployeeScheduleAsync(
        MySqlConnection connection,
        long employeeId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT RealName, JobTitle, ScheduledStartTime, ScheduledEndTime
            FROM bee_CrmEmployee
            WHERE id = @EmployeeId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Staff.MerchantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CrmEmployeeAttendanceSchedule(null, null, null, null);
        }

        return new CrmEmployeeAttendanceSchedule(
            reader["RealName"] as string,
            reader["JobTitle"] as string,
            reader.IsDBNull(reader.GetOrdinal("ScheduledStartTime")) ? null : reader.GetTimeSpan(reader.GetOrdinal("ScheduledStartTime")),
            reader.IsDBNull(reader.GetOrdinal("ScheduledEndTime")) ? null : reader.GetTimeSpan(reader.GetOrdinal("ScheduledEndTime")));
    }

    private DateTime GetMerchantLocalNow()
    {
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Staff.TimeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private string? GetClientIp()
    {
        return Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) && !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.ToString().Split(',')[0].Trim()
            : HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

public sealed class StaffAttendanceInput
{
    public long? OfficeAddressId { get; set; }

    [StringLength(1000)]
    public string? ClockInNote { get; set; }

    [StringLength(1000)]
    public string? ClockOutNote { get; set; }
}
