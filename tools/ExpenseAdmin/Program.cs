using MySqlConnector;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Net;
if(args[0]=="version")
{
    Console.WriteLine(System.Diagnostics.FileVersionInfo.GetVersionInfo(args[1]).ProductVersion);return;
}
await using var connection=new MySqlConnection(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")??throw new Exception("missing_connection"));await connection.OpenAsync();
if(args[0]=="companies")
{
    await using var command=new MySqlCommand("SELECT id,ProjectId,BusinessName,Status FROM bee_CrmMerchant WHERE LOWER(BusinessName) LIKE '%sentribee%'",connection);
    await using var reader=await command.ExecuteReaderAsync();while(await reader.ReadAsync())Console.WriteLine($"{reader.GetInt64(0)}\t{reader.GetInt32(1)}\t{reader.GetString(2)}\t{reader.GetString(3)}");
}
else if(args[0]=="migrate")
{
    await using var command=new MySqlCommand(await File.ReadAllTextAsync(args[1]),connection);await command.ExecuteNonQueryAsync();Console.WriteLine("Expense schema ready");
}
else if(args[0]=="counts")
{
    await using var command=new MySqlCommand("SELECT MerchantId,Category,ReviewStatus,COUNT(*) FROM bee_CrmChatExpenseEvidence GROUP BY MerchantId,Category,ReviewStatus",connection);
    await using var reader=await command.ExecuteReaderAsync();while(await reader.ReadAsync())Console.WriteLine($"{reader.GetInt64(0)}\t{reader.GetString(1)}\t{reader.GetString(2)}\t{reader.GetInt64(3)}");
}
else if(args[0]=="roundtrip")
{
    var secret=Environment.GetEnvironmentVariable("ChatExpenses__Secret")??throw new Exception("missing_signing_key");
    var tenant=Environment.GetEnvironmentVariable("ChatExpenses__TenantId")??throw new Exception("missing_tenant");
    var merchant=long.Parse(Environment.GetEnvironmentVariable("ChatExpenses__MerchantId")!);
    var eventId=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var image=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD1sAAAAASUVORK5CYII=");var hash=Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
    var doc=new JsonObject{["event"]="expense.evidence.collected",["tenantId"]=tenant,["eventId"]=eventId,["caseKey"]=eventId,["messageId"]="deployment-verification-"+eventId,["channelId"]="deployment-verification",["roomId"]="deployment-verification",["senderId"]="deployment-verification",["messageTime"]=DateTimeOffset.UtcNow.ToString("O"),["sourceText"]="Deployment verification; automatically removed",["classification"]=new JsonObject{["category"]="uncertain",["confidence"]=0,["amount"]="",["currency"]="",["expenseDate"]="",["evidence"]=new JsonArray("deployment-verification-"+eventId),["relatedMessageIds"]=new JsonArray(),["risks"]=new JsonArray("synthetic verification"),["missing"]=new JsonArray()},["images"]=new JsonArray(new JsonObject{["id"]=hash,["contentType"]="image/png",["sha256"]=hash,["data"]=Convert.ToBase64String(image)})};
    using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};
    async Task CheckPost(JsonObject value,HttpStatusCode expected,int minutes=0)
    {
        var body=value.ToJsonString();var stamp=DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds().ToString();
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://oa.sentribee.ai/api/oa/chat-expenses"){Content=new StringContent(body,Encoding.UTF8,"application/json")};
        request.Headers.Add("X-Chat-Event-Id",eventId);request.Headers.Add("X-Chat-Timestamp",stamp);request.Headers.Add("X-Chat-Signature",Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),Encoding.UTF8.GetBytes(stamp+"."+body))).ToLowerInvariant());
        using var response=await client.SendAsync(request);if(response.StatusCode!=expected)throw new Exception("roundtrip_http_"+(int)response.StatusCode+"_expected_"+(int)expected);
    }
    try
    {
        await CheckPost(doc,HttpStatusCode.OK);await CheckPost(doc,HttpStatusCode.OK);await CheckPost(doc,HttpStatusCode.Unauthorized,-10);
        var foreign=(JsonObject)doc.DeepClone();foreign["tenantId"]="foreign";await CheckPost(foreign,HttpStatusCode.BadRequest);
        var changed=(JsonObject)doc.DeepClone();changed["sourceText"]="changed";await CheckPost(changed,HttpStatusCode.Conflict);
        await using var check=new MySqlCommand("SELECT e.id,e.ReviewStatus,i.ImageBytes FROM bee_CrmChatExpenseEvidence e JOIN bee_CrmChatExpenseImage i ON i.EvidenceId=e.id WHERE e.EventId=@event AND e.SourceTenantId=@t AND e.MerchantId=@m",connection);check.Parameters.AddWithValue("@event",eventId);check.Parameters.AddWithValue("@t",tenant);check.Parameters.AddWithValue("@m",merchant);
        long id;await using(var reader=await check.ExecuteReaderAsync()){if(!await reader.ReadAsync()||reader.GetString(1)!="pending"||!((byte[])reader[2]).SequenceEqual(image))throw new Exception("roundtrip_storage_mismatch");id=reader.GetInt64(0);if(await reader.ReadAsync())throw new Exception("roundtrip_duplicate");}
        using var privateImage=await client.GetAsync("https://oa.sentribee.ai/oa/reimbursements?handler=Image&evidenceId="+id+"&imageId="+hash);if(privateImage.StatusCode!=HttpStatusCode.Unauthorized)throw new Exception("roundtrip_image_auth");
        Console.WriteLine("PASS: live signed delivery, exact original image bytes, pending status, deduplication, conflict rejection, tenant rejection, expiry and private image authorization");
    }
    finally
    {
        await using var clean=new MySqlCommand("DELETE i FROM bee_CrmChatExpenseImage i JOIN bee_CrmChatExpenseEvidence e ON e.id=i.EvidenceId WHERE e.EventId=@event AND e.SourceTenantId=@t AND e.MerchantId=@m; DELETE FROM bee_CrmChatExpenseEvidence WHERE EventId=@event AND SourceTenantId=@t AND MerchantId=@m",connection);clean.Parameters.AddWithValue("@event",eventId);clean.Parameters.AddWithValue("@t",tenant);clean.Parameters.AddWithValue("@m",merchant);await clean.ExecuteNonQueryAsync();Console.WriteLine("Synthetic deployment evidence removed");
    }
}
else throw new Exception("unknown_command");
