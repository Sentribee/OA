using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class PayrollModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public int Year { get; private set; }

    public int Month { get; private set; }

    public DateTime PeriodStart { get; private set; }

    public DateTime PeriodEnd { get; private set; }

    public int WorkdayCount { get; private set; }

    public IReadOnlyList<CrmPayrollRow> Rows { get; private set; } = [];

    public CrmPayrollTotals Totals { get; private set; } = new();

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? year, int? month, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmPayrollStatus"] as string;
        var today = GetMerchantToday();
        Year = year is >= 2000 and <= 2100 ? year.Value : today.Year;
        Month = month is >= 1 and <= 12 ? month.Value : today.Month;
        PeriodStart = new DateTime(Year, Month, 1);
        PeriodEnd = PeriodStart.AddMonths(1).AddDays(-1);
        WorkdayCount = CountWeekdays(PeriodStart, PeriodEnd);

        await LoadPayrollAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAdjustmentAsync(
        long employeeId,
        int year,
        int month,
        decimal deductionHours,
        string? notes,
        CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        var normalizedYear = year is >= 2000 and <= 2100 ? year : GetMerchantToday().Year;
        var normalizedMonth = month is >= 1 and <= 12 ? month : GetMerchantToday().Month;
        var normalizedDeduction = Math.Clamp(deductionHours, 0m, 744m);

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string employeeSql = "SELECT 1 FROM bee_CrmEmployee WHERE id = @EmployeeId AND MerchantId = @MerchantId LIMIT 1;";
        await using (var employeeCommand = new MySqlCommand(employeeSql, connection))
        {
            employeeCommand.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
            employeeCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            if (await employeeCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                TempData["CrmPayrollStatus"] = "Employee was not found.";
                return RedirectToPage(new { year = normalizedYear, month = normalizedMonth });
            }
        }

        const string sql = """
            INSERT INTO bee_CrmPayrollAdjustment
                (ProjectId, MerchantId, EmployeeId, PeriodYear, PeriodMonth, DeductionHours, Notes)
            VALUES
                (@ProjectId, @MerchantId, @EmployeeId, @PeriodYear, @PeriodMonth, @DeductionHours, @Notes)
            ON DUPLICATE KEY UPDATE
                DeductionHours = VALUES(DeductionHours),
                Notes = VALUES(Notes),
                UpdatedAtUtc = UTC_TIMESTAMP(6);
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@EmployeeId", MySqlDbType.Int64).Value = employeeId;
        command.Parameters.Add("@PeriodYear", MySqlDbType.Int32).Value = normalizedYear;
        command.Parameters.Add("@PeriodMonth", MySqlDbType.Int32).Value = normalizedMonth;
        command.Parameters.Add("@DeductionHours", MySqlDbType.Decimal).Value = normalizedDeduction;
        command.Parameters.Add("@Notes", MySqlDbType.VarChar, 1000).Value = string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim();
        await command.ExecuteNonQueryAsync(cancellationToken);

        TempData["CrmPayrollStatus"] = "Payroll deduction saved.";
        return RedirectToPage(new { year = normalizedYear, month = normalizedMonth });
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Payroll";
        ViewData["PageTitle"] = "Monthly Payroll";
        ViewData["ActiveMenu"] = "Payroll";
    }

    private async Task LoadPayrollAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT employee.id, employee.RealName, employee.PreferredName, employee.JobTitle,
                employee.PayType, employee.HourlyRate, employee.AnnualSalary, employee.StandardWeeklyHours,
                COALESCE(attendance.WorkedHours, 0) AS WorkedHours,
                COALESCE(attendance.ClockDays, 0) AS ClockDays,
                COALESCE(attendance.CompleteDays, 0) AS CompleteDays,
                COALESCE(attendance.LateDays, 0) AS LateDays,
                COALESCE(leave_summary.PaidLeaveHours, 0) AS PaidLeaveHours,
                COALESCE(leave_summary.UnpaidLeaveHours, 0) AS UnpaidLeaveHours,
                COALESCE(leave_summary.PendingLeaveHours, 0) AS PendingLeaveHours,
                COALESCE(adjustment.DeductionHours, 0) AS DeductionHours,
                adjustment.Notes AS AdjustmentNotes
            FROM bee_CrmEmployee AS employee
            LEFT JOIN (
                SELECT EmployeeId,
                    ROUND(SUM(TIMESTAMPDIFF(MINUTE, ClockInAtUtc, ClockOutAtUtc)) / 60, 2) AS WorkedHours,
                    COUNT(DISTINCT AttendanceDate) AS ClockDays,
                    COUNT(DISTINCT CASE WHEN IsCompleteDay = 1 THEN AttendanceDate END) AS CompleteDays,
                    COUNT(DISTINCT CASE WHEN IsLateBeyondGrace = 1 THEN AttendanceDate END) AS LateDays
                FROM bee_CrmEmployeeAttendance
                WHERE MerchantId = @MerchantId
                  AND AttendanceDate BETWEEN @PeriodStart AND @PeriodEnd
                  AND ClockOutAtUtc IS NOT NULL
                GROUP BY EmployeeId
            ) AS attendance ON attendance.EmployeeId = employee.id
            LEFT JOIN (
                SELECT EmployeeId,
                    ROUND(SUM(CASE WHEN Status = 'Approved' AND IsPaid = 1 THEN
                        Hours * (DATEDIFF(LEAST(EndDate, @PeriodEnd), GREATEST(StartDate, @PeriodStart)) + 1)
                            / NULLIF(DATEDIFF(EndDate, StartDate) + 1, 0)
                        ELSE 0 END), 2) AS PaidLeaveHours,
                    ROUND(SUM(CASE WHEN Status = 'Approved' AND IsPaid = 0 THEN
                        Hours * (DATEDIFF(LEAST(EndDate, @PeriodEnd), GREATEST(StartDate, @PeriodStart)) + 1)
                            / NULLIF(DATEDIFF(EndDate, StartDate) + 1, 0)
                        ELSE 0 END), 2) AS UnpaidLeaveHours,
                    ROUND(SUM(CASE WHEN Status = 'Pending' THEN
                        Hours * (DATEDIFF(LEAST(EndDate, @PeriodEnd), GREATEST(StartDate, @PeriodStart)) + 1)
                            / NULLIF(DATEDIFF(EndDate, StartDate) + 1, 0)
                        ELSE 0 END), 2) AS PendingLeaveHours
                FROM bee_CrmEmployeeLeave
                WHERE MerchantId = @MerchantId
                  AND StartDate <= @PeriodEnd
                  AND EndDate >= @PeriodStart
                GROUP BY EmployeeId
            ) AS leave_summary ON leave_summary.EmployeeId = employee.id
            LEFT JOIN bee_CrmPayrollAdjustment AS adjustment
                ON adjustment.EmployeeId = employee.id
               AND adjustment.MerchantId = @MerchantId
               AND adjustment.PeriodYear = @PeriodYear
               AND adjustment.PeriodMonth = @PeriodMonth
            WHERE employee.MerchantId = @MerchantId
              AND (employee.Status = 'Active' OR employee.EndDate >= @PeriodStart)
            ORDER BY employee.Status = 'Active' DESC, employee.RealName, employee.id;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@PeriodYear", MySqlDbType.Int32).Value = Year;
        command.Parameters.Add("@PeriodMonth", MySqlDbType.Int32).Value = Month;
        command.Parameters.Add("@PeriodStart", MySqlDbType.Date).Value = PeriodStart;
        command.Parameters.Add("@PeriodEnd", MySqlDbType.Date).Value = PeriodEnd;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmPayrollRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var standardWeeklyHours = reader.GetDecimal(reader.GetOrdinal("StandardWeeklyHours"));
            var expectedHours = Math.Round(WorkdayCount * (standardWeeklyHours / 5m), 2);
            var workedHours = reader.GetDecimal(reader.GetOrdinal("WorkedHours"));
            var paidLeaveHours = reader.GetDecimal(reader.GetOrdinal("PaidLeaveHours"));
            var unpaidLeaveHours = reader.GetDecimal(reader.GetOrdinal("UnpaidLeaveHours"));
            var pendingLeaveHours = reader.GetDecimal(reader.GetOrdinal("PendingLeaveHours"));
            var deductionHours = reader.GetDecimal(reader.GetOrdinal("DeductionHours"));
            var payType = reader["PayType"] as string ?? "Hourly";
            decimal? hourlyRate = reader.IsDBNull(reader.GetOrdinal("HourlyRate")) ? null : reader.GetDecimal(reader.GetOrdinal("HourlyRate"));
            decimal? annualSalary = reader.IsDBNull(reader.GetOrdinal("AnnualSalary")) ? null : reader.GetDecimal(reader.GetOrdinal("AnnualSalary"));
            var hourlyEquivalent = CalculateHourlyEquivalent(hourlyRate, annualSalary, standardWeeklyHours);
            var payableHours = Math.Max(0m, expectedHours + paidLeaveHours - unpaidLeaveHours - deductionHours);
            var estimatedGrossPay = CalculateGrossPay(payType, annualSalary, hourlyEquivalent, payableHours, unpaidLeaveHours, deductionHours);

            rows.Add(new CrmPayrollRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["RealName"] as string ?? string.Empty,
                reader["PreferredName"] as string,
                reader["JobTitle"] as string,
                payType,
                hourlyRate,
                annualSalary,
                standardWeeklyHours,
                expectedHours,
                workedHours,
                reader.GetInt32(reader.GetOrdinal("ClockDays")),
                reader.GetInt32(reader.GetOrdinal("CompleteDays")),
                reader.GetInt32(reader.GetOrdinal("LateDays")),
                paidLeaveHours,
                unpaidLeaveHours,
                pendingLeaveHours,
                deductionHours,
                reader["AdjustmentNotes"] as string,
                payableHours,
                hourlyEquivalent,
                estimatedGrossPay));
        }

        Rows = rows;
        Totals = new CrmPayrollTotals(
            rows.Sum(item => item.ExpectedHours),
            rows.Sum(item => item.WorkedHours),
            rows.Sum(item => item.PaidLeaveHours),
            rows.Sum(item => item.UnpaidLeaveHours),
            rows.Sum(item => item.DeductionHours),
            rows.Sum(item => item.PayableHours),
            rows.Sum(item => item.EstimatedGrossPay));
    }

    private DateTime GetMerchantToday()
    {
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Merchant.TimeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone).Date;
        }
        catch
        {
            return DateTime.UtcNow.Date;
        }
    }

    private static int CountWeekdays(DateTime start, DateTime end)
    {
        var count = 0;
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                count++;
            }
        }

        return count;
    }

    private static decimal CalculateHourlyEquivalent(decimal? hourlyRate, decimal? annualSalary, decimal standardWeeklyHours)
    {
        if (hourlyRate is > 0)
        {
            return hourlyRate.Value;
        }

        return annualSalary is > 0 && standardWeeklyHours > 0
            ? Math.Round(annualSalary.Value / 52m / standardWeeklyHours, 2)
            : 0m;
    }

    private static decimal CalculateGrossPay(
        string payType,
        decimal? annualSalary,
        decimal hourlyEquivalent,
        decimal payableHours,
        decimal unpaidLeaveHours,
        decimal deductionHours)
    {
        if (string.Equals(payType, "Salary", StringComparison.OrdinalIgnoreCase) && annualSalary is > 0)
        {
            return Math.Max(0m, Math.Round((annualSalary.Value / 12m) - ((unpaidLeaveHours + deductionHours) * hourlyEquivalent), 2));
        }

        return Math.Round(payableHours * hourlyEquivalent, 2);
    }
}

public sealed record CrmPayrollRow(
    long EmployeeId,
    string RealName,
    string? PreferredName,
    string? JobTitle,
    string PayType,
    decimal? HourlyRate,
    decimal? AnnualSalary,
    decimal StandardWeeklyHours,
    decimal ExpectedHours,
    decimal WorkedHours,
    int ClockDays,
    int CompleteDays,
    int LateDays,
    decimal PaidLeaveHours,
    decimal UnpaidLeaveHours,
    decimal PendingLeaveHours,
    decimal DeductionHours,
    string? AdjustmentNotes,
    decimal PayableHours,
    decimal HourlyEquivalent,
    decimal EstimatedGrossPay);

public sealed record CrmPayrollTotals(
    decimal ExpectedHours = 0,
    decimal WorkedHours = 0,
    decimal PaidLeaveHours = 0,
    decimal UnpaidLeaveHours = 0,
    decimal DeductionHours = 0,
    decimal PayableHours = 0,
    decimal EstimatedGrossPay = 0);
