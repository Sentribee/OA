using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using SentribeeConsole.Web.Application.Services;

namespace SentribeeConsole.Web.Pages.Crm;

public sealed class ReimbursementsModel(IConfiguration config):CrmMerchantPageModel(config)
{
    public List<ExpenseRow> Rows {get;}=[];
    public ExpenseRow? Selected {get;private set;}
    public List<(string Id,string Type)> Images {get;}=[];
    public List<ExpenseRow> Related {get;}=[];
    public string Filter {get;private set;}="";
    public string Category {get;private set;}="";
    public static readonly Dictionary<string,string> Categories=new(){["reimbursement_request"]="报销申请",["receipt"]="票据补充",["payment_proof"]="付款凭证",["amendment"]="更正",["withdrawal"]="撤回",["uncertain"]="待核对"};
    public static readonly Dictionary<string,string> States=new(){["pending"]="资料待审核",["needs_info"]="待补充",["checked"]="资料已核对",["excluded"]="不属报销"};
    public static string T(JsonNode? n,string key)=>ChatExpenseInbox.Text(n,key);
    public async Task<IActionResult> OnGetAsync(string? eventId,string? status,string? category,long? before,CancellationToken cancellationToken)
    {
        var merchant=await LoadCurrentMerchantAsync(cancellationToken);if(merchant is null||merchant.Status!="Active")return RedirectToPage("/Crm/Login");
        ViewData["CrmMerchant"]=merchant;ViewData["Title"]="报销收集";ViewData["PageTitle"]="报销收集";ViewData["ActiveMenu"]="Reimbursements";
        Filter=States.ContainsKey(status??"")?status!:"";Category=Categories.ContainsKey(category??"")?category!:"";
        await using var connection=new MySqlConnection(ConnectionString);await connection.OpenAsync(cancellationToken);
        await using(var command=new MySqlCommand("SELECT id,EventId,CaseKey,DocumentJson,ReviewStatus,Notes FROM bee_CrmChatExpenseEvidence WHERE MerchantId=@m AND (@status='' OR ReviewStatus=@status) AND (@category='' OR Category=@category) AND (@before=0 OR id<@before) ORDER BY id DESC LIMIT 100",connection))
        {
            command.Parameters.AddWithValue("@m",merchant.Id);command.Parameters.AddWithValue("@status",Filter);command.Parameters.AddWithValue("@category",Category);command.Parameters.AddWithValue("@before",before??0);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken))Rows.Add(Read(reader));
        }
        if(!string.IsNullOrEmpty(eventId))
        {
            await using(var command=new MySqlCommand("SELECT id,EventId,CaseKey,DocumentJson,ReviewStatus,Notes FROM bee_CrmChatExpenseEvidence WHERE MerchantId=@m AND EventId=@event",connection))
            {
                command.Parameters.AddWithValue("@m",merchant.Id);command.Parameters.AddWithValue("@event",eventId);
                await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(await reader.ReadAsync(cancellationToken))Selected=Read(reader);
            }
            if(Selected is null)return NotFound();
            await using(var command=new MySqlCommand("SELECT ImageId,ContentType FROM bee_CrmChatExpenseImage WHERE MerchantId=@m AND EvidenceId=@e",connection))
            {
                command.Parameters.AddWithValue("@m",merchant.Id);command.Parameters.AddWithValue("@e",Selected.Id);
                await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken))Images.Add((reader.GetString(0),reader.GetString(1)));
            }
            await using(var command=new MySqlCommand("SELECT id,EventId,CaseKey,DocumentJson,ReviewStatus,Notes FROM bee_CrmChatExpenseEvidence WHERE MerchantId=@m AND CaseKey=@case AND id<>@id ORDER BY id LIMIT 100",connection))
            {
                command.Parameters.AddWithValue("@m",merchant.Id);command.Parameters.AddWithValue("@case",Selected.CaseKey);command.Parameters.AddWithValue("@id",Selected.Id);
                await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken))Related.Add(Read(reader));
            }
        }
        Response.Headers.CacheControl="no-store";return Page();
    }
    public async Task<IActionResult> OnGetImageAsync(long evidenceId,string imageId,bool download,CancellationToken cancellationToken)
    {
        var merchant=await LoadCurrentMerchantAsync(cancellationToken);if(merchant is null||merchant.Status!="Active")return Unauthorized();
        await using var connection=new MySqlConnection(ConnectionString);await connection.OpenAsync(cancellationToken);
        await using var command=new MySqlCommand("SELECT ContentType,ImageBytes FROM bee_CrmChatExpenseImage WHERE MerchantId=@m AND EvidenceId=@e AND ImageId=@id",connection);
        command.Parameters.AddWithValue("@m",merchant.Id);command.Parameters.AddWithValue("@e",evidenceId);command.Parameters.AddWithValue("@id",imageId);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))return NotFound();
        Response.Headers.CacheControl="no-store";Response.Headers["X-Content-Type-Options"]="nosniff";
        var type=reader.GetString(0);var bytes=(byte[])reader[1];return download?File(bytes,type,"receipt-"+imageId+"."+type.Split('/')[1]):File(bytes,type);
    }
    public async Task<IActionResult> OnPostReviewAsync(long evidenceId,string reviewStatus,string? notes,CancellationToken cancellationToken)
    {
        var merchant=await LoadCurrentMerchantAsync(cancellationToken);if(merchant is null||merchant.Status!="Active")return Unauthorized();
        if(!States.ContainsKey(reviewStatus)||notes?.Length>4000)return BadRequest();
        await using var connection=new MySqlConnection(ConnectionString);await connection.OpenAsync(cancellationToken);await using var transaction=await connection.BeginTransactionAsync(cancellationToken);
        await using var command=new MySqlCommand("UPDATE bee_CrmChatExpenseEvidence SET ReviewStatus=@s,Notes=@n,UpdatedAtUtc=UTC_TIMESTAMP(6) WHERE MerchantId=@m AND id=@e",connection,transaction);
        command.Parameters.AddWithValue("@s",reviewStatus);command.Parameters.AddWithValue("@n",notes??"");command.Parameters.AddWithValue("@m",merchant.Id);command.Parameters.AddWithValue("@e",evidenceId);
        if(await command.ExecuteNonQueryAsync(cancellationToken)==0)return NotFound();
        await using var audit=new MySqlCommand("INSERT INTO bee_CrmChatExpenseReview(EvidenceId,MerchantId,ReviewStatus,Notes) VALUES(@e,@m,@s,@n)",connection,transaction);
        audit.Parameters.AddWithValue("@e",evidenceId);audit.Parameters.AddWithValue("@m",merchant.Id);audit.Parameters.AddWithValue("@s",reviewStatus);audit.Parameters.AddWithValue("@n",notes??"");await audit.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);return RedirectToPage();
    }
    private static ExpenseRow Read(MySqlDataReader reader)=>new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),JsonNode.Parse(reader.GetString(3))!.AsObject(),reader.GetString(4),reader.GetString(5));
}
public sealed record ExpenseRow(long Id,string EventId,string CaseKey,JsonObject Document,string Status,string Notes)
{
    public JsonNode Classification=>Document["classification"]!;
}
