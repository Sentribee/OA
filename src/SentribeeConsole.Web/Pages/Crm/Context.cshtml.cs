using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace SentribeeConsole.Web.Pages.Crm;

public class ContextModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    [BindProperty]
    [StringLength(6000)]
    public string? ContextInstructions { get; set; }

    [BindProperty]
    [StringLength(6000)]
    public string? ProfileGuidanceInstructions { get; set; }

    [BindProperty]
    [StringLength(6000)]
    public string? ProfileDimensionFocus { get; set; }

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        ContextInstructions = merchant.ContextInstructions;
        ProfileGuidanceInstructions = merchant.ProfileGuidanceInstructions;
        ProfileDimensionFocus = merchant.ProfileDimensionFocus;
        StatusMessage = TempData["CrmContextStatus"] as string;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
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
            return Page();
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            UPDATE bee_CrmMerchant
            SET ContextInstructions = @ContextInstructions,
                ProfileGuidanceInstructions = @ProfileGuidanceInstructions,
                ProfileDimensionFocus = @ProfileDimensionFocus,
                UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @MerchantId;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@ContextInstructions", MySqlDbType.Text).Value = (object?)ContextInstructions?.Trim() ?? DBNull.Value;
        command.Parameters.Add("@ProfileGuidanceInstructions", MySqlDbType.Text).Value = (object?)ProfileGuidanceInstructions?.Trim() ?? DBNull.Value;
        command.Parameters.Add("@ProfileDimensionFocus", MySqlDbType.Text).Value = (object?)ProfileDimensionFocus?.Trim() ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);

        TempData["CrmContextStatus"] = "Context settings saved.";
        return RedirectToPage();
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Context";
        ViewData["PageTitle"] = "Context";
        ViewData["ActiveMenu"] = "Context";
    }
}
