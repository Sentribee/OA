using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SentribeeConsole.Web.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using SentribeeConsole.Web.Pages.Crm;

var secret=new string('s',40);var now=DateTimeOffset.UtcNow;var stamp=now.ToUnixTimeSeconds().ToString();
var image=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jD1sAAAAASUVORK5CYII=");var hash=Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();var eventId=new string('a',64);
var doc=new JsonObject{["event"]="expense.evidence.collected",["tenantId"]="tenant",["eventId"]=eventId,["caseKey"]=eventId,["messageId"]="message",["channelId"]="channel",["roomId"]="room",["senderId"]="sender",["messageTime"]=now.ToString("O"),["sourceText"]="测试票据",["classification"]=new JsonObject{["category"]="receipt",["confidence"]=0.9,["amount"]="12.50",["currency"]="NZD",["expenseDate"]="2026-09-21",["evidence"]=new JsonArray("message"),["relatedMessageIds"]=new JsonArray(),["risks"]=new JsonArray(),["missing"]=new JsonArray()},["images"]=new JsonArray(new JsonObject{["id"]=hash,["contentType"]="image/png",["sha256"]=hash,["data"]=Convert.ToBase64String(image)})};
var body=doc.ToJsonString();var signature=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),Encoding.UTF8.GetBytes(stamp+"."+body))).ToLowerInvariant();
Check(ChatExpenseInbox.Verify(secret,stamp,signature,body,now),"valid signature");
Check(!ChatExpenseInbox.Verify(secret,stamp,signature,body+" ",now),"tamper rejected");
Check(!ChatExpenseInbox.Verify(secret,stamp,signature,body,now.AddMinutes(6)),"replay expiry");
Check(!ChatExpenseInbox.Verify("",stamp,signature,body,now),"missing secret");
var parsed=ChatExpenseInbox.Validate(body,"tenant",eventId);Check(parsed.Images[0].Bytes.SequenceEqual(image)&&parsed.Document["images"] is null,"original bytes separated from metadata");
Throws(()=>ChatExpenseInbox.Validate(body,"foreign",eventId),"company isolation");
Throws(()=>ChatExpenseInbox.Validate(body,"tenant",new string('b',64)),"event mismatch");
doc["images"]![0]!["sha256"]=new string('b',64);Throws(()=>ChatExpenseInbox.Validate(doc.ToJsonString(),"tenant",eventId),"image checksum");doc["images"]![0]!["sha256"]=hash;
doc["classification"]!["category"]="ordinary";Throws(()=>ChatExpenseInbox.Validate(doc.ToJsonString(),"tenant",eventId),"ordinary rejected");
Console.WriteLine("PASS: OA HMAC, timestamp, tamper, tenant binding, original image integrity and evidence validation");
if(args.Contains("--preview"))
{
    var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(ReimbursementsModel).Assembly.FullName,ContentRootPath=Path.GetFullPath("src/SentribeeConsole.Web"),EnvironmentName="Development"});
    builder.WebHost.UseUrls("http://127.0.0.1:18798");builder.Services.AddRazorPages();
    var app=builder.Build();app.UseStaticFiles();app.MapRazorPages();
    app.MapGet("/preview",async context=>
    {
        var model=new ReimbursementsModel(builder.Configuration);
        var document=(JsonObject)doc.DeepClone();document["groupName"]="Sentribee 报销群";document["senderName"]="测试员工";document["sourceText"]="客户拜访交通费，附上收据。";
        document["classification"]!["category"]="receipt";document["classification"]!["summary"]="客户拜访交通收据，待核对报销人";document["classification"]!["risks"]=new JsonArray("资料不代表审批或付款");
        var row=new ExpenseRow(1,eventId,eventId,document,"pending","");model.Rows.Add(row);model.Images.Add((hash,"image/png"));typeof(ReimbursementsModel).GetProperty("Selected")!.SetValue(model,row);
        var action=new ActionContext(context,new RouteData(),new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var engine=context.RequestServices.GetRequiredService<IRazorViewEngine>();var found=engine.GetView(null,"/Pages/Crm/Reimbursements.cshtml",true);if(!found.Success)throw new Exception(string.Join(",",found.SearchedLocations));
        using var output=new StringWriter();var data=new ViewDataDictionary<ReimbursementsModel>(new EmptyModelMetadataProvider(),new ModelStateDictionary()){Model=model};data["Title"]="报销收集";data["PageTitle"]="报销收集";data["ActiveMenu"]="Reimbursements";
        var pageContext=new Microsoft.AspNetCore.Mvc.RazorPages.PageContext(action){ViewData=data};model.PageContext=pageContext;
        if(((RazorView)found.View).RazorPage is Microsoft.AspNetCore.Mvc.RazorPages.Page page)page.PageContext=pageContext;
        var view=new ViewContext(action,found.View,data,new TempDataDictionary(context,context.RequestServices.GetRequiredService<ITempDataProvider>()),output,new HtmlHelperOptions());await found.View.RenderAsync(view);context.Response.ContentType="text/html;charset=utf-8";await context.Response.WriteAsync(output.ToString());
    });
    await app.RunAsync();
}
static void Check(bool value,string label){if(!value)throw new Exception(label);}
static void Throws(Action action,string label){try{action();}catch(InvalidDataException){return;}throw new Exception(label);}
