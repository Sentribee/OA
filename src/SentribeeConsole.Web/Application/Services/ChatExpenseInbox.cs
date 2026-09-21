using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using MySqlConnector;

namespace SentribeeConsole.Web.Application.Services;

public sealed record ChatExpenseImage(string Id,string ContentType,string Sha256,byte[] Bytes);
public sealed record ChatExpenseDocument(JsonObject Document,IReadOnlyList<ChatExpenseImage> Images);

public sealed class ChatExpenseInbox(IConfiguration config)
{
    public const int MaxBody=18*1024*1024;
    public static string Text(JsonNode? node,string field)=>node?[field]?.ToString()??"";
    public static bool Hex(string value)=>Regex.IsMatch(value,"^[a-f0-9]{64}$");
    public static bool Verify(string secret,string timestamp,string signature,string body,DateTimeOffset now)
    {
        if(secret.Length<32||!long.TryParse(timestamp,out var seconds)||seconds<now.ToUnixTimeSeconds()-300||seconds>now.ToUnixTimeSeconds()+300||!Hex(signature))return false;
        var expected=HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),Encoding.UTF8.GetBytes(timestamp+"."+body));
        return CryptographicOperations.FixedTimeEquals(expected,Convert.FromHexString(signature));
    }
    public static ChatExpenseDocument Validate(string body,string tenant,string eventId)
    {
        var doc=JsonNode.Parse(body,new JsonNodeOptions(),new System.Text.Json.JsonDocumentOptions{MaxDepth=32}) as JsonObject??throw new InvalidDataException();
        if(Text(doc,"event")!="expense.evidence.collected"||Text(doc,"tenantId")!=tenant||Text(doc,"eventId")!=eventId||!Hex(eventId)||!Hex(Text(doc,"caseKey")))throw new InvalidDataException();
        foreach(var field in new[]{"messageId","channelId","roomId","senderId"})if(Text(doc,field).Length is <1 or >256)throw new InvalidDataException();
        foreach(var field in new[]{"groupName","senderName"})if(Text(doc,field).Length>1000)throw new InvalidDataException();
        if(Text(doc,"sourceText").Length>100000||!DateTimeOffset.TryParse(Text(doc,"messageTime"),CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw new InvalidDataException();
        if(doc["classification"] is not JsonObject result)throw new InvalidDataException();
        if(!new[]{"reimbursement_request","receipt","payment_proof","amendment","withdrawal","uncertain"}.Contains(Text(result,"category")))throw new InvalidDataException();
        foreach(var field in new[]{"summary","claimant","merchant","expenseType","purpose","invoiceNumber"})if(Text(result,field).Length>4000)throw new InvalidDataException();
        if(!double.TryParse(Text(result,"confidence"),NumberStyles.Float,CultureInfo.InvariantCulture,out var confidence)||confidence is not (>=0 and <=1))throw new InvalidDataException();
        var amount=Text(result,"amount");if(amount.Length>0&&!Regex.IsMatch(amount,"^[0-9]{1,10}(\\.[0-9]{1,2})?$"))throw new InvalidDataException();
        var currency=Text(result,"currency");if(currency.Length>0&&!Regex.IsMatch(currency,"^[A-Z]{3}$"))throw new InvalidDataException();
        var date=Text(result,"expenseDate");if(date.Length>0&&!DateOnly.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw new InvalidDataException();
        foreach(var field in new[]{"missing","risks","evidence","relatedMessageIds"})
            if(result[field] is not JsonArray flags||flags.Count>50||flags.Any(x=>x is not JsonValue v||!v.TryGetValue<string>(out var s)||s.Length>1000))throw new InvalidDataException();
        if(!((JsonArray)result["evidence"]!).Any(x=>x?.ToString()==Text(doc,"messageId")))throw new InvalidDataException();
        if(doc["images"] is not JsonArray images||images.Count>6)throw new InvalidDataException();
        var parsed=new List<ChatExpenseImage>();var total=0;var ids=new HashSet<string>();
        foreach(var item in images)
        {
            var id=Text(item,"id");if(!Hex(id)||!ids.Add(id))throw new InvalidDataException();
            var bytes=Convert.FromBase64String(Text(item,"data"));total+=bytes.Length;
            if(bytes.Length is <1 or >8*1024*1024||total>12*1024*1024)throw new InvalidDataException();
            var mime=ImageType(bytes);var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if(mime!=Text(item,"contentType")||hash!=Text(item,"sha256"))throw new InvalidDataException();
            parsed.Add(new(id,mime,hash,bytes));
        }
        // Keep image bytes out of the searchable evidence document.
        doc.Remove("images");return new(doc,parsed);
    }
    public static string ImageType(byte[] bytes)
    {
        if(bytes.Length>=8&&bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))return "image/png";
        if(bytes.Length>=3&&bytes[0]==255&&bytes[1]==216&&bytes[2]==255)return "image/jpeg";
        if(bytes.Length>=12&&Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&Encoding.ASCII.GetString(bytes,8,4)=="WEBP")return "image/webp";
        if(bytes.Length>=6&&Encoding.ASCII.GetString(bytes,0,6) is "GIF87a" or "GIF89a")return "image/gif";
        throw new InvalidDataException();
    }
    public async Task<IResult> Receive(HttpContext context)
    {
        var ct=context.RequestAborted;
        var tenant=config["ChatExpenses:TenantId"]??"";var secret=config["ChatExpenses:Secret"]??"";
        if(tenant.Length==0||secret.Length<32||!long.TryParse(config["ChatExpenses:MerchantId"],out var merchant)||merchant<=0)return Results.StatusCode(503);
        var limits=context.Features.Get<IHttpMaxRequestBodySizeFeature>();if(limits is {IsReadOnly:false})limits.MaxRequestBodySize=MaxBody;
        if(context.Request.ContentLength>MaxBody)return Results.StatusCode(413);
        using var buffer=new MemoryStream();var block=new byte[65536];int read;
        while((read=await context.Request.Body.ReadAsync(block,ct))>0){if(buffer.Length+read>MaxBody)return Results.StatusCode(413);await buffer.WriteAsync(block.AsMemory(0,read),ct);}
        var body=Encoding.UTF8.GetString(buffer.ToArray());var timestamp=context.Request.Headers["X-Chat-Timestamp"].ToString();var signature=context.Request.Headers["X-Chat-Signature"].ToString();var eventId=context.Request.Headers["X-Chat-Event-Id"].ToString();
        if(!Verify(secret,timestamp,signature,body,DateTimeOffset.UtcNow))return Results.Unauthorized();
        ChatExpenseDocument parsed;
        try{parsed=Validate(body,tenant,eventId);}catch(Exception e)when(e is System.Text.Json.JsonException or InvalidDataException or FormatException or InvalidOperationException){return Results.BadRequest(new{error="invalid_evidence"});}
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        await using var connection=new MySqlConnection(config.GetConnectionString("DefaultConnection"));await connection.OpenAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using var company=new MySqlCommand("SELECT ProjectId FROM bee_CrmMerchant WHERE id=@m AND Status='Active'",connection,transaction);company.Parameters.AddWithValue("@m",merchant);
        var project=await company.ExecuteScalarAsync(ct);if(project is null)return Results.StatusCode(503);
        await using var insert=new MySqlCommand("INSERT INTO bee_CrmChatExpenseEvidence(MerchantId,ProjectId,SourceTenantId,EventId,CaseKey,PayloadHash,Category,DocumentJson,Notes) VALUES(@m,@p,@t,@event,@case,@hash,@category,@doc,'')",connection,transaction);
        insert.Parameters.AddWithValue("@m",merchant);insert.Parameters.AddWithValue("@p",project);insert.Parameters.AddWithValue("@t",tenant);insert.Parameters.AddWithValue("@event",eventId);insert.Parameters.AddWithValue("@case",Text(parsed.Document,"caseKey"));insert.Parameters.AddWithValue("@hash",hash);insert.Parameters.AddWithValue("@category",Text(parsed.Document["classification"],"category"));insert.Parameters.AddWithValue("@doc",parsed.Document.ToJsonString());
        try{await insert.ExecuteNonQueryAsync(ct);}
        catch(MySqlException e)when(e.Number==1062)
        {
            await transaction.RollbackAsync(ct);
            await using var existing=new MySqlCommand("SELECT PayloadHash FROM bee_CrmChatExpenseEvidence WHERE SourceTenantId=@t AND EventId=@event AND MerchantId=@m",connection);
            existing.Parameters.AddWithValue("@t",tenant);existing.Parameters.AddWithValue("@event",eventId);existing.Parameters.AddWithValue("@m",merchant);
            return await existing.ExecuteScalarAsync(ct) as string==hash?Results.Ok(new{ok=true,duplicate=true}):Results.Conflict(new{error="event_conflict"});
        }
        foreach(var image in parsed.Images)
        {
            await using var command=new MySqlCommand("INSERT INTO bee_CrmChatExpenseImage(EvidenceId,ImageId,MerchantId,ContentType,Sha256,ImageBytes) VALUES(@e,@id,@m,@type,@sha,@bytes)",connection,transaction);
            command.Parameters.AddWithValue("@e",insert.LastInsertedId);command.Parameters.AddWithValue("@id",image.Id);command.Parameters.AddWithValue("@m",merchant);command.Parameters.AddWithValue("@type",image.ContentType);command.Parameters.AddWithValue("@sha",image.Sha256);command.Parameters.AddWithValue("@bytes",image.Bytes);await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);return Results.Ok(new{ok=true,id=insert.LastInsertedId});
    }
}
