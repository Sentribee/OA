using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Domain.Entities;
using SentribeeConsole.Web.Infrastructure.OpenAI;

namespace SentribeeConsole.Web.Pages.Crm;

public class KnowledgeModel(
    IConfiguration configuration,
    IFileStorageService storageService,
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAIOptions> openAIOptions) : CrmMerchantPageModel(configuration)
{
    private const long MaxKnowledgeFileLength = 15 * 1024 * 1024;
    private readonly OpenAIOptions _openAIOptions = openAIOptions.Value;

    public CrmMerchantSession Merchant { get; private set; } = null!;

    public IReadOnlyList<CrmKnowledgeRow> Documents { get; private set; } = [];

    [BindProperty]
    public string? Notes { get; set; }

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
        StatusMessage = TempData["CrmKnowledgeStatus"] as string;
        await LoadDocumentsAsync(cancellationToken);

        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        SetViewData();
        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError(nameof(file), "Choose a document or screenshot.");
            await LoadDocumentsAsync(cancellationToken);
            return Page();
        }

        if (file.Length > MaxKnowledgeFileLength)
        {
            ModelState.AddModelError(nameof(file), "Upload a file under 15 MB.");
            await LoadDocumentsAsync(cancellationToken);
            return Page();
        }

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        await using var uploadStream = file.OpenReadStream();
        var stored = await storageService.UploadAsync(
            uploadStream,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            extension,
            $"crm/{Merchant.ProjectId}/{Merchant.CorpId}/knowledge",
            cancellationToken);

        var extractedText = await ExtractKnowledgeTextAsync(file, cancellationToken);
        if (!string.IsNullOrWhiteSpace(Notes))
        {
            extractedText = string.IsNullOrWhiteSpace(extractedText)
                ? Notes.Trim()
                : $"{extractedText.Trim()}\n\nMerchant notes:\n{Notes.Trim()}";
        }

        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var botId = await FindBotIdAsync(connection, cancellationToken);
        const string sql = """
            INSERT INTO bee_CrmKnowledgeDocument
                (ProjectId, MerchantId, ChatbotId, FileName, FileUrl, ContentType, FileSizeBytes, SourceType, ExtractedText, Status, ProcessedAtUtc)
            VALUES
                (@ProjectId, @MerchantId, @ChatbotId, @FileName, @FileUrl, @ContentType, @FileSizeBytes, @SourceType, @ExtractedText, 'Ready', UTC_TIMESTAMP(6));
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = Merchant.ProjectId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        command.Parameters.Add("@ChatbotId", MySqlDbType.Int64).Value = (object?)botId ?? DBNull.Value;
        command.Parameters.Add("@FileName", MySqlDbType.VarChar, 260).Value = Path.GetFileName(file.FileName);
        command.Parameters.Add("@FileUrl", MySqlDbType.VarChar, 1000).Value = stored.PublicUrl;
        command.Parameters.Add("@ContentType", MySqlDbType.VarChar, 120).Value = (object?)file.ContentType ?? DBNull.Value;
        command.Parameters.Add("@FileSizeBytes", MySqlDbType.Int64).Value = file.Length;
        var contentType = file.ContentType ?? string.Empty;
        command.Parameters.Add("@SourceType", MySqlDbType.VarChar, 40).Value = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "Screenshot" : "Document";
        command.Parameters.Add("@ExtractedText", MySqlDbType.MediumText).Value = (object?)extractedText ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);

        TempData["CrmKnowledgeStatus"] = string.IsNullOrWhiteSpace(extractedText)
            ? "Knowledge file uploaded. Add notes if this file has important details the bot should use."
            : "Knowledge file uploaded and text extracted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long documentId, CancellationToken cancellationToken)
    {
        var merchant = await LoadCurrentMerchantAsync(cancellationToken);
        if (merchant is null)
        {
            return RedirectToPage("/Crm/Login");
        }

        Merchant = merchant;
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = "DELETE FROM bee_CrmKnowledgeDocument WHERE id = @DocumentId AND MerchantId = @MerchantId;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@DocumentId", MySqlDbType.Int64).Value = documentId;
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await command.ExecuteNonQueryAsync(cancellationToken);
        TempData["CrmKnowledgeStatus"] = "Knowledge file removed.";
        return RedirectToPage();
    }

    private void SetViewData()
    {
        ViewData["CrmMerchant"] = Merchant;
        ViewData["Title"] = "Knowledge";
        ViewData["PageTitle"] = "Knowledge";
        ViewData["ActiveMenu"] = "Knowledge";
    }

    private async Task LoadDocumentsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT id, FileName, FileUrl, ContentType, FileSizeBytes, SourceType, Status, UploadedAtUtc, ProcessedAtUtc
            FROM bee_CrmKnowledgeDocument
            WHERE MerchantId = @MerchantId
            ORDER BY UploadedAtUtc DESC, id DESC;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CrmKnowledgeRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CrmKnowledgeRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader["FileName"] as string ?? string.Empty,
                reader["FileUrl"] as string,
                reader["ContentType"] as string,
                reader.IsDBNull(reader.GetOrdinal("FileSizeBytes")) ? null : reader.GetInt64(reader.GetOrdinal("FileSizeBytes")),
                reader["SourceType"] as string ?? string.Empty,
                reader["Status"] as string ?? string.Empty,
                reader.GetDateTime(reader.GetOrdinal("UploadedAtUtc")),
                reader.IsDBNull(reader.GetOrdinal("ProcessedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("ProcessedAtUtc"))));
        }

        Documents = rows;
    }

    private async Task<long?> FindBotIdAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = "SELECT id FROM bee_CrmChatbot WHERE MerchantId = @MerchantId ORDER BY id LIMIT 1;";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = Merchant.Id;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    private async Task<string?> ExtractKnowledgeTextAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var text = await TryReadTextFileAsync(file, cancellationToken);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (IsImageLike(file.ContentType, Path.GetExtension(file.FileName)))
        {
            return await TryOcrImageAsync(file, cancellationToken);
        }

        return null;
    }

    private static async Task<string?> TryReadTextFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (!IsTextLike(file.ContentType, Path.GetExtension(file.FileName)))
        {
            return null;
        }

        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        var buffer = new char[Math.Min(file.Length, 200_000)];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        return new string(buffer, 0, read);
    }

    private static bool IsTextLike(string? contentType, string extension)
    {
        return (contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true) ||
            string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageLike(string? contentType, string extension)
    {
        return (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> TryOcrImageAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_openAIOptions.ApiKey))
        {
            return null;
        }

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "image/jpeg" : file.ContentType;
        var imageUrl = $"data:{contentType};base64,{Convert.ToBase64String(memory.ToArray())}";

        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri($"{_openAIOptions.BaseUrl.TrimEnd('/')}/");
        client.Timeout = TimeSpan.FromSeconds(120);
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAIOptions.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = string.IsNullOrWhiteSpace(_openAIOptions.Model) ? "gpt-5.4-mini" : _openAIOptions.Model,
            input = new object[]
            {
                new
                {
                    role = "developer",
                    content = """
                        You extract searchable knowledge from business screenshots and document images.
                        Return plain text only. Preserve original language, item names, menu categories, prices, options, addresses, phone numbers, policies, case facts, and important notes.
                        If the image is a restaurant menu, list each visible dish/item with price and category where possible. Do not ask questions. Mark unclear text as [unclear].
                        """
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = $"OCR this CRM knowledge image for file: {Path.GetFileName(file.FileName)}" },
                        new { type = "input_image", image_url = imageUrl }
                    }
                }
            }
        });

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var text = ExtractOutputText(json.RootElement);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content))
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("type", out var type) &&
                    type.GetString() == "output_text" &&
                    contentItem.TryGetProperty("text", out var text))
                {
                    builder.AppendLine(text.GetString());
                }
            }
        }

        return builder.ToString().Trim();
    }
}

public sealed record CrmKnowledgeRow(
    long Id,
    string FileName,
    string? FileUrl,
    string? ContentType,
    long? FileSizeBytes,
    string SourceType,
    string Status,
    DateTime UploadedAtUtc,
    DateTime? ProcessedAtUtc);

public sealed record CrmKnowledgeDetail(
    long Id,
    string FileName,
    string? FileUrl,
    string? ContentType,
    long? FileSizeBytes,
    string SourceType,
    string Status,
    string? ExtractedText,
    DateTime UploadedAtUtc,
    DateTime? ProcessedAtUtc);
