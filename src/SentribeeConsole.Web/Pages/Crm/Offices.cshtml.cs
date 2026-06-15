using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class OfficesModel : CrmMerchantPageModel
{
    private readonly IConfiguration _configuration;

    public OfficesModel(IConfiguration configuration) : base(configuration)
    {
        _configuration = configuration;
    }

    public CrmMerchantSession Merchant { get; private set; } = null!;

    public IReadOnlyList<CrmOfficeRow> Offices { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    public string GoogleMapsApiKey => _configuration["GoogleMaps:ApiKey"] ?? string.Empty;

    [BindProperty]
    public OfficeInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(long? officeId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        StatusMessage = TempData["CrmOfficesStatus"] as string;
        await LoadOfficesAsync(cancellationToken);
        if (officeId.HasValue)
        {
            await LoadOfficeInputAsync(officeId.Value, cancellationToken);
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
            await LoadOfficesAsync(cancellationToken);
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (Input.IsPrimary)
        {
            await using var clearCommand = new MySqlCommand(
                "UPDATE bee_CrmOfficeAddress SET IsPrimary = 0 WHERE MerchantId = @MerchantId;",
                connection,
                (MySqlTransaction)transaction);
            clearCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
            await clearCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (Input.Id > 0)
        {
            const string updateSql = """
                UPDATE bee_CrmOfficeAddress
                SET LocationName = @LocationName,
                    GooglePlaceId = @GooglePlaceId,
                    AddressLine1 = @AddressLine1,
                    FormattedAddress = @FormattedAddress,
                    AddressLine2 = @AddressLine2,
                    Suburb = @Suburb,
                    City = @City,
                    Region = @Region,
                    Postcode = @Postcode,
                    Country = @Country,
                    Latitude = @Latitude,
                    Longitude = @Longitude,
                    Phone = @Phone,
                    IsPrimary = @IsPrimary,
                    Status = @Status,
                    Notes = @Notes,
                    UpdatedAtUtc = UTC_TIMESTAMP(6)
                WHERE id = @OfficeId AND MerchantId = @MerchantId;
                """;
            await using var command = new MySqlCommand(updateSql, connection, (MySqlTransaction)transaction);
            AddSaveParameters(command);
            command.Parameters.Add("@OfficeId", MySqlDbType.Int64).Value = Input.Id;
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmOfficesStatus"] = "Office address updated.";
        }
        else
        {
            const string insertSql = """
                INSERT INTO bee_CrmOfficeAddress
                    (ProjectId, MerchantId, LocationName, GooglePlaceId, AddressLine1, FormattedAddress,
                     AddressLine2, Suburb, City, Region, Postcode, Country, Latitude, Longitude,
                     Phone, IsPrimary, Status, Notes)
                VALUES
                    (@ProjectId, @MerchantId, @LocationName, @GooglePlaceId, @AddressLine1, @FormattedAddress,
                     @AddressLine2, @Suburb, @City, @Region, @Postcode, @Country, @Latitude, @Longitude,
                     @Phone, @IsPrimary, @Status, @Notes);
                """;
            await using var command = new MySqlCommand(insertSql, connection, (MySqlTransaction)transaction);
            AddSaveParameters(command);
            await command.ExecuteNonQueryAsync(cancellationToken);
            TempData["CrmOfficesStatus"] = "Office address added.";
        }

        await transaction.CommitAsync(cancellationToken);
        return RedirectToPage();
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Offices";
        ViewData["PageTitle"] = "Office Addresses";
        ViewData["ActiveMenu"] = "Offices";
    }

    private async Task LoadOfficesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, LocationName, GooglePlaceId, AddressLine1, FormattedAddress, AddressLine2,
                Suburb, City, Region, Postcode, Country, Latitude, Longitude,
                Phone, IsPrimary, Status, Notes
            FROM bee_CrmOfficeAddress
            WHERE MerchantId = @MerchantId
            ORDER BY Status = 'Active' DESC, IsPrimary DESC, LocationName, id DESC;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmOfficeRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmOfficeRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["LocationName"] as string ?? string.Empty,
                reader["GooglePlaceId"] as string,
                reader["AddressLine1"] as string ?? string.Empty,
                reader["FormattedAddress"] as string,
                reader["AddressLine2"] as string,
                reader["Suburb"] as string,
                reader["City"] as string,
                reader["Region"] as string,
                reader["Postcode"] as string,
                reader["Country"] as string ?? string.Empty,
                reader.IsDBNull(reader.GetOrdinal("Latitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Latitude")),
                reader.IsDBNull(reader.GetOrdinal("Longitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Longitude")),
                reader["Phone"] as string,
                reader.GetBoolean(reader.GetOrdinal("IsPrimary")),
                reader["Status"] as string ?? string.Empty,
                reader["Notes"] as string));
        }

        Offices = rows;
    }

    private async Task LoadOfficeInputAsync(long officeId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, LocationName, GooglePlaceId, AddressLine1, FormattedAddress, AddressLine2,
                Suburb, City, Region, Postcode, Country, Latitude, Longitude,
                Phone, IsPrimary, Status, Notes
            FROM bee_CrmOfficeAddress
            WHERE id = @OfficeId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@OfficeId", MySqlDbType.Int64).Value = officeId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        Input = new OfficeInput
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            LocationName = reader["LocationName"] as string ?? string.Empty,
            GooglePlaceId = reader["GooglePlaceId"] as string,
            AddressLine1 = reader["AddressLine1"] as string ?? string.Empty,
            FormattedAddress = reader["FormattedAddress"] as string,
            AddressLine2 = reader["AddressLine2"] as string,
            Suburb = reader["Suburb"] as string,
            City = reader["City"] as string,
            Region = reader["Region"] as string,
            Postcode = reader["Postcode"] as string,
            Country = reader["Country"] as string ?? "New Zealand",
            Latitude = reader.IsDBNull(reader.GetOrdinal("Latitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Latitude")),
            Longitude = reader.IsDBNull(reader.GetOrdinal("Longitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Longitude")),
            Phone = reader["Phone"] as string,
            IsPrimary = reader.GetBoolean(reader.GetOrdinal("IsPrimary")),
            Status = reader["Status"] as string ?? "Active",
            Notes = reader["Notes"] as string
        };
    }

    private void AddSaveParameters(MySqlCommand command)
    {
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@LocationName", MySqlDbType.VarChar, 160).Value = Input.LocationName.Trim();
        command.Parameters.Add("@GooglePlaceId", MySqlDbType.VarChar, 160).Value = DbValue(Input.GooglePlaceId);
        command.Parameters.Add("@AddressLine1", MySqlDbType.VarChar, 260).Value = Input.AddressLine1.Trim();
        command.Parameters.Add("@FormattedAddress", MySqlDbType.VarChar, 700).Value = DbValue(Input.FormattedAddress);
        command.Parameters.Add("@AddressLine2", MySqlDbType.VarChar, 260).Value = DbValue(Input.AddressLine2);
        command.Parameters.Add("@Suburb", MySqlDbType.VarChar, 120).Value = DbValue(Input.Suburb);
        command.Parameters.Add("@City", MySqlDbType.VarChar, 120).Value = DbValue(Input.City);
        command.Parameters.Add("@Region", MySqlDbType.VarChar, 120).Value = DbValue(Input.Region);
        command.Parameters.Add("@Postcode", MySqlDbType.VarChar, 40).Value = DbValue(Input.Postcode);
        command.Parameters.Add("@Country", MySqlDbType.VarChar, 120).Value = string.IsNullOrWhiteSpace(Input.Country) ? "New Zealand" : Input.Country.Trim();
        command.Parameters.Add("@Latitude", MySqlDbType.Decimal).Value = (object?)Input.Latitude ?? DBNull.Value;
        command.Parameters.Add("@Longitude", MySqlDbType.Decimal).Value = (object?)Input.Longitude ?? DBNull.Value;
        command.Parameters.Add("@Phone", MySqlDbType.VarChar, 80).Value = DbValue(Input.Phone);
        command.Parameters.Add("@IsPrimary", MySqlDbType.Bit).Value = Input.IsPrimary;
        command.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = string.Equals(Input.Status, "Inactive", StringComparison.OrdinalIgnoreCase) ? "Inactive" : "Active";
        command.Parameters.Add("@Notes", MySqlDbType.Text).Value = DbValue(Input.Notes);
    }

    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}

public sealed class OfficeInput
{
    public long Id { get; set; }

    [Required]
    [StringLength(160)]
    public string LocationName { get; set; } = string.Empty;

    [StringLength(160)]
    public string? GooglePlaceId { get; set; }

    [Required]
    [StringLength(260)]
    public string AddressLine1 { get; set; } = string.Empty;

    [StringLength(700)]
    public string? FormattedAddress { get; set; }

    [StringLength(260)]
    public string? AddressLine2 { get; set; }

    [StringLength(120)]
    public string? Suburb { get; set; }

    [StringLength(120)]
    public string? City { get; set; }

    [StringLength(120)]
    public string? Region { get; set; }

    [StringLength(40)]
    public string? Postcode { get; set; }

    [StringLength(120)]
    public string Country { get; set; } = "New Zealand";

    [Range(-90, 90)]
    public decimal? Latitude { get; set; }

    [Range(-180, 180)]
    public decimal? Longitude { get; set; }

    [StringLength(80)]
    public string? Phone { get; set; }

    public bool IsPrimary { get; set; }

    [Required]
    public string Status { get; set; } = "Active";

    [StringLength(3000)]
    public string? Notes { get; set; }
}

public sealed record CrmOfficeRow(
    long Id,
    string LocationName,
    string? GooglePlaceId,
    string AddressLine1,
    string? FormattedAddress,
    string? AddressLine2,
    string? Suburb,
    string? City,
    string? Region,
    string? Postcode,
    string Country,
    decimal? Latitude,
    decimal? Longitude,
    string? Phone,
    bool IsPrimary,
    string Status,
    string? Notes);
