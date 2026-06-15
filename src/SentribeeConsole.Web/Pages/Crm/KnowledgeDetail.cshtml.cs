using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Pages.Crm;

public class KnowledgeDetailModel(IConfiguration configuration) : CrmMerchantPageModel(configuration)
{
    public CrmMerchantSession Merchant { get; private set; } = null!;

    public CrmKnowledgeDetail? Document { get; private set; }

    public async Task<IActionResult> OnGetAsync(long documentId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        await LoadDocumentAsync(documentId, cancellationToken);
        if (Document is null)
        {
            return RedirectToPage("/Crm/Knowledge");
        }

        return Page();
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Knowledge Detail";
        ViewData["PageTitle"] = "Knowledge Detail";
        ViewData["ActiveMenu"] = "Knowledge";
    }

    private async Task LoadDocumentAsync(long documentId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, FileName, FileUrl, ContentType, FileSizeBytes, SourceType, Status,
                ExtractedText, UploadedAtUtc, ProcessedAtUtc
            FROM bee_CrmKnowledgeDocument
            WHERE id = @DocumentId AND MerchantId = @MerchantId
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@DocumentId", MySqlDbType.Int64).Value = documentId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        Document = new CrmKnowledgeDetail(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader["FileName"] as string ?? string.Empty,
            reader["FileUrl"] as string,
            reader["ContentType"] as string,
            reader.IsDBNull(reader.GetOrdinal("FileSizeBytes")) ? null : reader.GetInt64(reader.GetOrdinal("FileSizeBytes")),
            reader["SourceType"] as string ?? string.Empty,
            reader["Status"] as string ?? string.Empty,
            reader["ExtractedText"] as string,
            reader.GetDateTime(reader.GetOrdinal("UploadedAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("ProcessedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("ProcessedAtUtc")));
    }
}
