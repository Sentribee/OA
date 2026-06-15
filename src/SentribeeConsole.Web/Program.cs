using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MySqlConnector;
using SentribeeConsole.Web.Application.Contracts;
using SentribeeConsole.Web.Application.Services;
using SentribeeConsole.Web.Domain.Entities;
using SentribeeConsole.Web.Infrastructure.Analysis;
using SentribeeConsole.Web.Infrastructure.Aws;
using SentribeeConsole.Web.Infrastructure.Git;
using SentribeeConsole.Web.Infrastructure.Repositories;
using SentribeeConsole.Web.Infrastructure.OpenAI;
using SentribeeConsole.Web.Infrastructure.Runtime;
using SentribeeConsole.Web.Infrastructure.Storage;
using SentribeeConsole.Web.Infrastructure.Training;
using SentribeeConsole.Web.Infrastructure.Weather;
using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);

var dataProtectionPath = builder.Configuration["DataProtection:KeyPath"];
if (string.IsNullOrWhiteSpace(dataProtectionPath))
{
    dataProtectionPath = Path.Combine(
        builder.Environment.ContentRootPath,
        "..",
        "..",
        "shared",
        "data-protection-keys");
}

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("SentribeeConsole.Web");

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Dashboard");
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".Sentribee.Console.Session";
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.Configure<CosStorageOptions>(builder.Configuration.GetSection(CosStorageOptions.SectionName));
builder.Services.Configure<S3StorageOptions>(builder.Configuration.GetSection(S3StorageOptions.SectionName));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection(OpenAIOptions.SectionName));
builder.Services.Configure<EdgeEventAutoAnalysisOptions>(builder.Configuration.GetSection(EdgeEventAutoAnalysisOptions.SectionName));
builder.Services.AddScoped<IAdminRepository, AdminRepository>();
builder.Services.AddScoped<IAdminAuthenticationService, AdminAuthenticationService>();
builder.Services.AddScoped<IAdminProfileService, AdminProfileService>();
builder.Services.AddHttpClient<IFileStorageService, S3EdgeImageStorageService>();
builder.Services.AddHttpClient<IEdgeImageStorageService, S3EdgeImageStorageService>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddHttpClient<IConsoleEmailService, SesConsoleEmailService>();
builder.Services.AddScoped<IEdgeDeviceRepository, EdgeDeviceRepository>();
builder.Services.AddScoped<IEdgeDeviceService, EdgeDeviceService>();
builder.Services.AddScoped<IEdgeRuntimeService, LocalEdgeRuntimeService>();
builder.Services.AddHttpClient<IServerResourceService, AwsServerResourceService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddHttpClient<IWeatherForecastService, MetServiceForecastService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<IYoloModelRepository, YoloModelRepository>();
builder.Services.AddScoped<PanoramaTrainingDatasetExporter>();
builder.Services.AddScoped<YoloTrainingRunStore>();
builder.Services.AddScoped<YoloRemoteTrainingRunner>();
builder.Services.AddSingleton<PanoramaTrainingDatasetExportQueue>();
builder.Services.AddScoped<IYoloModelService, YoloModelService>();
builder.Services.AddHostedService<PanoramaTrainingDatasetExportWorker>();
builder.Services.AddHostedService<YoloTrainingScheduleWorker>();
builder.Services.AddScoped<IProjectRequirementService, ProjectRequirementService>();
builder.Services.AddScoped<IEdgeAiRepository, EdgeAiRepository>();
builder.Services.AddScoped<IEdgeAiGitService, EdgeAiGitService>();
builder.Services.AddScoped<IEdgeAiService, EdgeAiService>();
builder.Services.AddHttpClient<IProjectRuleGenerator, OpenAIProjectRuleGenerator>((services, client) =>
{
    var openAIOptions = services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
    client.BaseAddress = new Uri($"{openAIOptions.BaseUrl.TrimEnd('/')}/");
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddHttpClient<IPpeEventReviewService, PpeEventReviewService>((services, client) =>
{
    var openAIOptions = services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
    client.BaseAddress = new Uri($"{openAIOptions.BaseUrl.TrimEnd('/')}/");
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddHttpClient<IEdgeEventAutoAnalysisService, EdgeEventAutoAnalysisService>((services, client) =>
{
    var analysisOptions = services.GetRequiredService<IOptions<EdgeEventAutoAnalysisOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(Math.Max(10, analysisOptions.TimeoutSeconds));
});
builder.Services.AddHttpClient();
builder.Services.AddScoped<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    var path = context.Request.Path;
    if (host.Equals("crm.sentribee.ai", StringComparison.OrdinalIgnoreCase))
    {
        var targetPath = path.HasValue && path.Value.StartsWith("/crm/", StringComparison.OrdinalIgnoreCase)
            ? $"/oa{path.Value[4..]}"
            : path.Value is "/" or "" ? "/oa/dashboard" : path.Value ?? "/oa/dashboard";
        var target = $"https://oa.sentribee.ai{targetPath}{context.Request.QueryString}";
        context.Response.Redirect(target, permanent: false);
        return;
    }

    if (host.Equals("oa.sentribee.ai", StringComparison.OrdinalIgnoreCase))
    {
        if (path == "/" || path == PathString.Empty)
        {
            context.Request.Path = "/oa/dashboard";
        }
        else if (path.Equals("/login", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = "/oa/login";
        }
        else if (path.Equals("/register", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = "/oa/register";
        }
        else if (path.HasValue && path.Value.StartsWith("/crm/", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Path = $"/oa{path.Value[4..]}";
        }
        else if (path.HasValue &&
            !path.Value.StartsWith("/oa/", StringComparison.OrdinalIgnoreCase) &&
            !path.Value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !System.IO.Path.HasExtension(path.Value))
        {
            context.Request.Path = $"/oa{path.Value}";
        }
    }
    else if (host.Equals("chat.sentribee.ai", StringComparison.OrdinalIgnoreCase) &&
        path.HasValue &&
        !path.Value.StartsWith("/chat/", StringComparison.OrdinalIgnoreCase) &&
        !path.Value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
        !System.IO.Path.HasExtension(path.Value))
    {
        var corpId = path.Value.Trim('/');
        if (!string.IsNullOrWhiteSpace(corpId))
        {
            context.Request.Path = $"/chat/{corpId}";
        }
    }

    await next();
});

app.UseRouting();

app.UseAuthentication();
app.UseSession();
app.UseAuthorization();

app.MapRazorPages();

app.MapGet("/api/crm/public/chat/{publicChatPath}/avatar", async (
    string publicChatPath,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT bot.AvatarUrl
        FROM bee_CrmChatbot AS bot
        INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = bot.MerchantId
        WHERE bot.PublicChatPath = @PublicChatPath
          AND bot.Status = 'Active'
          AND merchant.Status = 'Active'
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PublicChatPath", MySqlDbType.VarChar, 160).Value = publicChatPath.Trim().ToLowerInvariant();
    var avatarUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(avatarUrl))
    {
        return Results.NotFound(new { message = "Bot avatar not found." });
    }

    return await StreamProtectedAnalysisImageAsync(avatarUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapGet("/api/crm/bots/{botId:long}/avatar", async (
    long botId,
    HttpContext context,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var merchantId = GetCurrentCrmMerchantId(context);
    if (merchantId is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT AvatarUrl
        FROM bee_CrmChatbot
        WHERE id = @BotId AND MerchantId = @MerchantId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@BotId", MySqlDbType.Int64).Value = botId;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId.Value;
    var avatarUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(avatarUrl))
    {
        return Results.NotFound(new { message = "Bot avatar not found." });
    }

    return await StreamProtectedAnalysisImageAsync(avatarUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapGet("/api/crm/public/chat/{publicChatPath}/messages/{messageId:long}/image", async (
    string publicChatPath,
    long messageId,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT message.ImageUrl
        FROM bee_CrmConversationMessage AS message
        INNER JOIN bee_CrmConversation AS conversation ON conversation.id = message.ConversationId
        INNER JOIN bee_CrmChatbot AS bot ON bot.id = conversation.ChatbotId
        INNER JOIN bee_CrmMerchant AS merchant ON merchant.id = conversation.MerchantId
        WHERE message.id = @MessageId
          AND bot.PublicChatPath = @PublicChatPath
          AND bot.Status = 'Active'
          AND merchant.Status = 'Active'
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@MessageId", MySqlDbType.Int64).Value = messageId;
    command.Parameters.Add("@PublicChatPath", MySqlDbType.VarChar, 160).Value = publicChatPath.Trim().ToLowerInvariant();
    var imageUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Message image not found." });
    }

    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapGet("/api/crm/conversation-messages/{messageId:long}/image", async (
    long messageId,
    HttpContext context,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var merchantId = GetCurrentCrmMerchantId(context);
    if (merchantId is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT message.ImageUrl
        FROM bee_CrmConversationMessage AS message
        INNER JOIN bee_CrmConversation AS conversation ON conversation.id = message.ConversationId
        WHERE message.id = @MessageId
          AND conversation.MerchantId = @MerchantId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@MessageId", MySqlDbType.Int64).Value = messageId;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId.Value;
    var imageUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Message image not found." });
    }

    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapGet("/api/crm/knowledge-documents/{documentId:long}/file", async (
    long documentId,
    HttpContext context,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    bool? download,
    CancellationToken cancellationToken) =>
{
    var merchantId = GetCurrentCrmMerchantId(context);
    if (merchantId is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT FileUrl, FileName, ContentType
        FROM bee_CrmKnowledgeDocument
        WHERE id = @DocumentId AND MerchantId = @MerchantId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@DocumentId", MySqlDbType.Int64).Value = documentId;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId.Value;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Knowledge document not found." });
    }

    var fileUrl = reader["FileUrl"] as string;
    if (string.IsNullOrWhiteSpace(fileUrl))
    {
        return Results.NotFound(new { message = "Knowledge document file not found." });
    }

    var fileName = download == true ? reader["FileName"] as string : null;
    var contentType = reader["ContentType"] as string;
    return await StreamProtectedAnalysisImageAsync(fileUrl, configuration, s3Options.Value, httpClient, cancellationToken, fileName, contentType);
});

app.MapGet("/api/model/training-lock", async (
    ClaimsPrincipal user,
    YoloTrainingRunStore trainingRunStore,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var locked = await trainingRunStore.HasActiveRunForAdminAsync(adminId, cancellationToken);
    return Results.Ok(new { locked });
}).RequireAuthorization();

app.MapGet("/api/admin/avatar", async (
    ClaimsPrincipal user,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT AvatarUrl
        FROM bee_Admin
        WHERE id = @AdminId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    var avatarUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(avatarUrl))
    {
        return Results.NotFound(new { message = "Administrator avatar not found." });
    }

    return await StreamProtectedAnalysisImageAsync(avatarUrl, configuration, s3Options.Value, httpClient, cancellationToken);
}).RequireAuthorization();

app.MapGet("/api/projects/current/logo", async (
    HttpContext context,
    ClaimsPrincipal user,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var selectedProjectId = context.Session.GetInt32("CurrentProjectId");
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT project.LogoUrl
        FROM bee_Project AS project
        LEFT JOIN bee_ProjectMember AS membership
            ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
        WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
        ORDER BY CASE WHEN project.id = @SelectedProjectId THEN 0 ELSE 1 END, project.CreatedAtUtc, project.id
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    command.Parameters.Add("@SelectedProjectId", MySqlDbType.Int32).Value =
        (object?)selectedProjectId ?? DBNull.Value;
    var logoUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(logoUrl))
    {
        return Results.NotFound(new { message = "Project logo not found." });
    }

    return await StreamProtectedAnalysisImageAsync(logoUrl, configuration, s3Options.Value, httpClient, cancellationToken);
}).RequireAuthorization();

app.MapPost("/api/edge/auth", async (
    EdgeAuthPayload payload,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(payload.ApiKey))
    {
        return Results.BadRequest(new { message = "API key is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var apiKeyHash = HashSecret(payload.ApiKey);
    const string projectSql = """
        SELECT id, ProjectName
        FROM bee_Project
        WHERE ApiKeyHash = @ApiKeyHash
        LIMIT 1;
        """;
    await using var projectCommand = new MySqlCommand(projectSql, connection);
    projectCommand.Parameters.Add("@ApiKeyHash", MySqlDbType.VarChar, 128).Value = apiKeyHash;
    await using var reader = await projectCommand.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.Unauthorized();
    }

    var projectId = reader.GetInt32(reader.GetOrdinal("id"));
    var projectName = reader["ProjectName"] as string ?? string.Empty;
    await reader.CloseAsync();

    var token = $"sb_token_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
    var expiresAtUtc = DateTime.UtcNow.AddHours(12);
    const string sessionSql = """
        INSERT INTO bee_ProjectApiClientSession (ProjectId, TokenHash, ClientName, ExpiresAtUtc)
        VALUES (@ProjectId, @TokenHash, @ClientName, @ExpiresAtUtc);
        """;
    await using var sessionCommand = new MySqlCommand(sessionSql, connection);
    sessionCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    sessionCommand.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 128).Value = HashSecret(token);
    sessionCommand.Parameters.Add("@ClientName", MySqlDbType.VarChar, 150).Value = (object?)payload.ClientName ?? DBNull.Value;
    sessionCommand.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
    await sessionCommand.ExecuteNonQueryAsync(cancellationToken);

    return Results.Ok(new
    {
        accessToken = token,
        tokenType = "Bearer",
        expiresAtUtc,
        projectId,
        projectName
    });
});

app.MapPost("/api/edge/heartbeat", async (
    EdgeHeartbeatPayload payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(payload.DeviceCode))
    {
        return Results.BadRequest(new { message = "Device code is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var deviceId = await FindDeviceIdAsync(connection, session.ProjectId, payload.DeviceCode, cancellationToken);
    if (deviceId is null)
    {
        return Results.NotFound(new { message = "Device not found for this project." });
    }

    const string sql = """
        INSERT INTO bee_EdgeAiHeartbeat
            (ProjectId, EdgeDeviceId, RuntimeStatus, DeviceStatus, DetailJson, ReportedAtUtc)
        VALUES (@ProjectId, @EdgeDeviceId, @RuntimeStatus, @DeviceStatus, @DetailJson, @ReportedAtUtc);
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = deviceId.Value;
    var runtimeStatus = NormalizeRequired(payload.RuntimeStatus, "Unknown");
    var deviceStatus = NormalizeHeartbeatDeviceStatus(payload);
    var heartbeatDetailJson = BuildHeartbeatDetailJson(payload);
    command.Parameters.Add("@RuntimeStatus", MySqlDbType.VarChar, 80).Value = runtimeStatus;
    command.Parameters.Add("@DeviceStatus", MySqlDbType.VarChar, 80).Value = deviceStatus;
    command.Parameters.Add("@DetailJson", MySqlDbType.JSON).Value =
        string.IsNullOrWhiteSpace(heartbeatDetailJson) ? DBNull.Value : heartbeatDetailJson;
    command.Parameters.Add("@ReportedAtUtc", MySqlDbType.DateTime).Value = payload.ReportedAtUtc ?? DateTime.UtcNow;
    await command.ExecuteNonQueryAsync(cancellationToken);
    await UpsertDailyStatFromHeartbeatAsync(
        connection,
        session.ProjectId,
        deviceId.Value,
        payload.ReportedAtUtc ?? DateTime.UtcNow,
        payload,
        heartbeatDetailJson,
        cancellationToken);

    return Results.Ok(new { success = true, acceptedAtUtc = DateTime.UtcNow });
});

app.MapPost("/api/edge/events", async (
    EdgeEventUploadPayload payload,
    HttpRequest request,
    IConfiguration configuration,
    IEdgeImageStorageService imageStorage,
    IPpeEventReviewService ppeReviewService,
    IEdgeEventAutoAnalysisService autoAnalysisService,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(payload.DeviceCode) || string.IsNullOrWhiteSpace(payload.Title))
    {
        return Results.BadRequest(new { message = "Device code and title are required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var deviceId = await FindDeviceIdAsync(connection, session.ProjectId, payload.DeviceCode, cancellationToken);
    if (deviceId is null)
    {
        return Results.NotFound(new { message = "Device not found for this project." });
    }

    var imageUrl = payload.ImageUrl;
    byte[]? uploadedImageBytes = null;
    if (!string.IsNullOrWhiteSpace(payload.ImageBase64))
    {
        var contentType = NormalizeRequired(payload.ImageContentType, "image/jpeg");
        var extension = contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        uploadedImageBytes = Convert.FromBase64String(payload.ImageBase64);
        await using var imageStream = new MemoryStream(uploadedImageBytes);
        var stored = await imageStorage.UploadAsync(
            imageStream,
            contentType,
            extension,
            $"edge-events/{session.ProjectId}/{payload.DeviceCode}",
            cancellationToken);
        imageUrl = stored.PublicUrl;
    }

    const string sql = """
        INSERT INTO bee_EdgeEvent
            (EdgeDeviceId, Title, EventDescription, ImageUrl, EventTimeUtc, RawPayloadJson)
        VALUES (@EdgeDeviceId, @Title, @EventDescription, @ImageUrl, @EventTimeUtc, @RawPayloadJson);
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = deviceId.Value;
    command.Parameters.Add("@Title", MySqlDbType.VarChar, 200).Value = payload.Title.Trim();
    command.Parameters.Add("@EventDescription", MySqlDbType.Text).Value = (object?)payload.Description ?? DBNull.Value;
    command.Parameters.Add("@ImageUrl", MySqlDbType.VarChar, 500).Value = (object?)imageUrl ?? DBNull.Value;
    command.Parameters.Add("@EventTimeUtc", MySqlDbType.DateTime).Value = payload.EventTimeUtc ?? DateTime.UtcNow;
    command.Parameters.Add("@RawPayloadJson", MySqlDbType.JSON).Value =
        JsonSerializer.Serialize(payload with { ImageBase64 = null }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await command.ExecuteNonQueryAsync(cancellationToken);
    var eventId = Convert.ToInt32(command.LastInsertedId);

    // Event uploads come from edge clients with their own request timeouts. Once the
    // event row exists, keep verification and auto-analysis running to completion.
    var processingCancellationToken = CancellationToken.None;
    await ppeReviewService.ReviewEventAsync(eventId, uploadedImageBytes, payload.ImageContentType, processingCancellationToken);
    var autoAnalysis = await autoAnalysisService.AnalyzeAsync(
        eventId,
        session.ProjectId,
        payload.DeviceCode,
        imageUrl,
        uploadedImageBytes,
        payload.ImageContentType,
        payload.DetectionJson?.GetRawText(),
        processingCancellationToken);
    var verifiedReview = await LoadVerifiedEventReviewAsync(connection, eventId, processingCancellationToken);
    var eventAnalysis = autoAnalysis?.Analysis ?? ApplyVerifiedReviewToAnalysis(BuildEventAnalysisFromUpload(payload), verifiedReview);
    eventAnalysis = await PersistEventSubjectImagesToS3Async(
        eventAnalysis,
        imageStorage,
        configuration,
        eventId,
        session.ProjectId,
        payload.DeviceCode,
        processingCancellationToken);
    if (!string.IsNullOrWhiteSpace(autoAnalysis?.AnnotationJson))
    {
        await UpdateEventAutoAnnotationAsync(connection, eventId, autoAnalysis.AnnotationJson, processingCancellationToken);
    }

    await SaveEventAnalysisAsync(connection, eventId, eventAnalysis, processingCancellationToken);
    await SaveEventSubjectsAsync(connection, eventId, eventAnalysis.Subjects, processingCancellationToken);
    await RefreshDailyStatFromEventAsync(
        connection,
        session.ProjectId,
        deviceId.Value,
        payload.EventTimeUtc ?? DateTime.UtcNow,
        processingCancellationToken);
    await CreateUnreadRiskNotificationsAsync(connection, eventId, processingCancellationToken);
    await DispatchQueuedAppPushNotificationsAsync(configuration, httpClient, eventId, processingCancellationToken);

    return Results.Ok(new
    {
        success = true,
        eventId,
        imageUrl,
        verifiedStatus = verifiedReview?.Status,
        analysis = eventAnalysis
    });
});

app.MapPost("/api/edge/events/{eventId:int}/analysis", async (
    int eventId,
    EdgeEventAnalysisPayload payload,
    HttpRequest request,
    IConfiguration configuration,
    IEdgeImageStorageService imageStorage,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var eventContext = await FindEdgeEventContextAsync(connection, session.ProjectId, eventId, cancellationToken);
    if (eventContext is null)
    {
        return Results.NotFound(new { message = "Event not found for this project." });
    }

    var eventAnalysis = await PersistEventSubjectImagesToS3Async(
        BuildEventAnalysisFromPayload(payload),
        imageStorage,
        configuration,
        eventId,
        session.ProjectId,
        eventContext.DeviceCode,
        cancellationToken);
    await SaveEventAnalysisAsync(connection, eventId, eventAnalysis, cancellationToken);
    await SaveEventSubjectsAsync(connection, eventId, eventAnalysis.Subjects, cancellationToken);

    const string eventSql = """
        SELECT evt.EdgeDeviceId, evt.EventTimeUtc
        FROM bee_EdgeEvent AS evt
        WHERE evt.id = @EventId
        LIMIT 1;
        """;
    await using var eventCommand = new MySqlCommand(eventSql, connection);
    eventCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    await using var reader = await eventCommand.ExecuteReaderAsync(cancellationToken);
    if (await reader.ReadAsync(cancellationToken))
    {
        var edgeDeviceId = reader.GetInt32(reader.GetOrdinal("EdgeDeviceId"));
        var eventTimeUtc = reader.GetDateTime(reader.GetOrdinal("EventTimeUtc"));
        await reader.CloseAsync();
        await RefreshDailyStatFromEventAsync(connection, session.ProjectId, edgeDeviceId, eventTimeUtc, cancellationToken);
        await CreateUnreadRiskNotificationsAsync(connection, eventId, cancellationToken);
        await DispatchQueuedAppPushNotificationsAsync(configuration, httpClient, eventId, cancellationToken);
    }

    return Results.Ok(new { success = true, eventId, analysis = eventAnalysis });
});

app.MapPost("/api/edge/events/{eventId:int}/video/uploads", async (
    int eventId,
    EdgeEventVideoUploadStartPayload payload,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var contentType = NormalizeRequired(payload.ContentType, "video/mp4");
    if (!contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "Video content type must start with video/." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var eventContext = await FindEdgeEventContextAsync(connection, session.ProjectId, eventId, cancellationToken);
    if (eventContext is null)
    {
        return Results.NotFound(new { message = "Event not found for this project." });
    }

    var options = s3Options.Value;
    ValidateS3Options(options);
    var extension = NormalizeVideoExtension(payload.FileName, contentType);
    var key = $"edge-events/{session.ProjectId}/{eventContext.DeviceCode}/videos/{eventId}/{Guid.NewGuid():N}{extension}";
    var s3Uri = BuildS3Uri(options, key, new Dictionary<string, string> { ["uploads"] = string.Empty });
    var s3Request = BuildS3Request(HttpMethod.Post, s3Uri, null, options, "UNSIGNED-PAYLOAD");
    using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
    if (!s3Response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)s3Response.StatusCode);
    }

    var xml = await s3Response.Content.ReadAsStringAsync(cancellationToken);
    var uploadId = XDocument.Parse(xml)
        .Descendants()
        .FirstOrDefault(element => element.Name.LocalName == "UploadId")
        ?.Value;
    if (string.IsNullOrWhiteSpace(uploadId))
    {
        return Results.Problem("S3 did not return a multipart upload id.");
    }

    const string insertSql = """
        INSERT INTO bee_EdgeEventVideo
            (EdgeEventId, S3Key, UploadId, FileName, ContentType, FileSizeBytes, Status, PartEtagsJson)
        VALUES (@EdgeEventId, @S3Key, @UploadId, @FileName, @ContentType, @FileSizeBytes, 'Uploading', JSON_ARRAY());
        """;
    await using var insertCommand = new MySqlCommand(insertSql, connection);
    insertCommand.Parameters.Add("@EdgeEventId", MySqlDbType.Int32).Value = eventId;
    insertCommand.Parameters.Add("@S3Key", MySqlDbType.VarChar, 700).Value = key;
    insertCommand.Parameters.Add("@UploadId", MySqlDbType.VarChar, 700).Value = uploadId;
    insertCommand.Parameters.Add("@FileName", MySqlDbType.VarChar, 255).Value = (object?)payload.FileName ?? DBNull.Value;
    insertCommand.Parameters.Add("@ContentType", MySqlDbType.VarChar, 100).Value = contentType;
    insertCommand.Parameters.Add("@FileSizeBytes", MySqlDbType.Int64).Value = (object?)payload.FileSizeBytes ?? DBNull.Value;
    await insertCommand.ExecuteNonQueryAsync(cancellationToken);

    return Results.Ok(new
    {
        success = true,
        videoUploadId = insertCommand.LastInsertedId,
        uploadId,
        eventId,
        key,
        recommendedPartSizeBytes = 8 * 1024 * 1024
    });
});

app.MapPut("/api/edge/events/{eventId:int}/video/uploads/{videoUploadId:int}/parts/{partNumber:int}", async (
    int eventId,
    int videoUploadId,
    int partNumber,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (partNumber is < 1 or > 10000)
    {
        return Results.BadRequest(new { message = "Part number must be between 1 and 10000." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindVideoUploadAsync(connection, session.ProjectId, eventId, videoUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Video upload not found for this event." });
    }

    if (!string.Equals(upload.Status, "Uploading", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = $"Video upload is {upload.Status} and cannot accept more parts." });
    }

    await using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, cancellationToken);
    if (buffer.Length == 0)
    {
        return Results.BadRequest(new { message = "Video part body is required." });
    }

    buffer.Position = 0;
    var options = s3Options.Value;
    ValidateS3Options(options);
    var s3Uri = BuildS3Uri(options, upload.S3Key, new Dictionary<string, string>
    {
        ["partNumber"] = partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["uploadId"] = upload.UploadId
    });
    var s3Request = BuildS3Request(HttpMethod.Put, s3Uri, null, options, "UNSIGNED-PAYLOAD");
    s3Request.Content = new StreamContent(buffer);
    s3Request.Content.Headers.ContentLength = buffer.Length;
    s3Request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(upload.ContentType);
    using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
    if (!s3Response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)s3Response.StatusCode);
    }

    var etag = s3Response.Headers.ETag?.Tag;
    if (string.IsNullOrWhiteSpace(etag) &&
        s3Response.Headers.TryGetValues("ETag", out var values))
    {
        etag = values.FirstOrDefault();
    }
    if (string.IsNullOrWhiteSpace(etag))
    {
        return Results.Problem("S3 did not return an ETag for the uploaded part.");
    }

    var parts = UpsertVideoPart(upload.Parts, partNumber, etag);
    await SaveVideoUploadPartsAsync(connection, videoUploadId, parts, cancellationToken);

    return Results.Ok(new
    {
        success = true,
        videoUploadId,
        partNumber,
        etag,
        uploadedParts = parts
    });
});

app.MapGet("/api/edge/events/{eventId:int}/video/uploads/{videoUploadId:int}", async (
    int eventId,
    int videoUploadId,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindVideoUploadAsync(connection, session.ProjectId, eventId, videoUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Video upload not found for this event." });
    }

    return Results.Ok(new
    {
        success = true,
        videoUploadId,
        eventId,
        upload.Status,
        upload.VideoUrl,
        uploadedParts = upload.Parts.OrderBy(part => part.PartNumber)
    });
});

app.MapPost("/api/edge/events/{eventId:int}/video/uploads/{videoUploadId:int}/complete", async (
    int eventId,
    int videoUploadId,
    EdgeEventVideoUploadCompletePayload? payload,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindVideoUploadAsync(connection, session.ProjectId, eventId, videoUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Video upload not found for this event." });
    }

    var parts = (payload?.Parts?.Count > 0 ? payload.Parts : upload.Parts)
        .OrderBy(part => part.PartNumber)
        .ToList();
    if (parts.Count == 0)
    {
        return Results.BadRequest(new { message = "Upload at least one video part before completing." });
    }

    var options = s3Options.Value;
    ValidateS3Options(options);
    var completeXml = BuildCompleteMultipartUploadXml(parts);
    var completeBytes = Encoding.UTF8.GetBytes(completeXml);
    var payloadHash = Convert.ToHexString(SHA256.HashData(completeBytes)).ToLowerInvariant();
    var s3Uri = BuildS3Uri(options, upload.S3Key, new Dictionary<string, string> { ["uploadId"] = upload.UploadId });
    var s3Request = BuildS3Request(HttpMethod.Post, s3Uri, "application/xml", options, payloadHash);
    s3Request.Content = new ByteArrayContent(completeBytes);
    s3Request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
    using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
    if (!s3Response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)s3Response.StatusCode);
    }

    var publicBaseUrl = string.IsNullOrWhiteSpace(options.PublicBaseUrl)
        ? $"https://{options.Bucket}.s3.{options.Region}.amazonaws.com"
        : options.PublicBaseUrl.TrimEnd('/');
    var videoUrl = $"{publicBaseUrl}/{string.Join('/', upload.S3Key.Split('/').Select(Uri.EscapeDataString))}";

    const string updateSql = """
        UPDATE bee_EdgeEventVideo
        SET Status = 'Completed',
            VideoUrl = @VideoUrl,
            PartEtagsJson = @PartEtagsJson,
            CompletedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @VideoUploadId;
        """;
    await using var updateCommand = new MySqlCommand(updateSql, connection);
    updateCommand.Parameters.Add("@VideoUrl", MySqlDbType.VarChar, 1000).Value = videoUrl;
    updateCommand.Parameters.Add("@PartEtagsJson", MySqlDbType.JSON).Value =
        JsonSerializer.Serialize(parts, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    updateCommand.Parameters.Add("@VideoUploadId", MySqlDbType.Int32).Value = videoUploadId;
    await updateCommand.ExecuteNonQueryAsync(cancellationToken);

    return Results.Ok(new
    {
        success = true,
        eventId,
        videoUploadId,
        videoUrl
    });
});

app.MapDelete("/api/edge/events/{eventId:int}/video/uploads/{videoUploadId:int}", async (
    int eventId,
    int videoUploadId,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindVideoUploadAsync(connection, session.ProjectId, eventId, videoUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Video upload not found for this event." });
    }

    var options = s3Options.Value;
    ValidateS3Options(options);
    var s3Uri = BuildS3Uri(options, upload.S3Key, new Dictionary<string, string> { ["uploadId"] = upload.UploadId });
    var s3Request = BuildS3Request(HttpMethod.Delete, s3Uri, null, options, "UNSIGNED-PAYLOAD");
    using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
    if (!s3Response.IsSuccessStatusCode && s3Response.StatusCode != System.Net.HttpStatusCode.NotFound)
    {
        return Results.StatusCode((int)s3Response.StatusCode);
    }

    const string updateSql = """
        UPDATE bee_EdgeEventVideo
        SET Status = 'Aborted'
        WHERE id = @VideoUploadId;
        """;
    await using var updateCommand = new MySqlCommand(updateSql, connection);
    updateCommand.Parameters.Add("@VideoUploadId", MySqlDbType.Int32).Value = videoUploadId;
    await updateCommand.ExecuteNonQueryAsync(cancellationToken);

    return Results.Ok(new { success = true, eventId, videoUploadId, status = "Aborted" });
});

app.MapGet("/api/events/{eventId:int}/image", async (
    int eventId,
    ClaimsPrincipal user,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string findSql = """
        SELECT evt.ImageUrl
        FROM bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        WHERE evt.id = @EventId
          AND device.ProjectId IN (
            SELECT project.id
            FROM bee_Project AS project
            LEFT JOIN bee_ProjectMember AS membership
                ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
            WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
          )
        LIMIT 1;
        """;
    await using var findCommand = new MySqlCommand(findSql, connection);
    findCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    findCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    var imageUrl = await findCommand.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Event image not found." });
    }

    var options = s3Options.Value;
    var s3Uri = new Uri(imageUrl);
    var expectedHost = $"{options.Bucket}.s3.{options.Region}.amazonaws.com";
    if (!string.Equals(s3Uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect(imageUrl);
    }

    var request = BuildS3Request(HttpMethod.Get, s3Uri, null, options, "UNSIGNED-PAYLOAD");
    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)response.StatusCode);
    }

    var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    return Results.File(bytes, contentType);
}).RequireAuthorization();

app.MapGet("/api/edge-analysis-artifacts/{**artifactPath}", async (
    string artifactPath,
    ClaimsPrincipal user,
    IConfiguration configuration,
    IOptions<EdgeEventAutoAnalysisOptions> analysisOptions,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out _))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(artifactPath)
        || artifactPath.Contains("..", StringComparison.Ordinal)
        || artifactPath.StartsWith('/'))
    {
        return Results.BadRequest(new { message = "Invalid artifact path." });
    }

    var remoteBaseUrl = configuration["EdgeEventAutoAnalysis:RemoteBaseUrl"]
        ?? analysisOptions.Value.RemoteBaseUrl;
    if (string.IsNullOrWhiteSpace(remoteBaseUrl))
    {
        return Results.NotFound(new { message = "Remote analysis artifact service is not configured." });
    }

    var baseUri = new Uri(remoteBaseUrl.TrimEnd('/') + "/");
    var artifactUri = new Uri(baseUri, $"artifacts/{Uri.EscapeDataString(artifactPath).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}");
    using var response = await httpClient.GetAsync(artifactUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)response.StatusCode);
    }

    var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    return Results.File(bytes, contentType);
}).RequireAuthorization();

app.MapGet("/api/edge-event-subjects/{subjectId:long}/image/{kind}", async (
    long subjectId,
    string kind,
    ClaimsPrincipal user,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var normalizedKind = kind.Trim().ToLowerInvariant();
    if (normalizedKind is not ("crop" or "preview"))
    {
        return Results.BadRequest(new { message = "Subject image kind must be crop or preview." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT subject.CropImageUrl, subject.PreviewImageUrl
        FROM bee_EdgeEventSubject AS subject
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        WHERE subject.id = @SubjectId
          AND device.ProjectId IN (
            SELECT project.id
            FROM bee_Project AS project
            LEFT JOIN bee_ProjectMember AS membership
                ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
            WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
          )
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@SubjectId", MySqlDbType.Int64).Value = subjectId;
    command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Event subject image not found." });
    }

    var imageUrl = normalizedKind == "crop"
        ? reader["CropImageUrl"] as string
        : reader["PreviewImageUrl"] as string;
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Event subject image not found." });
    }

    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken);
}).RequireAuthorization();

app.MapGet("/api/app/events/{eventId:int}/image", async (
    int eventId,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT evt.ImageUrl
        FROM bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        INNER JOIN bee_EdgeDeviceUserBinding AS binding
            ON binding.EdgeDeviceId = device.id AND binding.AppUserId = @AppUserId
        WHERE evt.id = @EventId
            AND device.ProjectId = @ProjectId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    var imageUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Event image not found." });
    }

    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapGet("/api/app/edge-event-subjects/{subjectId:long}/image/{kind}", async (
    long subjectId,
    string kind,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var normalizedKind = kind.Trim().ToLowerInvariant();
    if (normalizedKind is not ("crop" or "preview"))
    {
        return Results.BadRequest(new { message = "Subject image kind must be crop or preview." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT subject.CropImageUrl, subject.PreviewImageUrl
        FROM bee_EdgeEventSubject AS subject
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        INNER JOIN bee_EdgeDeviceUserBinding AS binding
            ON binding.EdgeDeviceId = device.id AND binding.AppUserId = @AppUserId
        WHERE subject.id = @SubjectId
            AND device.ProjectId = @ProjectId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@SubjectId", MySqlDbType.Int64).Value = subjectId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Event subject image not found." });
    }

    var imageUrl = normalizedKind == "crop"
        ? reader["CropImageUrl"] as string
        : reader["PreviewImageUrl"] as string;
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Event subject image not found." });
    }

    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapGet("/api/events/{eventId:int}/analysis-detail", async (
    int eventId,
    ClaimsPrincipal user,
    IProjectService projectService,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
    var canEditEvents = project?.CanEditEvents == true;

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string eventSql = """
        SELECT evt.id, evt.EdgeDeviceId, device.DeviceName, device.DeviceCode,
            evt.Title, evt.EventDescription, evt.ImageUrl, evt.EventTimeUtc, evt.Status,
            COALESCE(evt.LearningStatus, 'None') AS LearningStatus,
            evt.AnnotationJson, evt.PpeReviewJson,
            analysis.PeopleCount, analysis.MachineryVehicleCount, analysis.ToolCount,
            analysis.PpeCompliantPeopleCount, analysis.RiskPersonCount, analysis.PpeComplianceRate,
            analysis.RiskCategory, analysis.RiskSeverity, analysis.Summary, analysis.AnalysisJson
        FROM bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        LEFT JOIN bee_EdgeEventAnalysis AS analysis ON analysis.EdgeEventId = evt.id
        WHERE evt.id = @EventId
          AND device.ProjectId IN (
            SELECT project.id
            FROM bee_Project AS project
            LEFT JOIN bee_ProjectMember AS membership
                ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
            WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
          )
        LIMIT 1;
        """;
    await using var eventCommand = new MySqlCommand(eventSql, connection);
    eventCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    eventCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    await using var reader = await eventCommand.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Event not found." });
    }

    var analysisJson = reader["AnalysisJson"] as string;
    var eventAnnotationJson = reader["AnnotationJson"] as string;
    var analysisObject = ParseJsonObject(analysisJson);
    var panoramaAnnotation = ParseJsonNode(eventAnnotationJson)
        ?? analysisObject?["panoramaAnnotation"]
        ?? analysisObject?["sceneAnnotation"];
    var eventResult = new
    {
        id = reader.GetInt32(reader.GetOrdinal("id")),
        title = reader["Title"] as string ?? string.Empty,
        description = reader["EventDescription"] as string,
        status = reader["Status"] as string ?? "Ordinary Risk",
        learningStatus = reader["LearningStatus"] as string ?? "None",
        imageUrl = string.IsNullOrWhiteSpace(reader["ImageUrl"] as string) ? null : $"/api/events/{eventId}/image",
        eventTimeUtc = reader.GetDateTime(reader.GetOrdinal("EventTimeUtc")),
        edgeDevice = new
        {
            id = reader.GetInt32(reader.GetOrdinal("EdgeDeviceId")),
            name = reader["DeviceName"] as string ?? string.Empty,
            code = reader["DeviceCode"] as string ?? string.Empty
        },
        analysis = new
        {
            peopleCount = DbInt(reader, "PeopleCount"),
            machineryVehicleCount = DbInt(reader, "MachineryVehicleCount"),
            toolCount = DbInt(reader, "ToolCount"),
            ppeCompliantPeopleCount = DbInt(reader, "PpeCompliantPeopleCount"),
            riskPersonCount = DbInt(reader, "RiskPersonCount"),
            ppeComplianceRate = DbDecimal(reader, "PpeComplianceRate"),
            riskCategory = reader["RiskCategory"] as string,
            riskSeverity = reader["RiskSeverity"] as string,
            summary = reader["Summary"] as string,
            analysisJson = ParseJsonNode(analysisJson),
            ppeReview = ParseJsonNode(reader["PpeReviewJson"] as string),
            panoramaAnnotation
        }
    };
    await reader.CloseAsync();

    const string subjectSql = """
        SELECT id, SubjectKey, SubjectType, TrackingLabel, CropImageUrl, PreviewImageUrl,
            BoundingBoxJson, PpeBoxJson, PpeStatusJson, COALESCE(LearningStatus, 'None') AS LearningStatus,
            IsRisk, RiskCategory, RiskSeverity, RiskReason, AnalysisJson
        FROM bee_EdgeEventSubject
        WHERE EdgeEventId = @EventId
        ORDER BY
            CASE WHEN SubjectType = 'Person' THEN 0 ELSE 1 END,
            SubjectKey,
            id;
        """;
    var subjects = new List<object>();
    var personSubjectCount = 0;
    var riskSubjectCount = 0;
    await using var subjectCommand = new MySqlCommand(subjectSql, connection);
    subjectCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    await using var subjectReader = await subjectCommand.ExecuteReaderAsync(cancellationToken);
    while (await subjectReader.ReadAsync(cancellationToken))
    {
        var subjectType = subjectReader["SubjectType"] as string ?? string.Empty;
        var isRisk = subjectReader.GetBoolean(subjectReader.GetOrdinal("IsRisk"));
        if (subjectType.Equals("Person", StringComparison.OrdinalIgnoreCase))
        {
            personSubjectCount++;
        }

        if (isRisk)
        {
            riskSubjectCount++;
        }

        subjects.Add(new
        {
            id = subjectReader.GetInt32(subjectReader.GetOrdinal("id")),
            subjectKey = subjectReader["SubjectKey"] as string ?? string.Empty,
            subjectType,
            trackingLabel = subjectReader["TrackingLabel"] as string,
            cropImageUrl = BuildSubjectImageProxyUrl(subjectReader.GetInt64(subjectReader.GetOrdinal("id")), "crop", subjectReader["CropImageUrl"] as string),
            previewImageUrl = BuildSubjectImageProxyUrl(subjectReader.GetInt64(subjectReader.GetOrdinal("id")), "preview", subjectReader["PreviewImageUrl"] as string),
            boundingBox = ParseJsonNode(subjectReader["BoundingBoxJson"] as string),
            ppeBoxes = ParseJsonNode(subjectReader["PpeBoxJson"] as string),
            ppeStatus = ParseJsonNode(subjectReader["PpeStatusJson"] as string),
            learningStatus = subjectReader["LearningStatus"] as string ?? "None",
            isRisk,
            riskCategory = subjectReader["RiskCategory"] as string,
            riskSeverity = subjectReader["RiskSeverity"] as string,
            riskReason = subjectReader["RiskReason"] as string,
            analysisJson = ParseJsonNode(subjectReader["AnalysisJson"] as string)
        });
    }
    await subjectReader.CloseAsync();

    var annotationLogs = await LoadAnnotationOperationLogsAsync(connection, eventId, cancellationToken);

    return Results.Ok(new
    {
        eventResult.id,
        eventResult.title,
        eventResult.description,
        eventResult.status,
        eventResult.learningStatus,
        eventResult.imageUrl,
        eventResult.eventTimeUtc,
        eventResult.edgeDevice,
        eventResult.analysis,
        canEditEvents,
        subjects,
        annotationLogs,
        summary = new
        {
            panoramaObjectCount = CountJsonBoxes(panoramaAnnotation),
            subjectCount = subjects.Count,
            personSubjectCount,
            riskSubjectCount
        }
    });
}).RequireAuthorization();

app.MapPost("/api/events/{eventId:int}/annotations", async (
    int eventId,
    EventAnnotationPayload payload,
    ClaimsPrincipal user,
    IProjectService projectService,
    YoloTrainingRunStore trainingRunStore,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
    if (project?.CanEditEvents != true)
    {
        return Results.Forbid();
    }

    if (payload.SaveAsPendingLearning && await trainingRunStore.HasActiveRunForAdminAsync(adminId, cancellationToken))
    {
        return Results.BadRequest(new { message = "Pending Learning saves are disabled while model training is being prepared or running." });
    }

    if (payload.ImageWidth <= 0 || payload.ImageHeight <= 0)
    {
        return Results.BadRequest(new { message = "Invalid annotation image size." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string findSql = """
        SELECT evt.id, device.ProjectId
        FROM bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        WHERE evt.id = @EventId
          AND device.ProjectId IN (
            SELECT project.id
            FROM bee_Project AS project
            LEFT JOIN bee_ProjectMember AS membership
                ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
            WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
          )
        LIMIT 1;
        """;
    await using var findCommand = new MySqlCommand(findSql, connection);
    findCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    findCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    int projectId;
    await using (var findReader = await findCommand.ExecuteReaderAsync(cancellationToken))
    {
        if (!await findReader.ReadAsync(cancellationToken))
        {
            return Results.NotFound(new { message = "Event not found." });
        }

        projectId = findReader.GetInt32(findReader.GetOrdinal("ProjectId"));
    }

    if (projectId <= 0)
    {
        return Results.NotFound(new { message = "Event not found." });
    }

    var annotation = new EventAnnotationDocument(
        payload.ImageUrl,
        payload.ImageWidth,
        payload.ImageHeight,
        payload.Classes,
        payload.Boxes);
    var annotationJson = JsonSerializer.Serialize(annotation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var yoloText = BuildYoloText(payload);
    var relativeFolder = $"/annotations/events/{eventId}";
    var outputFolder = Path.Combine(environment.WebRootPath, "annotations", "events", eventId.ToString());
    Directory.CreateDirectory(outputFolder);
    await File.WriteAllTextAsync(Path.Combine(outputFolder, "labels.txt"), yoloText, Encoding.UTF8, cancellationToken);
    await File.WriteAllTextAsync(Path.Combine(outputFolder, "annotation.json"), annotationJson, Encoding.UTF8, cancellationToken);

    const string updateSql = """
        UPDATE bee_EdgeEvent
        SET AnnotationJson = @AnnotationJson,
            YoloLabelUrl = @YoloLabelUrl,
            LearningStatus = CASE WHEN @SaveAsPendingLearning = 1 THEN 'Pending Learning' ELSE COALESCE(LearningStatus, 'None') END,
            AnnotatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @EventId;
        """;
    await using var updateCommand = new MySqlCommand(updateSql, connection);
    updateCommand.Parameters.Add("@AnnotationJson", MySqlDbType.MediumText).Value = annotationJson;
    updateCommand.Parameters.Add("@YoloLabelUrl", MySqlDbType.VarChar, 500).Value = $"{relativeFolder}/labels.txt";
    updateCommand.Parameters.Add("@SaveAsPendingLearning", MySqlDbType.Bit).Value = payload.SaveAsPendingLearning;
    updateCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
    await SyncEventAnalysisAnnotationAsync(connection, eventId, annotationJson, cancellationToken);
    await InsertAnnotationOperationLogAsync(
        connection,
        projectId,
        "Event",
        eventId,
        eventId,
        null,
        adminId,
        user,
        payload.SaveAsPendingLearning ? "SaveEventAnnotationAsPendingLearning" : "SaveEventAnnotation",
        payload.Boxes.Count,
        payload.SaveAsPendingLearning,
        cancellationToken);

    return Results.Ok(new
    {
        success = true,
        yoloLabelUrl = $"{relativeFolder}/labels.txt",
        learningStatus = payload.SaveAsPendingLearning ? "Pending Learning" : null,
        savedBy = BuildAnnotationActor(user),
        savedAtUtc = DateTime.UtcNow
    });
}).RequireAuthorization();

app.MapPost("/api/edge-event-subjects/{subjectId:long}/ppe-annotations", async (
    long subjectId,
    EventAnnotationPayload payload,
    ClaimsPrincipal user,
    IProjectService projectService,
    YoloTrainingRunStore trainingRunStore,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
    if (project?.CanEditEvents != true)
    {
        return Results.Forbid();
    }

    if (payload.SaveAsPendingLearning && await trainingRunStore.HasActiveRunForAdminAsync(adminId, cancellationToken))
    {
        return Results.BadRequest(new { message = "Pending Learning saves are disabled while model training is being prepared or running." });
    }

    if (payload.ImageWidth <= 0 || payload.ImageHeight <= 0)
    {
        return Results.BadRequest(new { message = "Invalid annotation image size." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string updateSql = """
        UPDATE bee_EdgeEventSubject AS subject
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        SET subject.PpeBoxJson = @PpeBoxJson,
            subject.LearningStatus = CASE WHEN @SaveAsPendingLearning = 1 THEN 'Pending Learning' ELSE COALESCE(subject.LearningStatus, 'None') END,
            subject.UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE subject.id = @SubjectId
          AND device.ProjectId IN (
            SELECT project.id
            FROM bee_Project AS project
            LEFT JOIN bee_ProjectMember AS membership
                ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
            WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
          );
        """;
    var ppeBoxJson = BuildSubjectPpeBoxJson(payload);
    await using var updateCommand = new MySqlCommand(updateSql, connection);
    updateCommand.Parameters.Add("@PpeBoxJson", MySqlDbType.JSON).Value = ppeBoxJson;
    updateCommand.Parameters.Add("@SaveAsPendingLearning", MySqlDbType.Bit).Value = payload.SaveAsPendingLearning;
    updateCommand.Parameters.Add("@SubjectId", MySqlDbType.Int64).Value = subjectId;
    updateCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
    {
        return Results.NotFound(new { message = "Event subject not found." });
    }

    const string subjectContextSql = """
        SELECT subject.EdgeEventId, device.ProjectId
        FROM bee_EdgeEventSubject AS subject
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        WHERE subject.id = @SubjectId
        LIMIT 1;
        """;
    await using var subjectContextCommand = new MySqlCommand(subjectContextSql, connection);
    subjectContextCommand.Parameters.Add("@SubjectId", MySqlDbType.Int64).Value = subjectId;
    int? subjectProjectId = null;
    int? subjectEventId = null;
    await using (var subjectContextReader = await subjectContextCommand.ExecuteReaderAsync(cancellationToken))
    {
        if (await subjectContextReader.ReadAsync(cancellationToken))
        {
            subjectProjectId = subjectContextReader.GetInt32(subjectContextReader.GetOrdinal("ProjectId"));
            subjectEventId = subjectContextReader.GetInt32(subjectContextReader.GetOrdinal("EdgeEventId"));
        }
    }
    if (subjectProjectId is not null && subjectEventId is not null)
    {
        await InsertAnnotationOperationLogAsync(
            connection,
            subjectProjectId.Value,
            "PersonSlicePpe",
            subjectId,
            subjectEventId.Value,
            subjectId,
            adminId,
            user,
            payload.SaveAsPendingLearning ? "SavePersonSlicePpeAnnotationAsPendingLearning" : "SavePersonSlicePpeAnnotation",
            payload.Boxes.Count,
            payload.SaveAsPendingLearning,
            cancellationToken);
    }

    return Results.Ok(new
    {
        success = true,
        subjectId,
        ppeBoxJson,
        learningStatus = payload.SaveAsPendingLearning ? "Pending Learning" : null,
        savedBy = BuildAnnotationActor(user),
        savedAtUtc = DateTime.UtcNow
    });
}).RequireAuthorization();

app.MapPost("/api/events/{eventId:int}/real-risk", async (
    int eventId,
    ClaimsPrincipal user,
    IProjectService projectService,
    IConfiguration configuration,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
    if (project?.CanEditEvents != true)
    {
        return Results.Forbid();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string updateSql = """
        UPDATE bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        SET evt.Status = 'Real Risk'
        WHERE evt.id = @EventId
          AND device.ProjectId IN (
            SELECT project.id
            FROM bee_Project AS project
            LEFT JOIN bee_ProjectMember AS membership
                ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
            WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
          )
          AND evt.Status = 'Pending Review';
        """;
    await using var command = new MySqlCommand(updateSql, connection);
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    var rows = await command.ExecuteNonQueryAsync(cancellationToken);
    if (rows == 0)
    {
        return Results.BadRequest(new { message = "Only pending review events can be marked as real risk." });
    }

    await CreateUnreadRiskNotificationsAsync(connection, eventId, cancellationToken);
    await DispatchQueuedAppPushNotificationsAsync(configuration, httpClient, eventId, cancellationToken);

    return Results.Ok(new { success = true, status = "Real Risk" });
}).RequireAuthorization();

app.MapGet("/api/model/classes", async (
    ClaimsPrincipal user,
    IProjectService projectService,
    IYoloModelRepository yoloModelRepository,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
    if (project is null)
    {
        return Results.NotFound(new { message = "Project not found." });
    }

    return Results.Ok(new
    {
        classes = YoloYamlFile.DefaultModelClasses().Select(item => new
        {
            id = item.Index,
            name = item.Name
        })
    });
}).RequireAuthorization();

app.MapGet("/api/model/pending-learning-review", async (
    string modelKind,
    ClaimsPrincipal user,
    IProjectService projectService,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
    if (project?.CanViewModels != true)
    {
        return Results.Forbid();
    }

    var normalizedKind = NormalizeReviewModelKind(modelKind);
    if (normalizedKind is null)
    {
        return Results.BadRequest(new { message = "Unknown model kind." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var items = normalizedKind == "panorama"
        ? await LoadPanoramaPendingReviewItemsAsync(connection, project.Id, cancellationToken)
        : await LoadPersonSlicePendingReviewItemsAsync(connection, project.Id, cancellationToken);

    return Results.Ok(new { modelKind = normalizedKind, items });
}).RequireAuthorization();

app.MapPost("/api/model/pending-learning-review/cancel", async (
    PendingLearningReviewCancelPayload payload,
    ClaimsPrincipal user,
    IProjectService projectService,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
    if (project?.CanEditModel != true)
    {
        return Results.Forbid();
    }

    var normalizedKind = NormalizeReviewModelKind(payload.ModelKind);
    if (normalizedKind is null || payload.TargetId <= 0)
    {
        return Results.BadRequest(new { message = "Unknown pending learning item." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var result = normalizedKind == "panorama"
        ? await CancelPanoramaPendingLearningAsync(connection, project.Id, adminId, payload.TargetId, cancellationToken)
        : await CancelPersonSlicePendingLearningAsync(connection, project.Id, adminId, payload.TargetId, cancellationToken);
    if (!result)
    {
        return Results.NotFound(new { message = "Pending learning item was not found." });
    }

    return Results.Ok(new { success = true });
}).RequireAuthorization();

app.MapGet("/api/model/pending-learning-review/mistakes", async (
    DateTime? date,
    ClaimsPrincipal user,
    IProjectService projectService,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var project = await projectService.GetByAdminIdAsync(adminId, cancellationToken);
    if (project?.CanViewModels != true)
    {
        return Results.Forbid();
    }

    var localDate = (date ?? ProjectTimeZone.ConvertUtc(DateTime.UtcNow, project.TimeZoneId)).Date;
    var fromUtc = ProjectTimeZone.ConvertLocalToUtc(localDate, project.TimeZoneId);
    var toUtc = ProjectTimeZone.ConvertLocalToUtc(localDate.AddDays(1), project.TimeZoneId);
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var rows = await LoadAnnotationMistakeStatsAsync(connection, project.Id, fromUtc, toUtc, cancellationToken);
    return Results.Ok(new
    {
        date = localDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        rows
    });
}).RequireAuthorization();

app.MapGet("/api/events/{eventId:int}/video", async (
    int eventId,
    ClaimsPrincipal user,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string findSql = """
        SELECT video.VideoUrl
        FROM bee_EdgeEventVideo AS video
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = video.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        WHERE evt.id = @EventId
            AND device.ProjectId IN (
                SELECT project.id
                FROM bee_Project AS project
                LEFT JOIN bee_ProjectMember AS membership
                    ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
                WHERE project.AdminId = @AdminId OR membership.AdminId = @AdminId
            )
            AND video.Status = 'Completed'
            AND video.VideoUrl IS NOT NULL
        ORDER BY video.id DESC
        LIMIT 1;
        """;
    await using var findCommand = new MySqlCommand(findSql, connection);
    findCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    findCommand.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    var videoUrl = await findCommand.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(videoUrl))
    {
        return Results.NotFound(new { message = "Event video not found." });
    }

    var options = s3Options.Value;
    var s3Uri = new Uri(videoUrl);
    var expectedHost = $"{options.Bucket}.s3.{options.Region}.amazonaws.com";
    if (!string.Equals(s3Uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Redirect(videoUrl);
    }

    var request = BuildS3Request(HttpMethod.Get, s3Uri, null, options, "UNSIGNED-PAYLOAD");
    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)response.StatusCode);
    }

    var contentType = response.Content.Headers.ContentType?.ToString() ?? "video/mp4";
    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    return Results.File(bytes, contentType, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapPost("/api/app/auth/email-code", async (
    AppEmailCodeRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var edgeSession = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (edgeSession is null)
    {
        return Results.Unauthorized();
    }

    if (payload.ProjectId.HasValue && payload.ProjectId.Value != edgeSession.ProjectId)
    {
        return Results.Forbid();
    }

    var email = NormalizeEmail(payload.Email);
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new { message = "Email is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await ProjectExistsAsync(connection, edgeSession.ProjectId, cancellationToken))
    {
        return Results.NotFound(new { message = "Project not found." });
    }

    var requestedPurpose = NormalizeVerificationPurpose(payload.Purpose);
    var emailExists = await AppUserEmailExistsAsync(connection, null, edgeSession.ProjectId, email, cancellationToken);
    var purpose = emailExists ? "Login" : requestedPurpose == "Login" ? "Register" : requestedPurpose;
    var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
    var expiresAtUtc = DateTime.UtcNow.AddMinutes(10);
    const string insertSql = """
        INSERT INTO bee_AppUserVerificationCode
            (ProjectId, PhoneNumber, Email, Purpose, CodeHash, ExpiresAtUtc)
        VALUES (@ProjectId, NULL, @Email, @Purpose, @CodeHash, @ExpiresAtUtc);
        """;
    await using var command = new MySqlCommand(insertSql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = edgeSession.ProjectId;
    command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
    command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = purpose;
    command.Parameters.Add("@CodeHash", MySqlDbType.VarChar, 128).Value = HashSecret($"{edgeSession.ProjectId}:{email}:{purpose}:{code}");
    command.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
    await command.ExecuteNonQueryAsync(cancellationToken);
    var verificationCodeId = command.LastInsertedId;

    var emailResult = await SendVerificationEmailAsync(httpClient, configuration, email, code, cancellationToken);
    await SaveEmailDeliveryAsync(connection, edgeSession.ProjectId, verificationCodeId, email, purpose, emailResult, cancellationToken);
    if (!emailResult.Success)
    {
        return Results.Problem(emailResult.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    return Results.Ok(new { success = true, expiresAtUtc });
});

app.MapPost("/api/app/auth/sms-code", async (
    AppSmsCodeRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var edgeSession = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (edgeSession is null)
    {
        return Results.Unauthorized();
    }

    if (payload.ProjectId.HasValue && payload.ProjectId.Value != edgeSession.ProjectId)
    {
        return Results.Forbid();
    }

    var phoneNumber = NormalizePhoneNumber(payload.PhoneNumber);
    if (string.IsNullOrWhiteSpace(phoneNumber))
    {
        return Results.BadRequest(new { message = "Phone number is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await ProjectExistsAsync(connection, edgeSession.ProjectId, cancellationToken))
    {
        return Results.NotFound(new { message = "Project not found." });
    }

    var purpose = NormalizeVerificationPurpose(payload.Purpose);
    var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
    var expiresAtUtc = DateTime.UtcNow.AddMinutes(10);
    const string insertSql = """
        INSERT INTO bee_AppUserVerificationCode
            (ProjectId, PhoneNumber, Purpose, CodeHash, ExpiresAtUtc)
        VALUES (@ProjectId, @PhoneNumber, @Purpose, @CodeHash, @ExpiresAtUtc);
        """;
    await using var command = new MySqlCommand(insertSql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = edgeSession.ProjectId;
    command.Parameters.Add("@PhoneNumber", MySqlDbType.VarChar, 40).Value = phoneNumber;
    command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = purpose;
    command.Parameters.Add("@CodeHash", MySqlDbType.VarChar, 128).Value = HashSecret($"{edgeSession.ProjectId}:{phoneNumber}:{purpose}:{code}");
    command.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
    await command.ExecuteNonQueryAsync(cancellationToken);
    var verificationCodeId = command.LastInsertedId;

    var smsResult = await SendVonageSmsAsync(
        httpClientFactory.CreateClient(),
        configuration,
        request,
        phoneNumber,
        $"Your Sentribee verification code is {code}. It expires in 10 minutes.",
        cancellationToken);
    await SaveSmsDeliveryAsync(
        connection,
        edgeSession.ProjectId,
        verificationCodeId,
        phoneNumber,
        purpose,
        smsResult,
        cancellationToken);
    if (!smsResult.Success)
    {
        return Results.Problem(smsResult.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    return Results.Ok(new
    {
        success = true,
        expiresAtUtc,
        messageId = smsResult.ProviderMessageId,
        providerStatus = smsResult.ProviderStatus
    });
});

app.MapGet("/api/app/sms/delivery-receipt", async (
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var query = request.Query;
    var messageId = query["messageId"].FirstOrDefault()
        ?? query["message-id"].FirstOrDefault()
        ?? query["messageId"].FirstOrDefault();
    var status = query["status"].FirstOrDefault();
    var errorCode = query["err-code"].FirstOrDefault()
        ?? query["error-code"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(messageId))
    {
        return Results.BadRequest(new { message = "messageId is required." });
    }

    var receipt = JsonSerializer.Serialize(query.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.ToString(),
        StringComparer.OrdinalIgnoreCase));
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        UPDATE bee_AppSmsDelivery
        SET DeliveryStatus = @DeliveryStatus,
            ErrorCode = @ErrorCode,
            DeliveryReceiptJson = @DeliveryReceiptJson,
            DeliveredAtUtc = CASE
                WHEN LOWER(@DeliveryStatus) = 'delivered' THEN UTC_TIMESTAMP(6)
                ELSE DeliveredAtUtc
            END,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE Provider = 'Vonage'
            AND ProviderMessageId = @ProviderMessageId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProviderMessageId", MySqlDbType.VarChar, 120).Value = messageId;
    command.Parameters.Add("@DeliveryStatus", MySqlDbType.VarChar, 80).Value = DbNullable(NormalizeBounded(status, 80));
    command.Parameters.Add("@ErrorCode", MySqlDbType.VarChar, 40).Value = DbNullable(NormalizeBounded(errorCode, 40));
    command.Parameters.Add("@DeliveryReceiptJson", MySqlDbType.JSON).Value = receipt;
    await command.ExecuteNonQueryAsync(cancellationToken);
    return Results.Ok(new { success = true });
});

app.MapPost("/api/app/auth/register", async (
    AppRegisterRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var edgeSession = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (edgeSession is null)
    {
        return Results.Unauthorized();
    }

    if (payload.ProjectId.HasValue && payload.ProjectId.Value != edgeSession.ProjectId)
    {
        return Results.Forbid();
    }

    var email = NormalizeEmail(payload.Email);
    var displayName = payload.DisplayName?.Trim();
    var firstName = NormalizeBounded(payload.FirstName, 80);
    var lastName = NormalizeBounded(payload.LastName, 80);
    var gender = NormalizeGender(payload.Gender);
    var verificationCode = payload.VerificationCode?.Trim();
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new { message = "Email is required." });
    }

    if (string.IsNullOrWhiteSpace(displayName) || displayName.Length < 2)
    {
        return Results.BadRequest(new { message = "Display name is required." });
    }

    if (string.IsNullOrWhiteSpace(verificationCode))
    {
        return Results.BadRequest(new { message = "Verification code is required." });
    }

    if (!HasUsefulDeviceInfo(payload.Device))
    {
        return Results.BadRequest(new { message = "Device info is required for app registration." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

    if (await AppUserEmailExistsAsync(connection, transaction, edgeSession.ProjectId, email, cancellationToken))
    {
        return Results.Conflict(new { message = "This email is already registered. Please login instead." });
    }

    var codeId = await FindValidVerificationCodeAsync(
        connection,
        transaction,
        edgeSession.ProjectId,
        email,
        "Register",
        verificationCode,
        allowLoginFallback: true,
        cancellationToken);
    if (codeId is null)
    {
        return Results.BadRequest(new { message = "Verification code is invalid or expired." });
    }

    const string userSql = """
        INSERT INTO bee_AppUser (ProjectId, PhoneNumber, Email, DisplayName, FirstName, LastName, Gender)
        VALUES (@ProjectId, NULL, @Email, @DisplayName, @FirstName, @LastName, @Gender);
        SELECT id
        FROM bee_AppUser
        WHERE ProjectId = @ProjectId AND Email = @Email
        LIMIT 1;
        """;
    await using var userCommand = new MySqlCommand(userSql, connection, transaction);
    userCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = edgeSession.ProjectId;
    userCommand.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
    userCommand.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 100).Value = displayName;
    userCommand.Parameters.Add("@FirstName", MySqlDbType.VarChar, 80).Value = DbNullable(firstName);
    userCommand.Parameters.Add("@LastName", MySqlDbType.VarChar, 80).Value = DbNullable(lastName);
    userCommand.Parameters.Add("@Gender", MySqlDbType.VarChar, 40).Value = DbNullable(gender);
    var appUserId = Convert.ToInt32(await userCommand.ExecuteScalarAsync(cancellationToken));

    const string consumeSql = """
        UPDATE bee_AppUserVerificationCode
        SET ConsumedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @CodeId;
        """;
    await using var consumeCommand = new MySqlCommand(consumeSql, connection, transaction);
    consumeCommand.Parameters.Add("@CodeId", MySqlDbType.Int64).Value = codeId.Value;
    await consumeCommand.ExecuteNonQueryAsync(cancellationToken);

    var token = $"sb_app_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
    var expiresAtUtc = DateTime.UtcNow.AddDays(30);
    const string sessionSql = """
        INSERT INTO bee_AppUserSession (ProjectId, AppUserId, TokenHash, ExpiresAtUtc)
        VALUES (@ProjectId, @AppUserId, @TokenHash, @ExpiresAtUtc);
        """;
    await using var sessionCommand = new MySqlCommand(sessionSql, connection, transaction);
    sessionCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = edgeSession.ProjectId;
    sessionCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    sessionCommand.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 128).Value = HashSecret(token);
    sessionCommand.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
    await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
    await SaveAppUserDeviceAsync(connection, transaction, edgeSession.ProjectId, appUserId, payload.Device, cancellationToken);

    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new
    {
        accessToken = token,
        tokenType = "Bearer",
        expiresAtUtc,
        user = new { id = appUserId, email, displayName, firstName, lastName, gender, projectId = edgeSession.ProjectId }
    });
});

app.MapPost("/api/app/auth/login", async (
    AppLoginRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var edgeSession = await AuthenticateEdgeSessionAsync(request, configuration, cancellationToken);
    if (edgeSession is null)
    {
        return Results.Unauthorized();
    }

    if (payload.ProjectId.HasValue && payload.ProjectId.Value != edgeSession.ProjectId)
    {
        return Results.Forbid();
    }

    var email = NormalizeEmail(payload.Email);
    var verificationCode = payload.VerificationCode?.Trim();
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new { message = "Email is required." });
    }

    if (string.IsNullOrWhiteSpace(verificationCode))
    {
        return Results.BadRequest(new { message = "Verification code is required." });
    }

    if (!HasUsefulDeviceInfo(payload.Device))
    {
        return Results.BadRequest(new { message = "Device info is required for app login." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

    var codeId = await FindValidVerificationCodeAsync(
        connection,
        transaction,
        edgeSession.ProjectId,
        email,
        "Login",
        verificationCode,
        allowLoginFallback: false,
        cancellationToken);
    if (codeId is null)
    {
        return Results.BadRequest(new { message = "Verification code is invalid or expired." });
    }

    const string findUserSql = """
        SELECT id, DisplayName, FirstName, LastName, Gender
        FROM bee_AppUser
        WHERE ProjectId = @ProjectId AND Email = @Email AND Status = 'Active'
        LIMIT 1;
        """;
    await using var userCommand = new MySqlCommand(findUserSql, connection, transaction);
    userCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = edgeSession.ProjectId;
    userCommand.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
    await using var reader = await userCommand.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Registered app user not found." });
    }

    var appUserId = reader.GetInt32(reader.GetOrdinal("id"));
    var displayName = reader["DisplayName"] as string ?? string.Empty;
    var firstName = reader["FirstName"] as string;
    var lastName = reader["LastName"] as string;
    var gender = reader["Gender"] as string;
    await reader.CloseAsync();

    const string consumeSql = """
        UPDATE bee_AppUserVerificationCode
        SET ConsumedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @CodeId;
        """;
    await using var consumeCommand = new MySqlCommand(consumeSql, connection, transaction);
    consumeCommand.Parameters.Add("@CodeId", MySqlDbType.Int64).Value = codeId.Value;
    await consumeCommand.ExecuteNonQueryAsync(cancellationToken);

    var token = $"sb_app_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
    var expiresAtUtc = DateTime.UtcNow.AddDays(30);
    const string sessionSql = """
        INSERT INTO bee_AppUserSession (ProjectId, AppUserId, TokenHash, ExpiresAtUtc)
        VALUES (@ProjectId, @AppUserId, @TokenHash, @ExpiresAtUtc);
        """;
    await using var sessionCommand = new MySqlCommand(sessionSql, connection, transaction);
    sessionCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = edgeSession.ProjectId;
    sessionCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    sessionCommand.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 128).Value = HashSecret(token);
    sessionCommand.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
    await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
    await SaveAppUserDeviceAsync(connection, transaction, edgeSession.ProjectId, appUserId, payload.Device, cancellationToken);

    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new
    {
        accessToken = token,
        tokenType = "Bearer",
        expiresAtUtc,
        user = new { id = appUserId, email, displayName, firstName, lastName, gender, projectId = edgeSession.ProjectId }
    });
});

app.MapGet("/api/app/profile", async (
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var profile = await GetAppUserProfileAsync(configuration, session.ProjectId, session.AppUserId, cancellationToken);
    return profile is null
        ? Results.NotFound(new { message = "App user not found." })
        : Results.Ok(new { user = profile });
});

app.MapPut("/api/app/profile", async (
    AppProfileUpdateRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var displayName = payload.DisplayName?.Trim();
    var firstName = NormalizeBounded(payload.FirstName, 80);
    var lastName = NormalizeBounded(payload.LastName, 80);
    var gender = NormalizeGender(payload.Gender);
    if (string.IsNullOrWhiteSpace(displayName) || displayName.Length < 2)
    {
        return Results.BadRequest(new { message = "Display name is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        UPDATE bee_AppUser
        SET DisplayName = @DisplayName,
            FirstName = @FirstName,
            LastName = @LastName,
            Gender = @Gender,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @AppUserId
            AND ProjectId = @ProjectId
            AND Status = 'Active';
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 100).Value = displayName;
    command.Parameters.Add("@FirstName", MySqlDbType.VarChar, 80).Value = DbNullable(firstName);
    command.Parameters.Add("@LastName", MySqlDbType.VarChar, 80).Value = DbNullable(lastName);
    command.Parameters.Add("@Gender", MySqlDbType.VarChar, 40).Value = DbNullable(gender);
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
    {
        return Results.NotFound(new { message = "App user not found." });
    }

    var profile = await GetAppUserProfileAsync(configuration, session.ProjectId, session.AppUserId, cancellationToken);
    return Results.Ok(new { user = profile });
});

app.MapPost("/api/spendbee/v1/auth/email-code", async (
    SpendBeeEmailCodeRequest payload,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken) =>
{
    var email = NormalizeEmail(payload.Email);
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new { message = "Email is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var projectId = await ResolveSpendBeeProjectIdAsync(connection, cancellationToken);
    if (projectId is null)
    {
        return Results.NotFound(new { message = "SpendBee project not found." });
    }

    var requestedPurpose = NormalizeVerificationPurpose(payload.Purpose);
    var emailExists = await AppUserEmailExistsAsync(connection, null, projectId.Value, email, cancellationToken);
    var purpose = emailExists ? "Login" : requestedPurpose == "Login" ? "Register" : requestedPurpose;
    var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
    var expiresAtUtc = DateTime.UtcNow.AddMinutes(10);
    const string insertSql = """
        INSERT INTO bee_AppUserVerificationCode
            (ProjectId, PhoneNumber, Email, Purpose, CodeHash, ExpiresAtUtc)
        VALUES (@ProjectId, NULL, @Email, @Purpose, @CodeHash, @ExpiresAtUtc);
        """;
    await using var command = new MySqlCommand(insertSql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId.Value;
    command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
    command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = purpose;
    command.Parameters.Add("@CodeHash", MySqlDbType.VarChar, 128).Value = HashSecret($"{projectId.Value}:{email}:{purpose}:{code}");
    command.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
    await command.ExecuteNonQueryAsync(cancellationToken);
    var verificationCodeId = command.LastInsertedId;

    var emailResult = await SendVerificationEmailAsync(httpClientFactory.CreateClient(), configuration, email, code, cancellationToken);
    await SaveEmailDeliveryAsync(connection, projectId.Value, verificationCodeId, email, purpose, emailResult, cancellationToken);
    if (!emailResult.Success)
    {
        return Results.Problem(emailResult.Message, statusCode: StatusCodes.Status502BadGateway);
    }

    return Results.Ok(new { success = true, purpose, expiresAtUtc });
});

app.MapPost("/api/spendbee/v1/auth/register", async (
    SpendBeeRegisterRequest payload,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var email = NormalizeEmail(payload.Email);
    var displayName = payload.DisplayName?.Trim();
    var gender = NormalizeGender(payload.Gender);
    var bio = NormalizeBounded(payload.Bio, 280);
    var avatarUrl = NormalizeBounded(payload.AvatarUrl, 500);
    var verificationCode = payload.VerificationCode?.Trim();
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new { message = "Email is required." });
    }

    if (string.IsNullOrWhiteSpace(verificationCode))
    {
        return Results.BadRequest(new { message = "Verification code is required." });
    }

    if (!HasUsefulDeviceInfo(payload.Device))
    {
        return Results.BadRequest(new { message = "Device info is required for SpendBee registration." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var projectId = await ResolveSpendBeeProjectIdAsync(connection, cancellationToken);
    if (projectId is null)
    {
        return Results.NotFound(new { message = "SpendBee project not found." });
    }

    await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
    if (await AppUserEmailExistsAsync(connection, transaction, projectId.Value, email, cancellationToken))
    {
        var existingCodeId = await FindValidVerificationCodeAsync(
            connection,
            transaction,
            projectId.Value,
            email,
            "Register",
            verificationCode,
            allowLoginFallback: true,
            cancellationToken);
        if (existingCodeId is null)
        {
            return Results.BadRequest(new { message = "Verification code is invalid or expired." });
        }

        var existingUser = await LoadSpendBeeAppUserForAuthAsync(connection, transaction, projectId.Value, email, cancellationToken);
        if (existingUser is null)
        {
            return Results.NotFound(new { message = "Registered SpendBee user not found." });
        }

        await ConsumeVerificationCodeAsync(connection, transaction, existingCodeId.Value, cancellationToken);
        var existingAuth = await CreateSpendBeeAuthResponseAsync(
            connection,
            transaction,
            projectId.Value,
            existingUser.Id,
            email,
            existingUser.DisplayName,
            existingUser.Gender,
            existingUser.AvatarUrl,
            existingUser.Bio,
            payload.Device,
            isNewUser: false,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(existingAuth);
    }

    if (string.IsNullOrWhiteSpace(displayName) || displayName.Length < 2)
    {
        return Results.BadRequest(new { message = "Display name is required." });
    }

    var codeId = await FindValidVerificationCodeAsync(
        connection,
        transaction,
        projectId.Value,
        email,
        "Register",
        verificationCode,
        allowLoginFallback: true,
        cancellationToken);
    if (codeId is null)
    {
        return Results.BadRequest(new { message = "Verification code is invalid or expired." });
    }

    const string userSql = """
        INSERT INTO bee_AppUser
            (ProjectId, PhoneNumber, Email, DisplayName, Gender, AvatarUrl, Bio, Status, ActivatedAtUtc)
        VALUES
            (@ProjectId, NULL, @Email, @DisplayName, @Gender, @AvatarUrl, @Bio, 'Active', UTC_TIMESTAMP(6));
        SELECT id
        FROM bee_AppUser
        WHERE ProjectId = @ProjectId AND Email = @Email
        LIMIT 1;
        """;
    await using var userCommand = new MySqlCommand(userSql, connection, transaction);
    userCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId.Value;
    userCommand.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
    userCommand.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 100).Value = displayName;
    userCommand.Parameters.Add("@Gender", MySqlDbType.VarChar, 40).Value = DbNullable(gender);
    userCommand.Parameters.Add("@AvatarUrl", MySqlDbType.VarChar, 500).Value = DbNullable(avatarUrl);
    userCommand.Parameters.Add("@Bio", MySqlDbType.VarChar, 280).Value = DbNullable(bio);
    var appUserId = Convert.ToInt32(await userCommand.ExecuteScalarAsync(cancellationToken));

    await ConsumeVerificationCodeAsync(connection, transaction, codeId.Value, cancellationToken);

    var auth = await CreateSpendBeeAuthResponseAsync(
        connection,
        transaction,
        projectId.Value,
        appUserId,
        email,
        displayName,
        gender,
        avatarUrl,
        bio,
        payload.Device,
        isNewUser: true,
        cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(auth);
});

app.MapPost("/api/spendbee/v1/auth/login", async (
    SpendBeeLoginRequest payload,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var email = NormalizeEmail(payload.Email);
    var verificationCode = payload.VerificationCode?.Trim();
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new { message = "Email is required." });
    }

    if (string.IsNullOrWhiteSpace(verificationCode))
    {
        return Results.BadRequest(new { message = "Verification code is required." });
    }

    if (!HasUsefulDeviceInfo(payload.Device))
    {
        return Results.BadRequest(new { message = "Device info is required for SpendBee login." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var projectId = await ResolveSpendBeeProjectIdAsync(connection, cancellationToken);
    if (projectId is null)
    {
        return Results.NotFound(new { message = "SpendBee project not found." });
    }

    await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
    var codeId = await FindValidVerificationCodeAsync(
        connection,
        transaction,
        projectId.Value,
        email,
        "Login",
        verificationCode,
        allowLoginFallback: false,
        cancellationToken);
    if (codeId is null)
    {
        return Results.BadRequest(new { message = "Verification code is invalid or expired." });
    }

    var user = await LoadSpendBeeAppUserForAuthAsync(connection, transaction, projectId.Value, email, cancellationToken);
    if (user is null)
    {
        return Results.NotFound(new { message = "Registered SpendBee user not found." });
    }

    await ConsumeVerificationCodeAsync(connection, transaction, codeId.Value, cancellationToken);
    var auth = await CreateSpendBeeAuthResponseAsync(
        connection,
        transaction,
        projectId.Value,
        user.Id,
        email,
        user.DisplayName,
        user.Gender,
        user.AvatarUrl,
        user.Bio,
        payload.Device,
        isNewUser: false,
        cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(auth);
});

app.MapPut("/api/spendbee/v1/profile", async (
    SpendBeeProfileUpdateRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var displayName = payload.DisplayName?.Trim();
    if (string.IsNullOrWhiteSpace(displayName) || displayName.Length < 2)
    {
        return Results.BadRequest(new { message = "Display name is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var nextAvatarUrl = NormalizeBounded(payload.AvatarUrl, 500);
    const string sql = """
        UPDATE bee_AppUser
        SET DisplayName = @DisplayName,
            Gender = @Gender,
            AvatarUrl = @AvatarUrl,
            Bio = @Bio,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @AppUserId
            AND ProjectId = @ProjectId
            AND Status = 'Active';
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 100).Value = displayName;
    command.Parameters.Add("@Gender", MySqlDbType.VarChar, 40).Value = DbNullable(NormalizeGender(payload.Gender));
    command.Parameters.Add("@AvatarUrl", MySqlDbType.VarChar, 500).Value = DbNullable(nextAvatarUrl);
    command.Parameters.Add("@Bio", MySqlDbType.VarChar, 280).Value = DbNullable(NormalizeBounded(payload.Bio, 280));
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
    {
        return Results.NotFound(new { message = "App user not found." });
    }

    var profile = await GetAppUserProfileAsync(configuration, session.ProjectId, session.AppUserId, cancellationToken);
    return profile is null
        ? Results.NotFound(new { message = "App user not found." })
        : Results.Ok(new { user = BuildSpendBeeProfileResponse(profile, BuildPublicRequestBaseUrl(request)) });
});

app.MapGet("/api/spendbee/v1/profile", async (
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var profile = await GetAppUserProfileAsync(configuration, session.ProjectId, session.AppUserId, cancellationToken);
    return profile is null
        ? Results.NotFound(new { message = "App user not found." })
        : Results.Ok(new { user = BuildSpendBeeProfileResponse(profile, BuildPublicRequestBaseUrl(request)) });
});

app.MapPost("/api/spendbee/v1/profile/avatar", async (
    HttpRequest request,
    IConfiguration configuration,
    IFileStorageService storage,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var contentType = NormalizeImageContentType(request.ContentType);
    if (contentType is null)
    {
        return Results.BadRequest(new { message = "Only jpg, png, and webp avatar images are supported." });
    }

    const long maxAvatarBytes = 5 * 1024 * 1024;
    if (request.ContentLength is <= 0 or > maxAvatarBytes)
    {
        return Results.BadRequest(new { message = "Avatar image must be between 1 byte and 5 MB." });
    }

    await using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, cancellationToken);
    if (buffer.Length is <= 0 or > maxAvatarBytes)
    {
        return Results.BadRequest(new { message = "Avatar image must be between 1 byte and 5 MB." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    buffer.Position = 0;
    var extension = NormalizeImageExtension(null, contentType);
    var stored = await storage.UploadAsync(
        buffer,
        contentType,
        extension,
        $"spendbee/app-users/{session.ProjectId}/{session.AppUserId}/avatars",
        cancellationToken);

    const string sql = """
        UPDATE bee_AppUser
        SET AvatarUrl = @AvatarUrl,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @AppUserId
            AND ProjectId = @ProjectId
            AND Status = 'Active';
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AvatarUrl", MySqlDbType.VarChar, 500).Value = stored.PublicUrl;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
    {
        return Results.NotFound(new { message = "App user not found." });
    }

    var profile = await GetAppUserProfileAsync(configuration, session.ProjectId, session.AppUserId, cancellationToken);
    var publicRequestBaseUrl = BuildPublicRequestBaseUrl(request);
    return Results.Ok(new
    {
        success = true,
        avatarUrl = BuildSpendBeeAppUserAvatarUrl(publicRequestBaseUrl, session.AppUserId, stored.PublicUrl),
        user = profile is null ? null : BuildSpendBeeProfileResponse(profile, publicRequestBaseUrl)
    });
});

app.MapGet("/api/spendbee/v1/users/{appUserId:int}/avatar", async (
    int appUserId,
    HttpResponse response,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT AvatarUrl
        FROM bee_AppUser
        WHERE id = @AppUserId
            AND Status = 'Active'
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    var avatarUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(avatarUrl))
    {
        return Results.NotFound(new { message = "Avatar not found." });
    }

    response.Headers.CacheControl = "public, max-age=86400";
    return await StreamProtectedAnalysisImageAsync(avatarUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapPut("/api/spendbee/v1/devices/current", async (
    AppClientDeviceInfo payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (!HasUsefulDeviceInfo(payload))
    {
        return Results.BadRequest(new { message = "Device information is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
    await SaveAppUserDeviceAsync(connection, transaction, session.ProjectId, session.AppUserId, payload, cancellationToken);
    await transaction.CommitAsync(cancellationToken);

    return Results.Ok(new { success = true });
});

app.MapGet("/api/spendbee/v1/messages", async (
    HttpRequest request,
    IConfiguration configuration,
    int? limit,
    long? beforeId,
    bool? unreadOnly,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var messages = await LoadSpendBeeMessagesAsync(
        connection,
        session.ProjectId,
        session.AppUserId,
        limit,
        beforeId,
        unreadOnly == true,
        cancellationToken);
    return Results.Ok(new { messages = messages.Items, page = messages.Page });
});

app.MapGet("/api/spendbee/v1/messages/unread-count", async (
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var count = await CountSpendBeeUnreadMessagesAsync(connection, session.ProjectId, session.AppUserId, cancellationToken);
    return Results.Ok(new { unreadCount = count });
});

app.MapPost("/api/spendbee/v1/messages/{messageId:long}/read", async (
    long messageId,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var message = await MarkAndLoadSpendBeeMessageReadAsync(connection, session.ProjectId, session.AppUserId, messageId, cancellationToken);
    if (message is null)
    {
        return Results.NotFound(new { message = "Message not found." });
    }

    var unreadCount = await CountSpendBeeUnreadMessagesAsync(connection, session.ProjectId, session.AppUserId, cancellationToken);
    return Results.Ok(new { success = true, messageId, message, unreadCount });
});

app.MapPost("/api/spendbee/v1/messages/{messageId:long}/open", async (
    long messageId,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var message = await MarkAndLoadSpendBeeMessageReadAsync(connection, session.ProjectId, session.AppUserId, messageId, cancellationToken);
    if (message is null)
    {
        return Results.NotFound(new { message = "Message not found." });
    }

    var unreadCount = await CountSpendBeeUnreadMessagesAsync(connection, session.ProjectId, session.AppUserId, cancellationToken);
    return Results.Ok(new { success = true, message, unreadCount });
});

app.MapPost("/api/spendbee/v1/messages/read-all", async (
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        UPDATE bee_SpendBeeUserMessage
        SET ReadAtUtc = COALESCE(ReadAtUtc, UTC_TIMESTAMP(6))
        WHERE ProjectId = @ProjectId
            AND AppUserId = @AppUserId
            AND ReadAtUtc IS NULL
            AND MessageType <> 'profile_avatar_updated';
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    var updated = await command.ExecuteNonQueryAsync(cancellationToken);
    return Results.Ok(new { success = true, updated });
});

app.MapPost("/api/spendbee/v1/receipt-uploads", async (
    SpendBeeReceiptMultipartUploadStartRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (payload.Images is null || payload.Images.Count == 0)
    {
        return Results.BadRequest(new { message = "At least one receipt image is required." });
    }

    if (payload.Images.Count > 8)
    {
        return Results.BadRequest(new { message = "A single receipt can include up to 8 images." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var options = s3Options.Value;
    ValidateS3Options(options);

    const string insertUploadSql = """
        INSERT INTO bee_SpendBeeReceiptUpload (ProjectId, AppUserId, Status, Timezone)
        VALUES (@ProjectId, @AppUserId, 'Uploading', @Timezone);
        SELECT LAST_INSERT_ID();
        """;
    await using var uploadCommand = new MySqlCommand(insertUploadSql, connection);
    uploadCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    uploadCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    uploadCommand.Parameters.Add("@Timezone", MySqlDbType.VarChar, 80).Value = DbNullable(NormalizeBounded(payload.Timezone, 80));
    var receiptUploadId = Convert.ToInt64(await uploadCommand.ExecuteScalarAsync(cancellationToken));

    var imageUploads = new List<object>();
    foreach (var image in payload.Images.Select((value, index) => new { value, index }))
    {
        var contentType = NormalizeImageContentType(image.value.ContentType);
        if (contentType is null)
        {
            return Results.BadRequest(new { message = $"Image {image.index + 1} content type is not supported." });
        }

        if (image.value.FileSizeBytes is <= 0 or > 80 * 1024 * 1024)
        {
            return Results.BadRequest(new { message = $"Image {image.index + 1} must be between 1 byte and 80 MB." });
        }

        var extension = NormalizeImageExtension(image.value.FileName, contentType);
        var key = $"spendbee/receipts/{session.ProjectId}/{session.AppUserId}/uploads/{receiptUploadId}/{Guid.NewGuid():N}{extension}";
        var s3Uri = BuildS3Uri(options, key, new Dictionary<string, string> { ["uploads"] = string.Empty });
        var s3Request = BuildS3Request(HttpMethod.Post, s3Uri, null, options, "UNSIGNED-PAYLOAD");
        using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
        if (!s3Response.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)s3Response.StatusCode);
        }

        var xml = await s3Response.Content.ReadAsStringAsync(cancellationToken);
        var uploadId = XDocument.Parse(xml)
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "UploadId")
            ?.Value;
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return Results.Problem("S3 did not return a multipart upload id.");
        }

        const string insertImageSql = """
            INSERT INTO bee_SpendBeeReceiptUploadImage
                (ReceiptUploadId, S3Key, UploadId, FileName, ContentType, FileSizeBytes, SortOrder, Status, PartEtagsJson)
            VALUES
                (@ReceiptUploadId, @S3Key, @UploadId, @FileName, @ContentType, @FileSizeBytes, @SortOrder, 'Uploading', JSON_ARRAY());
            """;
        await using var imageCommand = new MySqlCommand(insertImageSql, connection);
        imageCommand.Parameters.Add("@ReceiptUploadId", MySqlDbType.Int64).Value = receiptUploadId;
        imageCommand.Parameters.Add("@S3Key", MySqlDbType.VarChar, 700).Value = key;
        imageCommand.Parameters.Add("@UploadId", MySqlDbType.VarChar, 700).Value = uploadId;
        imageCommand.Parameters.Add("@FileName", MySqlDbType.VarChar, 255).Value = DbNullable(NormalizeBounded(image.value.FileName, 255));
        imageCommand.Parameters.Add("@ContentType", MySqlDbType.VarChar, 80).Value = contentType;
        imageCommand.Parameters.Add("@FileSizeBytes", MySqlDbType.Int64).Value = (object?)image.value.FileSizeBytes ?? DBNull.Value;
        imageCommand.Parameters.Add("@SortOrder", MySqlDbType.Int32).Value = image.index;
        await imageCommand.ExecuteNonQueryAsync(cancellationToken);

        imageUploads.Add(new
        {
            imageUploadId = imageCommand.LastInsertedId,
            uploadId,
            key,
            contentType,
            sortOrder = image.index,
            uploadedParts = Array.Empty<EdgeEventVideoPart>()
        });
    }

    return Results.Ok(new
    {
        success = true,
        receiptUploadId,
        status = "Uploading",
        recommendedPartSizeBytes = 8 * 1024 * 1024,
        images = imageUploads
    });
});

app.MapPut("/api/spendbee/v1/receipt-uploads/{receiptUploadId:long}/images/{imageUploadId:long}/parts/{partNumber:int}", async (
    long receiptUploadId,
    long imageUploadId,
    int partNumber,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (partNumber is < 1 or > 10000)
    {
        return Results.BadRequest(new { message = "Part number must be between 1 and 10000." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var imageUpload = await FindSpendBeeReceiptUploadImageAsync(connection, session.ProjectId, session.AppUserId, receiptUploadId, imageUploadId, cancellationToken);
    if (imageUpload is null)
    {
        return Results.NotFound(new { message = "Receipt image upload not found." });
    }

    if (!string.Equals(imageUpload.UploadStatus, "Uploading", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(imageUpload.ImageStatus, "Uploading", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = $"Receipt upload is {imageUpload.UploadStatus}/{imageUpload.ImageStatus} and cannot accept more parts." });
    }

    await using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, cancellationToken);
    if (buffer.Length == 0)
    {
        return Results.BadRequest(new { message = "Image part body is required." });
    }

    buffer.Position = 0;
    var options = s3Options.Value;
    ValidateS3Options(options);
    var s3Uri = BuildS3Uri(options, imageUpload.S3Key, new Dictionary<string, string>
    {
        ["partNumber"] = partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["uploadId"] = imageUpload.UploadId
    });
    var s3Request = BuildS3Request(HttpMethod.Put, s3Uri, null, options, "UNSIGNED-PAYLOAD");
    s3Request.Content = new StreamContent(buffer);
    s3Request.Content.Headers.ContentLength = buffer.Length;
    s3Request.Content.Headers.ContentType = new MediaTypeHeaderValue(imageUpload.ContentType);
    using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
    if (!s3Response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)s3Response.StatusCode);
    }

    var etag = s3Response.Headers.ETag?.Tag;
    if (string.IsNullOrWhiteSpace(etag) && s3Response.Headers.TryGetValues("ETag", out var values))
    {
        etag = values.FirstOrDefault();
    }

    if (string.IsNullOrWhiteSpace(etag))
    {
        return Results.Problem("S3 did not return an ETag for the uploaded part.");
    }

    var parts = UpsertVideoPart(imageUpload.Parts, partNumber, etag);
    await SaveSpendBeeReceiptUploadImagePartsAsync(connection, imageUploadId, parts, cancellationToken);

    return Results.Ok(new
    {
        success = true,
        receiptUploadId,
        imageUploadId,
        partNumber,
        etag,
        uploadedParts = parts
    });
});

app.MapGet("/api/spendbee/v1/receipt-uploads/{receiptUploadId:long}", async (
    long receiptUploadId,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindSpendBeeReceiptUploadAsync(connection, session.ProjectId, session.AppUserId, receiptUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Receipt upload not found." });
    }

    var publicRequestBaseUrl = BuildPublicRequestBaseUrl(request);
    return Results.Ok(new
    {
        success = true,
        receiptUploadId,
        upload.Status,
        upload.Timezone,
        upload.CompletedAtUtc,
        upload.CancelledAtUtc,
        images = upload.Images.Select(image => new
        {
            imageUploadId = image.Id,
            image.Status,
            ImageUrl = string.IsNullOrWhiteSpace(image.ImageUrl) ? null : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/receipt-uploads/{receiptUploadId}/images/{image.Id}"),
            image.ContentType,
            image.FileName,
            image.FileSizeBytes,
            image.SortOrder,
            uploadedParts = image.Parts.OrderBy(part => part.PartNumber)
        })
    });
});

app.MapGet("/api/spendbee/v1/receipt-uploads/{receiptUploadId:long}/images/{imageUploadId:long}", async (
    long receiptUploadId,
    long imageUploadId,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT image.ImageUrl
        FROM bee_SpendBeeReceiptUploadImage AS image
        INNER JOIN bee_SpendBeeReceiptUpload AS upload ON upload.id = image.ReceiptUploadId
        WHERE image.id = @ImageUploadId
            AND upload.id = @ReceiptUploadId
            AND upload.ProjectId = @ProjectId
            AND upload.AppUserId = @AppUserId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ImageUploadId", MySqlDbType.Int64).Value = imageUploadId;
    command.Parameters.Add("@ReceiptUploadId", MySqlDbType.Int64).Value = receiptUploadId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    var imageUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Receipt upload image not found." });
    }

    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapPost("/api/spendbee/v1/receipt-uploads/{receiptUploadId:long}/complete", async (
    long receiptUploadId,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAIOptions> openAIOptions,
    IFileStorageService storage,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindSpendBeeReceiptUploadAsync(connection, session.ProjectId, session.AppUserId, receiptUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Receipt upload not found." });
    }

    if (!string.Equals(upload.Status, "Uploading", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = $"Receipt upload is {upload.Status} and cannot be completed." });
    }

    if (upload.Images.Count == 0 || upload.Images.Any(image => image.Parts.Count == 0))
    {
        return Results.BadRequest(new { message = "Every receipt image must have at least one uploaded part before completing." });
    }

    var publicRequestBaseUrl = BuildPublicRequestBaseUrl(request);
    var options = s3Options.Value;
    ValidateS3Options(options);
    var httpClient = httpClientFactory.CreateClient();
    var publicBaseUrl = string.IsNullOrWhiteSpace(options.PublicBaseUrl)
        ? $"https://{options.Bucket}.s3.{options.Region}.amazonaws.com"
        : options.PublicBaseUrl.TrimEnd('/');
    var uploadedImages = new List<SpendBeeUploadedReceiptImage>();

    foreach (var image in upload.Images.OrderBy(image => image.SortOrder))
    {
        var parts = image.Parts.OrderBy(part => part.PartNumber).ToList();
        var completeXml = BuildCompleteMultipartUploadXml(parts);
        var completeBytes = Encoding.UTF8.GetBytes(completeXml);
        var payloadHash = Convert.ToHexString(SHA256.HashData(completeBytes)).ToLowerInvariant();
        var completeUri = BuildS3Uri(options, image.S3Key, new Dictionary<string, string> { ["uploadId"] = image.UploadId });
        var completeRequest = BuildS3Request(HttpMethod.Post, completeUri, "application/xml", options, payloadHash);
        completeRequest.Content = new ByteArrayContent(completeBytes);
        completeRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        using var completeResponse = await httpClient.SendAsync(completeRequest, cancellationToken);
        if (!completeResponse.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)completeResponse.StatusCode);
        }

        var imageUrl = $"{publicBaseUrl}/{string.Join('/', image.S3Key.Split('/').Select(Uri.EscapeDataString))}";
        await CompleteSpendBeeReceiptUploadImageAsync(connection, image.Id, imageUrl, parts, cancellationToken);

        var getUri = BuildS3Uri(options, image.S3Key);
        var getRequest = BuildS3Request(HttpMethod.Get, getUri, null, options, "UNSIGNED-PAYLOAD");
        using var getResponse = await httpClient.SendAsync(getRequest, cancellationToken);
        if (!getResponse.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)getResponse.StatusCode);
        }

        var bytes = await getResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        uploadedImages.Add(new SpendBeeUploadedReceiptImage(imageUrl, image.ContentType, bytes, image.SortOrder));
    }

    var imageSetHash = ComputeSpendBeeReceiptImageSetHash(uploadedImages);
    var imageDuplicate = await FindDuplicateSpendBeeReceiptAsync(
        connection,
        session.ProjectId,
        imageSetHash,
        null,
        null,
        cancellationToken);
    var retryReceiptId = imageDuplicate is not null && CanRetrySpendBeeReceipt(imageDuplicate, session.AppUserId)
        ? imageDuplicate.ReceiptId
        : (long?)null;
    if (imageDuplicate is not null && retryReceiptId is null)
    {
        return BuildSpendBeeDuplicateUploadResult(imageDuplicate, session.AppUserId);
    }

    long receiptId;
    if (retryReceiptId is not null)
    {
        receiptId = retryReceiptId.Value;
        await PrepareSpendBeeReceiptRetryAsync(connection, receiptId, imageSetHash, uploadedImages, cancellationToken);
    }
    else
    {
        var now = DateTime.UtcNow;
        const string insertReceiptSql = """
            INSERT INTO bee_SpendBeeReceipt
                (ProjectId, AppUserId, ReceiptImageSetHash, Status, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                (@ProjectId, @AppUserId, @ReceiptImageSetHash, 'Processing', @Now, @Now);
            SELECT LAST_INSERT_ID();
            """;
        await using var receiptCommand = new MySqlCommand(insertReceiptSql, connection);
        receiptCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
        receiptCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
        receiptCommand.Parameters.Add("@ReceiptImageSetHash", MySqlDbType.VarChar, 128).Value = imageSetHash;
        receiptCommand.Parameters.Add("@Now", MySqlDbType.DateTime).Value = now;
        receiptId = Convert.ToInt64(await receiptCommand.ExecuteScalarAsync(cancellationToken));
        await InsertSpendBeeReceiptImagesAsync(connection, receiptId, uploadedImages, cancellationToken);
    }

    SpendBeeReceiptRecognition? recognition = null;
    string? rawRecognitionJson = null;
    try
    {
        recognition = await AnalyzeSpendBeeReceiptWithOpenAIAsync(
            httpClient,
            openAIOptions.Value,
            uploadedImages,
            upload.Timezone,
            cancellationToken);
        rawRecognitionJson = JsonSerializer.Serialize(recognition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (Exception ex)
    {
        rawRecognitionJson = JsonSerializer.Serialize(new { error = ex.Message, failedAtUtc = DateTime.UtcNow }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    await CompleteSpendBeeReceiptUploadAsync(connection, receiptUploadId, cancellationToken);
    if (recognition is null)
    {
        await UpdateSpendBeeReceiptFailureAsync(connection, receiptId, rawRecognitionJson, cancellationToken);
        await InsertSpendBeeUserMessageAsync(
            connection,
            session.ProjectId,
            session.AppUserId,
            "receipt_recognition_failed",
            "Error",
            "Receipt upload failed",
            "We could not read this receipt. Please upload a clearer photo.",
            "Receipt",
            receiptId,
            $"spendbee://receipts/{receiptId}",
            new { receiptUploadId, receiptId, status = "RecognitionFailed" },
            cancellationToken);
        return Results.Ok(new { receiptUploadId, receiptId, status = "RecognitionFailed", images = uploadedImages.Count });
    }

    var status = recognition.Quality.EstimatedErrorRate <= 0.01m && !recognition.Quality.NeedsHumanReview
        ? "Recognized"
        : "ReviewRequired";
    var canonicalHash = ComputeSpendBeeReceiptCanonicalHash(recognition);
    var canonicalDuplicate = await FindDuplicateSpendBeeReceiptAsync(
        connection,
        session.ProjectId,
        null,
        canonicalHash,
        receiptId,
        cancellationToken);
    canonicalDuplicate ??= await FindSoftDuplicateSpendBeeReceiptAsync(
        connection,
        session.ProjectId,
        recognition,
        receiptId,
        cancellationToken);
    var retried = retryReceiptId is not null;
    if (canonicalDuplicate is not null)
    {
        if (CanRetrySpendBeeReceipt(canonicalDuplicate, session.AppUserId))
        {
            retried = true;
            var targetReceiptId = canonicalDuplicate.ReceiptId;
            if (targetReceiptId != receiptId)
            {
                await PrepareSpendBeeReceiptRetryAsync(connection, targetReceiptId, imageSetHash, uploadedImages, cancellationToken);
                await DeleteSpendBeeReceiptAsync(connection, receiptId, cancellationToken);
                receiptId = targetReceiptId;
            }
        }
        else
        {
            await DeleteSpendBeeReceiptAsync(connection, receiptId, cancellationToken);
            return BuildSpendBeeDuplicateUploadResult(canonicalDuplicate, session.AppUserId);
        }
    }

    await SaveSpendBeeReceiptRecognitionAsync(connection, receiptId, status, recognition, rawRecognitionJson, canonicalHash, cancellationToken);
    await InsertSpendBeeUserMessageAsync(
        connection,
        session.ProjectId,
        session.AppUserId,
        status == "Recognized" ? (retried ? "receipt_retry_success" : "receipt_upload_success") : "receipt_review_required",
        "Success",
        retried ? "Receipt updated" : "Receipt uploaded",
        status == "Recognized"
            ? "Your receipt has been recognized successfully."
            : "Your receipt has been uploaded.",
        "Receipt",
        receiptId,
        $"spendbee://receipts/{receiptId}",
        new
        {
            receiptUploadId,
            receiptId,
            status,
            retried,
            estimatedErrorRate = recognition.Quality.EstimatedErrorRate,
            overallConfidence = recognition.Quality.OverallConfidence
        },
        cancellationToken);
    var merchant = await EnsureSpendBeeMerchantForReceiptAsync(
        connection,
        receiptId,
        session.ProjectId,
        recognition,
        publicRequestBaseUrl,
        configuration,
        httpClientFactory.CreateClient(),
        storage,
        openAIOptions.Value,
        cancellationToken);
    return Results.Ok(new
    {
        receiptUploadId,
        receiptId,
        status,
        retried,
        recognition,
        merchant,
        images = await LoadSpendBeeReceiptImageSummariesAsync(connection, receiptId, publicRequestBaseUrl, cancellationToken)
    });
});

app.MapDelete("/api/spendbee/v1/receipt-uploads/{receiptUploadId:long}", async (
    long receiptUploadId,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindSpendBeeReceiptUploadAsync(connection, session.ProjectId, session.AppUserId, receiptUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Receipt upload not found." });
    }

    var options = s3Options.Value;
    ValidateS3Options(options);
    foreach (var image in upload.Images.Where(image => string.Equals(image.Status, "Uploading", StringComparison.OrdinalIgnoreCase)))
    {
        var s3Uri = BuildS3Uri(options, image.S3Key, new Dictionary<string, string> { ["uploadId"] = image.UploadId });
        var s3Request = BuildS3Request(HttpMethod.Delete, s3Uri, null, options, "UNSIGNED-PAYLOAD");
        using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
        if (!s3Response.IsSuccessStatusCode && s3Response.StatusCode != HttpStatusCode.NotFound)
        {
            return Results.StatusCode((int)s3Response.StatusCode);
        }
    }

    await CancelSpendBeeReceiptUploadAsync(connection, receiptUploadId, cancellationToken);
    return Results.Ok(new { success = true, receiptUploadId, status = "Cancelled" });
});

app.MapPost("/api/spendbee/v1/receipts", async (
    SpendBeeReceiptUploadRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    IFileStorageService storage,
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAIOptions> openAIOptions,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (payload.Images is null || payload.Images.Count == 0)
    {
        return Results.BadRequest(new { message = "At least one receipt image is required." });
    }

    if (payload.Images.Count > 8)
    {
        return Results.BadRequest(new { message = "A single receipt can include up to 8 images." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var publicRequestBaseUrl = BuildPublicRequestBaseUrl(request);
    var uploadedImages = new List<SpendBeeUploadedReceiptImage>();
    foreach (var image in payload.Images.Select((value, index) => new { value, index }))
    {
        var contentType = NormalizeImageContentType(image.value.ContentType);
        if (contentType is null)
        {
            return Results.BadRequest(new { message = $"Image {image.index + 1} content type is not supported." });
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(StripDataUrlPrefix(image.value.ImageBase64));
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { message = $"Image {image.index + 1} is not valid base64." });
        }

        if (bytes.Length == 0 || bytes.Length > 12 * 1024 * 1024)
        {
            return Results.BadRequest(new { message = $"Image {image.index + 1} must be between 1 byte and 12 MB." });
        }

        await using var stream = new MemoryStream(bytes);
        var stored = await storage.UploadAsync(
            stream,
            contentType,
            contentType == "image/png" ? ".png" : ".jpg",
            $"spendbee/receipts/{session.ProjectId}/{session.AppUserId}",
            cancellationToken);
        uploadedImages.Add(new SpendBeeUploadedReceiptImage(stored.PublicUrl, contentType, bytes, image.index));
    }

    var imageSetHash = ComputeSpendBeeReceiptImageSetHash(uploadedImages);
    var imageDuplicate = await FindDuplicateSpendBeeReceiptAsync(
        connection,
        session.ProjectId,
        imageSetHash,
        null,
        null,
        cancellationToken);
    var retryReceiptId = imageDuplicate is not null && CanRetrySpendBeeReceipt(imageDuplicate, session.AppUserId)
        ? imageDuplicate.ReceiptId
        : (long?)null;
    if (imageDuplicate is not null && retryReceiptId is null)
    {
        return BuildSpendBeeDuplicateUploadResult(imageDuplicate, session.AppUserId);
    }

    long receiptId;
    if (retryReceiptId is not null)
    {
        receiptId = retryReceiptId.Value;
        await PrepareSpendBeeReceiptRetryAsync(connection, receiptId, imageSetHash, uploadedImages, cancellationToken);
    }
    else
    {
        var now = DateTime.UtcNow;
        const string insertReceiptSql = """
            INSERT INTO bee_SpendBeeReceipt
                (ProjectId, AppUserId, ReceiptImageSetHash, Status, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                (@ProjectId, @AppUserId, @ReceiptImageSetHash, 'Processing', @Now, @Now);
            SELECT LAST_INSERT_ID();
            """;
        await using var receiptCommand = new MySqlCommand(insertReceiptSql, connection);
        receiptCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
        receiptCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
        receiptCommand.Parameters.Add("@ReceiptImageSetHash", MySqlDbType.VarChar, 128).Value = imageSetHash;
        receiptCommand.Parameters.Add("@Now", MySqlDbType.DateTime).Value = now;
        receiptId = Convert.ToInt64(await receiptCommand.ExecuteScalarAsync(cancellationToken));
        await InsertSpendBeeReceiptImagesAsync(connection, receiptId, uploadedImages, cancellationToken);
    }

    SpendBeeReceiptRecognition? recognition = null;
    string? rawRecognitionJson = null;
    try
    {
        recognition = await AnalyzeSpendBeeReceiptWithOpenAIAsync(
            httpClientFactory.CreateClient(),
            openAIOptions.Value,
            uploadedImages,
            payload.Timezone,
            cancellationToken);
        rawRecognitionJson = JsonSerializer.Serialize(recognition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (Exception ex)
    {
        rawRecognitionJson = JsonSerializer.Serialize(new { error = ex.Message, failedAtUtc = DateTime.UtcNow }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    if (recognition is null)
    {
        await UpdateSpendBeeReceiptFailureAsync(connection, receiptId, rawRecognitionJson, cancellationToken);
        await InsertSpendBeeUserMessageAsync(
            connection,
            session.ProjectId,
            session.AppUserId,
            "receipt_recognition_failed",
            "Error",
            "Receipt upload failed",
            "We could not read this receipt. Please upload a clearer photo.",
            "Receipt",
            receiptId,
            $"spendbee://receipts/{receiptId}",
            new { receiptId, status = "RecognitionFailed" },
            cancellationToken);
        return Results.Ok(new { receiptId, status = "RecognitionFailed", images = uploadedImages.Count });
    }

    var status = recognition.Quality.EstimatedErrorRate <= 0.01m && !recognition.Quality.NeedsHumanReview
        ? "Recognized"
        : "ReviewRequired";
    var canonicalHash = ComputeSpendBeeReceiptCanonicalHash(recognition);
    var canonicalDuplicate = await FindDuplicateSpendBeeReceiptAsync(
        connection,
        session.ProjectId,
        null,
        canonicalHash,
        receiptId,
        cancellationToken);
    canonicalDuplicate ??= await FindSoftDuplicateSpendBeeReceiptAsync(
        connection,
        session.ProjectId,
        recognition,
        receiptId,
        cancellationToken);
    var retried = retryReceiptId is not null;
    if (canonicalDuplicate is not null)
    {
        if (CanRetrySpendBeeReceipt(canonicalDuplicate, session.AppUserId))
        {
            retried = true;
            var targetReceiptId = canonicalDuplicate.ReceiptId;
            if (targetReceiptId != receiptId)
            {
                await PrepareSpendBeeReceiptRetryAsync(connection, targetReceiptId, imageSetHash, uploadedImages, cancellationToken);
                await DeleteSpendBeeReceiptAsync(connection, receiptId, cancellationToken);
                receiptId = targetReceiptId;
            }
        }
        else
        {
            await DeleteSpendBeeReceiptAsync(connection, receiptId, cancellationToken);
            return BuildSpendBeeDuplicateUploadResult(canonicalDuplicate, session.AppUserId);
        }
    }

    await SaveSpendBeeReceiptRecognitionAsync(connection, receiptId, status, recognition, rawRecognitionJson, canonicalHash, cancellationToken);
    await InsertSpendBeeUserMessageAsync(
        connection,
        session.ProjectId,
        session.AppUserId,
        status == "Recognized" ? (retried ? "receipt_retry_success" : "receipt_upload_success") : "receipt_review_required",
        "Success",
        retried ? "Receipt updated" : "Receipt uploaded",
        status == "Recognized"
            ? "Your receipt has been recognized successfully."
            : "Your receipt has been uploaded.",
        "Receipt",
        receiptId,
        $"spendbee://receipts/{receiptId}",
        new
        {
            receiptId,
            status,
            retried,
            estimatedErrorRate = recognition.Quality.EstimatedErrorRate,
            overallConfidence = recognition.Quality.OverallConfidence
        },
        cancellationToken);
    var merchant = await EnsureSpendBeeMerchantForReceiptAsync(
        connection,
        receiptId,
        session.ProjectId,
        recognition,
        publicRequestBaseUrl,
        configuration,
        httpClientFactory.CreateClient(),
        storage,
        openAIOptions.Value,
        cancellationToken);
    return Results.Ok(new
    {
        receiptId,
        status,
        retried,
        recognition,
        merchant,
        images = await LoadSpendBeeReceiptImageSummariesAsync(connection, receiptId, publicRequestBaseUrl, cancellationToken)
    });
});

app.MapGet("/api/spendbee/v1/merchants/recent", async (
    HttpRequest request,
    IConfiguration configuration,
    double? lat,
    double? lng,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var publicRequestBaseUrl = BuildPublicRequestBaseUrl(request);
    var hasLocation = IsValidLatitudeLongitude(lat, lng);
    var distanceSql = hasLocation
        ? """
            (6371000 * 2 * ASIN(SQRT(
                POWER(SIN((RADIANS(merchant.Latitude) - RADIANS(@Latitude)) / 2), 2) +
                COS(RADIANS(@Latitude)) * COS(RADIANS(merchant.Latitude)) *
                POWER(SIN((RADIANS(merchant.Longitude) - RADIANS(@Longitude)) / 2), 2)
            )))
            """
        : "NULL";
    var orderSql = hasLocation
        ? "DistanceMeters IS NULL ASC, DistanceMeters DESC, LastReceiptAtUtc DESC, merchant.id DESC"
        : "LastReceiptAtUtc DESC, merchant.id DESC";
    var coverUrlSql = BuildSpendBeeMerchantCoverUrlSql("merchant");
    var sql = $"""
        SELECT merchant.id, merchant.Name, merchant.Address, merchant.PrimaryType,
            merchant.Rating, merchant.UserRatingCount, merchant.AiCoverImageUrl, merchant.GooglePhotoUri,
            merchant.GoogleMapsUri, merchant.SyncStatus, merchant.Latitude, merchant.Longitude,
            {coverUrlSql} AS CoverImageUrl,
            {distanceSql} AS DistanceMeters,
            MAX(COALESCE(receipt.PurchasedAtUtc, receipt.CreatedAtUtc)) AS LastReceiptAtUtc,
            COUNT(receipt.id) AS ReceiptCount,
            SUM(COALESCE(receipt.Total, 0)) AS TotalSpent
        FROM bee_SpendBeeReceipt AS receipt
        INNER JOIN bee_SpendBeeMerchant AS merchant ON merchant.id = receipt.MerchantId
        WHERE receipt.ProjectId = @ProjectId
            AND receipt.AppUserId = @AppUserId
        GROUP BY merchant.id, merchant.Name, merchant.Address, merchant.PrimaryType,
            merchant.Rating, merchant.UserRatingCount, merchant.AiCoverImageUrl, merchant.GooglePhotoUri,
            merchant.GoogleMapsUri, merchant.SyncStatus, merchant.Latitude, merchant.Longitude, merchant.ProjectId
        ORDER BY {orderSql}
        LIMIT 40;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    command.Parameters.Add("@Latitude", MySqlDbType.Double).Value = hasLocation ? lat!.Value : DBNull.Value;
    command.Parameters.Add("@Longitude", MySqlDbType.Double).Value = hasLocation ? lng!.Value : DBNull.Value;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var merchants = new List<object>();
    while (await reader.ReadAsync(cancellationToken))
    {
        var merchantId = reader.GetInt64(reader.GetOrdinal("id"));
        var coverImageUrl = reader["CoverImageUrl"] as string;
        var coverImageApiUrl = string.IsNullOrWhiteSpace(coverImageUrl)
            ? null
            : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/merchants/{merchantId}/cover");
        merchants.Add(new
        {
            id = merchantId,
            name = reader["Name"] as string,
            address = reader["Address"] as string,
            primaryType = reader["PrimaryType"] as string,
            rating = reader.IsDBNull(reader.GetOrdinal("Rating")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Rating")),
            userRatingCount = reader.IsDBNull(reader.GetOrdinal("UserRatingCount")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("UserRatingCount")),
            latitude = reader.IsDBNull(reader.GetOrdinal("Latitude")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Latitude")),
            longitude = reader.IsDBNull(reader.GetOrdinal("Longitude")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Longitude")),
            distanceMeters = reader.IsDBNull(reader.GetOrdinal("DistanceMeters")) ? (double?)null : reader.GetDouble(reader.GetOrdinal("DistanceMeters")),
            coverImageUrl = coverImageApiUrl,
            coverImageApiUrl,
            googleMapsUri = reader["GoogleMapsUri"] as string,
            syncStatus = reader["SyncStatus"] as string,
            lastReceiptAtUtc = reader.IsDBNull(reader.GetOrdinal("LastReceiptAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastReceiptAtUtc")).ToString("O"),
            receiptCount = Convert.ToInt32(reader["ReceiptCount"]),
            totalSpent = reader.IsDBNull(reader.GetOrdinal("TotalSpent")) ? 0m : reader.GetDecimal(reader.GetOrdinal("TotalSpent"))
        });
    }

    return Results.Ok(new { merchants });
});

app.MapGet("/api/spendbee/v1/merchants/nearby", async (
    HttpRequest request,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    double lat,
    double lng,
    double? radiusMeters,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (!IsValidLatitudeLongitude(lat, lng))
    {
        return Results.BadRequest(new { message = "Valid lat and lng are required." });
    }

    var pageSize = Math.Clamp(limit ?? 20, 1, 40);
    var radius = Math.Clamp(radiusMeters ?? 800d, 50d, 5000d);
    var publicRequestBaseUrl = BuildPublicRequestBaseUrl(request);
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var localMerchants = await LoadSpendBeeNearbyLocalMerchantsAsync(
        connection,
        session.ProjectId,
        lat,
        lng,
        radius,
        publicRequestBaseUrl,
        cancellationToken);
    var localByGooglePlaceId = localMerchants
        .Where(item => !string.IsNullOrWhiteSpace(item.GooglePlaceId))
        .ToDictionary(item => item.GooglePlaceId!, StringComparer.OrdinalIgnoreCase);

    var googlePlaces = await FetchGoogleNearbyPlacesAsync(
        configuration,
        httpClientFactory.CreateClient(),
        lat,
        lng,
        radius,
        pageSize,
        cancellationToken);

    var items = new List<object>();
    var emittedLocalIds = new HashSet<long>();
    foreach (var place in googlePlaces)
    {
        var distance = CalculateDistanceMeters(lat, lng, place.Latitude, place.Longitude);
        if (!string.IsNullOrWhiteSpace(place.PlaceId) && localByGooglePlaceId.TryGetValue(place.PlaceId, out var local))
        {
            emittedLocalIds.Add(local.Id);
            items.Add(new
            {
                source = "local",
                merchantId = local.Id,
                googlePlaceId = place.PlaceId,
                name = local.Name,
                address = local.Address,
                primaryType = local.PrimaryType,
                latitude = local.Latitude,
                longitude = local.Longitude,
                distanceMeters = local.DistanceMeters ?? distance,
                rating = local.Rating,
                userRatingCount = local.UserRatingCount,
                coverImageUrl = local.CoverImageUrl,
                coverImageApiUrl = local.CoverImageUrl,
                googleMapsUri = local.GoogleMapsUri,
                websiteUrl = local.WebsiteUrl,
                phoneNumber = local.PhoneNumber,
                syncStatus = local.SyncStatus,
                canBindOnUpload = true
            });
            continue;
        }

        items.Add(new
        {
            source = "google",
            merchantId = (long?)null,
            googlePlaceId = place.PlaceId,
            googlePlaceResourceName = place.ResourceName,
            name = place.Name,
            address = place.Address,
            primaryType = place.PrimaryType,
            latitude = place.Latitude,
            longitude = place.Longitude,
            distanceMeters = distance,
            rating = place.Rating,
            userRatingCount = place.UserRatingCount,
            coverImageUrl = place.PhotoUri,
            coverImageApiUrl = place.PhotoUri,
            googleMapsUri = place.GoogleMapsUri,
            websiteUrl = place.WebsiteUrl,
            phoneNumber = place.PhoneNumber,
            syncStatus = "GoogleOnly",
            canBindOnUpload = true
        });
    }

    foreach (var local in localMerchants.Where(item => !emittedLocalIds.Contains(item.Id)))
    {
        items.Add(new
        {
            source = "local",
            merchantId = local.Id,
            googlePlaceId = local.GooglePlaceId,
            name = local.Name,
            address = local.Address,
            primaryType = local.PrimaryType,
            latitude = local.Latitude,
            longitude = local.Longitude,
            distanceMeters = local.DistanceMeters,
            rating = local.Rating,
            userRatingCount = local.UserRatingCount,
            coverImageUrl = local.CoverImageUrl,
            coverImageApiUrl = local.CoverImageUrl,
            googleMapsUri = local.GoogleMapsUri,
            websiteUrl = local.WebsiteUrl,
            phoneNumber = local.PhoneNumber,
            syncStatus = local.SyncStatus,
            canBindOnUpload = true
        });
    }

    return Results.Ok(new
    {
        lat,
        lng,
        radiusMeters = radius,
        merchants = items
            .OrderBy(item => (double?)item.GetType().GetProperty("distanceMeters")?.GetValue(item) ?? double.MaxValue)
            .ThenBy(item => (string?)item.GetType().GetProperty("name")?.GetValue(item))
            .Take(pageSize)
            .ToList()
    });
});

app.MapPost("/api/spendbee/v1/merchants/google-place", async (
    SpendBeeEnsureGoogleMerchantRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IFileStorageService storage,
    IOptions<OpenAIOptions> openAIOptions,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var googlePlaceId = NormalizeBounded(payload.GooglePlaceId, 160);
    if (string.IsNullOrWhiteSpace(googlePlaceId))
    {
        return Results.BadRequest(new { message = "googlePlaceId is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var httpClient = httpClientFactory.CreateClient();
    var googlePlace = await FetchGooglePlaceDetailsAsync(configuration, httpClient, googlePlaceId, cancellationToken);
    if (googlePlace is null)
    {
        return Results.NotFound(new { message = "Google place was not found." });
    }

    var merchant = await FindSpendBeeMerchantAsync(
        connection,
        session.ProjectId,
        googlePlace.PlaceId,
        NormalizeMerchantName(googlePlace.Name),
        googlePlace.Address,
        cancellationToken);

    if (merchant is null)
    {
        merchant = await InsertSpendBeeMerchantAsync(
            connection,
            session.ProjectId,
            googlePlace.Name,
            NormalizeMerchantName(googlePlace.Name),
            googlePlace.Address,
            googlePlace,
            cancellationToken);
    }
    else
    {
        await UpdateSpendBeeMerchantFromGoogleAsync(connection, merchant.Id, googlePlace, cancellationToken);
        merchant = merchant with
        {
            GooglePlaceId = googlePlace.PlaceId,
            Name = googlePlace.Name,
            Address = googlePlace.Address,
            GooglePhotoUri = googlePlace.PhotoUri,
            PrimaryType = googlePlace.PrimaryType,
            Latitude = googlePlace.Latitude,
            Longitude = googlePlace.Longitude,
            SyncStatus = "GoogleMatched"
        };
    }

    if (string.IsNullOrWhiteSpace(merchant.AiCoverImageUrl))
    {
        var cover = await TryGenerateSpendBeeMerchantCoverAsync(
            configuration,
            connection,
            session.ProjectId,
            merchant,
            googlePlace,
            httpClient,
            storage,
            openAIOptions.Value,
            cancellationToken);
        if (cover is not null)
        {
            await UpdateSpendBeeMerchantAiCoverAsync(connection, merchant.Id, cover.Url, cover.Prompt, cover.Source, cover.Category, cover.StreetViewImageUrl, cancellationToken);
            if (cover.Latitude is not null && cover.Longitude is not null && (merchant.Latitude is null || merchant.Longitude is null))
            {
                await UpdateSpendBeeMerchantCoordinatesAsync(connection, merchant.Id, cover.Latitude.Value, cover.Longitude.Value, cancellationToken);
            }

            merchant = merchant with
            {
                AiCoverImageUrl = cover.Url,
                CoverSource = cover.Source,
                CoverCategory = cover.Category,
                StreetViewImageUrl = cover.StreetViewImageUrl,
                Latitude = cover.Latitude ?? merchant.Latitude,
                Longitude = cover.Longitude ?? merchant.Longitude
            };
        }
    }

    var coverImageUrl = await FindSpendBeeMerchantCoverUrlAsync(connection, merchant.Id, cancellationToken);
    var coverImageApiUrl = string.IsNullOrWhiteSpace(coverImageUrl)
        ? null
        : BuildPublicApiUrl(BuildPublicRequestBaseUrl(request), $"/api/spendbee/v1/merchants/{merchant.Id}/cover");
    return Results.Ok(new
    {
        merchant = new
        {
            id = merchant.Id,
            googlePlaceId = merchant.GooglePlaceId,
            name = merchant.Name,
            address = merchant.Address,
            latitude = merchant.Latitude,
            longitude = merchant.Longitude,
            coverImageUrl = coverImageApiUrl,
            coverImageApiUrl,
            syncStatus = merchant.SyncStatus
        }
    });
});

app.MapGet("/api/spendbee/v1/merchants/{merchantId:long}", async (
    long merchantId,
    HttpRequest request,
    IConfiguration configuration,
    int? limit,
    long? beforeId,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var publicRequestBaseUrl = BuildPublicRequestBaseUrl(request);
    var coverUrlSql = BuildSpendBeeMerchantCoverUrlSql("merchant");
    var merchantSql = $"""
        SELECT merchant.id, merchant.Name, merchant.Address, merchant.PrimaryType,
            merchant.Rating, merchant.UserRatingCount, merchant.AiCoverImageUrl, merchant.GooglePhotoUri,
            merchant.GoogleMapsUri, merchant.WebsiteUrl, merchant.PhoneNumber, merchant.SyncStatus,
            merchant.Latitude, merchant.Longitude,
            {coverUrlSql} AS CoverImageUrl,
            MAX(COALESCE(receipt.PurchasedAtUtc, receipt.CreatedAtUtc)) AS LastReceiptAtUtc,
            COUNT(receipt.id) AS ReceiptCount,
            SUM(COALESCE(receipt.Total, 0)) AS TotalSpent
        FROM bee_SpendBeeMerchant AS merchant
        INNER JOIN bee_SpendBeeReceipt AS receipt ON receipt.MerchantId = merchant.id
        WHERE merchant.id = @MerchantId
            AND merchant.ProjectId = @ProjectId
            AND receipt.AppUserId = @AppUserId
        GROUP BY merchant.id, merchant.Name, merchant.Address, merchant.PrimaryType,
            merchant.Rating, merchant.UserRatingCount, merchant.AiCoverImageUrl, merchant.GooglePhotoUri,
            merchant.GoogleMapsUri, merchant.WebsiteUrl, merchant.PhoneNumber, merchant.SyncStatus,
            merchant.Latitude, merchant.Longitude, merchant.ProjectId
        LIMIT 1;
        """;
    await using var merchantCommand = new MySqlCommand(merchantSql, connection);
    merchantCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    merchantCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    merchantCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    await using var merchantReader = await merchantCommand.ExecuteReaderAsync(cancellationToken);
    if (!await merchantReader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Merchant not found for this user." });
    }

    var coverImageUrl = merchantReader["CoverImageUrl"] as string;
    var coverImageApiUrl = string.IsNullOrWhiteSpace(coverImageUrl)
        ? null
        : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/merchants/{merchantId}/cover");
    var merchant = new
    {
        id = merchantReader.GetInt64(merchantReader.GetOrdinal("id")),
        name = merchantReader["Name"] as string,
        address = merchantReader["Address"] as string,
        primaryType = merchantReader["PrimaryType"] as string,
        rating = merchantReader.IsDBNull(merchantReader.GetOrdinal("Rating")) ? (decimal?)null : merchantReader.GetDecimal(merchantReader.GetOrdinal("Rating")),
        userRatingCount = merchantReader.IsDBNull(merchantReader.GetOrdinal("UserRatingCount")) ? (int?)null : merchantReader.GetInt32(merchantReader.GetOrdinal("UserRatingCount")),
        coverImageUrl = coverImageApiUrl,
        coverImageApiUrl,
        googleMapsUri = merchantReader["GoogleMapsUri"] as string,
        websiteUrl = merchantReader["WebsiteUrl"] as string,
        phoneNumber = merchantReader["PhoneNumber"] as string,
        latitude = merchantReader.IsDBNull(merchantReader.GetOrdinal("Latitude")) ? (decimal?)null : merchantReader.GetDecimal(merchantReader.GetOrdinal("Latitude")),
        longitude = merchantReader.IsDBNull(merchantReader.GetOrdinal("Longitude")) ? (decimal?)null : merchantReader.GetDecimal(merchantReader.GetOrdinal("Longitude")),
        syncStatus = merchantReader["SyncStatus"] as string,
        lastReceiptAtUtc = merchantReader.IsDBNull(merchantReader.GetOrdinal("LastReceiptAtUtc")) ? null : merchantReader.GetDateTime(merchantReader.GetOrdinal("LastReceiptAtUtc")).ToString("O"),
        receiptCount = Convert.ToInt32(merchantReader["ReceiptCount"]),
        totalSpent = merchantReader.IsDBNull(merchantReader.GetOrdinal("TotalSpent")) ? 0m : merchantReader.GetDecimal(merchantReader.GetOrdinal("TotalSpent"))
    };
    await merchantReader.CloseAsync();

    var receipts = await LoadSpendBeeReceiptListAsync(connection, session.ProjectId, session.AppUserId, merchantId, null, limit, beforeId, publicRequestBaseUrl, cancellationToken);
    return Results.Ok(new { merchant, receipts = receipts.Items, page = receipts.Page });
});

app.MapGet("/api/spendbee/v1/merchants/{merchantId:long}/receipts", async (
    long merchantId,
    HttpRequest request,
    IConfiguration configuration,
    int? limit,
    long? beforeId,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    if (!await SpendBeeMerchantBelongsToProjectAsync(connection, session.ProjectId, merchantId, cancellationToken))
    {
        return Results.NotFound(new { message = "Merchant not found." });
    }

    var receipts = await LoadSpendBeeReceiptListAsync(
        connection,
        session.ProjectId,
        session.AppUserId,
        merchantId,
        null,
        limit,
        beforeId,
        BuildPublicRequestBaseUrl(request),
        cancellationToken);
    return Results.Ok(new { merchantId, receipts = receipts.Items, page = receipts.Page });
});

app.MapGet("/api/spendbee/v1/merchants/{merchantId:long}/cover", async (
    long merchantId,
    HttpResponse response,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var coverUrlSql = BuildSpendBeeMerchantCoverUrlSql("merchant");
    var sql = $"""
        SELECT {coverUrlSql} AS CoverUrl
        FROM bee_SpendBeeMerchant AS merchant
        WHERE merchant.id = @MerchantId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    var coverUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(coverUrl))
    {
        return Results.NotFound(new { message = "Merchant cover image not found." });
    }

    response.Headers.CacheControl = "public, max-age=86400";
    return await StreamProtectedAnalysisImageAsync(coverUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapPost("/api/spendbee/v1/merchant-cover-backfill", async (
    HttpRequest request,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IFileStorageService storage,
    IOptions<OpenAIOptions> openAIOptions,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var httpClient = httpClientFactory.CreateClient();
    var merchants = await LoadSpendBeeMerchantsForCoverBackfillAsync(connection, session.ProjectId, limit ?? 20, cancellationToken);
    var results = new List<object>();
    foreach (var original in merchants)
    {
        var merchant = original;
        SpendBeeGooglePlace? googlePlace = null;
        if (!string.IsNullOrWhiteSpace(merchant.GooglePlaceId))
        {
            googlePlace = await FetchGooglePlaceDetailsAsync(configuration, httpClient, merchant.GooglePlaceId, cancellationToken);
        }

        googlePlace ??= await FetchGooglePlaceForMerchantAsync(configuration, httpClient, merchant.Name, merchant.Address, cancellationToken);
        if (googlePlace is not null)
        {
            await UpdateSpendBeeMerchantFromGoogleAsync(connection, merchant.Id, googlePlace, cancellationToken);
            merchant = merchant with
            {
                GooglePlaceId = googlePlace.PlaceId,
                Name = googlePlace.Name,
                Address = googlePlace.Address,
                PrimaryType = googlePlace.PrimaryType,
                Latitude = googlePlace.Latitude,
                Longitude = googlePlace.Longitude,
                GooglePhotoUri = googlePlace.PhotoUri,
                SyncStatus = "GoogleMatched"
            };
        }

        var cover = await TryGenerateSpendBeeMerchantCoverAsync(
            configuration,
            connection,
            session.ProjectId,
            merchant,
            googlePlace,
            httpClient,
            storage,
            openAIOptions.Value,
            cancellationToken);
        if (cover is not null)
        {
            await UpdateSpendBeeMerchantAiCoverAsync(connection, merchant.Id, cover.Url, cover.Prompt, cover.Source, cover.Category, cover.StreetViewImageUrl, cancellationToken);
            if (cover.Latitude is not null && cover.Longitude is not null && (merchant.Latitude is null || merchant.Longitude is null))
            {
                await UpdateSpendBeeMerchantCoordinatesAsync(connection, merchant.Id, cover.Latitude.Value, cover.Longitude.Value, cancellationToken);
            }

            merchant = merchant with
            {
                AiCoverImageUrl = cover.Url,
                CoverSource = cover.Source,
                CoverCategory = cover.Category,
                StreetViewImageUrl = cover.StreetViewImageUrl,
                Latitude = cover.Latitude ?? merchant.Latitude,
                Longitude = cover.Longitude ?? merchant.Longitude
            };
        }

        results.Add(new
        {
            merchantId = merchant.Id,
            merchant.Name,
            merchant.Address,
            merchant.Latitude,
            merchant.Longitude,
            coverSource = merchant.CoverSource,
            coverCategory = merchant.CoverCategory,
            hasCover = !string.IsNullOrWhiteSpace(merchant.AiCoverImageUrl)
        });
    }

    return Results.Ok(new { processed = results.Count, merchants = results });
});

app.MapPost("/api/spendbee/v1/merchant-photo-uploads", async (
    SpendBeeMerchantPhotoUploadStartRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (payload.MerchantId <= 0)
    {
        return Results.BadRequest(new { message = "merchantId is required." });
    }

    var contentType = NormalizeImageContentType(payload.ContentType);
    if (contentType is null)
    {
        return Results.BadRequest(new { message = "Only jpg, png, and webp images are supported." });
    }

    if (payload.FileSizeBytes is <= 0 or > 50 * 1024 * 1024)
    {
        return Results.BadRequest(new { message = "Photo must be between 1 byte and 50 MB." });
    }

    var category = NormalizeSpendBeeMerchantPhotoUploadCategory(payload.Category);
    if (category is null)
    {
        return Results.BadRequest(new
        {
            message = "Merchant photo uploads only support merchant gallery categories. Use /api/spendbee/v1/receipt-uploads for receipt images."
        });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken) ||
        !await SpendBeeMerchantBelongsToProjectAsync(connection, session.ProjectId, payload.MerchantId, cancellationToken))
    {
        return Results.Forbid();
    }

    var options = s3Options.Value;
    ValidateS3Options(options);
    var extension = NormalizeImageExtension(payload.FileName, contentType);
    var key = $"spendbee/merchant-photos/{session.ProjectId}/{payload.MerchantId}/originals/{session.AppUserId}/{Guid.NewGuid():N}{extension}";
    var s3Uri = BuildS3Uri(options, key, new Dictionary<string, string> { ["uploads"] = string.Empty });
    var s3Request = BuildS3Request(HttpMethod.Post, s3Uri, null, options, "UNSIGNED-PAYLOAD");
    using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
    if (!s3Response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)s3Response.StatusCode);
    }

    var xml = await s3Response.Content.ReadAsStringAsync(cancellationToken);
    var uploadId = XDocument.Parse(xml)
        .Descendants()
        .FirstOrDefault(element => element.Name.LocalName == "UploadId")
        ?.Value;
    if (string.IsNullOrWhiteSpace(uploadId))
    {
        return Results.Problem("S3 did not return a multipart upload id.");
    }

    const string sql = """
        INSERT INTO bee_SpendBeeMerchantPhotoUpload
            (ProjectId, MerchantId, AppUserId, S3Key, UploadId, FileName, ContentType, FileSizeBytes, Category, Caption, Status, PartEtagsJson)
        VALUES
            (@ProjectId, @MerchantId, @AppUserId, @S3Key, @UploadId, @FileName, @ContentType, @FileSizeBytes, @Category, @Caption, 'Uploading', JSON_ARRAY());
        SELECT LAST_INSERT_ID();
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = payload.MerchantId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    command.Parameters.Add("@S3Key", MySqlDbType.VarChar, 700).Value = key;
    command.Parameters.Add("@UploadId", MySqlDbType.VarChar, 700).Value = uploadId;
    command.Parameters.Add("@FileName", MySqlDbType.VarChar, 255).Value = DbNullable(NormalizeBounded(payload.FileName, 255));
    command.Parameters.Add("@ContentType", MySqlDbType.VarChar, 80).Value = contentType;
    command.Parameters.Add("@FileSizeBytes", MySqlDbType.Int64).Value = payload.FileSizeBytes;
    command.Parameters.Add("@Category", MySqlDbType.VarChar, 80).Value = category;
    command.Parameters.Add("@Caption", MySqlDbType.VarChar, 500).Value = DbNullable(NormalizeBounded(payload.Caption, 500));
    var photoUploadId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));

    return Results.Ok(new
    {
        success = true,
        photoUploadId,
        uploadId,
        key,
        contentType,
        recommendedPartSizeBytes = 8 * 1024 * 1024,
        uploadedParts = Array.Empty<EdgeEventVideoPart>()
    });
});

app.MapPut("/api/spendbee/v1/merchant-photo-uploads/{photoUploadId:long}/parts/{partNumber:int}", async (
    long photoUploadId,
    int partNumber,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (partNumber is < 1 or > 10000)
    {
        return Results.BadRequest(new { message = "Part number must be between 1 and 10000." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindSpendBeeMerchantPhotoUploadAsync(connection, session.ProjectId, session.AppUserId, photoUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Photo upload not found." });
    }

    if (!string.Equals(upload.Status, "Uploading", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = $"Photo upload is {upload.Status} and cannot accept more parts." });
    }

    if (!IsSpendBeeMerchantPhotoCategory(upload.Category))
    {
        return Results.BadRequest(new { message = "This upload was created for a non-merchant media category. Use the dedicated API for that media type." });
    }

    await using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, cancellationToken);
    if (buffer.Length == 0)
    {
        return Results.BadRequest(new { message = "Photo part body is required." });
    }

    buffer.Position = 0;
    var options = s3Options.Value;
    ValidateS3Options(options);
    var s3Uri = BuildS3Uri(options, upload.S3Key, new Dictionary<string, string>
    {
        ["partNumber"] = partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["uploadId"] = upload.UploadId
    });
    var s3Request = BuildS3Request(HttpMethod.Put, s3Uri, null, options, "UNSIGNED-PAYLOAD");
    s3Request.Content = new StreamContent(buffer);
    s3Request.Content.Headers.ContentLength = buffer.Length;
    s3Request.Content.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
    using var s3Response = await httpClient.SendAsync(s3Request, cancellationToken);
    if (!s3Response.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)s3Response.StatusCode);
    }

    var etag = s3Response.Headers.ETag?.Tag;
    if (string.IsNullOrWhiteSpace(etag) && s3Response.Headers.TryGetValues("ETag", out var values))
    {
        etag = values.FirstOrDefault();
    }

    if (string.IsNullOrWhiteSpace(etag))
    {
        return Results.Problem("S3 did not return an ETag for the uploaded part.");
    }

    var parts = UpsertVideoPart(upload.Parts, partNumber, etag);
    await SaveSpendBeeMerchantPhotoUploadPartsAsync(connection, photoUploadId, parts, cancellationToken);
    return Results.Ok(new { success = true, photoUploadId, partNumber, etag, uploadedParts = parts });
});

app.MapGet("/api/spendbee/v1/merchant-photo-uploads/{photoUploadId:long}", async (
    long photoUploadId,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindSpendBeeMerchantPhotoUploadAsync(connection, session.ProjectId, session.AppUserId, photoUploadId, cancellationToken);
    return upload is null
        ? Results.NotFound(new { message = "Photo upload not found." })
        : Results.Ok(new
        {
            success = true,
            photoUploadId = upload.Id,
            upload.MerchantId,
            upload.Status,
            upload.Category,
            upload.Caption,
            upload.ContentType,
            upload.FileName,
            upload.FileSizeBytes,
            uploadedParts = upload.Parts.OrderBy(part => part.PartNumber),
            upload.PhotoId,
            displayImageUrl = upload.PhotoId is null ? null : BuildPublicApiUrl(BuildPublicRequestBaseUrl(request), $"/api/spendbee/v1/merchant-photos/{upload.PhotoId}/image")
        });
});

app.MapPost("/api/spendbee/v1/merchant-photo-uploads/{photoUploadId:long}/complete", async (
    long photoUploadId,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAIOptions> openAIOptions,
    IFileStorageService storage,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var upload = await FindSpendBeeMerchantPhotoUploadAsync(connection, session.ProjectId, session.AppUserId, photoUploadId, cancellationToken);
    if (upload is null)
    {
        return Results.NotFound(new { message = "Photo upload not found." });
    }

    if (!string.Equals(upload.Status, "Uploading", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = $"Photo upload is {upload.Status} and cannot be completed." });
    }

    if (!IsSpendBeeMerchantPhotoCategory(upload.Category))
    {
        return Results.BadRequest(new { message = "This upload was created for a non-merchant media category. Use the dedicated API for that media type." });
    }

    if (upload.Parts.Count == 0)
    {
        return Results.BadRequest(new { message = "At least one photo part is required before completing." });
    }

    var options = s3Options.Value;
    ValidateS3Options(options);
    var httpClient = httpClientFactory.CreateClient();
    var completeXml = BuildCompleteMultipartUploadXml(upload.Parts);
    var completeBytes = Encoding.UTF8.GetBytes(completeXml);
    var payloadHash = Convert.ToHexString(SHA256.HashData(completeBytes)).ToLowerInvariant();
    var completeUri = BuildS3Uri(options, upload.S3Key, new Dictionary<string, string> { ["uploadId"] = upload.UploadId });
    var completeRequest = BuildS3Request(HttpMethod.Post, completeUri, "application/xml", options, payloadHash);
    completeRequest.Content = new ByteArrayContent(completeBytes);
    completeRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    using var completeResponse = await httpClient.SendAsync(completeRequest, cancellationToken);
    if (!completeResponse.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)completeResponse.StatusCode);
    }

    var publicBaseUrl = string.IsNullOrWhiteSpace(options.PublicBaseUrl)
        ? $"https://{options.Bucket}.s3.{options.Region}.amazonaws.com"
        : options.PublicBaseUrl.TrimEnd('/');
    var originalImageUrl = $"{publicBaseUrl}/{string.Join('/', upload.S3Key.Split('/').Select(Uri.EscapeDataString))}";
    var postUploadCancellationToken = CancellationToken.None;
    var photoId = await InsertSpendBeeMerchantPhotoAsync(connection, upload, originalImageUrl, postUploadCancellationToken);
    await CompleteSpendBeeMerchantPhotoUploadAsync(connection, upload.Id, originalImageUrl, photoId, postUploadCancellationToken);

    try
    {
        using var processingTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var originalBytes = await FetchS3ObjectBytesAsync(options, upload.S3Key, httpClient, processingTimeout.Token);
        var prompt = BuildSpendBeePhotoCartoonPrompt(upload);
        var cartoonUrl = await TryCartoonizeSpendBeePhotoAsync(
            originalBytes,
            upload.ContentType,
            upload.FileName ?? "spendbee-photo.jpg",
            prompt,
            httpClient,
            storage,
            openAIOptions.Value,
            upload.MerchantId,
            photoId,
            processingTimeout.Token);
        if (string.IsNullOrWhiteSpace(cartoonUrl))
        {
            await UpdateSpendBeeMerchantPhotoProcessingFailureAsync(connection, photoId, "OpenAI did not return a cartoon image.", postUploadCancellationToken);
            await InsertSpendBeeUserMessageAsync(
                connection,
                session.ProjectId,
                session.AppUserId,
                "merchant_photo_processing_failed",
                "Error",
                "Photo upload failed",
                "Your photo was uploaded, but we could not create the cartoon version. Please try again.",
                "MerchantPhoto",
                photoId,
                $"spendbee://merchant-photos/{photoId}",
                new { photoUploadId = upload.Id, photoId, merchantId = upload.MerchantId, status = "ProcessingFailed" },
                postUploadCancellationToken);
            return Results.Problem("OpenAI did not return a cartoon image.");
        }

        await UpdateSpendBeeMerchantPhotoProcessedAsync(connection, photoId, cartoonUrl, "image/jpeg", prompt, postUploadCancellationToken);
        await InsertSpendBeeUserMessageAsync(
            connection,
            session.ProjectId,
            session.AppUserId,
            "merchant_photo_upload_success",
            "Success",
            "Photo uploaded",
            "Your photo has been cartoonized and added to the merchant gallery.",
            "MerchantPhoto",
            photoId,
            $"spendbee://merchant-photos/{photoId}",
            new { photoUploadId = upload.Id, photoId, merchantId = upload.MerchantId, status = "Ready" },
            postUploadCancellationToken);
    }
    catch (Exception ex)
    {
        await UpdateSpendBeeMerchantPhotoProcessingFailureAsync(connection, photoId, NormalizeBounded(ex.Message, 700) ?? "Image processing failed.", postUploadCancellationToken);
        await InsertSpendBeeUserMessageAsync(
            connection,
            session.ProjectId,
            session.AppUserId,
            "merchant_photo_processing_failed",
            "Error",
            "Photo upload failed",
            "Your photo was uploaded, but we could not create the cartoon version. Please try again.",
            "MerchantPhoto",
            photoId,
            $"spendbee://merchant-photos/{photoId}",
            new { photoUploadId = upload.Id, photoId, merchantId = upload.MerchantId, status = "ProcessingFailed" },
            postUploadCancellationToken);
        return Results.Problem("Photo was uploaded but cartoon processing failed.");
    }

    return Results.Ok(new
    {
        success = true,
        photoUploadId = upload.Id,
        photoId,
        status = "Ready",
        imageUrl = BuildPublicApiUrl(BuildPublicRequestBaseUrl(request), $"/api/spendbee/v1/merchant-photos/{photoId}/image")
    });
});

app.MapGet("/api/spendbee/v1/merchants/{merchantId:long}/photos", async (
    long merchantId,
    HttpRequest request,
    IConfiguration configuration,
    string? category,
    int? limit,
    long? beforeId,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await SpendBeeMerchantBelongsToProjectAsync(connection, session.ProjectId, merchantId, cancellationToken))
    {
        return Results.NotFound(new { message = "Merchant not found." });
    }

    var categoryFilter = NormalizeSpendBeeMerchantPhotoCategoryFilter(category);
    if (!categoryFilter.IsValid)
    {
        return Results.BadRequest(new
        {
            message = "Merchant photo category must be one of group, food, menu, storefront, environment, or other. Use receipt APIs for receipt images."
        });
    }

    var result = await LoadSpendBeeMerchantPhotosAsync(
        connection,
        session.ProjectId,
        session.AppUserId,
        merchantId,
        categoryFilter.Category,
        limit,
        beforeId,
        BuildPublicRequestBaseUrl(request),
        cancellationToken);
    return Results.Ok(new { photos = result.Items, categories = result.Categories, page = result.Page });
});

app.MapGet("/api/spendbee/v1/merchant-photos/{photoId:long}/image", async (
    long photoId,
    HttpResponse response,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT DisplayImageUrl
        FROM bee_SpendBeeMerchantPhoto
        WHERE id = @PhotoId AND Status = 'Ready' AND DisplayImageUrl IS NOT NULL
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    var imageUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Photo image not found." });
    }

    response.Headers.CacheControl = "public, max-age=86400";
    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken);
});

app.MapPost("/api/spendbee/v1/merchant-photos/{photoId:long}/like", async (
    long photoId,
    SpendBeeLikeRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await SpendBeePhotoBelongsToProjectAsync(connection, session.ProjectId, photoId, cancellationToken))
    {
        return Results.NotFound(new { message = "Photo not found." });
    }

    var photoOwner = await FindSpendBeePhotoOwnerAsync(connection, session.ProjectId, photoId, cancellationToken);
    if (photoOwner is null)
    {
        return Results.NotFound(new { message = "Photo not found." });
    }

    var insertedLike = false;
    if (payload.Liked ?? true)
    {
        const string likeSql = """
            INSERT INTO bee_SpendBeeMerchantPhotoLike (PhotoId, AppUserId)
            VALUES (@PhotoId, @AppUserId)
            ON DUPLICATE KEY UPDATE CreatedAtUtc = CreatedAtUtc;
            """;
        await using var likeCommand = new MySqlCommand(likeSql, connection);
        likeCommand.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
        likeCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
        insertedLike = await likeCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
    else
    {
        const string unlikeSql = "DELETE FROM bee_SpendBeeMerchantPhotoLike WHERE PhotoId = @PhotoId AND AppUserId = @AppUserId;";
        await using var unlikeCommand = new MySqlCommand(unlikeSql, connection);
        unlikeCommand.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
        unlikeCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
        await unlikeCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    var summary = await GetSpendBeePhotoLikeSummaryAsync(connection, photoId, session.AppUserId, cancellationToken);
    if (insertedLike && photoOwner.AppUserId != session.AppUserId)
    {
        var liker = await FindSpendBeeUserPublicProfileAsync(connection, session.ProjectId, session.AppUserId, cancellationToken);
        await InsertSpendBeeUserMessageAsync(
            connection,
            session.ProjectId,
            photoOwner.AppUserId,
            "merchant_photo_liked",
            "Info",
            "Photo liked",
            $"{liker?.DisplayName ?? "Someone"} liked your photo.",
            "MerchantPhoto",
            photoId,
            $"spendbee://merchant-photos/{photoId}",
            new
            {
                photoId,
                merchantId = photoOwner.MerchantId,
                likedBy = new
                {
                    appUserId = session.AppUserId,
                    displayName = liker?.DisplayName,
                    avatarUrl = liker?.AvatarUrl,
                    gender = liker?.Gender
                },
                likeCount = summary.LikeCount
            },
            cancellationToken);
    }

    return Results.Ok(new { success = true, photoId, summary.LikeCount, summary.LikedByMe });
});

app.MapGet("/api/spendbee/v1/merchant-photos/{photoId:long}/likes", async (
    long photoId,
    HttpRequest request,
    IConfiguration configuration,
    int? limit,
    long? beforeUserId,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await SpendBeePhotoBelongsToProjectAsync(connection, session.ProjectId, photoId, cancellationToken))
    {
        return Results.NotFound(new { message = "Photo not found." });
    }

    var likes = await LoadSpendBeePhotoLikersAsync(connection, photoId, limit, beforeUserId, BuildPublicRequestBaseUrl(request), cancellationToken);
    return Results.Ok(new { likes = likes.Items, page = likes.Page });
});

app.MapGet("/api/spendbee/v1/merchant-photos/{photoId:long}/comments", async (
    long photoId,
    HttpRequest request,
    IConfiguration configuration,
    int? limit,
    long? beforeId,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await SpendBeePhotoBelongsToProjectAsync(connection, session.ProjectId, photoId, cancellationToken))
    {
        return Results.NotFound(new { message = "Photo not found." });
    }

    var comments = await LoadSpendBeePhotoCommentsAsync(connection, photoId, session.AppUserId, limit, beforeId, cancellationToken);
    return Results.Ok(new { comments = comments.Items, page = comments.Page });
});

app.MapPost("/api/spendbee/v1/merchant-photos/{photoId:long}/comments", async (
    long photoId,
    SpendBeePhotoCommentRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var body = NormalizeBounded(payload.Body, 1000);
    if (string.IsNullOrWhiteSpace(body))
    {
        return Results.BadRequest(new { message = "Comment body is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await SpendBeePhotoBelongsToProjectAsync(connection, session.ProjectId, photoId, cancellationToken))
    {
        return Results.NotFound(new { message = "Photo not found." });
    }

    if (payload.ParentCommentId is not null &&
        !await SpendBeeCommentBelongsToPhotoAsync(connection, photoId, payload.ParentCommentId.Value, cancellationToken))
    {
        return Results.BadRequest(new { message = "Parent comment does not belong to this photo." });
    }

    var commentId = await InsertSpendBeePhotoCommentAsync(connection, photoId, session.AppUserId, payload.ParentCommentId, body, cancellationToken);
    var comment = await LoadSpendBeePhotoCommentAsync(connection, commentId, session.AppUserId, cancellationToken);
    return Results.Ok(new { success = true, comment });
});

app.MapPost("/api/spendbee/v1/merchant-photo-comments/{commentId:long}/replies", async (
    long commentId,
    SpendBeePhotoCommentReplyRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var body = NormalizeBounded(payload.Body, 1000);
    if (string.IsNullOrWhiteSpace(body))
    {
        return Results.BadRequest(new { message = "Reply body is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var photoId = await FindSpendBeeCommentPhotoIdAsync(connection, session.ProjectId, commentId, cancellationToken);
    if (photoId is null)
    {
        return Results.NotFound(new { message = "Comment not found." });
    }

    var replyId = await InsertSpendBeePhotoCommentAsync(connection, photoId.Value, session.AppUserId, commentId, body, cancellationToken);
    var reply = await LoadSpendBeePhotoCommentAsync(connection, replyId, session.AppUserId, cancellationToken);
    return Results.Ok(new { success = true, comment = reply });
});

app.MapPost("/api/spendbee/v1/merchant-photo-comments/{commentId:long}/like", async (
    long commentId,
    SpendBeeLikeRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (await FindSpendBeeCommentPhotoIdAsync(connection, session.ProjectId, commentId, cancellationToken) is null)
    {
        return Results.NotFound(new { message = "Comment not found." });
    }

    if (payload.Liked ?? true)
    {
        const string likeSql = """
            INSERT INTO bee_SpendBeeMerchantPhotoCommentLike (CommentId, AppUserId)
            VALUES (@CommentId, @AppUserId)
            ON DUPLICATE KEY UPDATE CreatedAtUtc = CreatedAtUtc;
            """;
        await using var likeCommand = new MySqlCommand(likeSql, connection);
        likeCommand.Parameters.Add("@CommentId", MySqlDbType.Int64).Value = commentId;
        likeCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
        await likeCommand.ExecuteNonQueryAsync(cancellationToken);
    }
    else
    {
        const string unlikeSql = "DELETE FROM bee_SpendBeeMerchantPhotoCommentLike WHERE CommentId = @CommentId AND AppUserId = @AppUserId;";
        await using var unlikeCommand = new MySqlCommand(unlikeSql, connection);
        unlikeCommand.Parameters.Add("@CommentId", MySqlDbType.Int64).Value = commentId;
        unlikeCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
        await unlikeCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    var summary = await GetSpendBeeCommentLikeSummaryAsync(connection, commentId, session.AppUserId, cancellationToken);
    return Results.Ok(new { success = true, commentId, summary.LikeCount, summary.LikedByMe });
});

app.MapGet("/api/spendbee/v1/receipts", async (
    HttpRequest request,
    IConfiguration configuration,
    int? limit,
    long? beforeId,
    long? merchantId,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var receipts = await LoadSpendBeeReceiptListAsync(connection, session.ProjectId, session.AppUserId, merchantId, null, limit, beforeId, BuildPublicRequestBaseUrl(request), cancellationToken);
    return Results.Ok(new
    {
        receipts = receipts.Items,
        page = receipts.Page
    });
});

app.MapGet("/api/spendbee/v1/receipt-groups", async (
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var groups = await LoadSpendBeeReceiptGroupsAsync(connection, session.ProjectId, session.AppUserId, cancellationToken);
    return Results.Ok(new { groups });
});

app.MapPost("/api/spendbee/v1/receipt-groups", async (
    SpendBeeReceiptGroupUpdateRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var title = NormalizeBounded(payload.Title, 160);
    if (string.IsNullOrWhiteSpace(title))
    {
        return Results.BadRequest(new { message = "Title is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var groupId = await InsertSpendBeeReceiptGroupAsync(
        connection,
        session.ProjectId,
        session.AppUserId,
        title,
        NormalizeBounded(payload.Description, 500),
        cancellationToken);
    var group = await LoadSpendBeeReceiptGroupAsync(connection, session.ProjectId, session.AppUserId, groupId, cancellationToken);
    return Results.Ok(new { success = true, group });
});

app.MapGet("/api/spendbee/v1/receipt-groups/{groupId:long}", async (
    long groupId,
    HttpRequest request,
    IConfiguration configuration,
    int? limit,
    long? beforeId,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    var group = await LoadSpendBeeReceiptGroupAsync(connection, session.ProjectId, session.AppUserId, groupId, cancellationToken);
    if (group is null)
    {
        return Results.NotFound(new { message = "Receipt group not found." });
    }

    var receipts = await LoadSpendBeeReceiptListAsync(
        connection,
        session.ProjectId,
        session.AppUserId,
        null,
        groupId,
        limit,
        beforeId,
        BuildPublicRequestBaseUrl(request),
        cancellationToken);
    return Results.Ok(new { group, receipts = receipts.Items, page = receipts.Page });
});

app.MapPut("/api/spendbee/v1/receipt-groups/{groupId:long}", async (
    long groupId,
    SpendBeeReceiptGroupUpdateRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var title = NormalizeBounded(payload.Title, 160);
    if (string.IsNullOrWhiteSpace(title))
    {
        return Results.BadRequest(new { message = "Title is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    if (!await UpdateSpendBeeReceiptGroupAsync(connection, session.ProjectId, session.AppUserId, groupId, title, NormalizeBounded(payload.Description, 500), cancellationToken))
    {
        return Results.NotFound(new { message = "Receipt group not found." });
    }

    var group = await LoadSpendBeeReceiptGroupAsync(connection, session.ProjectId, session.AppUserId, groupId, cancellationToken);
    return Results.Ok(new { success = true, group });
});

app.MapPost("/api/spendbee/v1/receipt-groups/{groupId:long}/receipts", async (
    long groupId,
    SpendBeeReceiptGroupReceiptAddRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var receiptIds = payload.ReceiptIds?
        .Where(id => id > 0)
        .Distinct()
        .Take(200)
        .ToList() ?? new List<long>();
    if (receiptIds.Count == 0)
    {
        return Results.BadRequest(new { message = "receiptIds is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    if (!await IsSpendBeeProjectAsync(connection, session.ProjectId, cancellationToken))
    {
        return Results.Forbid();
    }

    if (!await SpendBeeReceiptGroupBelongsToUserAsync(connection, session.ProjectId, session.AppUserId, groupId, cancellationToken))
    {
        return Results.NotFound(new { message = "Receipt group not found." });
    }

    var added = await AddSpendBeeReceiptsToGroupAsync(connection, session.ProjectId, session.AppUserId, groupId, receiptIds, cancellationToken);
    var group = await LoadSpendBeeReceiptGroupAsync(connection, session.ProjectId, session.AppUserId, groupId, cancellationToken);
    return Results.Ok(new { success = true, groupId, added, group });
});

app.MapGet("/api/spendbee/v1/receipts/{receiptId:long}", async (
    long receiptId,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var receipt = await LoadSpendBeeReceiptAsync(configuration, receiptId, session.ProjectId, session.AppUserId, BuildPublicRequestBaseUrl(request), cancellationToken);
    return receipt is null ? Results.NotFound(new { message = "Receipt not found." }) : Results.Ok(receipt);
});

app.MapGet("/api/spendbee/v1/receipts/{receiptId:long}/images/{imageId:long}", async (
    long receiptId,
    long imageId,
    string? download,
    HttpRequest request,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT image.ImageUrl, image.ContentType, image.SortOrder
        FROM bee_SpendBeeReceiptImage AS image
        INNER JOIN bee_SpendBeeReceipt AS receipt ON receipt.id = image.ReceiptId
        WHERE image.id = @ImageId
            AND receipt.id = @ReceiptId
            AND receipt.ProjectId = @ProjectId
            AND receipt.AppUserId = @AppUserId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ImageId", MySqlDbType.Int64).Value = imageId;
    command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Receipt image not found." });
    }

    var imageUrl = reader["ImageUrl"] as string;
    var contentType = reader["ContentType"] as string;
    var sortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder"));
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Receipt image not found." });
    }

    var fileName = IsDownloadRequested(download) ? BuildSpendBeeReceiptImageDownloadFileName(receiptId, imageId, sortOrder, contentType) : null;
    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken, fileName, contentType);
});

app.MapGet("/api/spendbee/admin/receipts/{receiptId:long}/images/{imageId:long}", async (
    long receiptId,
    long imageId,
    string? download,
    ClaimsPrincipal user,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT image.ImageUrl, image.ContentType, image.SortOrder
        FROM bee_SpendBeeReceiptImage AS image
        INNER JOIN bee_SpendBeeReceipt AS receipt ON receipt.id = image.ReceiptId
        INNER JOIN bee_Project AS project ON project.id = receipt.ProjectId
        LEFT JOIN bee_ProjectMember AS membership
            ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
        WHERE image.id = @ImageId
            AND receipt.id = @ReceiptId
            AND (project.AdminId = @AdminId OR membership.AdminId = @AdminId)
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    command.Parameters.Add("@ImageId", MySqlDbType.Int64).Value = imageId;
    command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return Results.NotFound(new { message = "Receipt image not found." });
    }

    var imageUrl = reader["ImageUrl"] as string;
    var contentType = reader["ContentType"] as string;
    var sortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder"));
    if (string.IsNullOrWhiteSpace(imageUrl))
    {
        return Results.NotFound(new { message = "Receipt image not found." });
    }

    var fileName = IsDownloadRequested(download) ? BuildSpendBeeReceiptImageDownloadFileName(receiptId, imageId, sortOrder, contentType) : null;
    return await StreamProtectedAnalysisImageAsync(imageUrl, configuration, s3Options.Value, httpClient, cancellationToken, fileName, contentType);
}).RequireAuthorization();

app.MapGet("/api/spendbee/admin/merchants/{merchantId:long}/cover", async (
    long merchantId,
    ClaimsPrincipal user,
    IConfiguration configuration,
    IOptions<S3StorageOptions> s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
    {
        return Results.Unauthorized();
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var coverUrlSql = BuildSpendBeeMerchantCoverUrlSql("merchant");
    var sql = $"""
        SELECT {coverUrlSql} AS CoverUrl
        FROM bee_SpendBeeMerchant AS merchant
        INNER JOIN bee_Project AS project ON project.id = merchant.ProjectId
        LEFT JOIN bee_ProjectMember AS membership
            ON membership.ProjectId = project.id AND membership.AdminId = @AdminId
        WHERE merchant.id = @MerchantId
            AND (project.AdminId = @AdminId OR membership.AdminId = @AdminId)
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    var coverUrl = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (string.IsNullOrWhiteSpace(coverUrl))
    {
        return Results.NotFound(new { message = "Merchant cover image not found." });
    }

    return await StreamProtectedAnalysisImageAsync(coverUrl, configuration, s3Options.Value, httpClient, cancellationToken);
}).RequireAuthorization();

app.MapPost("/api/app/devices/bind", async (
    AppBindDeviceRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var bindingCode = NormalizeBindingCode(payload.BindingCode ?? payload.BindingToken);
    if (string.IsNullOrWhiteSpace(bindingCode) && string.IsNullOrWhiteSpace(payload.BindingToken))
    {
        return Results.BadRequest(new { message = "Binding code is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var transaction = (MySqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

    const string codeSql = """
        SELECT id
        FROM bee_EdgeDevice
        WHERE ProjectId = @ProjectId
            AND BindingCode = @BindingCode
        LIMIT 1;
        """;
    await using var codeCommand = new MySqlCommand(codeSql, connection, transaction);
    codeCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    codeCommand.Parameters.Add("@BindingCode", MySqlDbType.VarChar, 16).Value = bindingCode;
    var edgeDeviceIdValue = await codeCommand.ExecuteScalarAsync(cancellationToken);
    var usedLegacyToken = false;

    if (edgeDeviceIdValue is null && !string.IsNullOrWhiteSpace(payload.BindingToken))
    {
        const string tokenSql = """
            SELECT EdgeDeviceId
            FROM bee_EdgeDeviceBindingToken
            WHERE ProjectId = @ProjectId
                AND TokenHash = @TokenHash
                AND UsedAtUtc IS NULL
                AND ExpiresAtUtc > UTC_TIMESTAMP(6)
            LIMIT 1;
            """;
        await using var tokenCommand = new MySqlCommand(tokenSql, connection, transaction);
        tokenCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
        tokenCommand.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 128).Value = HashSecret(payload.BindingToken);
        edgeDeviceIdValue = await tokenCommand.ExecuteScalarAsync(cancellationToken);
        usedLegacyToken = edgeDeviceIdValue is not null;
    }

    if (edgeDeviceIdValue is null)
    {
        return Results.BadRequest(new { message = "Binding code is invalid." });
    }

    var edgeDeviceId = Convert.ToInt32(edgeDeviceIdValue);
    const string bindSql = """
        INSERT IGNORE INTO bee_EdgeDeviceUserBinding (EdgeDeviceId, AppUserId)
        VALUES (@EdgeDeviceId, @AppUserId);
        """;
    await using var bindCommand = new MySqlCommand(bindSql, connection, transaction);
    bindCommand.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = edgeDeviceId;
    bindCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    await bindCommand.ExecuteNonQueryAsync(cancellationToken);

    if (usedLegacyToken)
    {
        const string markTokenSql = """
            UPDATE bee_EdgeDeviceBindingToken
            SET UsedAtUtc = UTC_TIMESTAMP(6)
            WHERE TokenHash = @TokenHash;
            """;
        await using var markTokenCommand = new MySqlCommand(markTokenSql, connection, transaction);
        markTokenCommand.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 128).Value = HashSecret(payload.BindingToken!);
        await markTokenCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    await transaction.CommitAsync(cancellationToken);
    return Results.Ok(new { success = true, edgeDeviceId, bindingCode });
});

app.MapGet("/api/app/devices", async (
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var devices = await QueryAppBoundDevicesAsync(configuration, session.ProjectId, session.AppUserId, null, cancellationToken);
    return Results.Ok(new { devices });
});

app.MapGet("/api/app/devices/running", async (
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var devices = await QueryAppBoundDevicesAsync(configuration, session.ProjectId, session.AppUserId, null, cancellationToken);
    return Results.Ok(new { devices });
});

app.MapGet("/api/app/devices/{deviceCode}", async (
    string deviceCode,
    HttpRequest request,
    IConfiguration configuration,
    IServerResourceService serverResourceService,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var devices = await QueryAppBoundDevicesAsync(configuration, session.ProjectId, session.AppUserId, deviceCode, cancellationToken);
    var device = devices.FirstOrDefault();
    if (device is null)
    {
        return Results.NotFound(new { message = "Bound edge device not found." });
    }

    var streamUrl = await ResolveAppLiveStreamUrlAsync(serverResourceService, device.ServerResourceInstanceName, device.DeviceCode, cancellationToken);
    var riskNotificationSettings = await QueryAppRiskNotificationSettingsAsync(
        configuration,
        session.ProjectId,
        session.AppUserId,
        device.DeviceCode,
        cancellationToken);
    return Results.Ok(new
    {
        device.Id,
        device.DeviceCode,
        device.Name,
        device.Address,
        device.Status,
        device.RecognizableWorkerCount,
        device.PpeComplianceRate,
        device.RiskCount,
        device.CameraCount,
        device.HeavyEquipmentCount,
        videoStreamUrl = streamUrl,
        latitude = device.Latitude,
        longitude = device.Longitude,
        lastHeartbeatAtUtc = device.LastHeartbeatAtUtc,
        riskNotificationSettings = riskNotificationSettings?.Settings ?? DefaultRiskNotificationSettings()
    });
});

app.MapDelete("/api/app/devices/{deviceCode}/binding", async (
    string deviceCode,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(deviceCode))
    {
        return Results.BadRequest(new { message = "Device code is required." });
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        DELETE binding
        FROM bee_EdgeDeviceUserBinding AS binding
        INNER JOIN bee_EdgeDevice AS device ON device.id = binding.EdgeDeviceId
        WHERE binding.AppUserId = @AppUserId
            AND device.ProjectId = @ProjectId
            AND device.DeviceCode = @DeviceCode;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = session.AppUserId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = session.ProjectId;
    command.Parameters.Add("@DeviceCode", MySqlDbType.VarChar, 40).Value = deviceCode.Trim();
    var affected = await command.ExecuteNonQueryAsync(cancellationToken);
    return affected == 0
        ? Results.NotFound(new { message = "Bound edge device not found." })
        : Results.Ok(new { success = true, deviceCode = deviceCode.Trim() });
});

app.MapGet("/api/app/devices/{deviceCode}/daily-stats", async (
    string deviceCode,
    DateOnly? from,
    DateOnly? to,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var fromDate = from ?? toDate.AddDays(-30);
    var stats = await QueryAppDeviceDailyStatsAsync(
        configuration,
        session.ProjectId,
        session.AppUserId,
        deviceCode,
        fromDate,
        toDate,
        cancellationToken);
    return Results.Ok(new { deviceCode, from = fromDate, to = toDate, stats });
});

app.MapGet("/api/app/devices/{deviceCode}/risk-subjects", async (
    string deviceCode,
    DateOnly? date,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var statDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var subjects = await QueryAppDeviceRiskSubjectsAsync(
        configuration,
        session.ProjectId,
        session.AppUserId,
        deviceCode,
        statDate,
        cancellationToken);
    return Results.Ok(new { deviceCode, date = statDate, subjects });
});

app.MapGet("/api/app/devices/{deviceCode}/risk-notification-settings", async (
    string deviceCode,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var settings = await QueryAppRiskNotificationSettingsAsync(
        configuration,
        session.ProjectId,
        session.AppUserId,
        deviceCode,
        cancellationToken);
    return settings is null
        ? Results.NotFound(new { message = "Bound edge device not found." })
        : Results.Ok(settings);
});

app.MapPut("/api/app/devices/{deviceCode}/risk-notification-settings", async (
    string deviceCode,
    AppRiskNotificationSettingsUpdateRequest payload,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var settings = await UpsertAppRiskNotificationSettingsAsync(
        configuration,
        session.ProjectId,
        session.AppUserId,
        deviceCode,
        payload,
        cancellationToken);
    return settings is null
        ? Results.NotFound(new { message = "Bound edge device not found." })
        : Results.Ok(settings);
});

app.MapGet("/api/app/notifications", async (
    bool? unreadOnly,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var notifications = await QueryAppRiskNotificationsAsync(
        configuration,
        session.ProjectId,
        session.AppUserId,
        unreadOnly ?? false,
        cancellationToken);
    return Results.Ok(new { notifications });
});

app.MapPut("/api/app/notifications/{notificationId:long}/read", async (
    long notificationId,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var marked = await MarkAppRiskNotificationReadAsync(
        configuration,
        session.ProjectId,
        session.AppUserId,
        notificationId,
        cancellationToken);
    return marked
        ? Results.Ok(new { success = true, notificationId, read = true })
        : Results.NotFound(new { message = "Notification not found." });
});

app.MapGet("/api/app/events/{eventId:int}/analysis", async (
    int eventId,
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var session = await AuthenticateAppSessionAsync(request, configuration, cancellationToken);
    if (session is null)
    {
        return Results.Unauthorized();
    }

    var analysis = await QueryAppEventAnalysisAsync(configuration, session.ProjectId, session.AppUserId, eventId, cancellationToken);
    return analysis is null
        ? Results.NotFound(new { message = "Bound event analysis not found." })
        : Results.Ok(analysis);
});

app.Run();

static async Task<EdgeApiSession?> AuthenticateEdgeSessionAsync(
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken)
{
    var authorization = request.Headers.Authorization.ToString();
    const string bearerPrefix = "Bearer ";
    if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var token = authorization[bearerPrefix.Length..].Trim();
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    const string sql = """
        SELECT id, ProjectId
        FROM bee_ProjectApiClientSession
        WHERE TokenHash = @TokenHash
            AND RevokedAtUtc IS NULL
            AND ExpiresAtUtc > UTC_TIMESTAMP(6)
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 128).Value = HashSecret(token);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new EdgeApiSession(
        reader.GetInt32(reader.GetOrdinal("id")),
        reader.GetInt32(reader.GetOrdinal("ProjectId")));
}

static async Task<int?> FindDeviceIdAsync(
    MySqlConnection connection,
    int projectId,
    string deviceCode,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id
        FROM bee_EdgeDevice
        WHERE ProjectId = @ProjectId AND DeviceCode = @DeviceCode
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@DeviceCode", MySqlDbType.VarChar, 40).Value = deviceCode.Trim();
    var result = await command.ExecuteScalarAsync(cancellationToken);
    return result is null ? null : Convert.ToInt32(result);
}

static async Task<AppApiSession?> AuthenticateAppSessionAsync(
    HttpRequest request,
    IConfiguration configuration,
    CancellationToken cancellationToken)
{
    var authorization = request.Headers.Authorization.ToString();
    const string bearerPrefix = "Bearer ";
    if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var token = authorization[bearerPrefix.Length..].Trim();
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT session.id, session.ProjectId, session.AppUserId, user.PhoneNumber, user.Email,
            user.DisplayName, user.FirstName, user.LastName, user.Gender
        FROM bee_AppUserSession AS session
        INNER JOIN bee_AppUser AS user ON user.id = session.AppUserId
        WHERE session.TokenHash = @TokenHash
            AND session.RevokedAtUtc IS NULL
            AND session.ExpiresAtUtc > UTC_TIMESTAMP(6)
            AND user.Status = 'Active'
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 128).Value = HashSecret(token);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new AppApiSession(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader.GetInt32(reader.GetOrdinal("ProjectId")),
        reader.GetInt32(reader.GetOrdinal("AppUserId")),
        reader["PhoneNumber"] as string ?? string.Empty,
        reader["Email"] as string ?? string.Empty,
        reader["DisplayName"] as string ?? string.Empty,
        reader["FirstName"] as string,
        reader["LastName"] as string,
        reader["Gender"] as string);
}

static async Task<bool> ProjectExistsAsync(MySqlConnection connection, int projectId, CancellationToken cancellationToken)
{
    const string sql = "SELECT 1 FROM bee_Project WHERE id = @ProjectId LIMIT 1;";
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    return await command.ExecuteScalarAsync(cancellationToken) is not null;
}

static async Task<bool> AppUserEmailExistsAsync(
    MySqlConnection connection,
    MySqlTransaction? transaction,
    int projectId,
    string email,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT 1
        FROM bee_AppUser
        WHERE ProjectId = @ProjectId
            AND Email = @Email
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection, transaction);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
    return await command.ExecuteScalarAsync(cancellationToken) is not null;
}

static async Task<SpendBeeAuthUser?> LoadSpendBeeAppUserForAuthAsync(
    MySqlConnection connection,
    MySqlTransaction transaction,
    int projectId,
    string email,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, DisplayName, Gender, AvatarUrl, Bio
        FROM bee_AppUser
        WHERE ProjectId = @ProjectId
            AND Email = @Email
            AND Status = 'Active'
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection, transaction);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new SpendBeeAuthUser(
        reader.GetInt32(reader.GetOrdinal("id")),
        reader["DisplayName"] as string ?? string.Empty,
        reader["Gender"] as string,
        reader["AvatarUrl"] as string,
        reader["Bio"] as string);
}

static async Task ConsumeVerificationCodeAsync(
    MySqlConnection connection,
    MySqlTransaction transaction,
    long codeId,
    CancellationToken cancellationToken)
{
    await using var command = new MySqlCommand(
        "UPDATE bee_AppUserVerificationCode SET ConsumedAtUtc = UTC_TIMESTAMP(6) WHERE id = @CodeId;",
        connection,
        transaction);
    command.Parameters.Add("@CodeId", MySqlDbType.Int64).Value = codeId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<object> CreateSpendBeeAuthResponseAsync(
    MySqlConnection connection,
    MySqlTransaction transaction,
    int projectId,
    int appUserId,
    string email,
    string displayName,
    string? gender,
    string? avatarUrl,
    string? bio,
    AppClientDeviceInfo? device,
    bool isNewUser,
    CancellationToken cancellationToken)
{
    var token = $"sb_app_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
    var expiresAtUtc = DateTime.UtcNow.AddDays(30);
    const string sessionSql = """
        INSERT INTO bee_AppUserSession (ProjectId, AppUserId, TokenHash, ExpiresAtUtc)
        VALUES (@ProjectId, @AppUserId, @TokenHash, @ExpiresAtUtc);
        """;
    await using var sessionCommand = new MySqlCommand(sessionSql, connection, transaction);
    sessionCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    sessionCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    sessionCommand.Parameters.Add("@TokenHash", MySqlDbType.VarChar, 128).Value = HashSecret(token);
    sessionCommand.Parameters.Add("@ExpiresAtUtc", MySqlDbType.DateTime).Value = expiresAtUtc;
    await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
    await SaveAppUserDeviceAsync(connection, transaction, projectId, appUserId, device, cancellationToken);

    return new
    {
        accessToken = token,
        token = token,
        tokenType = "Bearer",
        expiresAtUtc,
        isNewUser,
        user = new
        {
            id = appUserId,
            email,
            displayName,
            gender,
            avatarUrl,
            bio,
            projectId,
            status = "Active"
        }
    };
}

static async Task<AppUserProfile?> GetAppUserProfileAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT id, ProjectId, PhoneNumber, Email, DisplayName, FirstName, LastName, Gender, AvatarUrl, Bio, CreatedAtUtc, UpdatedAtUtc
        FROM bee_AppUser
        WHERE id = @AppUserId
            AND ProjectId = @ProjectId
            AND Status = 'Active'
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new AppUserProfile(
        reader.GetInt32(reader.GetOrdinal("id")),
        reader.GetInt32(reader.GetOrdinal("ProjectId")),
        reader["Email"] as string,
        reader["PhoneNumber"] as string,
        reader["DisplayName"] as string ?? string.Empty,
        reader["FirstName"] as string,
        reader["LastName"] as string,
        reader["Gender"] as string,
        reader["AvatarUrl"] as string,
        reader["Bio"] as string,
        reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
        reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")));
}

static async Task<long?> FindValidVerificationCodeAsync(
    MySqlConnection connection,
    MySqlTransaction transaction,
    int projectId,
    string email,
    string purpose,
    string verificationCode,
    bool allowLoginFallback,
    CancellationToken cancellationToken)
{
    var purposes = allowLoginFallback && string.Equals(purpose, "Register", StringComparison.OrdinalIgnoreCase)
        ? new[] { "Register", "Login" }
        : new[] { purpose };
    foreach (var candidatePurpose in purposes)
    {
        const string sql = """
        SELECT id
        FROM bee_AppUserVerificationCode
        WHERE ProjectId = @ProjectId
            AND Email = @Email
            AND Purpose = @Purpose
            AND CodeHash = @CodeHash
            AND ConsumedAtUtc IS NULL
            AND ExpiresAtUtc > UTC_TIMESTAMP(6)
        ORDER BY id DESC
        LIMIT 1;
        """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
        command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = candidatePurpose;
        command.Parameters.Add("@CodeHash", MySqlDbType.VarChar, 128).Value = HashSecret($"{projectId}:{email}:{candidatePurpose}:{verificationCode}");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not null)
        {
            return Convert.ToInt64(value);
        }
    }

    return null;
}

static async Task SaveAppUserDeviceAsync(
    MySqlConnection connection,
    MySqlTransaction transaction,
    int projectId,
    int appUserId,
    AppClientDeviceInfo? device,
    CancellationToken cancellationToken)
{
    if (device is null)
    {
        return;
    }

    var deviceIdentifier = NormalizeBounded(device.DeviceIdentifier, 160);
    if (string.IsNullOrWhiteSpace(deviceIdentifier))
    {
        deviceIdentifier = HashSecret($"{appUserId}:{device.Platform}:{device.DeviceType}:{device.PushToken}")[..32];
    }

    var deviceKeyHash = HashSecret($"{appUserId}:{deviceIdentifier}:{NormalizeBounded(device.PushToken, 500)}");

    const string sql = """
        INSERT INTO bee_AppUserDevice
            (ProjectId, AppUserId, DeviceIdentifier, DeviceKeyHash, DeviceType, Platform, OsVersion, AppVersion, PushProvider, PushToken, LastLoginAtUtc)
        VALUES
            (@ProjectId, @AppUserId, @DeviceIdentifier, @DeviceKeyHash, @DeviceType, @Platform, @OsVersion, @AppVersion, @PushProvider, @PushToken, UTC_TIMESTAMP(6))
        ON DUPLICATE KEY UPDATE
            DeviceKeyHash = VALUES(DeviceKeyHash),
            DeviceType = VALUES(DeviceType),
            Platform = VALUES(Platform),
            OsVersion = VALUES(OsVersion),
            AppVersion = VALUES(AppVersion),
            PushProvider = VALUES(PushProvider),
            PushToken = VALUES(PushToken),
            LastLoginAtUtc = UTC_TIMESTAMP(6),
            UpdatedAtUtc = UTC_TIMESTAMP(6);
        """;
    await using var command = new MySqlCommand(sql, connection, transaction);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@DeviceIdentifier", MySqlDbType.VarChar, 160).Value = deviceIdentifier;
    command.Parameters.Add("@DeviceKeyHash", MySqlDbType.VarChar, 128).Value = deviceKeyHash;
    command.Parameters.Add("@DeviceType", MySqlDbType.VarChar, 80).Value = DbNullable(NormalizeBounded(device.DeviceType, 80));
    command.Parameters.Add("@Platform", MySqlDbType.VarChar, 80).Value = DbNullable(NormalizeBounded(device.Platform, 80));
    command.Parameters.Add("@OsVersion", MySqlDbType.VarChar, 80).Value = DbNullable(NormalizeBounded(device.OsVersion, 80));
    command.Parameters.Add("@AppVersion", MySqlDbType.VarChar, 80).Value = DbNullable(NormalizeBounded(device.AppVersion, 80));
    command.Parameters.Add("@PushProvider", MySqlDbType.VarChar, 40).Value = DbNullable(NormalizeBounded(device.PushProvider, 40));
    command.Parameters.Add("@PushToken", MySqlDbType.VarChar, 500).Value = DbNullable(NormalizeBounded(device.PushToken, 500));
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<int?> ResolveSpendBeeProjectIdAsync(
    MySqlConnection connection,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id
        FROM bee_Project
        WHERE ProjectKind = 'SpendBee' OR ProjectName = 'SpendBee'
        ORDER BY id
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    var value = await command.ExecuteScalarAsync(cancellationToken);
    return value is null ? null : Convert.ToInt32(value);
}

static async Task<bool> IsSpendBeeProjectAsync(
    MySqlConnection connection,
    int projectId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT 1
        FROM bee_Project
        WHERE id = @ProjectId
            AND (ProjectKind = 'SpendBee' OR ProjectName = 'SpendBee')
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    return await command.ExecuteScalarAsync(cancellationToken) is not null;
}

static async Task<SpendBeeReceiptRecognition> AnalyzeSpendBeeReceiptWithOpenAIAsync(
    HttpClient httpClient,
    OpenAIOptions options,
    IReadOnlyList<SpendBeeUploadedReceiptImage> images,
    string? timezone,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        throw new InvalidOperationException("OpenAI API key is not configured.");
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/responses");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    var content = new List<object>
    {
        new
        {
            type = "input_text",
            text = $"""
                Recognize this consumer receipt. Return only JSON matching the schema.
                The user's timezone is {NormalizeBounded(timezone, 80) ?? "unknown"}.
                Separate the real merchant from any ordering or delivery platform.
                If the receipt is from foodpanda/熊猫外卖/Uber Eats/DoorDash/Deliveroo/etc.,
                set receiptType=DeliveryOrder or PlatformInvoice, fulfillmentType=Delivery or Pickup,
                platform to the platform, and merchantName to the restaurant/store printed on the order.
                Use multiple consistency checks: merchant/date/location text, line item arithmetic,
                subtotal/tax/total reconciliation, repeated OCR cross-checks across all images.
                Set estimatedErrorRate above 0.01 and needsHumanReview=true unless the extracted
                merchant, purchase time, every line item amount, tax and total are internally consistent.
                """
        }
    };
    content.AddRange(images.Select(image => new
    {
        type = "input_image",
        image_url = $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}"
    }));

    request.Content = JsonContent.Create(new
    {
        model = options.Model,
        input = new object[]
        {
            new
            {
                role = "developer",
                content = """
                    You are SpendBee receipt OCR. Extract exact receipt facts, not guesses.
                    Prefer null over invented values. Amounts must be numeric decimal values.
                    Confidence is 0..1. Estimated error rate is 0..1.
                    """
            },
            new
            {
                role = "user",
                content
            }
        },
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "spendbee_receipt",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        receiptType = new { type = new[] { "string", "null" }, description = "InStoreReceipt, DeliveryOrder, PlatformInvoice, or unknown/null." },
                        fulfillmentType = new { type = new[] { "string", "null" }, description = "DineIn, Takeaway, Delivery, Pickup, or unknown/null." },
                        platform = new
                        {
                            type = new[] { "object", "null" },
                            properties = new
                            {
                                name = new { type = new[] { "string", "null" } },
                                displayName = new { type = new[] { "string", "null" } },
                                platformType = new { type = new[] { "string", "null" } },
                                websiteUrl = new { type = new[] { "string", "null" } },
                                confidence = new { type = "number" }
                            },
                            required = new[] { "name", "displayName", "platformType", "websiteUrl", "confidence" },
                            additionalProperties = false
                        },
                        merchantName = new { type = new[] { "string", "null" } },
                        merchantAddress = new { type = new[] { "string", "null" } },
                        platformOrderNumber = new { type = new[] { "string", "null" } },
                        purchasedAt = new { type = new[] { "string", "null" }, description = "ISO 8601 datetime if visible." },
                        orderedAt = new { type = new[] { "string", "null" }, description = "ISO 8601 datetime if visible." },
                        pickupAt = new { type = new[] { "string", "null" }, description = "ISO 8601 datetime if visible." },
                        deliveredAt = new { type = new[] { "string", "null" }, description = "ISO 8601 datetime if visible." },
                        currency = new { type = new[] { "string", "null" } },
                        subtotal = new { type = new[] { "number", "null" } },
                        tax = new { type = new[] { "number", "null" } },
                        deliveryFee = new { type = new[] { "number", "null" } },
                        serviceFee = new { type = new[] { "number", "null" } },
                        platformDiscount = new { type = new[] { "number", "null" } },
                        total = new { type = new[] { "number", "null" } },
                        lineItems = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string" },
                                    quantity = new { type = new[] { "number", "null" } },
                                    unitPrice = new { type = new[] { "number", "null" } },
                                    amount = new { type = new[] { "number", "null" } },
                                    category = new { type = new[] { "string", "null" } },
                                    confidence = new { type = "number" }
                                },
                                required = new[] { "name", "quantity", "unitPrice", "amount", "category", "confidence" },
                                additionalProperties = false
                            }
                        },
                        quality = new
                        {
                            type = "object",
                            properties = new
                            {
                                overallConfidence = new { type = "number" },
                                estimatedErrorRate = new { type = "number" },
                                needsHumanReview = new { type = "boolean" },
                                failedChecks = new
                                {
                                    type = "array",
                                    items = new { type = "string" }
                                }
                            },
                            required = new[] { "overallConfidence", "estimatedErrorRate", "needsHumanReview", "failedChecks" },
                            additionalProperties = false
                        }
                    },
                    required = new[]
                    {
                        "receiptType", "fulfillmentType", "platform", "merchantName", "merchantAddress",
                        "platformOrderNumber", "purchasedAt", "orderedAt", "pickupAt", "deliveredAt",
                        "currency", "subtotal", "tax", "deliveryFee", "serviceFee", "platformDiscount",
                        "total", "lineItems", "quality"
                    },
                    additionalProperties = false
                }
            }
        }
    });

    using var response = await httpClient.SendAsync(request, cancellationToken);
    var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"OpenAI returned HTTP {(int)response.StatusCode}: {TrimDiagnostic(responseText)}");
    }

    using var document = JsonDocument.Parse(responseText);
    var outputText = ExtractOpenAIOutputText(document.RootElement);
    return JsonSerializer.Deserialize<SpendBeeReceiptRecognition>(
        outputText,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("OpenAI returned an empty receipt recognition result.");
}

static async Task SaveSpendBeeReceiptRecognitionAsync(
    MySqlConnection connection,
    long receiptId,
    string status,
    SpendBeeReceiptRecognition recognition,
    string? rawRecognitionJson,
    string receiptCanonicalHash,
    CancellationToken cancellationToken)
{
    var platformId = await EnsureSpendBeePlatformForReceiptAsync(connection, receiptId, recognition.Platform, cancellationToken);
    const string updateSql = """
        UPDATE bee_SpendBeeReceipt
        SET Status = @Status,
            ReceiptCanonicalHash = @ReceiptCanonicalHash,
            PlatformId = @PlatformId,
            ReceiptType = @ReceiptType,
            FulfillmentType = @FulfillmentType,
            MerchantName = @MerchantName,
            MerchantAddress = @MerchantAddress,
            PlatformOrderNumber = @PlatformOrderNumber,
            PurchasedAtUtc = @PurchasedAtUtc,
            OrderedAtUtc = @OrderedAtUtc,
            PickupAtUtc = @PickupAtUtc,
            DeliveredAtUtc = @DeliveredAtUtc,
            Currency = @Currency,
            Subtotal = @Subtotal,
            Tax = @Tax,
            DeliveryFee = @DeliveryFee,
            ServiceFee = @ServiceFee,
            PlatformDiscount = @PlatformDiscount,
            Total = @Total,
            OverallConfidence = @OverallConfidence,
            EstimatedErrorRate = @EstimatedErrorRate,
            FailedChecksJson = @FailedChecksJson,
            RawOcrJson = @RawOcrJson,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @ReceiptId;
        """;
    await using var updateCommand = new MySqlCommand(updateSql, connection);
    updateCommand.Parameters.Add("@Status", MySqlDbType.VarChar, 40).Value = status;
    updateCommand.Parameters.Add("@ReceiptCanonicalHash", MySqlDbType.VarChar, 128).Value = receiptCanonicalHash;
    updateCommand.Parameters.Add("@PlatformId", MySqlDbType.Int64).Value = (object?)platformId ?? DBNull.Value;
    updateCommand.Parameters.Add("@ReceiptType", MySqlDbType.VarChar, 60).Value = DbNullable(NormalizeBounded(recognition.ReceiptType, 60));
    updateCommand.Parameters.Add("@FulfillmentType", MySqlDbType.VarChar, 60).Value = DbNullable(NormalizeBounded(recognition.FulfillmentType, 60));
    updateCommand.Parameters.Add("@MerchantName", MySqlDbType.VarChar, 200).Value = DbNullable(NormalizeBounded(recognition.MerchantName, 200));
    updateCommand.Parameters.Add("@MerchantAddress", MySqlDbType.VarChar, 500).Value = DbNullable(NormalizeBounded(recognition.MerchantAddress, 500));
    updateCommand.Parameters.Add("@PlatformOrderNumber", MySqlDbType.VarChar, 120).Value = DbNullable(NormalizeBounded(recognition.PlatformOrderNumber, 120));
    updateCommand.Parameters.Add("@PurchasedAtUtc", MySqlDbType.DateTime).Value =
        DateTimeOffset.TryParse(recognition.PurchasedAt, out var purchasedAt)
            ? purchasedAt.UtcDateTime
            : DBNull.Value;
    updateCommand.Parameters.Add("@OrderedAtUtc", MySqlDbType.DateTime).Value =
        DateTimeOffset.TryParse(recognition.OrderedAt, out var orderedAt)
            ? orderedAt.UtcDateTime
            : DBNull.Value;
    updateCommand.Parameters.Add("@PickupAtUtc", MySqlDbType.DateTime).Value =
        DateTimeOffset.TryParse(recognition.PickupAt, out var pickupAt)
            ? pickupAt.UtcDateTime
            : DBNull.Value;
    updateCommand.Parameters.Add("@DeliveredAtUtc", MySqlDbType.DateTime).Value =
        DateTimeOffset.TryParse(recognition.DeliveredAt, out var deliveredAt)
            ? deliveredAt.UtcDateTime
            : DBNull.Value;
    updateCommand.Parameters.Add("@Currency", MySqlDbType.VarChar, 12).Value = DbNullable(NormalizeBounded(recognition.Currency, 12));
    updateCommand.Parameters.Add("@Subtotal", MySqlDbType.Decimal).Value = (object?)recognition.Subtotal ?? DBNull.Value;
    updateCommand.Parameters.Add("@Tax", MySqlDbType.Decimal).Value = (object?)recognition.Tax ?? DBNull.Value;
    updateCommand.Parameters.Add("@DeliveryFee", MySqlDbType.Decimal).Value = (object?)recognition.DeliveryFee ?? DBNull.Value;
    updateCommand.Parameters.Add("@ServiceFee", MySqlDbType.Decimal).Value = (object?)recognition.ServiceFee ?? DBNull.Value;
    updateCommand.Parameters.Add("@PlatformDiscount", MySqlDbType.Decimal).Value = (object?)recognition.PlatformDiscount ?? DBNull.Value;
    updateCommand.Parameters.Add("@Total", MySqlDbType.Decimal).Value = (object?)recognition.Total ?? DBNull.Value;
    updateCommand.Parameters.Add("@OverallConfidence", MySqlDbType.Decimal).Value = recognition.Quality.OverallConfidence;
    updateCommand.Parameters.Add("@EstimatedErrorRate", MySqlDbType.Decimal).Value = recognition.Quality.EstimatedErrorRate;
    updateCommand.Parameters.Add("@FailedChecksJson", MySqlDbType.JSON).Value =
        JsonSerializer.Serialize(recognition.Quality.FailedChecks ?? [], new JsonSerializerOptions(JsonSerializerDefaults.Web));
    updateCommand.Parameters.Add("@RawOcrJson", MySqlDbType.JSON).Value = rawRecognitionJson ?? "{}";
    updateCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
    await updateCommand.ExecuteNonQueryAsync(cancellationToken);

    const string deleteSql = "DELETE FROM bee_SpendBeeReceiptLineItem WHERE ReceiptId = @ReceiptId;";
    await using (var deleteCommand = new MySqlCommand(deleteSql, connection))
    {
        deleteCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    foreach (var item in recognition.LineItems.Select((value, index) => new { value, index }))
    {
        const string insertSql = """
            INSERT INTO bee_SpendBeeReceiptLineItem
                (ReceiptId, ItemName, Quantity, UnitPrice, Amount, Category, Confidence, SortOrder)
            VALUES
                (@ReceiptId, @ItemName, @Quantity, @UnitPrice, @Amount, @Category, @Confidence, @SortOrder);
            """;
        await using var command = new MySqlCommand(insertSql, connection);
        command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        command.Parameters.Add("@ItemName", MySqlDbType.VarChar, 240).Value = NormalizeBounded(item.value.Name, 240) ?? "Unknown item";
        command.Parameters.Add("@Quantity", MySqlDbType.Decimal).Value = (object?)item.value.Quantity ?? DBNull.Value;
        command.Parameters.Add("@UnitPrice", MySqlDbType.Decimal).Value = (object?)item.value.UnitPrice ?? DBNull.Value;
        command.Parameters.Add("@Amount", MySqlDbType.Decimal).Value = (object?)item.value.Amount ?? DBNull.Value;
        command.Parameters.Add("@Category", MySqlDbType.VarChar, 80).Value = DbNullable(NormalizeBounded(item.value.Category, 80));
        command.Parameters.Add("@Confidence", MySqlDbType.Decimal).Value = item.value.Confidence;
        command.Parameters.Add("@SortOrder", MySqlDbType.Int32).Value = item.index;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

static async Task<long?> EnsureSpendBeePlatformForReceiptAsync(
    MySqlConnection connection,
    long receiptId,
    SpendBeeReceiptPlatformRecognition? platform,
    CancellationToken cancellationToken)
{
    var platformName = NormalizeBounded(platform?.Name ?? platform?.DisplayName, 160);
    if (string.IsNullOrWhiteSpace(platformName))
    {
        return null;
    }

    const string projectSql = "SELECT ProjectId FROM bee_SpendBeeReceipt WHERE id = @ReceiptId LIMIT 1;";
    int? projectId = null;
    await using (var projectCommand = new MySqlCommand(projectSql, connection))
    {
        projectCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        var value = await projectCommand.ExecuteScalarAsync(cancellationToken);
        if (value is not null)
        {
            projectId = Convert.ToInt32(value);
        }
    }

    if (projectId is null)
    {
        return null;
    }

    var normalizedName = NormalizePlatformName(platformName);
    var isFoodpanda = IsFoodpandaPlatform(normalizedName);
    var isUberEats = IsUberEatsPlatform(normalizedName);
    var displayName = NormalizeBounded(platform?.DisplayName, 160);
    if (isFoodpanda)
    {
        platformName = "foodpanda";
        normalizedName = "foodpanda";
        displayName = string.IsNullOrWhiteSpace(displayName) ? "\u718a\u732b\u5916\u5356" : displayName;
    }
    else if (isUberEats)
    {
        platformName = "Uber Eats";
        normalizedName = "ubereats";
        displayName = string.IsNullOrWhiteSpace(displayName) ? "Uber Eats" : displayName;
    }

    var aliases = isFoodpanda
        ? new[] { "foodpanda", "Foodpanda", "\u718a\u732b\u5916\u5356", "\u718a\u8c93\u5916\u8ce3", "\u718a\u732b", "\u5bcc\u80d6\u8fbe", "\u5bcc\u80d6\u9054", "pandamart" }
        : isUberEats
            ? new[] { "Uber", "Uber Eats", "UberEats", "ubereats", "\u4f18\u6b65\u5916\u5356", "\u512a\u6b65\u5916\u8ce3" }
            : new[] { platformName };
    var logoUrl = DefaultSpendBeePlatformLogoUrl(normalizedName);

    const string insertSql = """
        INSERT INTO bee_SpendBeePlatform
            (ProjectId, Name, DisplayName, NormalizedName, PlatformType, LogoUrl, WebsiteUrl, KnownAliasesJson, SourceJson)
        VALUES
            (@ProjectId, @Name, @DisplayName, @NormalizedName, @PlatformType, @LogoUrl, @WebsiteUrl, @KnownAliasesJson, @SourceJson)
        ON DUPLICATE KEY UPDATE
            DisplayName = COALESCE(VALUES(DisplayName), DisplayName),
            PlatformType = VALUES(PlatformType),
            LogoUrl = COALESCE(VALUES(LogoUrl), LogoUrl),
            WebsiteUrl = COALESCE(VALUES(WebsiteUrl), WebsiteUrl),
            KnownAliasesJson = VALUES(KnownAliasesJson),
            SourceJson = VALUES(SourceJson),
            UpdatedAtUtc = UTC_TIMESTAMP(6);
        SELECT id
        FROM bee_SpendBeePlatform
        WHERE ProjectId = @ProjectId AND NormalizedName = @NormalizedName
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(insertSql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId.Value;
    command.Parameters.Add("@Name", MySqlDbType.VarChar, 160).Value = platformName;
    command.Parameters.Add("@DisplayName", MySqlDbType.VarChar, 160).Value = DbNullable(displayName);
    command.Parameters.Add("@NormalizedName", MySqlDbType.VarChar, 180).Value = normalizedName;
    command.Parameters.Add("@PlatformType", MySqlDbType.VarChar, 80).Value = NormalizeBounded(platform?.PlatformType, 80) ?? "FoodDelivery";
    command.Parameters.Add("@LogoUrl", MySqlDbType.VarChar, 1000).Value = DbNullable(logoUrl);
    command.Parameters.Add("@WebsiteUrl", MySqlDbType.VarChar, 700).Value = DbNullable(NormalizeBounded(platform?.WebsiteUrl, 700));
    command.Parameters.Add("@KnownAliasesJson", MySqlDbType.JSON).Value = JsonSerializer.Serialize(aliases, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    command.Parameters.Add("@SourceJson", MySqlDbType.JSON).Value = JsonSerializer.Serialize(platform ?? new SpendBeeReceiptPlatformRecognition(platformName, displayName, "FoodDelivery", null, 0m), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var id = await command.ExecuteScalarAsync(cancellationToken);
    return id is null ? null : Convert.ToInt64(id);
}

static string NormalizePlatformName(string value)
{
    var normalized = NormalizeMerchantName(value)
        .Replace("\u5916\u8ce3", "\u5916\u5356", StringComparison.Ordinal)
        .Replace(" ", "", StringComparison.Ordinal);
    if (IsFoodpandaPlatform(normalized))
    {
        return "foodpanda";
    }

    return IsUberEatsPlatform(normalized) ? "ubereats" : normalized;
}

static bool IsFoodpandaPlatform(string normalizedName)
{
    return normalizedName.Contains("foodpanda", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("pandamart", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("\u718a\u732b", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("\u718a\u8c93", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("\u5bcc\u80d6\u8fbe", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("\u5bcc\u80d6\u9054", StringComparison.OrdinalIgnoreCase);
}

static bool IsUberEatsPlatform(string normalizedName)
{
    return normalizedName.Contains("ubereats", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Equals("uber", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("\u4f18\u6b65\u5916\u5356", StringComparison.OrdinalIgnoreCase)
        || normalizedName.Contains("\u512a\u6b65\u5916\u8ce3", StringComparison.OrdinalIgnoreCase);
}

static string? DefaultSpendBeePlatformLogoUrl(string normalizedName)
{
    return normalizedName switch
    {
        "foodpanda" => "https://upload.wikimedia.org/wikipedia/commons/7/74/Foodpanda_wordmark.svg",
        "ubereats" => "https://upload.wikimedia.org/wikipedia/commons/b/b3/Uber_Eats_2020_logo.svg",
        _ => null
    };
}
static string ComputeSpendBeeReceiptImageSetHash(IReadOnlyList<SpendBeeUploadedReceiptImage> images)
{
    var imageHashes = images
        .OrderBy(image => image.SortOrder)
        .Select(image => Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant());
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', imageHashes)))).ToLowerInvariant();
}

static string ComputeSpendBeeReceiptCanonicalHash(SpendBeeReceiptRecognition recognition)
{
    var purchasedAt = DateTimeOffset.TryParse(recognition.PurchasedAt, out var parsedAt)
        ? parsedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture)
        : NormalizeSpendBeeReceiptPart(recognition.PurchasedAt);
    var lines = recognition.LineItems
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Amount)
        .Select(item => string.Join(':',
            NormalizeSpendBeeReceiptPart(item.Name),
            item.Quantity?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            item.UnitPrice?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            item.Amount?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? ""));
    var canonical = string.Join('|',
        NormalizeSpendBeeReceiptPart(recognition.MerchantName),
        NormalizeSpendBeeReceiptPart(recognition.MerchantAddress),
        purchasedAt,
        NormalizeSpendBeeReceiptPart(recognition.Currency),
        recognition.Total?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "",
        string.Join(';', lines));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}

static string ComputeSpendBeeReceiptSoftFingerprint(SpendBeeReceiptRecognition recognition)
{
    var purchasedDate = TryParseSpendBeePurchasedAtUtc(recognition, out var purchasedAtUtc)
        ? purchasedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : NormalizeSpendBeeReceiptPart(recognition.PurchasedAt);
    var total = recognition.Total?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    var currency = NormalizeSpendBeeReceiptPart(recognition.Currency);
    var address = NormalizeSpendBeeAddressForDuplicate(recognition.MerchantAddress);
    var locationKey = string.IsNullOrWhiteSpace(address)
        ? NormalizeMerchantName(recognition.MerchantName)
        : address;
    var amounts = recognition.LineItems
        .Where(item => item.Amount.HasValue)
        .Select(item => item.Amount!.Value.ToString("0.00", CultureInfo.InvariantCulture))
        .Order(StringComparer.Ordinal)
        .ToArray();
    return string.Join('|', purchasedDate, total, currency, locationKey, string.Join(',', amounts));
}

static bool TryParseSpendBeePurchasedAtUtc(SpendBeeReceiptRecognition recognition, out DateTime purchasedAtUtc)
{
    if (DateTimeOffset.TryParse(recognition.PurchasedAt, out var parsedAt))
    {
        purchasedAtUtc = parsedAt.UtcDateTime;
        return true;
    }

    purchasedAtUtc = default;
    return false;
}

static string NormalizeSpendBeeReceiptPart(string? value) => string.Join(
    ' ',
    (value ?? string.Empty)
        .Trim()
        .ToLowerInvariant()
        .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || character is '.' or ':' or '-' or '+')
        .ToArray()
        .AsSpan()
        .ToString()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries));

static string NormalizeSpendBeeAddressForDuplicate(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var normalized = NormalizeMerchantName(value)
        .Replace("new zealand", "", StringComparison.Ordinal)
        .Replace("nz", "", StringComparison.Ordinal)
        .Replace("auckland", "", StringComparison.Ordinal);
    return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

static string BuildSpendBeeMerchantCoverUrlSql(string merchantAlias)
{
    return $"""
        COALESCE(
            NULLIF({merchantAlias}.AiCoverImageUrl, ''),
            NULLIF({merchantAlias}.GooglePhotoUri, ''),
            (
                SELECT fallbackPhoto.DisplayImageUrl
                FROM bee_SpendBeeMerchantPhoto AS fallbackPhoto
                LEFT JOIN bee_SpendBeeMerchantPhotoLike AS fallbackLike
                    ON fallbackLike.PhotoId = fallbackPhoto.id
                WHERE fallbackPhoto.ProjectId = {merchantAlias}.ProjectId
                    AND fallbackPhoto.MerchantId = {merchantAlias}.id
                    AND fallbackPhoto.Status = 'Ready'
                    AND fallbackPhoto.DisplayImageUrl IS NOT NULL
                    AND fallbackPhoto.DisplayImageUrl <> ''
                    AND LOWER(COALESCE(fallbackPhoto.Category, 'group')) = 'group'
                GROUP BY fallbackPhoto.id, fallbackPhoto.DisplayImageUrl, fallbackPhoto.CreatedAtUtc
                ORDER BY COUNT(fallbackLike.AppUserId) DESC, fallbackPhoto.CreatedAtUtc DESC, fallbackPhoto.id DESC
                LIMIT 1
            )
        )
        """;
}

static async Task<string?> FindSpendBeeMerchantCoverUrlAsync(
    MySqlConnection connection,
    long merchantId,
    CancellationToken cancellationToken)
{
    var coverUrlSql = BuildSpendBeeMerchantCoverUrlSql("merchant");
    var sql = $"""
        SELECT {coverUrlSql} AS CoverUrl
        FROM bee_SpendBeeMerchant AS merchant
        WHERE merchant.id = @MerchantId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    return await command.ExecuteScalarAsync(cancellationToken) as string;
}

static async Task<IReadOnlyList<SpendBeeNearbyLocalMerchant>> LoadSpendBeeNearbyLocalMerchantsAsync(
    MySqlConnection connection,
    int projectId,
    double latitude,
    double longitude,
    double radiusMeters,
    string publicRequestBaseUrl,
    CancellationToken cancellationToken)
{
    var coverUrlSql = BuildSpendBeeMerchantCoverUrlSql("merchant");
    var sql = $"""
        SELECT merchant.id, merchant.GooglePlaceId, merchant.Name, merchant.Address,
            merchant.PrimaryType, merchant.Latitude, merchant.Longitude, merchant.Rating,
            merchant.UserRatingCount, merchant.GoogleMapsUri, merchant.WebsiteUrl,
            merchant.PhoneNumber, merchant.SyncStatus,
            {coverUrlSql} AS CoverImageUrl,
            (6371000 * 2 * ASIN(SQRT(
                POWER(SIN((RADIANS(merchant.Latitude) - RADIANS(@Latitude)) / 2), 2) +
                COS(RADIANS(@Latitude)) * COS(RADIANS(merchant.Latitude)) *
                POWER(SIN((RADIANS(merchant.Longitude) - RADIANS(@Longitude)) / 2), 2)
            ))) AS DistanceMeters
        FROM bee_SpendBeeMerchant AS merchant
        WHERE merchant.ProjectId = @ProjectId
            AND merchant.Latitude IS NOT NULL
            AND merchant.Longitude IS NOT NULL
        HAVING DistanceMeters <= @RadiusMeters
        ORDER BY DistanceMeters ASC, merchant.Name
        LIMIT 80;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@Latitude", MySqlDbType.Double).Value = latitude;
    command.Parameters.Add("@Longitude", MySqlDbType.Double).Value = longitude;
    command.Parameters.Add("@RadiusMeters", MySqlDbType.Double).Value = radiusMeters;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var results = new List<SpendBeeNearbyLocalMerchant>();
    while (await reader.ReadAsync(cancellationToken))
    {
        var id = reader.GetInt64(reader.GetOrdinal("id"));
        var coverImageUrl = reader["CoverImageUrl"] as string;
        results.Add(new SpendBeeNearbyLocalMerchant(
            id,
            reader["GooglePlaceId"] as string,
            reader["Name"] as string ?? string.Empty,
            reader["Address"] as string,
            reader["PrimaryType"] as string,
            reader.IsDBNull(reader.GetOrdinal("Latitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Latitude")),
            reader.IsDBNull(reader.GetOrdinal("Longitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Longitude")),
            reader.IsDBNull(reader.GetOrdinal("DistanceMeters")) ? null : reader.GetDouble(reader.GetOrdinal("DistanceMeters")),
            reader.IsDBNull(reader.GetOrdinal("Rating")) ? null : reader.GetDecimal(reader.GetOrdinal("Rating")),
            reader.IsDBNull(reader.GetOrdinal("UserRatingCount")) ? null : reader.GetInt32(reader.GetOrdinal("UserRatingCount")),
            string.IsNullOrWhiteSpace(coverImageUrl) ? null : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/merchants/{id}/cover"),
            reader["GoogleMapsUri"] as string,
            reader["WebsiteUrl"] as string,
            reader["PhoneNumber"] as string,
            reader["SyncStatus"] as string ?? "LocalOnly"));
    }

    return results;
}

static async Task<IReadOnlyList<SpendBeeMerchantRecord>> LoadSpendBeeMerchantsForCoverBackfillAsync(
    MySqlConnection connection,
    int projectId,
    int limit,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, GooglePlaceId, Name, Address, PrimaryType, Latitude, Longitude,
            GooglePhotoUri, AiCoverImageUrl, CoverSource, CoverCategory, StreetViewImageUrl, SyncStatus
        FROM bee_SpendBeeMerchant
        WHERE ProjectId = @ProjectId
            AND (
                Latitude IS NULL
                OR Longitude IS NULL
                OR AiCoverImageUrl IS NULL
                OR AiCoverImageUrl = ''
                OR CoverSource IS NULL
                OR CoverSource <> 'GoogleStreetViewCartoon'
            )
        ORDER BY UpdatedAtUtc DESC, id DESC
        LIMIT @Limit;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@Limit", MySqlDbType.Int32).Value = Math.Clamp(limit, 1, 50);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var merchants = new List<SpendBeeMerchantRecord>();
    while (await reader.ReadAsync(cancellationToken))
    {
        merchants.Add(new SpendBeeMerchantRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader["GooglePlaceId"] as string,
            reader["Name"] as string ?? string.Empty,
            reader["Address"] as string,
            reader["PrimaryType"] as string,
            reader.IsDBNull(reader.GetOrdinal("Latitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Latitude")),
            reader.IsDBNull(reader.GetOrdinal("Longitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Longitude")),
            reader["GooglePhotoUri"] as string,
            reader["AiCoverImageUrl"] as string,
            reader["CoverSource"] as string,
            reader["CoverCategory"] as string,
            reader["StreetViewImageUrl"] as string,
            reader["SyncStatus"] as string ?? "LocalOnly"));
    }

    return merchants;
}

static async Task<SpendBeeReceiptDuplicate?> FindDuplicateSpendBeeReceiptAsync(
    MySqlConnection connection,
    int projectId,
    string? imageSetHash,
    string? canonicalHash,
    long? excludeReceiptId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT receipt.id, receipt.AppUserId, receipt.Status, receipt.MerchantName, receipt.MerchantAddress,
            receipt.PurchasedAtUtc, receipt.Total, receipt.Currency, user.DisplayName, user.Email
        FROM bee_SpendBeeReceipt AS receipt
        INNER JOIN bee_AppUser AS user ON user.id = receipt.AppUserId
        WHERE receipt.ProjectId = @ProjectId
            AND (@ExcludeReceiptId IS NULL OR receipt.id <> @ExcludeReceiptId)
            AND (
                (@ImageSetHash IS NOT NULL AND receipt.ReceiptImageSetHash = @ImageSetHash)
                OR (@CanonicalHash IS NOT NULL AND receipt.ReceiptCanonicalHash = @CanonicalHash)
            )
        ORDER BY receipt.CreatedAtUtc ASC, receipt.id ASC
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@ImageSetHash", MySqlDbType.VarChar, 128).Value = DbNullable(imageSetHash);
    command.Parameters.Add("@CanonicalHash", MySqlDbType.VarChar, 128).Value = DbNullable(canonicalHash);
    command.Parameters.Add("@ExcludeReceiptId", MySqlDbType.Int64).Value = (object?)excludeReceiptId ?? DBNull.Value;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new SpendBeeReceiptDuplicate(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader.GetInt32(reader.GetOrdinal("AppUserId")),
        reader["Status"] as string ?? string.Empty,
        reader["DisplayName"] as string ?? string.Empty,
        reader["Email"] as string,
        reader["MerchantName"] as string,
        reader["MerchantAddress"] as string,
        reader.IsDBNull(reader.GetOrdinal("PurchasedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PurchasedAtUtc")),
        reader.IsDBNull(reader.GetOrdinal("Total")) ? null : reader.GetDecimal(reader.GetOrdinal("Total")),
        reader["Currency"] as string);
}

static async Task<SpendBeeReceiptDuplicate?> FindSoftDuplicateSpendBeeReceiptAsync(
    MySqlConnection connection,
    int projectId,
    SpendBeeReceiptRecognition recognition,
    long? excludeReceiptId,
    CancellationToken cancellationToken)
{
    if (recognition.Total is null || !TryParseSpendBeePurchasedAtUtc(recognition, out var purchasedAtUtc))
    {
        return null;
    }

    var targetFingerprint = ComputeSpendBeeReceiptSoftFingerprint(recognition);
    const string sql = """
        SELECT receipt.id, receipt.AppUserId, receipt.Status, receipt.MerchantName, receipt.MerchantAddress,
            receipt.PurchasedAtUtc, receipt.Total, receipt.Currency, receipt.RawOcrJson, user.DisplayName, user.Email
        FROM bee_SpendBeeReceipt AS receipt
        INNER JOIN bee_AppUser AS user ON user.id = receipt.AppUserId
        WHERE receipt.ProjectId = @ProjectId
            AND (@ExcludeReceiptId IS NULL OR receipt.id <> @ExcludeReceiptId)
            AND receipt.Total = @Total
            AND COALESCE(receipt.Currency, '') = COALESCE(@Currency, '')
            AND receipt.PurchasedAtUtc IS NOT NULL
            AND ABS(TIMESTAMPDIFF(HOUR, receipt.PurchasedAtUtc, @PurchasedAtUtc)) <= 36
        ORDER BY receipt.CreatedAtUtc ASC, receipt.id ASC
        LIMIT 25;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@ExcludeReceiptId", MySqlDbType.Int64).Value = (object?)excludeReceiptId ?? DBNull.Value;
    command.Parameters.Add("@Total", MySqlDbType.Decimal).Value = recognition.Total.Value;
    command.Parameters.Add("@Currency", MySqlDbType.VarChar, 8).Value = DbNullable(NormalizeBounded(recognition.Currency, 8));
    command.Parameters.Add("@PurchasedAtUtc", MySqlDbType.DateTime).Value = purchasedAtUtc;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var rawOcr = reader["RawOcrJson"] as string;
        if (string.IsNullOrWhiteSpace(rawOcr))
        {
            continue;
        }

        SpendBeeReceiptRecognition? existingRecognition;
        try
        {
            existingRecognition = JsonSerializer.Deserialize<SpendBeeReceiptRecognition>(rawOcr, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            continue;
        }

        if (existingRecognition is null ||
            !string.Equals(targetFingerprint, ComputeSpendBeeReceiptSoftFingerprint(existingRecognition), StringComparison.Ordinal))
        {
            continue;
        }

        return new SpendBeeReceiptDuplicate(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("AppUserId")),
            reader["Status"] as string ?? string.Empty,
            reader["DisplayName"] as string ?? string.Empty,
            reader["Email"] as string,
            reader["MerchantName"] as string,
            reader["MerchantAddress"] as string,
            reader.IsDBNull(reader.GetOrdinal("PurchasedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PurchasedAtUtc")),
            reader.IsDBNull(reader.GetOrdinal("Total")) ? null : reader.GetDecimal(reader.GetOrdinal("Total")),
            reader["Currency"] as string);
    }

    return null;
}

static object BuildDuplicateSpendBeeReceiptResponse(SpendBeeReceiptDuplicate duplicate)
{
    return new
    {
        code = "receipt_already_bound",
        message = "This receipt is already bound to one SpendBee user. Other users should relate to it through AA split bill flow.",
        nextAction = "CreateSplitBillParticipant",
        duplicateReceipt = new
        {
            receiptId = duplicate.ReceiptId,
            appUserId = duplicate.AppUserId,
            status = duplicate.Status,
            displayName = duplicate.DisplayName,
            email = duplicate.Email,
            merchantName = duplicate.MerchantName,
            merchantAddress = duplicate.MerchantAddress,
            purchasedAtUtc = duplicate.PurchasedAtUtc?.ToString("O"),
            total = duplicate.Total,
            currency = duplicate.Currency
        }
    };
}

static bool CanRetrySpendBeeReceipt(SpendBeeReceiptDuplicate duplicate, int currentAppUserId)
{
    return duplicate.AppUserId == currentAppUserId &&
        (string.Equals(duplicate.Status, "ReviewRequired", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(duplicate.Status, "RecognitionFailed", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(duplicate.Status, "Processing", StringComparison.OrdinalIgnoreCase));
}

static async Task PrepareSpendBeeReceiptRetryAsync(
    MySqlConnection connection,
    long receiptId,
    string imageSetHash,
    IReadOnlyList<SpendBeeUploadedReceiptImage> uploadedImages,
    CancellationToken cancellationToken)
{
    const string deleteImagesSql = "DELETE FROM bee_SpendBeeReceiptImage WHERE ReceiptId = @ReceiptId;";
    await using (var deleteImagesCommand = new MySqlCommand(deleteImagesSql, connection))
    {
        deleteImagesCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        await deleteImagesCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    const string deleteItemsSql = "DELETE FROM bee_SpendBeeReceiptLineItem WHERE ReceiptId = @ReceiptId;";
    await using (var deleteItemsCommand = new MySqlCommand(deleteItemsSql, connection))
    {
        deleteItemsCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        await deleteItemsCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    const string updateSql = """
        UPDATE bee_SpendBeeReceipt
        SET ReceiptImageSetHash = @ReceiptImageSetHash,
            ReceiptCanonicalHash = NULL,
            Status = 'Processing',
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @ReceiptId;
        """;
    await using (var updateCommand = new MySqlCommand(updateSql, connection))
    {
        updateCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        updateCommand.Parameters.Add("@ReceiptImageSetHash", MySqlDbType.VarChar, 128).Value = imageSetHash;
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    await InsertSpendBeeReceiptImagesAsync(connection, receiptId, uploadedImages, cancellationToken);
}

static async Task InsertSpendBeeReceiptImagesAsync(
    MySqlConnection connection,
    long receiptId,
    IReadOnlyList<SpendBeeUploadedReceiptImage> uploadedImages,
    CancellationToken cancellationToken)
{
    foreach (var image in uploadedImages)
    {
        const string imageSql = """
            INSERT INTO bee_SpendBeeReceiptImage (ReceiptId, ImageUrl, ContentType, SortOrder)
            VALUES (@ReceiptId, @ImageUrl, @ContentType, @SortOrder);
            """;
        await using var imageCommand = new MySqlCommand(imageSql, connection);
        imageCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        imageCommand.Parameters.Add("@ImageUrl", MySqlDbType.VarChar, 800).Value = image.Url;
        imageCommand.Parameters.Add("@ContentType", MySqlDbType.VarChar, 80).Value = image.ContentType;
        imageCommand.Parameters.Add("@SortOrder", MySqlDbType.Int32).Value = image.SortOrder;
        await imageCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}

static IResult BuildSpendBeeDuplicateUploadResult(SpendBeeReceiptDuplicate duplicate, int currentAppUserId)
{
    var response = BuildDuplicateSpendBeeReceiptResponse(duplicate);
    return duplicate.AppUserId == currentAppUserId
        ? Results.Ok(new
        {
            success = true,
            duplicate = true,
            code = "receipt_already_uploaded",
            message = "This receipt is already uploaded by the current user.",
            receiptId = duplicate.ReceiptId,
            duplicateReceipt = duplicate
        })
        : Results.Conflict(response);
}

static async Task DeleteSpendBeeReceiptAsync(
    MySqlConnection connection,
    long receiptId,
    CancellationToken cancellationToken)
{
    const string sql = "DELETE FROM bee_SpendBeeReceipt WHERE id = @ReceiptId;";
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<object?> EnsureSpendBeeMerchantForReceiptAsync(
    MySqlConnection connection,
    long receiptId,
    int projectId,
    SpendBeeReceiptRecognition recognition,
    string publicRequestBaseUrl,
    IConfiguration configuration,
    HttpClient httpClient,
    IFileStorageService storage,
    OpenAIOptions openAIOptions,
    CancellationToken cancellationToken)
{
    var merchantName = NormalizeBounded(recognition.MerchantName, 220);
    if (string.IsNullOrWhiteSpace(merchantName))
    {
        return null;
    }

    var merchantAddress = NormalizeBounded(recognition.MerchantAddress, 600);
    var normalizedName = NormalizeMerchantName(merchantName);
    var merchant = await FindSpendBeeMerchantAsync(connection, projectId, null, normalizedName, merchantAddress, cancellationToken);
    var googlePlace = await FetchGooglePlaceForMerchantAsync(configuration, httpClient, merchantName, merchantAddress, cancellationToken);
    if (googlePlace is not null)
    {
        merchant = await FindSpendBeeMerchantAsync(connection, projectId, googlePlace.PlaceId, NormalizeMerchantName(googlePlace.Name), googlePlace.Address, cancellationToken)
            ?? merchant;
    }

    if (merchant is null)
    {
        merchant = await InsertSpendBeeMerchantAsync(connection, projectId, merchantName, normalizedName, merchantAddress, googlePlace, cancellationToken);
    }
    else if (googlePlace is not null)
    {
        await UpdateSpendBeeMerchantFromGoogleAsync(connection, merchant.Id, googlePlace, cancellationToken);
        merchant = merchant with
        {
            Name = googlePlace.Name ?? merchant.Name,
            Address = googlePlace.Address ?? merchant.Address,
            GooglePlaceId = googlePlace.PlaceId,
            GooglePhotoUri = googlePlace.PhotoUri,
            AiCoverImageUrl = merchant.AiCoverImageUrl,
            PrimaryType = googlePlace.PrimaryType ?? merchant.PrimaryType,
            Latitude = googlePlace.Latitude ?? merchant.Latitude,
            Longitude = googlePlace.Longitude ?? merchant.Longitude
        };
    }

    if (string.IsNullOrWhiteSpace(merchant.AiCoverImageUrl))
    {
        var cover = await TryGenerateSpendBeeMerchantCoverAsync(configuration, connection, projectId, merchant, googlePlace, httpClient, storage, openAIOptions, cancellationToken);
        if (cover is not null)
        {
            await UpdateSpendBeeMerchantAiCoverAsync(connection, merchant.Id, cover.Url, cover.Prompt, cover.Source, cover.Category, cover.StreetViewImageUrl, cancellationToken);
            if (cover.Latitude is not null && cover.Longitude is not null && (merchant.Latitude is null || merchant.Longitude is null))
            {
                await UpdateSpendBeeMerchantCoordinatesAsync(connection, merchant.Id, cover.Latitude.Value, cover.Longitude.Value, cancellationToken);
            }

            merchant = merchant with
            {
                AiCoverImageUrl = cover.Url,
                CoverSource = cover.Source,
                CoverCategory = cover.Category,
                StreetViewImageUrl = cover.StreetViewImageUrl,
                Latitude = cover.Latitude ?? merchant.Latitude,
                Longitude = cover.Longitude ?? merchant.Longitude
            };
        }
    }

    const string receiptSql = """
        UPDATE bee_SpendBeeReceipt
        SET MerchantId = @MerchantId,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @ReceiptId;
        """;
    await using var receiptCommand = new MySqlCommand(receiptSql, connection);
    receiptCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchant.Id;
    receiptCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
    await receiptCommand.ExecuteNonQueryAsync(cancellationToken);

    var coverImageUrl = await FindSpendBeeMerchantCoverUrlAsync(connection, merchant.Id, cancellationToken);
    var coverImageApiUrl = string.IsNullOrWhiteSpace(coverImageUrl)
        ? null
        : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/merchants/{merchant.Id}/cover");
    return new
    {
        id = merchant.Id,
        name = merchant.Name,
        address = merchant.Address,
        googlePlaceId = merchant.GooglePlaceId,
        coverImageUrl = coverImageApiUrl,
        coverImageApiUrl,
        syncStatus = merchant.SyncStatus
    };
}

static async Task<SpendBeeMerchantRecord?> FindSpendBeeMerchantAsync(
    MySqlConnection connection,
    int projectId,
    string? googlePlaceId,
    string normalizedName,
    string? address,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, GooglePlaceId, Name, Address, PrimaryType, Latitude, Longitude,
            GooglePhotoUri, AiCoverImageUrl, CoverSource, CoverCategory, StreetViewImageUrl, SyncStatus
        FROM bee_SpendBeeMerchant
        WHERE ProjectId = @ProjectId
            AND (
                (@GooglePlaceId IS NOT NULL AND GooglePlaceId = @GooglePlaceId)
                OR (
                    NormalizedName = @NormalizedName
                    AND (
                        @Address IS NULL
                        OR Address IS NULL
                        OR Address = @Address
                        OR Address LIKE CONCAT('%', @Address, '%')
                        OR @Address LIKE CONCAT('%', Address, '%')
                    )
                )
                OR (
                    @Address IS NOT NULL
                    AND Address IS NOT NULL
                    AND (
                        Address = @Address
                        OR Address LIKE CONCAT('%', @Address, '%')
                        OR @Address LIKE CONCAT('%', Address, '%')
                    )
                    AND (
                        NormalizedName = @NormalizedName
                        OR NormalizedName LIKE CONCAT('%', @NormalizedName, '%')
                        OR @NormalizedName LIKE CONCAT('%', NormalizedName, '%')
                    )
                )
            )
        ORDER BY CASE WHEN @GooglePlaceId IS NOT NULL AND GooglePlaceId = @GooglePlaceId THEN 0 ELSE 1 END, id
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@GooglePlaceId", MySqlDbType.VarChar, 160).Value = DbNullable(googlePlaceId);
    command.Parameters.Add("@NormalizedName", MySqlDbType.VarChar, 220).Value = normalizedName;
    command.Parameters.Add("@Address", MySqlDbType.VarChar, 600).Value = DbNullable(address);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new SpendBeeMerchantRecord(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader["GooglePlaceId"] as string,
        reader["Name"] as string ?? string.Empty,
        reader["Address"] as string,
        reader["PrimaryType"] as string,
        reader.IsDBNull(reader.GetOrdinal("Latitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Latitude")),
        reader.IsDBNull(reader.GetOrdinal("Longitude")) ? null : reader.GetDecimal(reader.GetOrdinal("Longitude")),
        reader["GooglePhotoUri"] as string,
        reader["AiCoverImageUrl"] as string,
        reader["CoverSource"] as string,
        reader["CoverCategory"] as string,
        reader["StreetViewImageUrl"] as string,
        reader["SyncStatus"] as string ?? "LocalOnly");
}

static async Task<SpendBeeGooglePlace?> FetchGooglePlaceForMerchantAsync(
    IConfiguration configuration,
    HttpClient httpClient,
    string merchantName,
    string? merchantAddress,
    CancellationToken cancellationToken)
{
    var apiKey = configuration["GoogleMaps:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return null;
    }

    foreach (var query in BuildSpendBeeGoogleMerchantSearchQueries(merchantName, merchantAddress))
    {
        var place = await FetchGoogleTextSearchPlaceAsync(apiKey, httpClient, query, cancellationToken);
        if (place is not null)
        {
            return place;
        }
    }

    return null;
}

static IReadOnlyList<string> BuildSpendBeeGoogleMerchantSearchQueries(string merchantName, string? merchantAddress)
{
    var queries = new List<string>();
    var name = merchantName.Trim();
    var address = NormalizeBounded(merchantAddress, 600);
    if (!string.IsNullOrWhiteSpace(address))
    {
        queries.Add($"{name}, {address}");
        queries.Add($"{name} {address}");
        queries.Add($"{address} {name}");
        queries.Add($"{address} restaurant");
    }

    queries.Add($"{name} Auckland NZ");
    queries.Add(name);
    return queries
        .Where(query => !string.IsNullOrWhiteSpace(query))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static async Task<SpendBeeGooglePlace?> FetchGoogleTextSearchPlaceAsync(
    string apiKey,
    HttpClient httpClient,
    string query,
    CancellationToken cancellationToken)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText");
    AddGoogleApiHeaders(
        request,
        apiKey,
        "places.id,places.name,places.displayName,places.formattedAddress,places.nationalPhoneNumber,places.websiteUri,places.googleMapsUri,places.primaryType,places.businessStatus,places.location,places.rating,places.userRatingCount,places.priceLevel,places.dineIn,places.takeout,places.editorialSummary,places.photos");
    request.Content = JsonContent.Create(new
    {
        textQuery = query,
        maxResultCount = 3,
        languageCode = "zh",
        regionCode = "NZ"
    });
    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return null;
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    if (!document.RootElement.TryGetProperty("places", out var places) ||
        places.ValueKind != JsonValueKind.Array ||
        places.GetArrayLength() == 0)
    {
        return null;
    }

    var place = places[0];
    var parsed = ParseGooglePlace(place);
    return await AddGooglePlacePhotoUriAsync(apiKey, parsed, httpClient, cancellationToken);
}

static async Task<IReadOnlyList<SpendBeeGooglePlace>> FetchGoogleNearbyPlacesAsync(
    IConfiguration configuration,
    HttpClient httpClient,
    double latitude,
    double longitude,
    double radiusMeters,
    int limit,
    CancellationToken cancellationToken)
{
    var apiKey = configuration["GoogleMaps:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return [];
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchNearby");
    AddGoogleApiHeaders(
        request,
        apiKey,
        "places.id,places.name,places.displayName,places.formattedAddress,places.nationalPhoneNumber,places.websiteUri,places.googleMapsUri,places.primaryType,places.businessStatus,places.location,places.rating,places.userRatingCount,places.priceLevel,places.dineIn,places.takeout,places.editorialSummary,places.photos");
    request.Content = JsonContent.Create(new
    {
        includedTypes = new[] { "restaurant", "cafe", "bakery", "meal_takeaway" },
        maxResultCount = Math.Clamp(limit, 1, 20),
        rankPreference = "DISTANCE",
        languageCode = "zh",
        regionCode = "NZ",
        locationRestriction = new
        {
            circle = new
            {
                center = new { latitude, longitude },
                radius = radiusMeters
            }
        }
    });

    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return [];
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    if (!document.RootElement.TryGetProperty("places", out var places) ||
        places.ValueKind != JsonValueKind.Array)
    {
        return [];
    }

    var results = new List<SpendBeeGooglePlace>();
    foreach (var place in places.EnumerateArray())
    {
        var parsed = ParseGooglePlace(place);
        if (parsed is null)
        {
            continue;
        }

        var placeWithPhoto = await AddGooglePlacePhotoUriAsync(apiKey, parsed, httpClient, cancellationToken);
        if (placeWithPhoto is not null)
        {
            results.Add(placeWithPhoto);
        }
    }

    return results;
}

static async Task<SpendBeeGooglePlace?> FetchGooglePlaceDetailsAsync(
    IConfiguration configuration,
    HttpClient httpClient,
    string googlePlaceId,
    CancellationToken cancellationToken)
{
    var apiKey = configuration["GoogleMaps:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return null;
    }

    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(googlePlaceId)}?languageCode=zh&regionCode=NZ");
    AddGoogleApiHeaders(
        request,
        apiKey,
        "id,name,displayName,formattedAddress,nationalPhoneNumber,websiteUri,googleMapsUri,primaryType,businessStatus,location,rating,userRatingCount,priceLevel,dineIn,takeout,editorialSummary,photos");
    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return null;
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var parsed = ParseGooglePlace(document.RootElement);
    return await AddGooglePlacePhotoUriAsync(apiKey, parsed, httpClient, cancellationToken);
}

static async Task<SpendBeeGooglePlace?> AddGooglePlacePhotoUriAsync(
    string apiKey,
    SpendBeeGooglePlace? parsed,
    HttpClient httpClient,
    CancellationToken cancellationToken)
{
    if (parsed is null || string.IsNullOrWhiteSpace(parsed.PhotoName))
    {
        return parsed;
    }

    var photoUri = await FetchGooglePhotoUriAsync(apiKey, parsed.PhotoName, httpClient, cancellationToken);
    return parsed with { PhotoUri = photoUri };
}

static void AddGoogleApiHeaders(HttpRequestMessage request, string? apiKey = null, string? fieldMask = null)
{
    request.Headers.Referrer = new Uri("https://console.sentribee.ai/");
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", apiKey);
    }

    if (!string.IsNullOrWhiteSpace(fieldMask))
    {
        request.Headers.TryAddWithoutValidation("X-Goog-FieldMask", fieldMask);
    }
}

static SpendBeeGooglePlace? ParseGooglePlace(JsonElement place)
{
    var placeId = ReadJsonString(place, "id");
    var name = ReadNestedLocalizedText(place, "displayName") ?? ReadJsonString(place, "name");
    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(placeId))
    {
        return null;
    }

    string? photoName = null;
    string? attributionsJson = null;
    if (place.TryGetProperty("photos", out var photos) &&
        photos.ValueKind == JsonValueKind.Array &&
        photos.GetArrayLength() > 0)
    {
        var firstPhoto = photos[0];
        photoName = ReadJsonString(firstPhoto, "name");
        if (firstPhoto.TryGetProperty("authorAttributions", out var attributions))
        {
            attributionsJson = attributions.GetRawText();
        }
    }

    decimal? latitude = null;
    decimal? longitude = null;
    if (place.TryGetProperty("location", out var location))
    {
        latitude = ReadJsonDecimal(location, "latitude");
        longitude = ReadJsonDecimal(location, "longitude");
    }

    return new SpendBeeGooglePlace(
        placeId,
        ReadJsonString(place, "name"),
        NormalizeBounded(name, 220) ?? "Unknown merchant",
        NormalizeBounded(ReadJsonString(place, "formattedAddress"), 600),
        NormalizeBounded(ReadJsonString(place, "nationalPhoneNumber"), 80),
        NormalizeBounded(ReadJsonString(place, "websiteUri"), 700),
        NormalizeBounded(ReadJsonString(place, "googleMapsUri"), 700),
        NormalizeBounded(ReadJsonString(place, "primaryType"), 120),
        NormalizeBounded(ReadJsonString(place, "businessStatus"), 80),
        latitude,
        longitude,
        ReadJsonDecimal(place, "rating"),
        ReadJsonInt(place, "userRatingCount"),
        NormalizeBounded(ReadJsonString(place, "priceLevel"), 80),
        ReadJsonBool(place, "dineIn"),
        ReadJsonBool(place, "takeout"),
        ReadNestedLocalizedText(place, "editorialSummary"),
        photoName,
        null,
        attributionsJson,
        place.GetRawText());
}

static async Task<string?> FetchGooglePhotoUriAsync(
    string apiKey,
    string photoName,
    HttpClient httpClient,
    CancellationToken cancellationToken)
{
    var uri = $"https://places.googleapis.com/v1/{photoName}/media?maxWidthPx=1200&skipHttpRedirect=true&key={Uri.EscapeDataString(apiKey)}";
    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
    AddGoogleApiHeaders(request);
    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return null;
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    return ReadJsonString(document.RootElement, "photoUri");
}

static async Task<SpendBeeMerchantRecord> InsertSpendBeeMerchantAsync(
    MySqlConnection connection,
    int projectId,
    string merchantName,
    string normalizedName,
    string? merchantAddress,
    SpendBeeGooglePlace? googlePlace,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_SpendBeeMerchant
            (ProjectId, GooglePlaceId, GooglePlaceResourceName, Name, NormalizedName, Address, PhoneNumber,
             WebsiteUrl, GoogleMapsUri, PrimaryType, BusinessStatus, Latitude, Longitude, Rating, UserRatingCount,
             PriceLevel, DineIn, Takeout, GooglePhotoName, GooglePhotoUri, GooglePhotoAttributionsJson,
             SourceJson, SyncStatus, LastGoogleSyncAtUtc)
        VALUES
            (@ProjectId, @GooglePlaceId, @GooglePlaceResourceName, @Name, @NormalizedName, @Address, @PhoneNumber,
             @WebsiteUrl, @GoogleMapsUri, @PrimaryType, @BusinessStatus, @Latitude, @Longitude, @Rating, @UserRatingCount,
             @PriceLevel, @DineIn, @Takeout, @GooglePhotoName, @GooglePhotoUri, @GooglePhotoAttributionsJson,
             @SourceJson, @SyncStatus, @LastGoogleSyncAtUtc);
        SELECT LAST_INSERT_ID();
        """;
    await using var command = new MySqlCommand(sql, connection);
    AddSpendBeeMerchantParameters(command, projectId, merchantName, normalizedName, merchantAddress, googlePlace);
    var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    return new SpendBeeMerchantRecord(
        id,
        googlePlace?.PlaceId,
        googlePlace?.Name ?? merchantName,
        googlePlace?.Address ?? merchantAddress,
        googlePlace?.PrimaryType,
        googlePlace?.Latitude,
        googlePlace?.Longitude,
        googlePlace?.PhotoUri,
        null,
        null,
        null,
        null,
        googlePlace is null ? "LocalOnly" : "GoogleMatched");
}

static async Task UpdateSpendBeeMerchantFromGoogleAsync(
    MySqlConnection connection,
    long merchantId,
    SpendBeeGooglePlace googlePlace,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeMerchant
        SET GooglePlaceId = @GooglePlaceId,
            GooglePlaceResourceName = @GooglePlaceResourceName,
            Name = @Name,
            NormalizedName = @NormalizedName,
            Address = @Address,
            PhoneNumber = @PhoneNumber,
            WebsiteUrl = @WebsiteUrl,
            GoogleMapsUri = @GoogleMapsUri,
            PrimaryType = @PrimaryType,
            BusinessStatus = @BusinessStatus,
            Latitude = @Latitude,
            Longitude = @Longitude,
            Rating = @Rating,
            UserRatingCount = @UserRatingCount,
            PriceLevel = @PriceLevel,
            DineIn = @DineIn,
            Takeout = @Takeout,
            GooglePhotoName = @GooglePhotoName,
            GooglePhotoUri = @GooglePhotoUri,
            GooglePhotoAttributionsJson = @GooglePhotoAttributionsJson,
            SourceJson = @SourceJson,
            SyncStatus = 'GoogleMatched',
            LastGoogleSyncAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @MerchantId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    AddSpendBeeMerchantParameters(command, 0, googlePlace.Name, NormalizeMerchantName(googlePlace.Name), googlePlace.Address, googlePlace);
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static void AddSpendBeeMerchantParameters(
    MySqlCommand command,
    int projectId,
    string merchantName,
    string normalizedName,
    string? merchantAddress,
    SpendBeeGooglePlace? googlePlace)
{
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@GooglePlaceId", MySqlDbType.VarChar, 160).Value = DbNullable(googlePlace?.PlaceId);
    command.Parameters.Add("@GooglePlaceResourceName", MySqlDbType.VarChar, 240).Value = DbNullable(googlePlace?.ResourceName);
    command.Parameters.Add("@Name", MySqlDbType.VarChar, 220).Value = NormalizeBounded(googlePlace?.Name ?? merchantName, 220) ?? merchantName;
    command.Parameters.Add("@NormalizedName", MySqlDbType.VarChar, 220).Value = NormalizeMerchantName(googlePlace?.Name ?? normalizedName);
    command.Parameters.Add("@Address", MySqlDbType.VarChar, 600).Value = DbNullable(googlePlace?.Address ?? merchantAddress);
    command.Parameters.Add("@PhoneNumber", MySqlDbType.VarChar, 80).Value = DbNullable(googlePlace?.PhoneNumber);
    command.Parameters.Add("@WebsiteUrl", MySqlDbType.VarChar, 700).Value = DbNullable(googlePlace?.WebsiteUrl);
    command.Parameters.Add("@GoogleMapsUri", MySqlDbType.VarChar, 700).Value = DbNullable(googlePlace?.GoogleMapsUri);
    command.Parameters.Add("@PrimaryType", MySqlDbType.VarChar, 120).Value = DbNullable(googlePlace?.PrimaryType);
    command.Parameters.Add("@BusinessStatus", MySqlDbType.VarChar, 80).Value = DbNullable(googlePlace?.BusinessStatus);
    command.Parameters.Add("@Latitude", MySqlDbType.Decimal).Value = (object?)googlePlace?.Latitude ?? DBNull.Value;
    command.Parameters.Add("@Longitude", MySqlDbType.Decimal).Value = (object?)googlePlace?.Longitude ?? DBNull.Value;
    command.Parameters.Add("@Rating", MySqlDbType.Decimal).Value = (object?)googlePlace?.Rating ?? DBNull.Value;
    command.Parameters.Add("@UserRatingCount", MySqlDbType.Int32).Value = (object?)googlePlace?.UserRatingCount ?? DBNull.Value;
    command.Parameters.Add("@PriceLevel", MySqlDbType.VarChar, 80).Value = DbNullable(googlePlace?.PriceLevel);
    command.Parameters.Add("@DineIn", MySqlDbType.Bit).Value = (object?)googlePlace?.DineIn ?? DBNull.Value;
    command.Parameters.Add("@Takeout", MySqlDbType.Bit).Value = (object?)googlePlace?.Takeout ?? DBNull.Value;
    command.Parameters.Add("@GooglePhotoName", MySqlDbType.VarChar, 500).Value = DbNullable(googlePlace?.PhotoName);
    command.Parameters.Add("@GooglePhotoUri", MySqlDbType.VarChar, 1000).Value = DbNullable(googlePlace?.PhotoUri);
    command.Parameters.Add("@GooglePhotoAttributionsJson", MySqlDbType.JSON).Value = googlePlace?.PhotoAttributionsJson ?? "[]";
    command.Parameters.Add("@SourceJson", MySqlDbType.JSON).Value = googlePlace?.SourceJson ?? "{}";
    command.Parameters.Add("@SyncStatus", MySqlDbType.VarChar, 40).Value = googlePlace is null ? "LocalOnly" : "GoogleMatched";
    command.Parameters.Add("@LastGoogleSyncAtUtc", MySqlDbType.DateTime).Value = googlePlace is null ? DBNull.Value : DateTime.UtcNow;
}

static async Task<SpendBeeMerchantCoverResult?> TryGenerateSpendBeeMerchantCoverAsync(
    IConfiguration configuration,
    MySqlConnection connection,
    int projectId,
    SpendBeeMerchantRecord merchant,
    SpendBeeGooglePlace? googlePlace,
    HttpClient httpClient,
    IFileStorageService storage,
    OpenAIOptions options,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        return null;
    }

    var category = NormalizeSpendBeeMerchantCoverCategory(googlePlace?.PrimaryType ?? merchant.PrimaryType);
    var streetView = await TryFetchSpendBeeMerchantStreetViewAsync(configuration, httpClient, googlePlace, merchant, storage, cancellationToken);
    if (streetView is not null)
    {
        var prompt = BuildSpendBeeMerchantStreetViewCoverPrompt(merchant, googlePlace, category);
        var streetViewCoverUrl = await TryCreateSpendBeeImageEditAsync(
            streetView.Bytes,
            "image/jpeg",
            "streetview.jpg",
            prompt,
            httpClient,
            storage,
            options,
            $"spendbee/merchants/{merchant.Id}/covers",
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(streetViewCoverUrl))
        {
            return new SpendBeeMerchantCoverResult(
                streetViewCoverUrl,
                "GoogleStreetViewCartoon",
                category,
                prompt,
                streetView.StoredUrl,
                streetView.Latitude,
                streetView.Longitude);
        }
    }

    var sharedCoverUrl = await FindSpendBeeSharedIndustryCoverAsync(connection, projectId, category, cancellationToken);
    if (!string.IsNullOrWhiteSpace(sharedCoverUrl))
    {
        return new SpendBeeMerchantCoverResult(
            sharedCoverUrl,
            "IndustryFallback",
            category,
            BuildSpendBeeMerchantIndustryFallbackCoverPrompt(category),
            null,
            null,
            null);
    }

    var fallbackPrompt = BuildSpendBeeMerchantIndustryFallbackCoverPrompt(category);
    var fallbackCoverUrl = await TryCreateSpendBeeImageGenerationAsync(
        fallbackPrompt,
        httpClient,
        storage,
        options,
        $"spendbee/merchants/shared-covers/{category}",
        cancellationToken);
    return string.IsNullOrWhiteSpace(fallbackCoverUrl)
        ? null
        : new SpendBeeMerchantCoverResult(fallbackCoverUrl, "IndustryFallback", category, fallbackPrompt, null, null, null);
}

static async Task<string?> TryCreateSpendBeeImageGenerationAsync(
    string prompt,
    HttpClient httpClient,
    IFileStorageService storage,
    OpenAIOptions options,
    string storagePrefix,
    CancellationToken cancellationToken)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/images/generations");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    request.Content = JsonContent.Create(new
    {
        model = string.IsNullOrWhiteSpace(options.ImageModel) ? "gpt-image-1.5" : options.ImageModel,
        prompt,
        size = "1024x1024",
        quality = "low",
        output_format = "jpeg",
        output_compression = 62
    });
    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return null;
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var data = document.RootElement.TryGetProperty("data", out var dataElement) &&
        dataElement.ValueKind == JsonValueKind.Array &&
        dataElement.GetArrayLength() > 0
        ? dataElement[0]
        : default;
    var b64 = data.ValueKind == JsonValueKind.Object ? ReadJsonString(data, "b64_json") : null;
    if (string.IsNullOrWhiteSpace(b64))
    {
        var url = data.ValueKind == JsonValueKind.Object ? ReadJsonString(data, "url") : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        using var remote = await httpClient.GetAsync(url, cancellationToken);
        if (!remote.IsSuccessStatusCode)
        {
            return null;
        }

        var remoteBytes = await remote.Content.ReadAsByteArrayAsync(cancellationToken);
        await using var remoteStream = new MemoryStream(remoteBytes);
        var remoteStored = await storage.UploadAsync(remoteStream, "image/jpeg", ".jpg", storagePrefix, cancellationToken);
        return remoteStored.PublicUrl;
    }

    await using var stream = new MemoryStream(Convert.FromBase64String(b64));
    var stored = await storage.UploadAsync(stream, "image/jpeg", ".jpg", storagePrefix, cancellationToken);
    return stored.PublicUrl;
}

static async Task<string?> TryCreateSpendBeeImageEditAsync(
    byte[] sourceBytes,
    string sourceContentType,
    string fileName,
    string prompt,
    HttpClient httpClient,
    IFileStorageService storage,
    OpenAIOptions options,
    string storagePrefix,
    CancellationToken cancellationToken)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/images/edits");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    using var form = new MultipartFormDataContent();
    form.Add(new StringContent(string.IsNullOrWhiteSpace(options.ImageModel) ? "gpt-image-1.5" : options.ImageModel), "model");
    form.Add(new StringContent(prompt), "prompt");
    form.Add(new StringContent("1024x1024"), "size");
    form.Add(new StringContent("low"), "quality");
    form.Add(new StringContent("jpeg"), "output_format");
    form.Add(new StringContent("62"), "output_compression");
    var imageContent = new ByteArrayContent(sourceBytes);
    imageContent.Headers.ContentType = new MediaTypeHeaderValue(sourceContentType);
    form.Add(imageContent, "image", fileName);
    request.Content = form;

    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return null;
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var data = document.RootElement.TryGetProperty("data", out var dataElement) &&
        dataElement.ValueKind == JsonValueKind.Array &&
        dataElement.GetArrayLength() > 0
        ? dataElement[0]
        : default;
    var b64 = data.ValueKind == JsonValueKind.Object ? ReadJsonString(data, "b64_json") : null;
    if (string.IsNullOrWhiteSpace(b64))
    {
        var url = data.ValueKind == JsonValueKind.Object ? ReadJsonString(data, "url") : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        using var remote = await httpClient.GetAsync(url, cancellationToken);
        if (!remote.IsSuccessStatusCode)
        {
            return null;
        }

        var remoteBytes = await remote.Content.ReadAsByteArrayAsync(cancellationToken);
        await using var remoteStream = new MemoryStream(remoteBytes);
        var remoteStored = await storage.UploadAsync(remoteStream, "image/jpeg", ".jpg", storagePrefix, cancellationToken);
        return remoteStored.PublicUrl;
    }

    await using var stream = new MemoryStream(Convert.FromBase64String(b64));
    var stored = await storage.UploadAsync(stream, "image/jpeg", ".jpg", storagePrefix, cancellationToken);
    return stored.PublicUrl;
}

static async Task<SpendBeeStreetViewImage?> TryFetchSpendBeeMerchantStreetViewAsync(
    IConfiguration configuration,
    HttpClient httpClient,
    SpendBeeGooglePlace? googlePlace,
    SpendBeeMerchantRecord merchant,
    IFileStorageService storage,
    CancellationToken cancellationToken)
{
    var apiKey = configuration["GoogleMaps:ApiKey"];
    var latitude = googlePlace?.Latitude ?? merchant.Latitude;
    var longitude = googlePlace?.Longitude ?? merchant.Longitude;
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        return null;
    }

    var location = latitude is not null && longitude is not null
        ? $"{latitude.Value.ToString(CultureInfo.InvariantCulture)},{longitude.Value.ToString(CultureInfo.InvariantCulture)}"
        : BuildSpendBeeStreetViewLocationQuery(googlePlace, merchant);
    if (string.IsNullOrWhiteSpace(location))
    {
        return null;
    }

    var metadataUri = $"https://maps.googleapis.com/maps/api/streetview/metadata?location={Uri.EscapeDataString(location)}&radius=80&source=outdoor&key={Uri.EscapeDataString(apiKey)}";
    using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, metadataUri);
    AddGoogleApiHeaders(metadataRequest);
    using var metadataResponse = await httpClient.SendAsync(metadataRequest, cancellationToken);
    if (!metadataResponse.IsSuccessStatusCode)
    {
        return null;
    }

    using var metadataDocument = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(cancellationToken));
    if (!string.Equals(ReadJsonString(metadataDocument.RootElement, "status"), "OK", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    if (metadataDocument.RootElement.TryGetProperty("location", out var metadataLocation))
    {
        latitude = ReadJsonDecimal(metadataLocation, "lat") ?? latitude;
        longitude = ReadJsonDecimal(metadataLocation, "lng") ?? longitude;
    }

    var imageLocation = latitude is not null && longitude is not null
        ? $"{latitude.Value.ToString(CultureInfo.InvariantCulture)},{longitude.Value.ToString(CultureInfo.InvariantCulture)}"
        : location;
    var imageUri = $"https://maps.googleapis.com/maps/api/streetview?size=640x360&location={Uri.EscapeDataString(imageLocation)}&radius=80&source=outdoor&fov=80&pitch=0&key={Uri.EscapeDataString(apiKey)}";
    using var imageRequest = new HttpRequestMessage(HttpMethod.Get, imageUri);
    AddGoogleApiHeaders(imageRequest);
    using var imageResponse = await httpClient.SendAsync(imageRequest, cancellationToken);
    if (!imageResponse.IsSuccessStatusCode)
    {
        return null;
    }

    var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
    if (bytes.Length < 1024)
    {
        return null;
    }

    await using var stream = new MemoryStream(bytes);
    var stored = await storage.UploadAsync(stream, "image/jpeg", ".jpg", $"spendbee/merchants/{merchant.Id}/streetview", cancellationToken);
    return new SpendBeeStreetViewImage(bytes, stored.PublicUrl, latitude, longitude);
}

static string? BuildSpendBeeStreetViewLocationQuery(SpendBeeGooglePlace? googlePlace, SpendBeeMerchantRecord merchant)
{
    var address = googlePlace?.Address ?? merchant.Address;
    if (!string.IsNullOrWhiteSpace(address))
    {
        return address.Contains("New Zealand", StringComparison.OrdinalIgnoreCase) ||
            address.Contains("NZ", StringComparison.OrdinalIgnoreCase)
            ? address
            : $"{address}, Auckland, New Zealand";
    }

    return string.IsNullOrWhiteSpace(merchant.Name)
        ? null
        : $"{merchant.Name}, Auckland, New Zealand";
}

static async Task<string?> FindSpendBeeSharedIndustryCoverAsync(
    MySqlConnection connection,
    int projectId,
    string category,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT AiCoverImageUrl
        FROM bee_SpendBeeMerchant
        WHERE ProjectId = @ProjectId
            AND CoverSource = 'IndustryFallback'
            AND CoverCategory = @CoverCategory
            AND AiCoverImageUrl IS NOT NULL
            AND AiCoverImageUrl <> ''
        ORDER BY LastAiCoverGeneratedAtUtc DESC, id DESC
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@CoverCategory", MySqlDbType.VarChar, 80).Value = category;
    return await command.ExecuteScalarAsync(cancellationToken) as string;
}

static string BuildSpendBeeMerchantStreetViewCoverPrompt(SpendBeeMerchantRecord merchant, SpendBeeGooglePlace? googlePlace, string category)
{
    var address = googlePlace?.Address ?? merchant.Address ?? "unknown address";
    var type = googlePlace?.PrimaryType ?? merchant.PrimaryType ?? category;
    return $"""
        Transform the supplied Google Street View storefront image into a polished cartoon cover for the SpendBee app.
        Merchant name: {merchant.Name}.
        Address context: {address}.
        Business type: {type}.
        Keep the real street/building geometry and storefront feeling from the source image, but render it as a clean,
        friendly editorial cartoon. Do not invent readable signage or fake exact logos. Remove faces/license plates and
        avoid text overlays. Output must work as a small square mobile merchant cover.
        """;
}

static string BuildSpendBeeMerchantIndustryFallbackCoverPrompt(string category)
{
    return $"""
        Create one reusable SpendBee merchant cover for the category "{category}".
        It should be a clean, friendly, square mobile app cartoon illustration, not a specific real storefront.
        Use category-appropriate visual cues, warm daylight, simple composition, no readable text, no logos,
        no identifiable people, and no brand-specific elements. The image should be compact and readable in an app list.
        """;
}

static string NormalizeSpendBeeMerchantCoverCategory(string? primaryType)
{
    var normalized = (primaryType ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
    return normalized switch
    {
        "cafe" or "coffee_shop" => "cafe",
        "bakery" => "bakery",
        "bar" or "pub" or "night_club" => "bar",
        "meal_takeaway" or "meal_delivery" => "takeaway",
        "grocery_store" or "supermarket" or "convenience_store" => "grocery",
        "restaurant" or "food" or "" => "restaurant",
        _ when normalized.Contains("restaurant", StringComparison.Ordinal) => "restaurant",
        _ when normalized.Contains("cafe", StringComparison.Ordinal) || normalized.Contains("coffee", StringComparison.Ordinal) => "cafe",
        _ when normalized.Contains("bakery", StringComparison.Ordinal) => "bakery",
        _ when normalized.Contains("takeaway", StringComparison.Ordinal) || normalized.Contains("delivery", StringComparison.Ordinal) => "takeaway",
        _ => "merchant"
    };
}

static async Task UpdateSpendBeeMerchantAiCoverAsync(
    MySqlConnection connection,
    long merchantId,
    string coverUrl,
    string prompt,
    string source,
    string category,
    string? streetViewImageUrl,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeMerchant
        SET AiCoverImageUrl = @AiCoverImageUrl,
            AiCoverPrompt = @AiCoverPrompt,
            CoverSource = @CoverSource,
            CoverCategory = @CoverCategory,
            StreetViewImageUrl = @StreetViewImageUrl,
            LastAiCoverGeneratedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @MerchantId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AiCoverImageUrl", MySqlDbType.VarChar, 1000).Value = coverUrl;
    command.Parameters.Add("@AiCoverPrompt", MySqlDbType.VarChar, 1600).Value = NormalizeBounded(prompt, 1600) ?? prompt;
    command.Parameters.Add("@CoverSource", MySqlDbType.VarChar, 40).Value = source;
    command.Parameters.Add("@CoverCategory", MySqlDbType.VarChar, 80).Value = category;
    command.Parameters.Add("@StreetViewImageUrl", MySqlDbType.VarChar, 1000).Value = DbNullable(streetViewImageUrl);
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task UpdateSpendBeeMerchantCoordinatesAsync(
    MySqlConnection connection,
    long merchantId,
    decimal latitude,
    decimal longitude,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeMerchant
        SET Latitude = COALESCE(Latitude, @Latitude),
            Longitude = COALESCE(Longitude, @Longitude)
        WHERE id = @MerchantId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@Latitude", MySqlDbType.Decimal).Value = latitude;
    command.Parameters.Add("@Longitude", MySqlDbType.Decimal).Value = longitude;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static string NormalizeMerchantName(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var normalized = new string(value
        .Trim()
        .ToLowerInvariant()
        .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
        .ToArray());
    var parts = normalized
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part switch
        {
            "technologies" or "technology" => "tech",
            "limited" or "ltd" or "company" or "co" => "",
            _ => part
        })
        .Where(part => !string.IsNullOrWhiteSpace(part))
        .ToArray();
    return string.Join(' ', parts);
}

static string? ReadJsonString(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString()
        : null;
}

static string? ReadNestedLocalizedText(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Object &&
        property.TryGetProperty("text", out var text) &&
        text.ValueKind == JsonValueKind.String
        ? text.GetString()
        : null;
}

static decimal? ReadJsonDecimal(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value)
        ? value
        : null;
}

static int? ReadJsonInt(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
        ? value
        : null;
}

static bool? ReadJsonBool(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? property.GetBoolean()
        : null;
}

static async Task UpdateSpendBeeReceiptFailureAsync(
    MySqlConnection connection,
    long receiptId,
    string? rawRecognitionJson,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeReceipt
        SET Status = 'RecognitionFailed',
            RawOcrJson = @RawOcrJson,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @ReceiptId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@RawOcrJson", MySqlDbType.JSON).Value = rawRecognitionJson ?? "{}";
    command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<object?> LoadSpendBeeReceiptAsync(
    IConfiguration configuration,
    long receiptId,
    int projectId,
    int appUserId,
    string publicRequestBaseUrl,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string receiptSql = """
        SELECT receipt.id, receipt.Status, receipt.ReceiptType, receipt.FulfillmentType,
            receipt.MerchantName, receipt.MerchantAddress, receipt.PlatformOrderNumber,
            receipt.PurchasedAtUtc, receipt.OrderedAtUtc, receipt.PickupAtUtc, receipt.DeliveredAtUtc,
            receipt.Currency, receipt.Subtotal, receipt.Tax, receipt.DeliveryFee, receipt.ServiceFee,
            receipt.PlatformDiscount, receipt.Total, receipt.OverallConfidence, receipt.EstimatedErrorRate,
            receipt.FailedChecksJson, receipt.RawOcrJson, receipt.CreatedAtUtc, receipt.UpdatedAtUtc,
            platform.id AS PlatformId, platform.Name AS PlatformName, platform.DisplayName AS PlatformDisplayName,
            platform.PlatformType AS PlatformType, platform.LogoUrl AS PlatformLogoUrl,
            platform.WebsiteUrl AS PlatformWebsiteUrl
        FROM bee_SpendBeeReceipt AS receipt
        LEFT JOIN bee_SpendBeePlatform AS platform ON platform.id = receipt.PlatformId
        WHERE receipt.id = @ReceiptId
            AND receipt.ProjectId = @ProjectId
            AND receipt.AppUserId = @AppUserId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(receiptSql, connection);
    command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    var receipt = new
    {
        id = reader.GetInt64(reader.GetOrdinal("id")),
        status = reader["Status"] as string,
        receiptType = reader["ReceiptType"] as string,
        fulfillmentType = reader["FulfillmentType"] as string,
        merchantName = reader["MerchantName"] as string,
        merchantAddress = reader["MerchantAddress"] as string,
        platformOrderNumber = reader["PlatformOrderNumber"] as string,
        purchasedAtUtc = reader.IsDBNull(reader.GetOrdinal("PurchasedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PurchasedAtUtc")).ToString("O"),
        orderedAtUtc = reader.IsDBNull(reader.GetOrdinal("OrderedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("OrderedAtUtc")).ToString("O"),
        pickupAtUtc = reader.IsDBNull(reader.GetOrdinal("PickupAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PickupAtUtc")).ToString("O"),
        deliveredAtUtc = reader.IsDBNull(reader.GetOrdinal("DeliveredAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("DeliveredAtUtc")).ToString("O"),
        currency = reader["Currency"] as string,
        subtotal = reader.IsDBNull(reader.GetOrdinal("Subtotal")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Subtotal")),
        tax = reader.IsDBNull(reader.GetOrdinal("Tax")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Tax")),
        deliveryFee = reader.IsDBNull(reader.GetOrdinal("DeliveryFee")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("DeliveryFee")),
        serviceFee = reader.IsDBNull(reader.GetOrdinal("ServiceFee")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("ServiceFee")),
        platformDiscount = reader.IsDBNull(reader.GetOrdinal("PlatformDiscount")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("PlatformDiscount")),
        total = reader.IsDBNull(reader.GetOrdinal("Total")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Total")),
        overallConfidence = reader.IsDBNull(reader.GetOrdinal("OverallConfidence")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("OverallConfidence")),
        estimatedErrorRate = reader.IsDBNull(reader.GetOrdinal("EstimatedErrorRate")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("EstimatedErrorRate")),
        failedChecks = reader["FailedChecksJson"] as string,
        rawOcr = reader["RawOcrJson"] as string,
        platform = reader.IsDBNull(reader.GetOrdinal("PlatformId"))
            ? null
            : new
            {
                id = reader.GetInt64(reader.GetOrdinal("PlatformId")),
                name = reader["PlatformName"] as string,
                displayName = reader["PlatformDisplayName"] as string,
                platformType = reader["PlatformType"] as string,
                logoUrl = reader["PlatformLogoUrl"] as string,
                websiteUrl = reader["PlatformWebsiteUrl"] as string
            },
        createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
        updatedAtUtc = reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc"))
    };
    await reader.CloseAsync();

    var images = new List<object>();
    await using (var imageCommand = new MySqlCommand(
        "SELECT id, ImageUrl, ContentType, SortOrder FROM bee_SpendBeeReceiptImage WHERE ReceiptId = @ReceiptId ORDER BY SortOrder, id;",
        connection))
    {
        imageCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        await using var imageReader = await imageCommand.ExecuteReaderAsync(cancellationToken);
        while (await imageReader.ReadAsync(cancellationToken))
        {
            var imageId = imageReader.GetInt64(imageReader.GetOrdinal("id"));
            var imageUrl = BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/receipts/{receiptId}/images/{imageId}");
            images.Add(new
            {
                id = imageId,
                imageUrl,
                downloadUrl = $"{imageUrl}?download=1",
                contentType = imageReader["ContentType"] as string,
                sortOrder = imageReader.GetInt32(imageReader.GetOrdinal("SortOrder"))
            });
        }
    }

    var lineItems = new List<object>();
    await using (var itemCommand = new MySqlCommand(
        "SELECT id, ItemName, Quantity, UnitPrice, Amount, Category, Confidence, SortOrder FROM bee_SpendBeeReceiptLineItem WHERE ReceiptId = @ReceiptId ORDER BY SortOrder, id;",
        connection))
    {
        itemCommand.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            lineItems.Add(new
            {
                id = itemReader.GetInt64(itemReader.GetOrdinal("id")),
                name = itemReader["ItemName"] as string,
                quantity = itemReader.IsDBNull(itemReader.GetOrdinal("Quantity")) ? (decimal?)null : itemReader.GetDecimal(itemReader.GetOrdinal("Quantity")),
                unitPrice = itemReader.IsDBNull(itemReader.GetOrdinal("UnitPrice")) ? (decimal?)null : itemReader.GetDecimal(itemReader.GetOrdinal("UnitPrice")),
                amount = itemReader.IsDBNull(itemReader.GetOrdinal("Amount")) ? (decimal?)null : itemReader.GetDecimal(itemReader.GetOrdinal("Amount")),
                category = itemReader["Category"] as string,
                confidence = itemReader.IsDBNull(itemReader.GetOrdinal("Confidence")) ? (decimal?)null : itemReader.GetDecimal(itemReader.GetOrdinal("Confidence")),
                sortOrder = itemReader.GetInt32(itemReader.GetOrdinal("SortOrder"))
            });
        }
    }

    return new { receipt, images, lineItems };
}

static async Task<IReadOnlyList<object>> LoadSpendBeeReceiptImageSummariesAsync(
    MySqlConnection connection,
    long receiptId,
    string publicRequestBaseUrl,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, ContentType, SortOrder
        FROM bee_SpendBeeReceiptImage
        WHERE ReceiptId = @ReceiptId
        ORDER BY SortOrder, id;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var images = new List<object>();
    while (await reader.ReadAsync(cancellationToken))
    {
        var imageId = reader.GetInt64(reader.GetOrdinal("id"));
        var imageUrl = BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/receipts/{receiptId}/images/{imageId}");
        images.Add(new
        {
            id = imageId,
            url = imageUrl,
            imageUrl,
            downloadUrl = $"{imageUrl}?download=1",
            contentType = reader["ContentType"] as string,
            sortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder"))
        });
    }

    return images;
}

static object BuildSpendBeeProfileResponse(AppUserProfile profile, string publicRequestBaseUrl) => new
{
    id = profile.Id,
    userId = profile.Id,
    profile.ProjectId,
    profile.Email,
    profile.PhoneNumber,
    profile.DisplayName,
    profile.FirstName,
    profile.LastName,
    profile.Gender,
    avatarUrl = BuildSpendBeeAppUserAvatarUrl(publicRequestBaseUrl, profile.Id, profile.AvatarUrl),
    profile.Bio,
    createdAtUtc = profile.CreatedAtUtc.ToString("O"),
    updatedAtUtc = profile.UpdatedAtUtc.ToString("O")
};

static string? BuildSpendBeeAppUserAvatarUrl(string publicRequestBaseUrl, int appUserId, string? avatarUrl) =>
    string.IsNullOrWhiteSpace(avatarUrl)
        ? null
        : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/users/{appUserId}/avatar");

static async Task<IReadOnlyList<object>> LoadSpendBeeReceiptGroupsAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT receiptGroup.id, receiptGroup.Title, receiptGroup.Description,
            receiptGroup.CreatedAtUtc, receiptGroup.UpdatedAtUtc,
            COUNT(groupReceipt.ReceiptId) AS ReceiptCount,
            MAX(COALESCE(receipt.PurchasedAtUtc, receipt.CreatedAtUtc)) AS LastReceiptAtUtc
        FROM bee_SpendBeeReceiptGroup AS receiptGroup
        LEFT JOIN bee_SpendBeeReceiptGroupReceipt AS groupReceipt ON groupReceipt.ReceiptGroupId = receiptGroup.id
        LEFT JOIN bee_SpendBeeReceipt AS receipt
            ON receipt.id = groupReceipt.ReceiptId
            AND receipt.ProjectId = receiptGroup.ProjectId
            AND receipt.AppUserId = receiptGroup.AppUserId
        WHERE receiptGroup.ProjectId = @ProjectId
            AND receiptGroup.AppUserId = @AppUserId
        GROUP BY receiptGroup.id, receiptGroup.Title, receiptGroup.Description, receiptGroup.CreatedAtUtc, receiptGroup.UpdatedAtUtc
        ORDER BY receiptGroup.UpdatedAtUtc DESC, receiptGroup.id DESC;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var groups = new List<object>();
    while (await reader.ReadAsync(cancellationToken))
    {
        groups.Add(MapSpendBeeReceiptGroup(reader));
    }

    return groups;
}

static async Task<object?> LoadSpendBeeReceiptGroupAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long groupId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT receiptGroup.id, receiptGroup.Title, receiptGroup.Description,
            receiptGroup.CreatedAtUtc, receiptGroup.UpdatedAtUtc,
            COUNT(groupReceipt.ReceiptId) AS ReceiptCount,
            MAX(COALESCE(receipt.PurchasedAtUtc, receipt.CreatedAtUtc)) AS LastReceiptAtUtc
        FROM bee_SpendBeeReceiptGroup AS receiptGroup
        LEFT JOIN bee_SpendBeeReceiptGroupReceipt AS groupReceipt ON groupReceipt.ReceiptGroupId = receiptGroup.id
        LEFT JOIN bee_SpendBeeReceipt AS receipt
            ON receipt.id = groupReceipt.ReceiptId
            AND receipt.ProjectId = receiptGroup.ProjectId
            AND receipt.AppUserId = receiptGroup.AppUserId
        WHERE receiptGroup.id = @GroupId
            AND receiptGroup.ProjectId = @ProjectId
            AND receiptGroup.AppUserId = @AppUserId
        GROUP BY receiptGroup.id, receiptGroup.Title, receiptGroup.Description, receiptGroup.CreatedAtUtc, receiptGroup.UpdatedAtUtc
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@GroupId", MySqlDbType.Int64).Value = groupId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken) ? MapSpendBeeReceiptGroup(reader) : null;
}

static object MapSpendBeeReceiptGroup(MySqlDataReader reader) => new
{
    id = reader.GetInt64(reader.GetOrdinal("id")),
    title = reader["Title"] as string,
    description = reader["Description"] as string,
    receiptCount = Convert.ToInt32(reader["ReceiptCount"]),
    lastReceiptAtUtc = reader.IsDBNull(reader.GetOrdinal("LastReceiptAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastReceiptAtUtc")).ToString("O"),
    createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")).ToString("O"),
    updatedAtUtc = reader.GetDateTime(reader.GetOrdinal("UpdatedAtUtc")).ToString("O")
};

static async Task<long> InsertSpendBeeReceiptGroupAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    string title,
    string? description,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_SpendBeeReceiptGroup (ProjectId, AppUserId, Title, Description)
        VALUES (@ProjectId, @AppUserId, @Title, @Description);
        SELECT LAST_INSERT_ID();
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@Title", MySqlDbType.VarChar, 160).Value = title;
    command.Parameters.Add("@Description", MySqlDbType.VarChar, 500).Value = DbNullable(description);
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
}

static async Task<bool> UpdateSpendBeeReceiptGroupAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long groupId,
    string title,
    string? description,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeReceiptGroup
        SET Title = @Title,
            Description = @Description,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @GroupId
            AND ProjectId = @ProjectId
            AND AppUserId = @AppUserId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@GroupId", MySqlDbType.Int64).Value = groupId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@Title", MySqlDbType.VarChar, 160).Value = title;
    command.Parameters.Add("@Description", MySqlDbType.VarChar, 500).Value = DbNullable(description);
    return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
}

static async Task<bool> SpendBeeReceiptGroupBelongsToUserAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long groupId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT 1
        FROM bee_SpendBeeReceiptGroup
        WHERE id = @GroupId
            AND ProjectId = @ProjectId
            AND AppUserId = @AppUserId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@GroupId", MySqlDbType.Int64).Value = groupId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    return await command.ExecuteScalarAsync(cancellationToken) is not null;
}

static async Task<int> AddSpendBeeReceiptsToGroupAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long groupId,
    IReadOnlyList<long> receiptIds,
    CancellationToken cancellationToken)
{
    var added = 0;
    const string sql = """
        INSERT IGNORE INTO bee_SpendBeeReceiptGroupReceipt (ReceiptGroupId, ReceiptId)
        SELECT @GroupId, receipt.id
        FROM bee_SpendBeeReceipt AS receipt
        WHERE receipt.id = @ReceiptId
            AND receipt.ProjectId = @ProjectId
            AND receipt.AppUserId = @AppUserId;
        """;
    foreach (var receiptId in receiptIds)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@GroupId", MySqlDbType.Int64).Value = groupId;
        command.Parameters.Add("@ReceiptId", MySqlDbType.Int64).Value = receiptId;
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
        added += await command.ExecuteNonQueryAsync(cancellationToken);
    }

    if (added > 0)
    {
        const string updateSql = """
            UPDATE bee_SpendBeeReceiptGroup
            SET UpdatedAtUtc = UTC_TIMESTAMP(6)
            WHERE id = @GroupId;
            """;
        await using var updateCommand = new MySqlCommand(updateSql, connection);
        updateCommand.Parameters.Add("@GroupId", MySqlDbType.Int64).Value = groupId;
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    return added;
}

static string BuildPublicRequestBaseUrl(HttpRequest request)
{
    static string FirstHeaderValue(string? value) => (value ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault() ?? string.Empty;

    var scheme = FirstHeaderValue(request.Headers["X-Forwarded-Proto"].ToString());
    if (string.IsNullOrWhiteSpace(scheme))
    {
        scheme = request.Scheme;
    }

    var host = FirstHeaderValue(request.Headers["X-Forwarded-Host"].ToString());
    if (string.IsNullOrWhiteSpace(host))
    {
        host = request.Host.Value;
    }

    return $"{scheme}://{host}".TrimEnd('/');
}

static string BuildPublicApiUrl(string publicRequestBaseUrl, string path)
{
    return $"{publicRequestBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}

static bool IsValidLatitudeLongitude(double? latitude, double? longitude)
{
    return latitude is >= -90d and <= 90d &&
        longitude is >= -180d and <= 180d;
}

static double? CalculateDistanceMeters(double fromLatitude, double fromLongitude, decimal? toLatitude, decimal? toLongitude)
{
    if (toLatitude is null || toLongitude is null)
    {
        return null;
    }

    var lat1 = DegreesToRadians(fromLatitude);
    var lat2 = DegreesToRadians((double)toLatitude.Value);
    var deltaLat = DegreesToRadians((double)toLatitude.Value - fromLatitude);
    var deltaLng = DegreesToRadians((double)toLongitude.Value - fromLongitude);
    var a = Math.Pow(Math.Sin(deltaLat / 2), 2) +
        Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(deltaLng / 2), 2);
    return 6371000d * 2 * Math.Asin(Math.Sqrt(a));
}

static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

static async Task InsertSpendBeeUserMessageAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    string messageType,
    string severity,
    string title,
    string? body,
    string? targetType,
    long? targetId,
    string? targetUrl,
    object? payload,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_SpendBeeUserMessage
            (ProjectId, AppUserId, MessageType, Severity, Title, Body, TargetType, TargetId, TargetUrl, PayloadJson)
        VALUES
            (@ProjectId, @AppUserId, @MessageType, @Severity, @Title, @Body, @TargetType, @TargetId, @TargetUrl, @PayloadJson);
        SELECT LAST_INSERT_ID();
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@MessageType", MySqlDbType.VarChar, 80).Value = NormalizeBounded(messageType, 80) ?? messageType;
    command.Parameters.Add("@Severity", MySqlDbType.VarChar, 30).Value = NormalizeBounded(severity, 30) ?? "Info";
    command.Parameters.Add("@Title", MySqlDbType.VarChar, 200).Value = NormalizeBounded(title, 200) ?? title;
    command.Parameters.Add("@Body", MySqlDbType.VarChar, 1000).Value = DbNullable(NormalizeBounded(body, 1000));
    command.Parameters.Add("@TargetType", MySqlDbType.VarChar, 80).Value = DbNullable(NormalizeBounded(targetType, 80));
    command.Parameters.Add("@TargetId", MySqlDbType.Int64).Value = targetId.HasValue ? targetId.Value : DBNull.Value;
    command.Parameters.Add("@TargetUrl", MySqlDbType.VarChar, 500).Value = DbNullable(NormalizeBounded(targetUrl, 500));
    command.Parameters.Add("@PayloadJson", MySqlDbType.JSON).Value = payload is null
        ? DBNull.Value
        : JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var messageId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    await DispatchSpendBeeUserMessagePushAsync(
        connection,
        projectId,
        appUserId,
        messageId,
        messageType,
        severity,
        title,
        body,
        targetType,
        targetId,
        targetUrl,
        cancellationToken);
}

static async Task DispatchSpendBeeUserMessagePushAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long messageId,
    string messageType,
    string severity,
    string title,
    string? body,
    string? targetType,
    long? targetId,
    string? targetUrl,
    CancellationToken cancellationToken)
{
    var apnsOptions = LoadApnsOptionsFromEnvironment();
    if (apnsOptions is null)
    {
        return;
    }

    var pushTokens = await QuerySpendBeeUserApnsPushTokensAsync(connection, projectId, appUserId, cancellationToken);
    if (pushTokens.Count == 0)
    {
        return;
    }

    using var httpClient = new HttpClient();
    foreach (var pushToken in pushTokens)
    {
        await SendSpendBeeMessageApnsPushAsync(
            httpClient,
            apnsOptions,
            pushToken,
            messageId,
            messageType,
            severity,
            title,
            body,
            targetType,
            targetId,
            targetUrl,
            cancellationToken);
    }
}

static async Task<IReadOnlyList<string>> QuerySpendBeeUserApnsPushTokensAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT PushToken
        FROM (
            SELECT PushToken, MAX(UpdatedAtUtc) AS LastUpdatedAtUtc
            FROM bee_AppUserDevice
            WHERE ProjectId = @ProjectId
                AND AppUserId = @AppUserId
                AND LOWER(COALESCE(PushProvider, '')) = 'apns'
                AND COALESCE(PushToken, '') <> ''
            GROUP BY PushToken
        ) AS token
        ORDER BY LastUpdatedAtUtc DESC
        LIMIT 10;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    var tokens = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        if (reader["PushToken"] is string token && !string.IsNullOrWhiteSpace(token))
        {
            tokens.Add(token);
        }
    }

    return tokens;
}

static ApnsOptions? LoadApnsOptionsFromEnvironment()
{
    var teamId = Environment.GetEnvironmentVariable("AppPush__Apns__TeamId");
    var keyId = Environment.GetEnvironmentVariable("AppPush__Apns__KeyId");
    var bundleId = Environment.GetEnvironmentVariable("AppPush__Apns__BundleId");
    var privateKeyPath = Environment.GetEnvironmentVariable("AppPush__Apns__PrivateKeyPath");
    var environment = Environment.GetEnvironmentVariable("AppPush__Apns__Environment") ?? "production";
    if (string.IsNullOrWhiteSpace(teamId) ||
        string.IsNullOrWhiteSpace(keyId) ||
        string.IsNullOrWhiteSpace(bundleId) ||
        string.IsNullOrWhiteSpace(privateKeyPath) ||
        !File.Exists(privateKeyPath))
    {
        return null;
    }

    var endpoint = environment.Equals("sandbox", StringComparison.OrdinalIgnoreCase)
        ? "https://api.sandbox.push.apple.com"
        : "https://api.push.apple.com";
    return new ApnsOptions(teamId.Trim(), keyId.Trim(), bundleId.Trim(), privateKeyPath.Trim(), endpoint);
}

static async Task<ApnsSendResult> SendSpendBeeMessageApnsPushAsync(
    HttpClient httpClient,
    ApnsOptions options,
    string pushToken,
    long messageId,
    string messageType,
    string severity,
    string title,
    string? body,
    string? targetType,
    long? targetId,
    string? targetUrl,
    CancellationToken cancellationToken)
{
    var jwt = ApnsJwtTokenCache.GetOrCreate(options);
    var payload = new
    {
        aps = new
        {
            alert = new
            {
                title = NormalizeBounded(title, 120) ?? "SpendBee",
                body = NormalizeBounded(body, 180) ?? NormalizeBounded(title, 180) ?? "You have a new SpendBee message."
            },
            sound = "default",
            badge = 1
        },
        type = "spendbee_message",
        messageId,
        messageType = NormalizeBounded(messageType, 80),
        severity = NormalizeBounded(severity, 30),
        targetType = NormalizeBounded(targetType, 80),
        targetId,
        targetUrl = NormalizeBounded(targetUrl, 500)
    };
    var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Endpoint}/3/device/{pushToken}")
    {
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
    request.Headers.TryAddWithoutValidation("apns-topic", options.BundleId);
    request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
    request.Headers.TryAddWithoutValidation("apns-priority", "10");

    try
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        var apnsId = response.Headers.TryGetValues("apns-id", out var values) ? values.FirstOrDefault() : null;
        return response.IsSuccessStatusCode
            ? new ApnsSendResult(true, apnsId, null)
            : new ApnsSendResult(false, apnsId, $"APNS returned HTTP {(int)response.StatusCode}: {TrimDiagnostic(responseText)}");
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or CryptographicException)
    {
        return new ApnsSendResult(false, null, exception.Message);
    }
}

static async Task<(IReadOnlyList<object> Items, object Page)> LoadSpendBeeMessagesAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    int? limit,
    long? beforeId,
    bool unreadOnly,
    CancellationToken cancellationToken)
{
    var pageSize = Math.Clamp(limit ?? 20, 1, 100);
    const string sql = """
        SELECT id, MessageType, Severity, Title, Body, TargetType, TargetId, TargetUrl, PayloadJson, ReadAtUtc, CreatedAtUtc
        FROM bee_SpendBeeUserMessage
        WHERE ProjectId = @ProjectId
            AND AppUserId = @AppUserId
            AND MessageType <> 'profile_avatar_updated'
            AND (@UnreadOnly = 0 OR ReadAtUtc IS NULL)
            AND (@BeforeId IS NULL OR id < @BeforeId)
            AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > UTC_TIMESTAMP(6))
        ORDER BY id DESC
        LIMIT @Limit;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@UnreadOnly", MySqlDbType.Int32).Value = unreadOnly ? 1 : 0;
    command.Parameters.Add("@BeforeId", MySqlDbType.Int64).Value = beforeId.HasValue ? beforeId.Value : DBNull.Value;
    command.Parameters.Add("@Limit", MySqlDbType.Int32).Value = pageSize + 1;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var items = new List<object>();
    long? nextBeforeId = null;
    while (await reader.ReadAsync(cancellationToken))
    {
        var id = reader.GetInt64(reader.GetOrdinal("id"));
        if (items.Count >= pageSize)
        {
            nextBeforeId = id;
            break;
        }

        items.Add(MapSpendBeeMessage(reader));
    }

    return (items, new { limit = pageSize, nextBeforeId });
}

static async Task<int> CountSpendBeeUnreadMessagesAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT COUNT(*)
        FROM bee_SpendBeeUserMessage
        WHERE ProjectId = @ProjectId
            AND AppUserId = @AppUserId
            AND MessageType <> 'profile_avatar_updated'
            AND ReadAtUtc IS NULL
            AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > UTC_TIMESTAMP(6));
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
}

static async Task<object?> MarkAndLoadSpendBeeMessageReadAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long messageId,
    CancellationToken cancellationToken)
{
    const string updateSql = """
        UPDATE bee_SpendBeeUserMessage
        SET ReadAtUtc = COALESCE(ReadAtUtc, UTC_TIMESTAMP(6))
        WHERE id = @MessageId
            AND ProjectId = @ProjectId
            AND AppUserId = @AppUserId
            AND MessageType <> 'profile_avatar_updated'
            AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > UTC_TIMESTAMP(6));
        """;
    await using (var command = new MySqlCommand(updateSql, connection))
    {
        command.Parameters.Add("@MessageId", MySqlDbType.Int64).Value = messageId;
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    return await LoadSpendBeeMessageAsync(connection, projectId, appUserId, messageId, cancellationToken);
}

static async Task<object?> LoadSpendBeeMessageAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long messageId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, MessageType, Severity, Title, Body, TargetType, TargetId, TargetUrl, PayloadJson, ReadAtUtc, CreatedAtUtc
        FROM bee_SpendBeeUserMessage
        WHERE id = @MessageId
            AND ProjectId = @ProjectId
            AND AppUserId = @AppUserId
            AND MessageType <> 'profile_avatar_updated'
            AND (ExpiresAtUtc IS NULL OR ExpiresAtUtc > UTC_TIMESTAMP(6))
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@MessageId", MySqlDbType.Int64).Value = messageId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken) ? MapSpendBeeMessage(reader) : null;
}

static object MapSpendBeeMessage(MySqlDataReader reader)
{
    return new
    {
        id = reader.GetInt64(reader.GetOrdinal("id")),
        messageType = reader["MessageType"] as string,
        severity = reader["Severity"] as string,
        title = reader["Title"] as string,
        body = reader["Body"] as string,
        targetType = reader["TargetType"] as string,
        targetId = reader.IsDBNull(reader.GetOrdinal("TargetId")) ? (long?)null : reader.GetInt64(reader.GetOrdinal("TargetId")),
        targetUrl = reader["TargetUrl"] as string,
        payload = ParseJsonNode(reader["PayloadJson"] as string),
        readAtUtc = reader.IsDBNull(reader.GetOrdinal("ReadAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("ReadAtUtc")).ToString("O"),
        createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")).ToString("O")
    };
}

static string? NormalizeSpendBeeMerchantPhotoUploadCategory(string? value)
{
    var normalized = NormalizeBounded(value, 80);
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return "group";
    }

    return normalized.Trim().ToLowerInvariant() switch
    {
        "receipt" or "invoice" or "bill" or "avatar" or "profile" or "profile_photo" => null,
        "food" or "dish" or "meal" or "drink" => "food",
        "menu" => "menu",
        "storefront" or "front" or "merchant" or "store" => "storefront",
        "environment" or "interior" or "ambience" or "scene" => "environment",
        "group" or "people" or "friends" or "table" or "moment" or "photo" => "group",
        "other" => "other",
        _ => "other"
    };
}

static (bool IsValid, string? Category) NormalizeSpendBeeMerchantPhotoCategoryFilter(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return (true, null);
    }

    var normalized = NormalizeSpendBeeMerchantPhotoUploadCategory(value);
    return normalized is null ? (false, null) : (true, normalized);
}

static bool IsSpendBeeMerchantPhotoCategory(string? value)
{
    var normalized = NormalizeBounded(value, 80);
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return true;
    }

    return normalized.Trim().ToLowerInvariant() switch
    {
        "receipt" or "invoice" or "bill" or "avatar" or "profile" or "profile_photo" => false,
        _ => true
    };
}

static async Task<bool> SpendBeeMerchantBelongsToProjectAsync(
    MySqlConnection connection,
    int projectId,
    long merchantId,
    CancellationToken cancellationToken)
{
    const string sql = "SELECT 1 FROM bee_SpendBeeMerchant WHERE id = @MerchantId AND ProjectId = @ProjectId LIMIT 1;";
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    return await command.ExecuteScalarAsync(cancellationToken) is not null;
}

static async Task<bool> SpendBeePhotoBelongsToProjectAsync(
    MySqlConnection connection,
    int projectId,
    long photoId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT 1
        FROM bee_SpendBeeMerchantPhoto
        WHERE id = @PhotoId
            AND ProjectId = @ProjectId
            AND Status <> 'Deleted'
            AND LOWER(COALESCE(Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    return await command.ExecuteScalarAsync(cancellationToken) is not null;
}

static async Task<SpendBeeMerchantPhotoUpload?> FindSpendBeeMerchantPhotoUploadAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long photoUploadId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, ProjectId, MerchantId, AppUserId, S3Key, UploadId, FileName, ContentType, FileSizeBytes,
            Category, Caption, Status, PartEtagsJson, OriginalImageUrl, PhotoId, CompletedAtUtc, CancelledAtUtc
        FROM bee_SpendBeeMerchantPhotoUpload
        WHERE id = @PhotoUploadId
            AND ProjectId = @ProjectId
            AND AppUserId = @AppUserId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoUploadId", MySqlDbType.Int64).Value = photoUploadId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new SpendBeeMerchantPhotoUpload(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader.GetInt32(reader.GetOrdinal("ProjectId")),
        reader.GetInt64(reader.GetOrdinal("MerchantId")),
        reader.GetInt32(reader.GetOrdinal("AppUserId")),
        reader["S3Key"] as string ?? string.Empty,
        reader["UploadId"] as string ?? string.Empty,
        reader["FileName"] as string,
        reader["ContentType"] as string ?? "image/jpeg",
        reader.IsDBNull(reader.GetOrdinal("FileSizeBytes")) ? null : reader.GetInt64(reader.GetOrdinal("FileSizeBytes")),
        reader["Category"] as string,
        reader["Caption"] as string,
        reader["Status"] as string ?? "Uploading",
        ParseSpendBeeUploadParts(reader["PartEtagsJson"] as string),
        reader["OriginalImageUrl"] as string,
        reader.IsDBNull(reader.GetOrdinal("PhotoId")) ? null : reader.GetInt64(reader.GetOrdinal("PhotoId")),
        reader.IsDBNull(reader.GetOrdinal("CompletedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("CompletedAtUtc")),
        reader.IsDBNull(reader.GetOrdinal("CancelledAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("CancelledAtUtc")));
}

static IReadOnlyList<EdgeEventVideoPart> ParseSpendBeeUploadParts(string? partsJson)
{
    return string.IsNullOrWhiteSpace(partsJson)
        ? []
        : JsonSerializer.Deserialize<List<EdgeEventVideoPart>>(partsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
}

static async Task SaveSpendBeeMerchantPhotoUploadPartsAsync(
    MySqlConnection connection,
    long photoUploadId,
    IReadOnlyList<EdgeEventVideoPart> parts,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeMerchantPhotoUpload
        SET PartEtagsJson = @PartEtagsJson
        WHERE id = @PhotoUploadId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoUploadId", MySqlDbType.Int64).Value = photoUploadId;
    command.Parameters.Add("@PartEtagsJson", MySqlDbType.JSON).Value = JsonSerializer.Serialize(parts);
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task CompleteSpendBeeMerchantPhotoUploadAsync(
    MySqlConnection connection,
    long photoUploadId,
    string originalImageUrl,
    long photoId,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeMerchantPhotoUpload
        SET Status = 'Completed',
            OriginalImageUrl = @OriginalImageUrl,
            PhotoId = @PhotoId,
            CompletedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @PhotoUploadId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoUploadId", MySqlDbType.Int64).Value = photoUploadId;
    command.Parameters.Add("@OriginalImageUrl", MySqlDbType.VarChar, 1000).Value = originalImageUrl;
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<long> InsertSpendBeeMerchantPhotoAsync(
    MySqlConnection connection,
    SpendBeeMerchantPhotoUpload upload,
    string originalImageUrl,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_SpendBeeMerchantPhoto
            (ProjectId, MerchantId, AppUserId, UploadId, Category, Caption, OriginalImageUrl, OriginalContentType, Status)
        VALUES
            (@ProjectId, @MerchantId, @AppUserId, @UploadId, @Category, @Caption, @OriginalImageUrl, @OriginalContentType, 'Processing');
        SELECT LAST_INSERT_ID();
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = upload.ProjectId;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = upload.MerchantId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = upload.AppUserId;
    command.Parameters.Add("@UploadId", MySqlDbType.Int64).Value = upload.Id;
    command.Parameters.Add("@Category", MySqlDbType.VarChar, 80).Value = DbNullable(upload.Category);
    command.Parameters.Add("@Caption", MySqlDbType.VarChar, 500).Value = DbNullable(upload.Caption);
    command.Parameters.Add("@OriginalImageUrl", MySqlDbType.VarChar, 1000).Value = originalImageUrl;
    command.Parameters.Add("@OriginalContentType", MySqlDbType.VarChar, 80).Value = upload.ContentType;
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
}

static async Task<byte[]> FetchS3ObjectBytesAsync(
    S3StorageOptions options,
    string key,
    HttpClient httpClient,
    CancellationToken cancellationToken)
{
    var getUri = BuildS3Uri(options, key);
    var getRequest = BuildS3Request(HttpMethod.Get, getUri, null, options, "UNSIGNED-PAYLOAD");
    using var response = await httpClient.SendAsync(getRequest, cancellationToken);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync(cancellationToken);
}

static string BuildSpendBeePhotoCartoonPrompt(SpendBeeMerchantPhotoUpload upload)
{
    return "Modify the uploaded image into a cartoon style. Do not add anything.";
}

static async Task<string?> TryCartoonizeSpendBeePhotoAsync(
    byte[] sourceBytes,
    string sourceContentType,
    string fileName,
    string prompt,
    HttpClient httpClient,
    IFileStorageService storage,
    OpenAIOptions options,
    long merchantId,
    long photoId,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        return null;
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/images/edits");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    using var form = new MultipartFormDataContent();
    form.Add(new StringContent(string.IsNullOrWhiteSpace(options.ImageModel) ? "gpt-image-1.5" : options.ImageModel), "model");
    form.Add(new StringContent(prompt), "prompt");
    form.Add(new StringContent("1024x1024"), "size");
    form.Add(new StringContent("low"), "quality");
    form.Add(new StringContent("jpeg"), "output_format");
    form.Add(new StringContent("70"), "output_compression");
    var imageContent = new ByteArrayContent(sourceBytes);
    imageContent.Headers.ContentType = new MediaTypeHeaderValue(sourceContentType);
    form.Add(imageContent, "image", string.IsNullOrWhiteSpace(fileName) ? "spendbee-photo.jpg" : fileName);
    request.Content = form;

    using var response = await httpClient.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return null;
    }

    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var data = document.RootElement.TryGetProperty("data", out var dataElement) &&
        dataElement.ValueKind == JsonValueKind.Array &&
        dataElement.GetArrayLength() > 0
        ? dataElement[0]
        : default;
    var b64 = data.ValueKind == JsonValueKind.Object ? ReadJsonString(data, "b64_json") : null;
    if (string.IsNullOrWhiteSpace(b64))
    {
        var url = data.ValueKind == JsonValueKind.Object ? ReadJsonString(data, "url") : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        using var remote = await httpClient.GetAsync(url, cancellationToken);
        if (!remote.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await remote.Content.ReadAsByteArrayAsync(cancellationToken);
        await using var remoteStream = new MemoryStream(bytes);
        var remoteStored = await storage.UploadAsync(remoteStream, "image/jpeg", ".jpg", $"spendbee/merchant-photos/{merchantId}/processed/{photoId}", cancellationToken);
        return remoteStored.PublicUrl;
    }

    await using var stream = new MemoryStream(Convert.FromBase64String(b64));
    var stored = await storage.UploadAsync(stream, "image/jpeg", ".jpg", $"spendbee/merchant-photos/{merchantId}/processed/{photoId}", cancellationToken);
    return stored.PublicUrl;
}

static async Task UpdateSpendBeeMerchantPhotoProcessedAsync(
    MySqlConnection connection,
    long photoId,
    string displayImageUrl,
    string displayContentType,
    string prompt,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeMerchantPhoto
        SET DisplayImageUrl = @DisplayImageUrl,
            DisplayContentType = @DisplayContentType,
            OpenAIPrompt = @OpenAIPrompt,
            Status = 'Ready',
            ProcessingError = NULL,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @PhotoId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    command.Parameters.Add("@DisplayImageUrl", MySqlDbType.VarChar, 1000).Value = displayImageUrl;
    command.Parameters.Add("@DisplayContentType", MySqlDbType.VarChar, 80).Value = displayContentType;
    command.Parameters.Add("@OpenAIPrompt", MySqlDbType.VarChar, 1600).Value = NormalizeBounded(prompt, 1600) ?? prompt;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task UpdateSpendBeeMerchantPhotoProcessingFailureAsync(
    MySqlConnection connection,
    long photoId,
    string error,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeMerchantPhoto
        SET Status = 'ProcessingFailed',
            ProcessingError = @ProcessingError,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @PhotoId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    command.Parameters.Add("@ProcessingError", MySqlDbType.VarChar, 700).Value = NormalizeBounded(error, 700) ?? "Image processing failed.";
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<(IReadOnlyList<object> Items, IReadOnlyList<object> Categories, object Page)> LoadSpendBeeMerchantPhotosAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long merchantId,
    string? category,
    int? limit,
    long? beforeId,
    string publicRequestBaseUrl,
    CancellationToken cancellationToken)
{
    var pageSize = Math.Clamp(limit ?? 20, 1, 60);
    const string categorySql = """
        SELECT LOWER(COALESCE(Category, 'group')) AS Category, COUNT(*) AS Count
        FROM bee_SpendBeeMerchantPhoto
        WHERE ProjectId = @ProjectId
            AND MerchantId = @MerchantId
            AND Status = 'Ready'
            AND DisplayImageUrl IS NOT NULL
            AND DisplayImageUrl <> ''
            AND LOWER(COALESCE(Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
        GROUP BY LOWER(COALESCE(Category, 'group'))
        ORDER BY Count DESC, Category;
        """;
    var categories = new List<object>();
    await using (var categoryCommand = new MySqlCommand(categorySql, connection))
    {
        categoryCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        categoryCommand.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
        await using var categoryReader = await categoryCommand.ExecuteReaderAsync(cancellationToken);
        while (await categoryReader.ReadAsync(cancellationToken))
        {
            categories.Add(new
            {
                category = categoryReader["Category"] as string,
                count = Convert.ToInt32(categoryReader["Count"])
            });
        }
    }

    const string sql = """
        SELECT photo.id, photo.MerchantId, photo.AppUserId, LOWER(COALESCE(photo.Category, 'group')) AS Category, photo.Caption,
            photo.DisplayContentType, photo.CreatedAtUtc,
            user.DisplayName, user.AvatarUrl, user.Gender,
            COUNT(DISTINCT likeRow.AppUserId) AS LikeCount,
            COUNT(DISTINCT comment.id) AS CommentCount,
            MAX(CASE WHEN myLike.AppUserId IS NULL THEN 0 ELSE 1 END) AS LikedByMe
        FROM bee_SpendBeeMerchantPhoto AS photo
        INNER JOIN bee_AppUser AS user ON user.id = photo.AppUserId
        LEFT JOIN bee_SpendBeeMerchantPhotoLike AS likeRow ON likeRow.PhotoId = photo.id
        LEFT JOIN bee_SpendBeeMerchantPhotoLike AS myLike ON myLike.PhotoId = photo.id AND myLike.AppUserId = @AppUserId
        LEFT JOIN bee_SpendBeeMerchantPhotoComment AS comment ON comment.PhotoId = photo.id AND comment.Status = 'Visible'
        WHERE photo.ProjectId = @ProjectId
            AND photo.MerchantId = @MerchantId
            AND photo.Status = 'Ready'
            AND photo.DisplayImageUrl IS NOT NULL
            AND photo.DisplayImageUrl <> ''
            AND LOWER(COALESCE(photo.Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
            AND (@Category IS NULL OR LOWER(COALESCE(photo.Category, 'group')) = @Category)
            AND (@BeforeId IS NULL OR photo.id < @BeforeId)
        GROUP BY photo.id, photo.MerchantId, photo.AppUserId, LOWER(COALESCE(photo.Category, 'group')), photo.Caption,
            photo.DisplayContentType, photo.CreatedAtUtc, user.DisplayName, user.AvatarUrl, user.Gender
        ORDER BY photo.id DESC
        LIMIT @Limit;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@Category", MySqlDbType.VarChar, 80).Value = DbNullable(category);
    command.Parameters.Add("@BeforeId", MySqlDbType.Int64).Value = beforeId.HasValue ? beforeId.Value : DBNull.Value;
    command.Parameters.Add("@Limit", MySqlDbType.Int32).Value = pageSize + 1;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var items = new List<object>();
    long? nextBeforeId = null;
    long? lastPhotoId = null;
    while (await reader.ReadAsync(cancellationToken))
    {
        var photoId = reader.GetInt64(reader.GetOrdinal("id"));
        if (items.Count >= pageSize)
        {
            nextBeforeId = lastPhotoId;
            break;
        }

        lastPhotoId = photoId;
        items.Add(new
        {
            id = photoId,
            merchantId = reader.GetInt64(reader.GetOrdinal("MerchantId")),
            category = reader["Category"] as string,
            status = "Ready",
            caption = reader["Caption"] as string,
            imageUrl = BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/merchant-photos/{photoId}/image"),
            contentType = reader["DisplayContentType"] as string ?? "image/jpeg",
            likeCount = Convert.ToInt32(reader["LikeCount"]),
            commentCount = Convert.ToInt32(reader["CommentCount"]),
            likedByMe = Convert.ToInt32(reader["LikedByMe"]) == 1,
            createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")).ToString("O"),
            user = new
            {
                id = reader.GetInt32(reader.GetOrdinal("AppUserId")),
                displayName = reader["DisplayName"] as string,
                avatarUrl = BuildSpendBeeAppUserAvatarUrl(publicRequestBaseUrl, reader.GetInt32(reader.GetOrdinal("AppUserId")), reader["AvatarUrl"] as string),
                gender = reader["Gender"] as string
            }
        });
    }

    return (items, categories, new { limit = pageSize, nextBeforeId });
}

static async Task<(int LikeCount, bool LikedByMe)> GetSpendBeePhotoLikeSummaryAsync(
    MySqlConnection connection,
    long photoId,
    int appUserId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT COUNT(*) AS LikeCount,
            COALESCE(MAX(CASE WHEN AppUserId = @AppUserId THEN 1 ELSE 0 END), 0) AS LikedByMe
        FROM bee_SpendBeeMerchantPhotoLike
        WHERE PhotoId = @PhotoId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return (0, false);
    }

    return (Convert.ToInt32(reader["LikeCount"]), Convert.ToInt32(reader["LikedByMe"]) == 1);
}

static async Task<SpendBeePhotoOwner?> FindSpendBeePhotoOwnerAsync(
    MySqlConnection connection,
    int projectId,
    long photoId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT AppUserId, MerchantId
        FROM bee_SpendBeeMerchantPhoto
        WHERE id = @PhotoId
            AND ProjectId = @ProjectId
            AND Status <> 'Deleted'
            AND LOWER(COALESCE(Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken)
        ? new SpendBeePhotoOwner(
            reader.GetInt32(reader.GetOrdinal("AppUserId")),
            reader.GetInt64(reader.GetOrdinal("MerchantId")))
        : null;
}

static async Task<SpendBeeUserPublicProfile?> FindSpendBeeUserPublicProfileAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, DisplayName, AvatarUrl, Gender
        FROM bee_AppUser
        WHERE id = @AppUserId
            AND ProjectId = @ProjectId
            AND Status = 'Active'
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken)
        ? new SpendBeeUserPublicProfile(
            reader.GetInt32(reader.GetOrdinal("id")),
            reader["DisplayName"] as string,
            reader["AvatarUrl"] as string,
            reader["Gender"] as string)
        : null;
}

static async Task<(IReadOnlyList<object> Items, object Page)> LoadSpendBeePhotoLikersAsync(
    MySqlConnection connection,
    long photoId,
    int? limit,
    long? beforeUserId,
    string publicRequestBaseUrl,
    CancellationToken cancellationToken)
{
    var pageSize = Math.Clamp(limit ?? 20, 1, 80);
    const string sql = """
        SELECT likeRow.AppUserId, likeRow.CreatedAtUtc, user.DisplayName, user.AvatarUrl, user.Gender
        FROM bee_SpendBeeMerchantPhotoLike AS likeRow
        INNER JOIN bee_AppUser AS user ON user.id = likeRow.AppUserId
        WHERE likeRow.PhotoId = @PhotoId
            AND (@BeforeUserId IS NULL OR likeRow.AppUserId < @BeforeUserId)
        ORDER BY likeRow.AppUserId DESC
        LIMIT @Limit;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    command.Parameters.Add("@BeforeUserId", MySqlDbType.Int64).Value = beforeUserId.HasValue ? beforeUserId.Value : DBNull.Value;
    command.Parameters.Add("@Limit", MySqlDbType.Int32).Value = pageSize + 1;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var items = new List<object>();
    long? nextBeforeUserId = null;
    while (await reader.ReadAsync(cancellationToken))
    {
        var appUserId = reader.GetInt32(reader.GetOrdinal("AppUserId"));
        if (items.Count >= pageSize)
        {
            nextBeforeUserId = appUserId;
            break;
        }

        items.Add(new
        {
            appUserId,
            displayName = reader["DisplayName"] as string,
            avatarUrl = BuildSpendBeeAppUserAvatarUrl(publicRequestBaseUrl, appUserId, reader["AvatarUrl"] as string),
            gender = reader["Gender"] as string,
            likedAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")).ToString("O")
        });
    }

    return (items, new { limit = pageSize, nextBeforeUserId });
}

static async Task<bool> SpendBeeCommentBelongsToPhotoAsync(
    MySqlConnection connection,
    long photoId,
    long commentId,
    CancellationToken cancellationToken)
{
    const string sql = "SELECT 1 FROM bee_SpendBeeMerchantPhotoComment WHERE id = @CommentId AND PhotoId = @PhotoId AND Status = 'Visible' LIMIT 1;";
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@CommentId", MySqlDbType.Int64).Value = commentId;
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    return await command.ExecuteScalarAsync(cancellationToken) is not null;
}

static async Task<long?> FindSpendBeeCommentPhotoIdAsync(
    MySqlConnection connection,
    int projectId,
    long commentId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT comment.PhotoId
        FROM bee_SpendBeeMerchantPhotoComment AS comment
        INNER JOIN bee_SpendBeeMerchantPhoto AS photo ON photo.id = comment.PhotoId
        WHERE comment.id = @CommentId
            AND photo.ProjectId = @ProjectId
            AND comment.Status = 'Visible'
            AND LOWER(COALESCE(photo.Category, 'group')) NOT IN ('receipt', 'invoice', 'bill', 'avatar', 'profile', 'profile_photo')
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@CommentId", MySqlDbType.Int64).Value = commentId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    var result = await command.ExecuteScalarAsync(cancellationToken);
    return result is null ? null : Convert.ToInt64(result);
}

static async Task<long> InsertSpendBeePhotoCommentAsync(
    MySqlConnection connection,
    long photoId,
    int appUserId,
    long? parentCommentId,
    string body,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_SpendBeeMerchantPhotoComment (PhotoId, AppUserId, ParentCommentId, Body)
        VALUES (@PhotoId, @AppUserId, @ParentCommentId, @Body);
        SELECT LAST_INSERT_ID();
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@ParentCommentId", MySqlDbType.Int64).Value = parentCommentId.HasValue ? parentCommentId.Value : DBNull.Value;
    command.Parameters.Add("@Body", MySqlDbType.VarChar, 1000).Value = body.Trim();
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
}

static async Task<object?> LoadSpendBeePhotoCommentAsync(
    MySqlConnection connection,
    long commentId,
    int appUserId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT comment.id, comment.PhotoId, comment.ParentCommentId, comment.Body, comment.CreatedAtUtc,
            user.id AS UserId, user.DisplayName, user.AvatarUrl,
            COUNT(DISTINCT likeRow.AppUserId) AS LikeCount,
            COUNT(DISTINCT reply.id) AS ReplyCount,
            MAX(CASE WHEN myLike.AppUserId IS NULL THEN 0 ELSE 1 END) AS LikedByMe
        FROM bee_SpendBeeMerchantPhotoComment AS comment
        INNER JOIN bee_AppUser AS user ON user.id = comment.AppUserId
        LEFT JOIN bee_SpendBeeMerchantPhotoCommentLike AS likeRow ON likeRow.CommentId = comment.id
        LEFT JOIN bee_SpendBeeMerchantPhotoCommentLike AS myLike ON myLike.CommentId = comment.id AND myLike.AppUserId = @AppUserId
        LEFT JOIN bee_SpendBeeMerchantPhotoComment AS reply ON reply.ParentCommentId = comment.id AND reply.Status = 'Visible'
        WHERE comment.id = @CommentId
            AND comment.Status = 'Visible'
        GROUP BY comment.id, comment.PhotoId, comment.ParentCommentId, comment.Body, comment.CreatedAtUtc,
            user.id, user.DisplayName, user.AvatarUrl
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@CommentId", MySqlDbType.Int64).Value = commentId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken)
        ? MapSpendBeePhotoComment(reader)
        : null;
}

static async Task<(IReadOnlyList<object> Items, object Page)> LoadSpendBeePhotoCommentsAsync(
    MySqlConnection connection,
    long photoId,
    int appUserId,
    int? limit,
    long? beforeId,
    CancellationToken cancellationToken)
{
    var pageSize = Math.Clamp(limit ?? 20, 1, 60);
    const string sql = """
        SELECT comment.id, comment.PhotoId, comment.ParentCommentId, comment.Body, comment.CreatedAtUtc,
            user.id AS UserId, user.DisplayName, user.AvatarUrl,
            COUNT(DISTINCT likeRow.AppUserId) AS LikeCount,
            COUNT(DISTINCT reply.id) AS ReplyCount,
            MAX(CASE WHEN myLike.AppUserId IS NULL THEN 0 ELSE 1 END) AS LikedByMe
        FROM bee_SpendBeeMerchantPhotoComment AS comment
        INNER JOIN bee_AppUser AS user ON user.id = comment.AppUserId
        LEFT JOIN bee_SpendBeeMerchantPhotoCommentLike AS likeRow ON likeRow.CommentId = comment.id
        LEFT JOIN bee_SpendBeeMerchantPhotoCommentLike AS myLike ON myLike.CommentId = comment.id AND myLike.AppUserId = @AppUserId
        LEFT JOIN bee_SpendBeeMerchantPhotoComment AS reply ON reply.ParentCommentId = comment.id AND reply.Status = 'Visible'
        WHERE comment.PhotoId = @PhotoId
            AND comment.Status = 'Visible'
            AND comment.ParentCommentId IS NULL
            AND (@BeforeId IS NULL OR comment.id < @BeforeId)
        GROUP BY comment.id, comment.PhotoId, comment.ParentCommentId, comment.Body, comment.CreatedAtUtc,
            user.id, user.DisplayName, user.AvatarUrl
        ORDER BY comment.id DESC
        LIMIT @Limit;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@PhotoId", MySqlDbType.Int64).Value = photoId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@BeforeId", MySqlDbType.Int64).Value = beforeId.HasValue ? beforeId.Value : DBNull.Value;
    command.Parameters.Add("@Limit", MySqlDbType.Int32).Value = pageSize + 1;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var items = new List<object>();
    long? nextBeforeId = null;
    while (await reader.ReadAsync(cancellationToken))
    {
        var id = reader.GetInt64(reader.GetOrdinal("id"));
        if (items.Count >= pageSize)
        {
            nextBeforeId = id;
            break;
        }

        items.Add(MapSpendBeePhotoComment(reader));
    }

    return (items, new { limit = pageSize, nextBeforeId });
}

static object MapSpendBeePhotoComment(MySqlDataReader reader)
{
    return new
    {
        id = reader.GetInt64(reader.GetOrdinal("id")),
        photoId = reader.GetInt64(reader.GetOrdinal("PhotoId")),
        parentCommentId = reader.IsDBNull(reader.GetOrdinal("ParentCommentId")) ? (long?)null : reader.GetInt64(reader.GetOrdinal("ParentCommentId")),
        body = reader["Body"] as string,
        likeCount = Convert.ToInt32(reader["LikeCount"]),
        replyCount = Convert.ToInt32(reader["ReplyCount"]),
        likedByMe = Convert.ToInt32(reader["LikedByMe"]) == 1,
        createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")).ToString("O"),
        user = new
        {
            id = reader.GetInt32(reader.GetOrdinal("UserId")),
            displayName = reader["DisplayName"] as string,
            avatarUrl = reader["AvatarUrl"] as string
        }
    };
}

static async Task<(int LikeCount, bool LikedByMe)> GetSpendBeeCommentLikeSummaryAsync(
    MySqlConnection connection,
    long commentId,
    int appUserId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT COUNT(*) AS LikeCount,
            COALESCE(MAX(CASE WHEN AppUserId = @AppUserId THEN 1 ELSE 0 END), 0) AS LikedByMe
        FROM bee_SpendBeeMerchantPhotoCommentLike
        WHERE CommentId = @CommentId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@CommentId", MySqlDbType.Int64).Value = commentId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return (0, false);
    }

    return (Convert.ToInt32(reader["LikeCount"]), Convert.ToInt32(reader["LikedByMe"]) == 1);
}

static async Task<(IReadOnlyList<object> Items, object Page)> LoadSpendBeeReceiptListAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long? merchantId,
    long? receiptGroupId,
    int? limit,
    long? beforeId,
    string publicRequestBaseUrl,
    CancellationToken cancellationToken)
{
    var pageSize = Math.Clamp(limit ?? 20, 1, 100);
    var merchantCoverUrlSql = BuildSpendBeeMerchantCoverUrlSql("merchant");
    var sql = $"""
        SELECT receipt.id, receipt.Status, receipt.ReceiptType, receipt.FulfillmentType,
            receipt.MerchantName, receipt.MerchantAddress, receipt.PlatformOrderNumber,
            receipt.PurchasedAtUtc, receipt.OrderedAtUtc, receipt.PickupAtUtc, receipt.DeliveredAtUtc,
            receipt.Currency, receipt.Subtotal, receipt.Tax, receipt.DeliveryFee, receipt.ServiceFee,
            receipt.PlatformDiscount, receipt.Total,
            receipt.OverallConfidence, receipt.EstimatedErrorRate, receipt.CreatedAtUtc,
            merchant.id AS MerchantId, merchant.Name AS MerchantDisplayName,
            merchant.Address AS MerchantDisplayAddress,
            {merchantCoverUrlSql} AS MerchantCoverImageUrl,
            platform.id AS PlatformId, platform.Name AS PlatformName, platform.DisplayName AS PlatformDisplayName,
            platform.PlatformType AS PlatformType, platform.LogoUrl AS PlatformLogoUrl,
            platform.WebsiteUrl AS PlatformWebsiteUrl,
            COUNT(DISTINCT image.id) AS ImageCount,
            MIN(image.id) AS FirstImageId,
            COUNT(DISTINCT item.id) AS LineItemCount
        FROM bee_SpendBeeReceipt AS receipt
        LEFT JOIN bee_SpendBeeReceiptGroupReceipt AS groupReceipt
            ON groupReceipt.ReceiptId = receipt.id
            AND @ReceiptGroupId IS NOT NULL
            AND groupReceipt.ReceiptGroupId = @ReceiptGroupId
        LEFT JOIN bee_SpendBeeMerchant AS merchant ON merchant.id = receipt.MerchantId
        LEFT JOIN bee_SpendBeePlatform AS platform ON platform.id = receipt.PlatformId
        LEFT JOIN bee_SpendBeeReceiptImage AS image ON image.ReceiptId = receipt.id
        LEFT JOIN bee_SpendBeeReceiptLineItem AS item ON item.ReceiptId = receipt.id
        WHERE receipt.ProjectId = @ProjectId
            AND receipt.AppUserId = @AppUserId
            AND (@MerchantId IS NULL OR receipt.MerchantId = @MerchantId)
            AND (@ReceiptGroupId IS NULL OR groupReceipt.ReceiptGroupId IS NOT NULL)
            AND (@BeforeId IS NULL OR receipt.id < @BeforeId)
        GROUP BY receipt.id, receipt.Status, receipt.ReceiptType, receipt.FulfillmentType,
            receipt.MerchantName, receipt.MerchantAddress, receipt.PlatformOrderNumber,
            receipt.PurchasedAtUtc, receipt.OrderedAtUtc, receipt.PickupAtUtc, receipt.DeliveredAtUtc,
            receipt.Currency, receipt.Subtotal, receipt.Tax, receipt.DeliveryFee, receipt.ServiceFee,
            receipt.PlatformDiscount, receipt.Total,
            receipt.OverallConfidence, receipt.EstimatedErrorRate, receipt.CreatedAtUtc,
            merchant.id, merchant.ProjectId, merchant.Name, merchant.Address, merchant.AiCoverImageUrl, merchant.GooglePhotoUri,
            platform.id, platform.Name, platform.DisplayName, platform.PlatformType, platform.LogoUrl, platform.WebsiteUrl
        ORDER BY receipt.id DESC
        LIMIT @Limit;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@MerchantId", MySqlDbType.Int64).Value = merchantId.HasValue ? merchantId.Value : DBNull.Value;
    command.Parameters.Add("@ReceiptGroupId", MySqlDbType.Int64).Value = receiptGroupId.HasValue ? receiptGroupId.Value : DBNull.Value;
    command.Parameters.Add("@BeforeId", MySqlDbType.Int64).Value = beforeId.HasValue ? beforeId.Value : DBNull.Value;
    command.Parameters.Add("@Limit", MySqlDbType.Int32).Value = pageSize + 1;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var receipts = new List<object>();
    long? nextBeforeId = null;
    while (await reader.ReadAsync(cancellationToken))
    {
        var receiptId = reader.GetInt64(reader.GetOrdinal("id"));
        if (receipts.Count >= pageSize)
        {
            nextBeforeId = receiptId;
            break;
        }

        var receiptMerchantId = reader.IsDBNull(reader.GetOrdinal("MerchantId"))
            ? (long?)null
            : reader.GetInt64(reader.GetOrdinal("MerchantId"));
        var merchantCoverImageUrl = reader["MerchantCoverImageUrl"] as string;
        var firstImageId = reader.IsDBNull(reader.GetOrdinal("FirstImageId"))
            ? (long?)null
            : reader.GetInt64(reader.GetOrdinal("FirstImageId"));
        var receiptImageUrl = firstImageId is null
            ? null
            : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/receipts/{receiptId}/images/{firstImageId.Value}");
        receipts.Add(new
        {
            id = receiptId,
            merchantId = receiptMerchantId,
            status = reader["Status"] as string,
            receiptType = reader["ReceiptType"] as string,
            fulfillmentType = reader["FulfillmentType"] as string,
            merchantName = reader["MerchantName"] as string,
            merchantAddress = reader["MerchantAddress"] as string,
            platformOrderNumber = reader["PlatformOrderNumber"] as string,
            purchasedAtUtc = reader.IsDBNull(reader.GetOrdinal("PurchasedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PurchasedAtUtc")).ToString("O"),
            orderedAtUtc = reader.IsDBNull(reader.GetOrdinal("OrderedAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("OrderedAtUtc")).ToString("O"),
            pickupAtUtc = reader.IsDBNull(reader.GetOrdinal("PickupAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("PickupAtUtc")).ToString("O"),
            deliveredAtUtc = reader.IsDBNull(reader.GetOrdinal("DeliveredAtUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("DeliveredAtUtc")).ToString("O"),
            currency = reader["Currency"] as string,
            subtotal = reader.IsDBNull(reader.GetOrdinal("Subtotal")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Subtotal")),
            tax = reader.IsDBNull(reader.GetOrdinal("Tax")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Tax")),
            deliveryFee = reader.IsDBNull(reader.GetOrdinal("DeliveryFee")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("DeliveryFee")),
            serviceFee = reader.IsDBNull(reader.GetOrdinal("ServiceFee")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("ServiceFee")),
            platformDiscount = reader.IsDBNull(reader.GetOrdinal("PlatformDiscount")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("PlatformDiscount")),
            total = reader.IsDBNull(reader.GetOrdinal("Total")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("Total")),
            overallConfidence = reader.IsDBNull(reader.GetOrdinal("OverallConfidence")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("OverallConfidence")),
            estimatedErrorRate = reader.IsDBNull(reader.GetOrdinal("EstimatedErrorRate")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("EstimatedErrorRate")),
            createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")).ToString("O"),
            imageCount = Convert.ToInt32(reader["ImageCount"]),
            imageUrl = receiptImageUrl,
            firstImageUrl = receiptImageUrl,
            lineItemCount = Convert.ToInt32(reader["LineItemCount"]),
            merchant = receiptMerchantId is null
                ? null
                : new
                {
                    id = receiptMerchantId.Value,
                    name = reader["MerchantDisplayName"] as string,
                    address = reader["MerchantDisplayAddress"] as string,
                    coverImageUrl = string.IsNullOrWhiteSpace(merchantCoverImageUrl) ? null : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/merchants/{receiptMerchantId.Value}/cover"),
                    coverImageApiUrl = string.IsNullOrWhiteSpace(merchantCoverImageUrl) ? null : BuildPublicApiUrl(publicRequestBaseUrl, $"/api/spendbee/v1/merchants/{receiptMerchantId.Value}/cover")
                },
            platform = reader.IsDBNull(reader.GetOrdinal("PlatformId"))
                ? null
                : new
                {
                    id = reader.GetInt64(reader.GetOrdinal("PlatformId")),
                    name = reader["PlatformName"] as string,
                    displayName = reader["PlatformDisplayName"] as string,
                    platformType = reader["PlatformType"] as string,
                    logoUrl = reader["PlatformLogoUrl"] as string,
                    websiteUrl = reader["PlatformWebsiteUrl"] as string
                }
        });
    }

    return (receipts, new
    {
        limit = pageSize,
        nextBeforeId
    });
}

static string StripDataUrlPrefix(string value)
{
    var commaIndex = value.IndexOf(',', StringComparison.Ordinal);
    return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0
        ? value[(commaIndex + 1)..]
        : value;
}

static string? NormalizeImageContentType(string? contentType)
{
    return contentType?.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "image/jpeg",
        "image/png" => "image/png",
        "image/webp" => "image/webp",
        _ => null
    };
}

static string ExtractOpenAIOutputText(JsonElement root)
{
    foreach (var output in root.GetProperty("output").EnumerateArray())
    {
        if (!output.TryGetProperty("content", out var content))
        {
            continue;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) &&
                type.GetString() == "output_text" &&
                item.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? "{}";
            }
        }
    }

    return "{}";
}

static async Task SaveSmsDeliveryAsync(
    MySqlConnection connection,
    int projectId,
    long verificationCodeId,
    string phoneNumber,
    string purpose,
    SmsSendResult smsResult,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_AppSmsDelivery
            (ProjectId, VerificationCodeId, PhoneNumber, Purpose, ProviderMessageId, RequestStatus,
             DeliveryStatus, ErrorCode, ErrorText, RawResponseJson)
        VALUES
            (@ProjectId, @VerificationCodeId, @PhoneNumber, @Purpose, @ProviderMessageId, @RequestStatus,
             @DeliveryStatus, @ErrorCode, @ErrorText, @RawResponseJson);
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@VerificationCodeId", MySqlDbType.Int64).Value = verificationCodeId;
    command.Parameters.Add("@PhoneNumber", MySqlDbType.VarChar, 40).Value = phoneNumber;
    command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = purpose;
    command.Parameters.Add("@ProviderMessageId", MySqlDbType.VarChar, 120).Value =
        DbNullable(NormalizeBounded(smsResult.ProviderMessageId, 120));
    command.Parameters.Add("@RequestStatus", MySqlDbType.VarChar, 40).Value =
        smsResult.Success ? "Accepted" : "Rejected";
    command.Parameters.Add("@DeliveryStatus", MySqlDbType.VarChar, 80).Value =
        DbNullable(NormalizeBounded(smsResult.ProviderStatus, 80));
    command.Parameters.Add("@ErrorCode", MySqlDbType.VarChar, 40).Value =
        DbNullable(NormalizeBounded(smsResult.ProviderStatus, 40));
    command.Parameters.Add("@ErrorText", MySqlDbType.VarChar, 500).Value =
        DbNullable(NormalizeBounded(smsResult.ErrorText, 500));
    command.Parameters.Add("@RawResponseJson", MySqlDbType.JSON).Value =
        string.IsNullOrWhiteSpace(smsResult.RawResponseJson) ? DBNull.Value : smsResult.RawResponseJson;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task SaveEmailDeliveryAsync(
    MySqlConnection connection,
    int projectId,
    long verificationCodeId,
    string email,
    string purpose,
    EmailSendResult result,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_AppEmailDelivery
            (ProjectId, VerificationCodeId, Email, Purpose, Provider, ProviderMessageId, RequestStatus, ErrorText)
        VALUES
            (@ProjectId, @VerificationCodeId, @Email, @Purpose, @Provider, @ProviderMessageId, @RequestStatus, @ErrorText);
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@VerificationCodeId", MySqlDbType.Int64).Value = verificationCodeId;
    command.Parameters.Add("@Email", MySqlDbType.VarChar, 150).Value = email;
    command.Parameters.Add("@Purpose", MySqlDbType.VarChar, 40).Value = purpose;
    command.Parameters.Add("@Provider", MySqlDbType.VarChar, 40).Value = result.Provider;
    command.Parameters.Add("@ProviderMessageId", MySqlDbType.VarChar, 150).Value = DbNullable(NormalizeBounded(result.ProviderMessageId, 150));
    command.Parameters.Add("@RequestStatus", MySqlDbType.VarChar, 40).Value = result.Success ? "Sent" : "Failed";
    command.Parameters.Add("@ErrorText", MySqlDbType.VarChar, 500).Value = DbNullable(NormalizeBounded(result.ErrorText, 500));
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<EmailSendResult> SendVerificationEmailAsync(
    HttpClient httpClient,
    IConfiguration configuration,
    string email,
    string code,
    CancellationToken cancellationToken)
{
    const string provider = "AmazonSes";
    var fromAddress = configuration["EmailAuth:FromAddress"];
    if (string.IsNullOrWhiteSpace(fromAddress))
    {
        return new EmailSendResult(false, provider, null, "EmailAuth from address is not configured.");
    }

    var accessKeyId = configuration["EmailAuth:SesAccessKeyId"];
    var secretAccessKey = configuration["EmailAuth:SesSecretAccessKey"];
    var region = configuration["EmailAuth:SesRegion"];
    if (string.IsNullOrWhiteSpace(accessKeyId))
    {
        accessKeyId = configuration["S3Storage:AccessKeyId"];
    }

    if (string.IsNullOrWhiteSpace(secretAccessKey))
    {
        secretAccessKey = configuration["S3Storage:SecretAccessKey"];
    }

    if (string.IsNullOrWhiteSpace(region))
    {
        region = configuration["S3Storage:Region"] ?? "ap-southeast-2";
    }

    if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey) || string.IsNullOrWhiteSpace(region))
    {
        return new EmailSendResult(false, provider, null, "Amazon SES access key, secret, and region must be configured.");
    }

    var payload = new
    {
        FromEmailAddress = fromAddress,
        Destination = new { ToAddresses = new[] { email } },
        Content = new
        {
            Simple = new
            {
                Subject = new { Data = "Your Sentribee verification code", Charset = "UTF-8" },
                Body = new
                {
                    Html = new { Data = BuildVerificationEmailHtml(code), Charset = "UTF-8" },
                    Text = new { Data = BuildVerificationEmailText(code), Charset = "UTF-8" }
                }
            }
        }
    };
    var payloadJson = JsonSerializer.Serialize(payload);
    var endpoint = new Uri($"https://email.{region}.amazonaws.com/v2/email/outbound-emails");
    var request = BuildAwsSignedJsonRequest(
        HttpMethod.Post,
        endpoint,
        "ses",
        region,
        accessKeyId,
        secretAccessKey,
        payloadJson);

    try
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new EmailSendResult(true, provider, ExtractSesMessageId(responseText), null);
        }

        return new EmailSendResult(
            false,
            provider,
            null,
            $"Amazon SES returned HTTP {(int)response.StatusCode}: {TrimDiagnostic(responseText)}");
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return new EmailSendResult(false, provider, null, exception.Message);
    }
}

static string? ExtractSesMessageId(string responseText)
{
    if (string.IsNullOrWhiteSpace(responseText))
    {
        return null;
    }

    try
    {
        using var document = JsonDocument.Parse(responseText);
        return document.RootElement.TryGetProperty("MessageId", out var messageIdElement)
            ? messageIdElement.GetString()
            : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

static string BuildVerificationEmailHtml(string code)
{
    return $$"""
        <!doctype html>
        <html>
        <body style="margin:0;background:#f6f7fb;font-family:Arial,Helvetica,sans-serif;color:#172033;">
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f6f7fb;padding:32px 12px;">
            <tr>
              <td align="center">
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:520px;background:#ffffff;border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;">
                  <tr>
                    <td style="padding:28px 32px 14px;">
                      <div style="font-size:13px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#6366f1;">Sentribee</div>
                      <h1 style="margin:12px 0 8px;font-size:24px;line-height:1.25;color:#111827;">Verification code</h1>
                      <p style="margin:0;color:#4b5563;font-size:15px;line-height:1.6;">Use this code to continue signing in to your Sentribee app account.</p>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:12px 32px 4px;">
                      <div style="background:#f3f4ff;border:1px solid #d9dcff;border-radius:10px;padding:18px;text-align:center;font-size:32px;font-weight:700;letter-spacing:.18em;color:#3730a3;">{{WebUtility.HtmlEncode(code)}}</div>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:18px 32px 30px;">
                      <p style="margin:0;color:#6b7280;font-size:13px;line-height:1.6;">This code expires in 10 minutes. If you did not request it, you can safely ignore this email.</p>
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;
}

static string BuildVerificationEmailText(string code)
{
    return $"Your Sentribee verification code is {code}. It expires in 10 minutes. If you did not request it, you can safely ignore this email.";
}

static string TrimDiagnostic(string? value, int maxLength = 500)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var trimmed = value.Trim();
    return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
}

static string BuildPublicCallbackUrl(HttpRequest request, IConfiguration configuration, string path)
{
    var configured = configuration["AppApi:PublicBaseUrl"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return $"{configured.TrimEnd('/')}{path}";
    }

    var proto = request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? request.Scheme;
    var host = request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? request.Host.Value;
    return $"{proto}://{host}{path}";
}

static async Task<SmsSendResult> SendVonageSmsAsync(
    HttpClient httpClient,
    IConfiguration configuration,
    HttpRequest request,
    string phoneNumber,
    string text,
    CancellationToken cancellationToken)
{
    var apiKey = configuration["Vonage:ApiKey"];
    var apiSecret = configuration["Vonage:ApiSecret"];
    var from = string.IsNullOrWhiteSpace(configuration["Vonage:From"])
        ? "Sentribee"
        : configuration["Vonage:From"]!;
    if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
    {
        return new SmsSendResult(false, "Vonage API key and secret are not configured.", null, null, null, null);
    }

    var callbackUrl = BuildPublicCallbackUrl(request, configuration, "/api/app/sms/delivery-receipt");
    using var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["api_key"] = apiKey,
        ["api_secret"] = apiSecret,
        ["to"] = phoneNumber.TrimStart('+'),
        ["from"] = from,
        ["text"] = text,
        ["status-report-req"] = "1",
        ["callback"] = callbackUrl
    });
    using var response = await httpClient.PostAsync("https://rest.nexmo.com/sms/json", content, cancellationToken);
    var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return new SmsSendResult(
            false,
            $"Vonage SMS failed with HTTP {(int)response.StatusCode}: {responseText}",
            null,
            response.StatusCode.ToString(),
            null,
            responseText);
    }

    try
    {
        using var document = JsonDocument.Parse(responseText);
        var messages = document.RootElement.GetProperty("messages");
        var message = messages[0];
        var status = message.GetProperty("status").GetString();
        var messageId = message.TryGetProperty("message-id", out var messageIdElement)
            ? messageIdElement.GetString()
            : null;
        if (status != "0")
        {
            var errorText = message.TryGetProperty("error-text", out var error)
                ? error.GetString()
                : "Vonage SMS was rejected.";
            return new SmsSendResult(false, errorText ?? "Vonage SMS was rejected.", messageId, status, errorText, responseText);
        }

        return new SmsSendResult(true, "SMS sent.", messageId, status, null, responseText);
    }
    catch (JsonException)
    {
        return new SmsSendResult(false, "Vonage SMS returned an unreadable response.", null, null, null, responseText);
    }
}

static async Task<IReadOnlyList<AppDeviceSummary>> QueryAppBoundDevicesAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    string? deviceCode,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT device.id, device.DeviceCode, device.DeviceName, device.Address, device.Latitude, device.Longitude,
            device.ServerResourceInstanceName,
            heartbeat.RuntimeStatus, heartbeat.DeviceStatus, heartbeat.DetailJson, heartbeat.ReportedAtUtc,
            todayStat.PeopleCount, todayStat.MachineryVehicleCount, todayStat.PpeComplianceRate,
            COALESCE(endpointCounts.CameraCount, 0) AS CameraCount,
            COALESCE(eventCounts.RiskCount, 0) AS RiskCount
        FROM bee_EdgeDevice AS device
        INNER JOIN bee_EdgeDeviceUserBinding AS binding
            ON binding.EdgeDeviceId = device.id
            AND binding.AppUserId = @AppUserId
        LEFT JOIN (
            SELECT latest.ProjectId, latest.EdgeDeviceId, latest.RuntimeStatus, latest.DeviceStatus,
                latest.DetailJson, latest.ReportedAtUtc
            FROM bee_EdgeAiHeartbeat AS latest
            INNER JOIN (
                SELECT EdgeDeviceId, MAX(id) AS LatestHeartbeatId
                FROM bee_EdgeAiHeartbeat
                GROUP BY EdgeDeviceId
            ) AS grouped ON grouped.LatestHeartbeatId = latest.id
        ) AS heartbeat ON heartbeat.EdgeDeviceId = device.id
        LEFT JOIN (
            SELECT EdgeDeviceId, COUNT(*) AS CameraCount
            FROM bee_EdgeDeviceEndpoint AS endpoint
            LEFT JOIN bee_DeviceCatalog AS catalog ON catalog.id = endpoint.CatalogDeviceId
            WHERE endpoint.AccessUrl LIKE 'rtsp://%'
                OR endpoint.DeviceName LIKE '%camera%'
                OR catalog.CatalogName LIKE '%camera%'
            GROUP BY EdgeDeviceId
        ) AS endpointCounts ON endpointCounts.EdgeDeviceId = device.id
        LEFT JOIN (
            SELECT EdgeDeviceId, COUNT(*) AS RiskCount
            FROM bee_EdgeEvent
            WHERE Status IN ('Severe Danger', 'Ordinary Risk', 'Real Risk')
            GROUP BY EdgeDeviceId
        ) AS eventCounts ON eventCounts.EdgeDeviceId = device.id
        LEFT JOIN bee_EdgeDeviceDailyStat AS todayStat
            ON todayStat.ProjectId = device.ProjectId
            AND todayStat.EdgeDeviceId = device.id
            AND todayStat.StatDate = UTC_DATE()
        WHERE device.ProjectId = @ProjectId
            AND (@DeviceCode IS NULL OR device.DeviceCode = @DeviceCode)
        ORDER BY device.DeviceName;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@DeviceCode", MySqlDbType.VarChar, 40).Value =
        string.IsNullOrWhiteSpace(deviceCode) ? DBNull.Value : deviceCode;
    var devices = new List<AppDeviceSummary>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var detailJson = reader["DetailJson"] as string;
        var reportedAtUtc = reader["ReportedAtUtc"] is DBNull
            ? (DateTime?)null
            : reader.GetDateTime(reader.GetOrdinal("ReportedAtUtc"));
        var runtimeStatus = reader["RuntimeStatus"] as string;
        var deviceStatus = reader["DeviceStatus"] as string;
        devices.Add(new AppDeviceSummary(
            reader.GetInt32(reader.GetOrdinal("id")),
            reader["DeviceCode"] as string ?? string.Empty,
            reader["DeviceName"] as string ?? string.Empty,
            reader["Address"] as string ?? string.Empty,
            ResolveAppDeviceStatus(runtimeStatus, deviceStatus, reportedAtUtc),
            reader.GetInt32(reader.GetOrdinal("CameraCount")),
            reader.GetInt32(reader.GetOrdinal("RiskCount")),
            reader["PeopleCount"] is DBNull
                ? ExtractIntMetric(detailJson, "recognizableWorkerCount", "workerCount", "currentWorkerCount", "peopleCount")
                : reader.GetInt32(reader.GetOrdinal("PeopleCount")),
            reader["PpeComplianceRate"] is DBNull
                ? ExtractDecimalMetric(detailJson, "ppeComplianceRate", "ppeQualifiedRate", "ppePassRate")
                : reader.GetDecimal(reader.GetOrdinal("PpeComplianceRate")),
            reader["MachineryVehicleCount"] is DBNull
                ? ExtractIntMetric(detailJson, "heavyEquipmentCount", "heavyEquipment", "plantCount")
                : reader.GetInt32(reader.GetOrdinal("MachineryVehicleCount")),
            reader["ServerResourceInstanceName"] as string,
            reader["Latitude"] is DBNull ? null : reader.GetDecimal(reader.GetOrdinal("Latitude")),
            reader["Longitude"] is DBNull ? null : reader.GetDecimal(reader.GetOrdinal("Longitude")),
            reportedAtUtc));
    }

    return devices;
}

static async Task UpsertDailyStatFromHeartbeatAsync(
    MySqlConnection connection,
    int projectId,
    int edgeDeviceId,
    DateTime reportedAtUtc,
    EdgeHeartbeatPayload payload,
    string? detailJson,
    CancellationToken cancellationToken)
{
    var peopleCount = payload.PeopleCount ?? ExtractIntMetric(detailJson, "peopleCount", "personCount", "currentPeopleCount", "workerCount");
    var braceletCount = payload.BraceletCount ?? ExtractIntMetric(detailJson, "braceletCount", "bluetoothBraceletCount", "wristbandCount", "bleBraceletCount");
    var machineryVehicleCount = payload.MachineryVehicleCount ?? ExtractIntMetric(detailJson, "machineryVehicleCount", "vehicleCount", "heavyEquipmentCount", "plantCount");
    const string sql = """
        INSERT INTO bee_EdgeDeviceDailyStat
            (ProjectId, EdgeDeviceId, StatDate, PeopleCount, BraceletCount, MachineryVehicleCount,
             LastHeartbeatAtUtc, DetailJson)
        VALUES
            (@ProjectId, @EdgeDeviceId, @StatDate, @PeopleCount, @BraceletCount, @MachineryVehicleCount,
             @LastHeartbeatAtUtc, @DetailJson)
        ON DUPLICATE KEY UPDATE
            PeopleCount = VALUES(PeopleCount),
            BraceletCount = VALUES(BraceletCount),
            MachineryVehicleCount = VALUES(MachineryVehicleCount),
            LastHeartbeatAtUtc = VALUES(LastHeartbeatAtUtc),
            DetailJson = VALUES(DetailJson),
            UpdatedAtUtc = UTC_TIMESTAMP(6);
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = edgeDeviceId;
    command.Parameters.Add("@StatDate", MySqlDbType.Date).Value = reportedAtUtc.Date;
    command.Parameters.Add("@PeopleCount", MySqlDbType.Int32).Value = peopleCount;
    command.Parameters.Add("@BraceletCount", MySqlDbType.Int32).Value = braceletCount;
    command.Parameters.Add("@MachineryVehicleCount", MySqlDbType.Int32).Value = machineryVehicleCount;
    command.Parameters.Add("@LastHeartbeatAtUtc", MySqlDbType.DateTime).Value = reportedAtUtc;
    command.Parameters.Add("@DetailJson", MySqlDbType.JSON).Value = string.IsNullOrWhiteSpace(detailJson) ? DBNull.Value : detailJson;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task RefreshDailyStatFromEventAsync(
    MySqlConnection connection,
    int projectId,
    int edgeDeviceId,
    DateTime eventTimeUtc,
    CancellationToken cancellationToken)
{
    var statDate = eventTimeUtc.Date;
    var nextDate = statDate.AddDays(1);
    const string sql = """
        INSERT INTO bee_EdgeDeviceDailyStat
            (ProjectId, EdgeDeviceId, StatDate, RiskEventCount, RiskPersonCount,
             TopRiskSubjectKey, TopRiskSubjectRiskCount, PpeComplianceRate, LastEventAtUtc)
        SELECT
            @ProjectId,
            @EdgeDeviceId,
            @StatDate,
            COALESCE(eventCounts.RiskEventCount, 0),
            COALESCE(eventCounts.RiskPersonCount, 0),
            topSubject.SubjectKey,
            COALESCE(topSubject.RiskCount, 0),
            latestAnalysis.PpeComplianceRate,
            eventCounts.LastEventAtUtc
        FROM (
            SELECT
                COUNT(*) AS RiskEventCount,
                COALESCE(SUM(COALESCE(analysis.RiskPersonCount, 0)), 0) AS RiskPersonCount,
                MAX(evt.EventTimeUtc) AS LastEventAtUtc
            FROM bee_EdgeEvent AS evt
            LEFT JOIN bee_EdgeEventAnalysis AS analysis ON analysis.EdgeEventId = evt.id
            WHERE evt.EdgeDeviceId = @EdgeDeviceId
                AND evt.EventTimeUtc >= @StartUtc
                AND evt.EventTimeUtc < @EndUtc
                AND evt.Status IN ('Severe Danger', 'Ordinary Risk', 'Real Risk')
        ) AS eventCounts
        LEFT JOIN (
            SELECT subject.SubjectKey, COUNT(*) AS RiskCount
            FROM bee_EdgeEventSubject AS subject
            INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
            WHERE evt.EdgeDeviceId = @EdgeDeviceId
                AND evt.EventTimeUtc >= @StartUtc
                AND evt.EventTimeUtc < @EndUtc
                AND subject.IsRisk = 1
                AND subject.SubjectType = 'Person'
            GROUP BY subject.SubjectKey
            ORDER BY RiskCount DESC, subject.SubjectKey
            LIMIT 1
        ) AS topSubject ON 1 = 1
        LEFT JOIN (
            SELECT analysis.PpeComplianceRate
            FROM bee_EdgeEvent AS evt
            INNER JOIN bee_EdgeEventAnalysis AS analysis ON analysis.EdgeEventId = evt.id
            WHERE evt.EdgeDeviceId = @EdgeDeviceId
                AND evt.EventTimeUtc >= @StartUtc
                AND evt.EventTimeUtc < @EndUtc
                AND analysis.PpeComplianceRate IS NOT NULL
            ORDER BY evt.EventTimeUtc DESC, evt.id DESC
            LIMIT 1
        ) AS latestAnalysis ON 1 = 1
        ON DUPLICATE KEY UPDATE
            RiskEventCount = VALUES(RiskEventCount),
            RiskPersonCount = VALUES(RiskPersonCount),
            TopRiskSubjectKey = VALUES(TopRiskSubjectKey),
            TopRiskSubjectRiskCount = VALUES(TopRiskSubjectRiskCount),
            PpeComplianceRate = VALUES(PpeComplianceRate),
            LastEventAtUtc = VALUES(LastEventAtUtc),
            UpdatedAtUtc = UTC_TIMESTAMP(6);
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = edgeDeviceId;
    command.Parameters.Add("@StatDate", MySqlDbType.Date).Value = statDate;
    command.Parameters.Add("@StartUtc", MySqlDbType.DateTime).Value = statDate;
    command.Parameters.Add("@EndUtc", MySqlDbType.DateTime).Value = nextDate;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static EdgeEventAnalysisResult BuildEventAnalysisFromUpload(EdgeEventUploadPayload payload)
{
    if (payload.Analysis is { } supplied)
    {
        return BuildEventAnalysisFromPayload(supplied);
    }

    var peopleCount = ExtractIntMetricFromJson(payload.DetectionJson, "peopleCount", "personCount", "persons", "workers");
    var vehicleCount = ExtractIntMetricFromJson(payload.DetectionJson, "machineryVehicleCount", "vehicleCount", "heavyEquipmentCount", "plantCount");
    var toolCount = ExtractIntMetricFromJson(payload.DetectionJson, "toolCount", "equipmentCount");
    var ppeCompliant = ExtractIntMetricFromJson(payload.DetectionJson, "ppeCompliantPeopleCount", "ppeOkPeopleCount");
    var riskPeople = ExtractIntMetricFromJson(payload.DetectionJson, "riskPersonCount", "ppeRiskPeopleCount", "violationPeopleCount");
    var ppeRate = ExtractDecimalMetricFromJson(payload.DetectionJson, "ppeComplianceRate", "ppeQualifiedRate", "ppePassRate");
    if (!ppeRate.HasValue && peopleCount > 0)
    {
        ppeRate = Math.Round(ppeCompliant * 100m / peopleCount, 2);
    }

    var text = $"{payload.Title} {payload.Description}".ToLowerInvariant();
    var riskCategory = text.Contains("helmet", StringComparison.Ordinal) || text.Contains("hardhat", StringComparison.Ordinal) || text.Contains("安全帽", StringComparison.Ordinal)
        ? "PPE helmet risk"
        : text.Contains("vehicle", StringComparison.Ordinal) || text.Contains("machine", StringComparison.Ordinal) || text.Contains("机械", StringComparison.Ordinal)
            ? "Plant interaction risk"
            : "Safety risk";
    var severity = ResolveEventStatusFromAnalysis(peopleCount, vehicleCount, riskPeople, null);

    return new EdgeEventAnalysisResult(
        peopleCount,
        vehicleCount,
        toolCount,
        ppeCompliant,
        riskPeople,
        ppeRate,
        riskCategory,
        severity,
        $"Detected {peopleCount} people, {vehicleCount} machinery vehicles, {riskPeople} risk people.",
        payload.DetectionJson?.GetRawText(),
        payload.Subjects ?? []);
}

static EdgeEventAnalysisResult BuildEventAnalysisFromPayload(EdgeEventAnalysisPayload payload)
{
    var peopleCount = payload.PeopleCount ?? payload.Subjects?.Count(subject => IsPersonSubject(subject.SubjectType)) ?? 0;
    var vehicleCount = payload.MachineryVehicleCount ?? 0;
    var toolCount = payload.ToolCount ?? 0;
    var riskPeople = payload.RiskPersonCount ?? payload.Subjects?.Count(subject => IsPersonSubject(subject.SubjectType) && subject.IsRisk) ?? 0;
    var ppeCompliant = payload.PpeCompliantPeopleCount ?? Math.Max(0, peopleCount - riskPeople);
    var ppeRate = payload.PpeComplianceRate ?? (peopleCount > 0 ? Math.Round(ppeCompliant * 100m / peopleCount, 2) : null);
    var severity = ResolveEventStatusFromAnalysis(
        peopleCount,
        vehicleCount,
        riskPeople,
        NormalizeBounded(payload.RiskSeverity, 40));
    var riskCategory = NormalizeBounded(payload.RiskCategory, 120) ?? severity switch
    {
        "Severe Danger" => "Severe site safety danger",
        "Ordinary Risk" => "PPE compliance risk",
        "No Risk" => "Site safety clear",
        "Invalid Event" => "Invalid event",
        _ => "Safety review"
    };
    return new EdgeEventAnalysisResult(
        peopleCount,
        vehicleCount,
        toolCount,
        ppeCompliant,
        riskPeople,
        ppeRate,
        riskCategory,
        severity,
        NormalizeBounded(payload.Summary, 500),
        payload.AnalysisJson?.GetRawText(),
        payload.Subjects ?? []);
}

static string ResolveEventStatusFromAnalysis(
    int peopleCount,
    int machineryVehicleCount,
    int riskPersonCount,
    string? suppliedSeverity)
{
    if (!string.IsNullOrWhiteSpace(suppliedSeverity))
    {
        if (suppliedSeverity.Equals("Severe Danger", StringComparison.OrdinalIgnoreCase) ||
            suppliedSeverity.Equals("Major Risk", StringComparison.OrdinalIgnoreCase) ||
            suppliedSeverity.Equals("Critical", StringComparison.OrdinalIgnoreCase))
        {
            return "Severe Danger";
        }

        if (suppliedSeverity.Equals("Ordinary Risk", StringComparison.OrdinalIgnoreCase) ||
            suppliedSeverity.Equals("Real Risk", StringComparison.OrdinalIgnoreCase))
        {
            return "Ordinary Risk";
        }

        if (suppliedSeverity.Equals("No Risk", StringComparison.OrdinalIgnoreCase))
        {
            return "No Risk";
        }

        if (suppliedSeverity.Equals("Invalid Event", StringComparison.OrdinalIgnoreCase) ||
            suppliedSeverity.Equals("Review", StringComparison.OrdinalIgnoreCase) && peopleCount == 0)
        {
            return "Invalid Event";
        }
    }

    if (riskPersonCount > 0)
    {
        return "Ordinary Risk";
    }

    return peopleCount <= 0 && machineryVehicleCount <= 0 ? "Invalid Event" : "No Risk";
}

static async Task<EdgeEventAnalysisResult> PersistEventSubjectImagesToS3Async(
    EdgeEventAnalysisResult analysis,
    IEdgeImageStorageService imageStorage,
    IConfiguration configuration,
    int eventId,
    int projectId,
    string deviceCode,
    CancellationToken cancellationToken)
{
    if (analysis.Subjects.Count == 0)
    {
        return analysis;
    }

    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
    var updatedSubjects = new List<EdgeEventSubjectPayload>(analysis.Subjects.Count);
    var imageIndex = 0;
    foreach (var subject in analysis.Subjects)
    {
        imageIndex++;
        var cropUrl = await PersistOneSubjectImageToS3Async(
            subject.CropImageUrl,
            imageStorage,
            configuration,
            httpClient,
            eventId,
            projectId,
            deviceCode,
            subject.SubjectKey ?? $"person-{imageIndex:000}",
            "crop",
            cancellationToken);
        var previewUrl = await PersistOneSubjectImageToS3Async(
            subject.PreviewImageUrl,
            imageStorage,
            configuration,
            httpClient,
            eventId,
            projectId,
            deviceCode,
            subject.SubjectKey ?? $"person-{imageIndex:000}",
            "preview",
            cancellationToken);
        updatedSubjects.Add(subject with
        {
            CropImageUrl = cropUrl ?? subject.CropImageUrl,
            PreviewImageUrl = previewUrl ?? subject.PreviewImageUrl
        });
    }

    return analysis with { Subjects = updatedSubjects };
}

static async Task<string?> PersistOneSubjectImageToS3Async(
    string? imageUrl,
    IEdgeImageStorageService imageStorage,
    IConfiguration configuration,
    HttpClient httpClient,
    int eventId,
    int projectId,
    string deviceCode,
    string subjectKey,
    string kind,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(imageUrl) || IsS3Url(imageUrl))
    {
        return null;
    }

    var fetchUrl = ResolveAnalysisArtifactFetchUrl(imageUrl, configuration);
    if (fetchUrl is null)
    {
        return null;
    }

    try
    {
        using var response = await httpClient.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
        var extension = GuessImageExtension(contentType, fetchUrl);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var stored = await imageStorage.UploadAsync(
            stream,
            contentType,
            extension,
            $"edge-event-analysis/{projectId}/{deviceCode}/{eventId}/{NormalizeStorageSegment(subjectKey)}/{kind}",
            cancellationToken);
        return stored.PublicUrl;
    }
    catch
    {
        return null;
    }
}

static string? ResolveAnalysisArtifactFetchUrl(string imageUrl, IConfiguration configuration)
{
    if (imageUrl.Contains("/api/edge-analysis-artifacts/", StringComparison.OrdinalIgnoreCase))
    {
        var marker = "/api/edge-analysis-artifacts/";
        var index = imageUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var artifactPath = imageUrl[(index + marker.Length)..].TrimStart('/');
        var remoteBaseUrl = configuration["EdgeEventAutoAnalysis:RemoteBaseUrl"];
        if (string.IsNullOrWhiteSpace(remoteBaseUrl))
        {
            return null;
        }

        return $"{remoteBaseUrl.TrimEnd('/')}/artifacts/{artifactPath}";
    }

    if (imageUrl.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase)
        || imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        return imageUrl;
    }

    return null;
}

static async Task<IResult> StreamProtectedAnalysisImageAsync(
    string imageUrl,
    IConfiguration configuration,
    S3StorageOptions s3Options,
    HttpClient httpClient,
    CancellationToken cancellationToken,
    string? fileDownloadName = null,
    string? contentTypeOverride = null)
{
    if (TryResolveS3Uri(imageUrl, s3Options, out var s3Uri))
    {
        ValidateS3Options(s3Options);
        var request = BuildS3Request(HttpMethod.Get, s3Uri, null, s3Options, "UNSIGNED-PAYLOAD");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)response.StatusCode);
        }

        var contentType = contentTypeOverride ?? response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Results.File(bytes, contentType, fileDownloadName);
    }

    var fetchUrl = ResolveAnalysisArtifactFetchUrl(imageUrl, configuration);
    if (string.IsNullOrWhiteSpace(fetchUrl))
    {
        return Results.NotFound(new { message = "Event subject image source is not available." });
    }

    using var remoteResponse = await httpClient.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!remoteResponse.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)remoteResponse.StatusCode);
    }

    var remoteContentType = contentTypeOverride ?? remoteResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    var remoteBytes = await remoteResponse.Content.ReadAsByteArrayAsync(cancellationToken);
    return Results.File(remoteBytes, remoteContentType, fileDownloadName);
}

static long? GetCurrentCrmMerchantId(HttpContext context)
{
    return context.Session.GetString("CrmMerchantId") is { Length: > 0 } value &&
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var merchantId)
            ? merchantId
            : null;
}

static string BuildSpendBeeReceiptImageDownloadFileName(long receiptId, long imageId, int sortOrder, string? contentType)
{
    var extension = NormalizeImageContentType(contentType) switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg"
    };
    return $"spendbee-receipt-{receiptId}-image-{sortOrder + 1}-{imageId}{extension}";
}

static bool IsDownloadRequested(string? value)
{
    return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}

static bool TryResolveS3Uri(string imageUrl, S3StorageOptions options, out Uri s3Uri)
{
    s3Uri = null!;
    if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var sourceUri))
    {
        return false;
    }

    var expectedHost = $"{options.Bucket}.s3.{options.Region}.amazonaws.com";
    if (string.Equals(sourceUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
    {
        s3Uri = sourceUri;
        return true;
    }

    if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)
        && Uri.TryCreate(options.PublicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var publicBaseUri)
        && string.Equals(sourceUri.Host, publicBaseUri.Host, StringComparison.OrdinalIgnoreCase)
        && sourceUri.AbsolutePath.StartsWith(publicBaseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
    {
        var key = Uri.UnescapeDataString(sourceUri.AbsolutePath[publicBaseUri.AbsolutePath.Length..].TrimStart('/'));
        if (!string.IsNullOrWhiteSpace(key))
        {
            s3Uri = BuildS3Uri(options, key);
            return true;
        }
    }

    return false;
}

static bool IsS3Url(string imageUrl)
{
    return imageUrl.Contains(".s3.", StringComparison.OrdinalIgnoreCase)
        || imageUrl.Contains("amazonaws.com/", StringComparison.OrdinalIgnoreCase);
}

static string GuessImageExtension(string contentType, string imageUrl)
{
    if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase)) return ".png";
    if (contentType.Contains("webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
    if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (extension is ".jpg" or ".jpeg" or ".png" or ".webp")
        {
            return extension;
        }
    }

    return ".jpg";
}

static string NormalizeStorageSegment(string value)
{
    var normalized = new string(value
        .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
        .ToArray());
    return string.IsNullOrWhiteSpace(normalized) ? "subject" : normalized;
}

static string? BuildSubjectImageProxyUrl(long subjectId, string kind, string? imageUrl)
{
    return string.IsNullOrWhiteSpace(imageUrl)
        ? null
        : $"/api/edge-event-subjects/{subjectId}/image/{kind}";
}

static string? BuildAppSubjectImageProxyUrl(long? subjectId, string kind, string? imageUrl)
{
    return !subjectId.HasValue || string.IsNullOrWhiteSpace(imageUrl)
        ? null
        : $"/api/app/edge-event-subjects/{subjectId.Value}/image/{kind}";
}

static async Task<VerifiedEventReview?> LoadVerifiedEventReviewAsync(
    MySqlConnection connection,
    int eventId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT Status, PpeReviewJson, AnnotationJson
        FROM bee_EdgeEvent
        WHERE id = @EventId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new VerifiedEventReview(
        reader["Status"] as string,
        reader["PpeReviewJson"] as string,
        reader["AnnotationJson"] as string);
}

static EdgeEventAnalysisResult ApplyVerifiedReviewToAnalysis(
    EdgeEventAnalysisResult analysis,
    VerifiedEventReview? review)
{
    if (review is null)
    {
        return analysis;
    }

    var status = ResolveEventStatusFromAnalysis(
        analysis.PeopleCount,
        analysis.MachineryVehicleCount,
        analysis.RiskPersonCount,
        NormalizeBounded(review.Status, 40) ?? analysis.RiskSeverity);
    var isRealRisk = status is "Severe Danger" or "Ordinary Risk";
    var reviewDocument = ParseJsonObject(review.PpeReviewJson);
    var annotationDocument = ParseJsonObject(review.AnnotationJson);
    var summary = ExtractReviewSummary(reviewDocument)
        ?? analysis.Summary
        ?? (isRealRisk ? "Verified PPE risk after backend review." : "PPE reviewed and queued for annotation check.");
    var peopleFromAnnotation = CountAnnotationBoxes(annotationDocument, "person", "worker");
    var ppeBoxesFromAnnotation = CountAnnotationBoxes(annotationDocument, "helmet", "hardhat", "mask", "glove", "vest", "ppe");
    var awsHasPeople = ExtractBool(reviewDocument, "aws", "hasPeople")
        || ExtractBool(reviewDocument, "openAI", "hasPeople")
        || ExtractBool(reviewDocument, "openAIAwsFallback", "hasPeople");
    var peopleCount = Math.Max(analysis.PeopleCount, peopleFromAnnotation);
    if (peopleCount == 0 && awsHasPeople)
    {
        peopleCount = 1;
    }

    var riskPeople = analysis.RiskPersonCount;
    if (isRealRisk && riskPeople == 0)
    {
        riskPeople = Math.Max(1, analysis.Subjects.Count(subject => IsPersonSubject(subject.SubjectType) && subject.IsRisk));
    }

    if (isRealRisk && peopleCount == 0)
    {
        peopleCount = riskPeople;
    }

    var ppeCompliant = isRealRisk
        ? Math.Max(0, peopleCount - riskPeople)
        : Math.Max(analysis.PpeCompliantPeopleCount, peopleCount);
    var ppeRate = peopleCount > 0
        ? Math.Round(ppeCompliant * 100m / peopleCount, 2)
        : analysis.PpeComplianceRate;
    var subjects = EnsureVerifiedRiskSubject(analysis.Subjects, isRealRisk, summary);
    var analysisJson = MergeVerifiedAnalysisJson(
        analysis.AnalysisJson,
        review.PpeReviewJson,
        review.AnnotationJson,
        ppeBoxesFromAnnotation);

    return analysis with
    {
        PeopleCount = peopleCount,
        PpeCompliantPeopleCount = ppeCompliant,
        RiskPersonCount = riskPeople,
        PpeComplianceRate = ppeRate,
        RiskCategory = isRealRisk ? "PPE compliance risk" : analysis.RiskCategory,
        RiskSeverity = status,
        Summary = summary,
        AnalysisJson = analysisJson,
        Subjects = subjects
    };
}

static IReadOnlyList<EdgeEventSubjectPayload> EnsureVerifiedRiskSubject(
    IReadOnlyList<EdgeEventSubjectPayload> subjects,
    bool isRealRisk,
    string? riskReason)
{
    if (!isRealRisk || subjects.Any(subject => IsPersonSubject(subject.SubjectType) && subject.IsRisk))
    {
        return subjects;
    }

    var verifiedSubjects = subjects.ToList();
    verifiedSubjects.Add(new EdgeEventSubjectPayload(
        "person-risk-001",
        "Person",
        "Verified risk person",
        IsRisk: true,
        RiskCategory: "PPE compliance risk",
        RiskSeverity: "Ordinary Risk",
        RiskReason: NormalizeBounded(riskReason, 500)));
    return verifiedSubjects;
}

static string? MergeVerifiedAnalysisJson(
    string? analysisJson,
    string? reviewJson,
    string? annotationJson,
    int annotationPpeBoxCount)
{
    var root = ParseJsonObject(analysisJson) ?? new JsonObject();
    if (ParseJsonNode(reviewJson) is { } reviewNode)
    {
        root["verifiedReview"] = reviewNode;
    }

    if (ParseJsonNode(annotationJson) is { } annotationNode)
    {
        root["panoramaAnnotation"] = annotationNode;
    }

    root["annotationPpeBoxCount"] = annotationPpeBoxCount;
    root["analysisSource"] = "backend_verified_event_upload";
    return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

static string MergePanoramaAnnotationJson(string? analysisJson, string annotationJson)
{
    var root = ParseJsonObject(analysisJson) ?? new JsonObject();
    if (ParseJsonNode(annotationJson) is { } annotationNode)
    {
        root["panoramaAnnotation"] = annotationNode;
    }

    return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

static JsonObject? ParseJsonObject(string? json)
{
    return ParseJsonNode(json) as JsonObject;
}

static JsonNode? ParseJsonNode(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return null;
    }

    try
    {
        return JsonNode.Parse(json);
    }
    catch (JsonException)
    {
        return null;
    }
}

static int? DbInt(MySqlDataReader reader, string columnName)
{
    var ordinal = reader.GetOrdinal(columnName);
    return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}

static decimal? DbDecimal(MySqlDataReader reader, string columnName)
{
    var ordinal = reader.GetOrdinal(columnName);
    return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
}

static int CountJsonBoxes(JsonNode? annotation)
{
    return annotation?["boxes"] is JsonArray boxes ? boxes.Count : 0;
}

static bool ExtractBool(JsonObject? root, string objectName, string propertyName)
{
    return root?[objectName]?[propertyName]?.GetValue<bool>() == true;
}

static string? ExtractReviewSummary(JsonObject? review)
{
    return NormalizeBounded(
        review?["openAI"]?["reason"]?.GetValue<string>()
        ?? review?["openAIAwsFallback"]?["reason"]?.GetValue<string>()
        ?? review?["aws"]?["message"]?.GetValue<string>()
        ?? review?["error"]?.GetValue<string>(),
        500);
}

static int CountAnnotationBoxes(JsonObject? annotation, params string[] classNameFragments)
{
    if (annotation?["classes"] is not JsonArray classes || annotation["boxes"] is not JsonArray boxes)
    {
        return 0;
    }

    var matchingClassIds = new HashSet<int>();
    foreach (var item in classes.OfType<JsonObject>())
    {
        var id = item["id"]?.GetValue<int?>();
        var name = item["name"]?.GetValue<string>();
        if (!id.HasValue || string.IsNullOrWhiteSpace(name))
        {
            continue;
        }

        if (classNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            matchingClassIds.Add(id.Value);
        }
    }

    if (matchingClassIds.Count == 0)
    {
        return 0;
    }

    var count = 0;
    foreach (var box in boxes.OfType<JsonObject>())
    {
        var classId = box["classId"]?.GetValue<int?>();
        if (classId.HasValue && matchingClassIds.Contains(classId.Value))
        {
            count++;
        }
    }

    return count;
}

static async Task SaveEventAnalysisAsync(
    MySqlConnection connection,
    int eventId,
    EdgeEventAnalysisResult analysis,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_EdgeEventAnalysis
            (EdgeEventId, PeopleCount, MachineryVehicleCount, ToolCount, PpeCompliantPeopleCount,
             RiskPersonCount, PpeComplianceRate, RiskCategory, RiskSeverity, Summary, AnalysisJson)
        VALUES
            (@EdgeEventId, @PeopleCount, @MachineryVehicleCount, @ToolCount, @PpeCompliantPeopleCount,
             @RiskPersonCount, @PpeComplianceRate, @RiskCategory, @RiskSeverity, @Summary, @AnalysisJson)
        ON DUPLICATE KEY UPDATE
            PeopleCount = VALUES(PeopleCount),
            MachineryVehicleCount = VALUES(MachineryVehicleCount),
            ToolCount = VALUES(ToolCount),
            PpeCompliantPeopleCount = VALUES(PpeCompliantPeopleCount),
            RiskPersonCount = VALUES(RiskPersonCount),
            PpeComplianceRate = VALUES(PpeComplianceRate),
            RiskCategory = VALUES(RiskCategory),
            RiskSeverity = VALUES(RiskSeverity),
            Summary = VALUES(Summary),
            AnalysisJson = VALUES(AnalysisJson),
            UpdatedAtUtc = UTC_TIMESTAMP(6);

        UPDATE bee_EdgeEvent
        SET Status = @EventStatus
        WHERE id = @EdgeEventId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EdgeEventId", MySqlDbType.Int32).Value = eventId;
    command.Parameters.Add("@PeopleCount", MySqlDbType.Int32).Value = analysis.PeopleCount;
    command.Parameters.Add("@MachineryVehicleCount", MySqlDbType.Int32).Value = analysis.MachineryVehicleCount;
    command.Parameters.Add("@ToolCount", MySqlDbType.Int32).Value = analysis.ToolCount;
    command.Parameters.Add("@PpeCompliantPeopleCount", MySqlDbType.Int32).Value = analysis.PpeCompliantPeopleCount;
    command.Parameters.Add("@RiskPersonCount", MySqlDbType.Int32).Value = analysis.RiskPersonCount;
    command.Parameters.Add("@PpeComplianceRate", MySqlDbType.Decimal).Value = analysis.PpeComplianceRate.HasValue ? analysis.PpeComplianceRate.Value : DBNull.Value;
    command.Parameters.Add("@RiskCategory", MySqlDbType.VarChar, 120).Value = DbNullable(analysis.RiskCategory);
    command.Parameters.Add("@RiskSeverity", MySqlDbType.VarChar, 40).Value = analysis.RiskSeverity;
    command.Parameters.Add("@EventStatus", MySqlDbType.VarChar, 40).Value = ResolveEventStatusFromAnalysis(
        analysis.PeopleCount,
        analysis.MachineryVehicleCount,
        analysis.RiskPersonCount,
        analysis.RiskSeverity);
    command.Parameters.Add("@Summary", MySqlDbType.VarChar, 500).Value = DbNullable(analysis.Summary);
    command.Parameters.Add("@AnalysisJson", MySqlDbType.JSON).Value = string.IsNullOrWhiteSpace(analysis.AnalysisJson) ? DBNull.Value : analysis.AnalysisJson;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task UpdateEventAutoAnnotationAsync(
    MySqlConnection connection,
    int eventId,
    string annotationJson,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_EdgeEvent
        SET AnnotationJson = @AnnotationJson,
            AnnotatedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @EventId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@AnnotationJson", MySqlDbType.MediumText).Value = annotationJson;
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    await command.ExecuteNonQueryAsync(cancellationToken);
    await SyncEventAnalysisAnnotationAsync(connection, eventId, annotationJson, cancellationToken);
}

static async Task SyncEventAnalysisAnnotationAsync(
    MySqlConnection connection,
    int eventId,
    string annotationJson,
    CancellationToken cancellationToken)
{
    const string selectSql = """
        SELECT AnalysisJson
        FROM bee_EdgeEventAnalysis
        WHERE EdgeEventId = @EventId
        LIMIT 1;
        """;
    await using var selectCommand = new MySqlCommand(selectSql, connection);
    selectCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    var existingAnalysisValue = await selectCommand.ExecuteScalarAsync(cancellationToken);
    if (existingAnalysisValue is null)
    {
        return;
    }

    var existingAnalysisJson = existingAnalysisValue == DBNull.Value
        ? null
        : existingAnalysisValue as string;

    const string updateSql = """
        UPDATE bee_EdgeEventAnalysis
        SET AnalysisJson = @AnalysisJson,
            UpdatedAtUtc = UTC_TIMESTAMP(6)
        WHERE EdgeEventId = @EventId;
        """;
    await using var updateCommand = new MySqlCommand(updateSql, connection);
    updateCommand.Parameters.Add("@AnalysisJson", MySqlDbType.JSON).Value = MergePanoramaAnnotationJson(existingAnalysisJson, annotationJson);
    updateCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
}

static async Task SaveEventSubjectsAsync(
    MySqlConnection connection,
    int eventId,
    IReadOnlyList<EdgeEventSubjectPayload> subjects,
    CancellationToken cancellationToken)
{
    if (subjects.Count == 0)
    {
        return;
    }

    const string sql = """
        INSERT INTO bee_EdgeEventSubject
            (EdgeEventId, SubjectKey, SubjectType, TrackingLabel, CropImageUrl, PreviewImageUrl,
             BoundingBoxJson, PpeBoxJson, PpeStatusJson, IsRisk, RiskCategory, RiskSeverity, RiskReason, AnalysisJson)
        VALUES
            (@EdgeEventId, @SubjectKey, @SubjectType, @TrackingLabel, @CropImageUrl, @PreviewImageUrl,
             @BoundingBoxJson, @PpeBoxJson, @PpeStatusJson, @IsRisk, @RiskCategory, @RiskSeverity, @RiskReason, @AnalysisJson)
        ON DUPLICATE KEY UPDATE
            SubjectType = VALUES(SubjectType),
            TrackingLabel = VALUES(TrackingLabel),
            CropImageUrl = VALUES(CropImageUrl),
            PreviewImageUrl = VALUES(PreviewImageUrl),
            BoundingBoxJson = VALUES(BoundingBoxJson),
            PpeBoxJson = VALUES(PpeBoxJson),
            PpeStatusJson = VALUES(PpeStatusJson),
            IsRisk = VALUES(IsRisk),
            RiskCategory = VALUES(RiskCategory),
            RiskSeverity = VALUES(RiskSeverity),
            RiskReason = VALUES(RiskReason),
            AnalysisJson = VALUES(AnalysisJson),
            UpdatedAtUtc = UTC_TIMESTAMP(6);
        """;
    var index = 0;
    foreach (var subject in subjects)
    {
        index++;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@EdgeEventId", MySqlDbType.Int32).Value = eventId;
        command.Parameters.Add("@SubjectKey", MySqlDbType.VarChar, 120).Value =
            NormalizeBounded(subject.SubjectKey, 120) ?? $"person-{index:000}";
        command.Parameters.Add("@SubjectType", MySqlDbType.VarChar, 40).Value =
            NormalizeBounded(subject.SubjectType, 40) ?? "Person";
        command.Parameters.Add("@TrackingLabel", MySqlDbType.VarChar, 150).Value = DbNullable(NormalizeBounded(subject.TrackingLabel, 150));
        command.Parameters.Add("@CropImageUrl", MySqlDbType.VarChar, 1000).Value = DbNullable(NormalizeBounded(subject.CropImageUrl, 1000));
        command.Parameters.Add("@PreviewImageUrl", MySqlDbType.VarChar, 1000).Value = DbNullable(NormalizeBounded(subject.PreviewImageUrl, 1000));
        command.Parameters.Add("@BoundingBoxJson", MySqlDbType.JSON).Value = DbJson(subject.BoundingBoxJson);
        command.Parameters.Add("@PpeBoxJson", MySqlDbType.JSON).Value = DbJson(subject.PpeBoxJson);
        command.Parameters.Add("@PpeStatusJson", MySqlDbType.JSON).Value = DbJson(subject.PpeStatusJson);
        command.Parameters.Add("@IsRisk", MySqlDbType.Bit).Value = subject.IsRisk;
        command.Parameters.Add("@RiskCategory", MySqlDbType.VarChar, 120).Value = DbNullable(NormalizeBounded(subject.RiskCategory, 120));
        command.Parameters.Add("@RiskSeverity", MySqlDbType.VarChar, 40).Value = DbNullable(NormalizeBounded(subject.RiskSeverity, 40));
        command.Parameters.Add("@RiskReason", MySqlDbType.VarChar, 500).Value = DbNullable(NormalizeBounded(subject.RiskReason, 500));
        command.Parameters.Add("@AnalysisJson", MySqlDbType.JSON).Value = DbJson(subject.AnalysisJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

static async Task<IReadOnlyList<AppDeviceDailyStat>> QueryAppDeviceDailyStatsAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    string deviceCode,
    DateOnly from,
    DateOnly to,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT stat.StatDate, stat.PeopleCount, stat.BraceletCount, stat.MachineryVehicleCount,
            stat.PpeComplianceRate, stat.RiskEventCount, stat.RiskPersonCount,
            stat.TopRiskSubjectKey, stat.TopRiskSubjectRiskCount,
            stat.LastHeartbeatAtUtc, stat.LastEventAtUtc
        FROM bee_EdgeDeviceDailyStat AS stat
        INNER JOIN bee_EdgeDevice AS device ON device.id = stat.EdgeDeviceId
        INNER JOIN bee_EdgeDeviceUserBinding AS binding
            ON binding.EdgeDeviceId = device.id AND binding.AppUserId = @AppUserId
        WHERE stat.ProjectId = @ProjectId
            AND device.DeviceCode = @DeviceCode
            AND stat.StatDate >= @FromDate
            AND stat.StatDate <= @ToDate
        ORDER BY stat.StatDate DESC;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@DeviceCode", MySqlDbType.VarChar, 40).Value = deviceCode.Trim();
    command.Parameters.Add("@FromDate", MySqlDbType.Date).Value = from.ToDateTime(TimeOnly.MinValue);
    command.Parameters.Add("@ToDate", MySqlDbType.Date).Value = to.ToDateTime(TimeOnly.MinValue);
    var stats = new List<AppDeviceDailyStat>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        stats.Add(new AppDeviceDailyStat(
            DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("StatDate"))),
            reader.GetInt32(reader.GetOrdinal("PeopleCount")),
            reader.GetInt32(reader.GetOrdinal("BraceletCount")),
            reader.GetInt32(reader.GetOrdinal("MachineryVehicleCount")),
            reader["PpeComplianceRate"] is DBNull ? null : reader.GetDecimal(reader.GetOrdinal("PpeComplianceRate")),
            reader.GetInt32(reader.GetOrdinal("RiskEventCount")),
            reader.GetInt32(reader.GetOrdinal("RiskPersonCount")),
            reader["TopRiskSubjectKey"] as string,
            reader.GetInt32(reader.GetOrdinal("TopRiskSubjectRiskCount")),
            reader["LastHeartbeatAtUtc"] is DBNull ? null : reader.GetDateTime(reader.GetOrdinal("LastHeartbeatAtUtc")),
            reader["LastEventAtUtc"] is DBNull ? null : reader.GetDateTime(reader.GetOrdinal("LastEventAtUtc"))));
    }

    return stats;
}

static async Task<IReadOnlyList<AppRiskSubjectSummary>> QueryAppDeviceRiskSubjectsAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    string deviceCode,
    DateOnly date,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string groupedSql = """
        SELECT risk.PersonGroupKey, risk.DisplayLabel, risk.RepresentativeSubjectId,
            risk.RiskEventCount, risk.RiskSubjectCount,
            risk.RepresentativeCropImageUrl, risk.RepresentativePreviewImageUrl
        FROM bee_EdgeDeviceDailyRiskPerson AS risk
        INNER JOIN bee_EdgeDevice AS device ON device.id = risk.EdgeDeviceId
        INNER JOIN bee_EdgeDeviceUserBinding AS binding
            ON binding.EdgeDeviceId = device.id AND binding.AppUserId = @AppUserId
        WHERE risk.ProjectId = @ProjectId
            AND device.DeviceCode = @DeviceCode
            AND risk.StatDate = @StatDate
        ORDER BY risk.RiskEventCount DESC, risk.RiskSubjectCount DESC, risk.PersonGroupKey
        LIMIT 50;
        """;
    await using (var groupedCommand = new MySqlCommand(groupedSql, connection))
    {
        groupedCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        groupedCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
        groupedCommand.Parameters.Add("@DeviceCode", MySqlDbType.VarChar, 40).Value = deviceCode.Trim();
        groupedCommand.Parameters.Add("@StatDate", MySqlDbType.Date).Value = date.ToDateTime(TimeOnly.MinValue);
        var groupedSubjects = new List<AppRiskSubjectSummary>();
        await using var groupedReader = await groupedCommand.ExecuteReaderAsync(cancellationToken);
        while (await groupedReader.ReadAsync(cancellationToken))
        {
            groupedSubjects.Add(new AppRiskSubjectSummary(
                groupedReader["PersonGroupKey"] as string ?? string.Empty,
                groupedReader["DisplayLabel"] as string,
                groupedReader["RepresentativeSubjectId"] is DBNull ? null : groupedReader.GetInt64(groupedReader.GetOrdinal("RepresentativeSubjectId")),
                groupedReader.GetInt32(groupedReader.GetOrdinal("RiskEventCount")),
                BuildAppSubjectImageProxyUrl(
                    groupedReader["RepresentativeSubjectId"] is DBNull ? null : groupedReader.GetInt64(groupedReader.GetOrdinal("RepresentativeSubjectId")),
                    "crop",
                    groupedReader["RepresentativeCropImageUrl"] as string),
                BuildAppSubjectImageProxyUrl(
                    groupedReader["RepresentativeSubjectId"] is DBNull ? null : groupedReader.GetInt64(groupedReader.GetOrdinal("RepresentativeSubjectId")),
                    "preview",
                    groupedReader["RepresentativePreviewImageUrl"] as string),
                null,
                null));
        }

        if (groupedSubjects.Count > 0)
        {
            return groupedSubjects;
        }
    }

    var start = date.ToDateTime(TimeOnly.MinValue);
    var end = start.AddDays(1);
    const string sql = """
        SELECT subject.SubjectKey, MAX(subject.id) AS SubjectId,
            COALESCE(MAX(subject.TrackingLabel), subject.SubjectKey) AS TrackingLabel,
            COUNT(*) AS RiskCount,
            MAX(subject.CropImageUrl) AS CropImageUrl,
            MAX(subject.PreviewImageUrl) AS PreviewImageUrl,
            MAX(subject.RiskCategory) AS RiskCategory,
            MAX(subject.RiskSeverity) AS RiskSeverity
        FROM bee_EdgeEventSubject AS subject
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        INNER JOIN bee_EdgeDeviceUserBinding AS binding
            ON binding.EdgeDeviceId = device.id AND binding.AppUserId = @AppUserId
        WHERE device.ProjectId = @ProjectId
            AND device.DeviceCode = @DeviceCode
            AND evt.EventTimeUtc >= @StartUtc
            AND evt.EventTimeUtc < @EndUtc
            AND subject.SubjectType = 'Person'
            AND subject.IsRisk = 1
        GROUP BY subject.SubjectKey
        ORDER BY RiskCount DESC, subject.SubjectKey
        LIMIT 50;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@DeviceCode", MySqlDbType.VarChar, 40).Value = deviceCode.Trim();
    command.Parameters.Add("@StartUtc", MySqlDbType.DateTime).Value = start;
    command.Parameters.Add("@EndUtc", MySqlDbType.DateTime).Value = end;
    var subjects = new List<AppRiskSubjectSummary>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        subjects.Add(new AppRiskSubjectSummary(
            reader["SubjectKey"] as string ?? string.Empty,
            reader["TrackingLabel"] as string,
            reader["SubjectId"] is DBNull ? null : reader.GetInt64(reader.GetOrdinal("SubjectId")),
            reader.GetInt32(reader.GetOrdinal("RiskCount")),
            BuildAppSubjectImageProxyUrl(
                reader["SubjectId"] is DBNull ? null : reader.GetInt64(reader.GetOrdinal("SubjectId")),
                "crop",
                reader["CropImageUrl"] as string),
            BuildAppSubjectImageProxyUrl(
                reader["SubjectId"] is DBNull ? null : reader.GetInt64(reader.GetOrdinal("SubjectId")),
                "preview",
                reader["PreviewImageUrl"] as string),
            reader["RiskCategory"] as string,
            reader["RiskSeverity"] as string));
    }

    return subjects;
}

static async Task<AppRiskNotificationSettingsResponse?> QueryAppRiskNotificationSettingsAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    string deviceCode,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var device = await FindBoundAppDeviceAsync(connection, projectId, appUserId, deviceCode, cancellationToken);
    if (device is null)
    {
        return null;
    }

    return await LoadRiskNotificationSettingsAsync(connection, projectId, appUserId, device.Value.Id, device.Value.DeviceCode, cancellationToken);
}

static async Task<AppRiskNotificationSettingsResponse?> UpsertAppRiskNotificationSettingsAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    string deviceCode,
    AppRiskNotificationSettingsUpdateRequest payload,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    var device = await FindBoundAppDeviceAsync(connection, projectId, appUserId, deviceCode, cancellationToken);
    if (device is null)
    {
        return null;
    }

    const string sql = """
        INSERT INTO bee_AppUserRiskNotificationPreference
            (ProjectId, AppUserId, EdgeDeviceId, RiskSeverity, PushEnabled)
        VALUES
            (@ProjectId, @AppUserId, @EdgeDeviceId, @RiskSeverity, @PushEnabled)
        ON DUPLICATE KEY UPDATE
            PushEnabled = VALUES(PushEnabled),
            UpdatedAtUtc = UTC_TIMESTAMP(6);
        """;
    foreach (var setting in NormalizeRiskNotificationSettings(payload))
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
        command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = device.Value.Id;
        command.Parameters.Add("@RiskSeverity", MySqlDbType.VarChar, 40).Value = setting.RiskSeverity;
        command.Parameters.Add("@PushEnabled", MySqlDbType.Bit).Value = setting.PushEnabled;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    return await LoadRiskNotificationSettingsAsync(connection, projectId, appUserId, device.Value.Id, device.Value.DeviceCode, cancellationToken);
}

static async Task<IReadOnlyList<AppRiskNotificationResponse>> QueryAppRiskNotificationsAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    bool unreadOnly,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        SELECT notification.id, notification.EdgeEventId, notification.RiskSeverity,
            notification.Title, notification.Message, notification.IsRead, notification.PushStatus,
            notification.CreatedAtUtc, notification.ReadAtUtc,
            device.DeviceCode, device.DeviceName
        FROM bee_AppRiskNotification AS notification
        INNER JOIN bee_EdgeDevice AS device ON device.id = notification.EdgeDeviceId
        WHERE notification.ProjectId = @ProjectId
            AND notification.AppUserId = @AppUserId
            AND (@UnreadOnly = 0 OR notification.IsRead = 0)
        ORDER BY notification.CreatedAtUtc DESC
        LIMIT 100;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@UnreadOnly", MySqlDbType.Bit).Value = unreadOnly;
    var notifications = new List<AppRiskNotificationResponse>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        notifications.Add(new AppRiskNotificationResponse(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("EdgeEventId")),
            reader["DeviceCode"] as string ?? string.Empty,
            reader["DeviceName"] as string ?? string.Empty,
            reader["RiskSeverity"] as string ?? "Real Risk",
            reader["Title"] as string ?? string.Empty,
            reader["Message"] as string,
            Convert.ToBoolean(reader["IsRead"]),
            reader["PushStatus"] as string ?? "Suppressed",
            reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc")),
            reader["ReadAtUtc"] is DBNull ? null : reader.GetDateTime(reader.GetOrdinal("ReadAtUtc"))));
    }

    return notifications;
}

static async Task<bool> MarkAppRiskNotificationReadAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    long notificationId,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string sql = """
        UPDATE bee_AppRiskNotification
        SET IsRead = 1,
            ReadAtUtc = COALESCE(ReadAtUtc, UTC_TIMESTAMP(6))
        WHERE id = @NotificationId
            AND ProjectId = @ProjectId
            AND AppUserId = @AppUserId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@NotificationId", MySqlDbType.Int64).Value = notificationId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
}

static async Task CreateUnreadRiskNotificationsAsync(
    MySqlConnection connection,
    int eventId,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT IGNORE INTO bee_AppRiskNotification
            (ProjectId, AppUserId, EdgeDeviceId, EdgeEventId, RiskSeverity, Title, Message, PushStatus)
        SELECT device.ProjectId,
            binding.AppUserId,
            device.id,
            evt.id,
            COALESCE(analysis.RiskSeverity, evt.Status, 'Real Risk') AS RiskSeverity,
            CONCAT('Risk verified at ', device.DeviceName) AS Title,
            COALESCE(analysis.Summary, evt.EventDescription, evt.Title, 'A site risk was verified by SentriBee.') AS Message,
            CASE
                WHEN COALESCE(pref.PushEnabled, CASE
                    WHEN COALESCE(analysis.RiskSeverity, evt.Status) = 'Severe Danger' THEN 1
                    ELSE 0
                END) = 1 THEN 'Queued'
                ELSE 'Suppressed'
            END AS PushStatus
        FROM bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        INNER JOIN bee_EdgeDeviceUserBinding AS binding ON binding.EdgeDeviceId = device.id
        LEFT JOIN bee_EdgeEventAnalysis AS analysis ON analysis.EdgeEventId = evt.id
        LEFT JOIN bee_AppUserRiskNotificationPreference AS pref
            ON pref.ProjectId = device.ProjectId
            AND pref.AppUserId = binding.AppUserId
            AND pref.EdgeDeviceId = device.id
            AND pref.RiskSeverity = COALESCE(analysis.RiskSeverity, evt.Status, 'Real Risk')
        WHERE evt.id = @EventId
            AND evt.Status IN ('Real Risk', 'Severe Danger', 'Ordinary Risk');
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task DispatchQueuedAppPushNotificationsAsync(
    IConfiguration configuration,
    HttpClient httpClient,
    int edgeEventId,
    CancellationToken cancellationToken)
{
    var apnsOptions = LoadApnsOptions(configuration);
    if (apnsOptions is null)
    {
        return;
    }

    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);

    var queued = await QueryQueuedApnsNotificationsAsync(connection, edgeEventId, cancellationToken);
    foreach (var notification in queued)
    {
        var result = await SendApnsNotificationAsync(httpClient, apnsOptions, notification, cancellationToken);
        await UpdateAppPushDeliveryStatusAsync(connection, notification.NotificationId, result, cancellationToken);
    }
}

static ApnsOptions? LoadApnsOptions(IConfiguration configuration)
{
    var teamId = configuration["AppPush:Apns:TeamId"];
    var keyId = configuration["AppPush:Apns:KeyId"];
    var bundleId = configuration["AppPush:Apns:BundleId"];
    var privateKeyPath = configuration["AppPush:Apns:PrivateKeyPath"];
    var environment = configuration["AppPush:Apns:Environment"] ?? "production";
    if (string.IsNullOrWhiteSpace(teamId) ||
        string.IsNullOrWhiteSpace(keyId) ||
        string.IsNullOrWhiteSpace(bundleId) ||
        string.IsNullOrWhiteSpace(privateKeyPath) ||
        !File.Exists(privateKeyPath))
    {
        return null;
    }

    var endpoint = environment.Equals("sandbox", StringComparison.OrdinalIgnoreCase)
        ? "https://api.sandbox.push.apple.com"
        : "https://api.push.apple.com";
    return new ApnsOptions(
        teamId.Trim(),
        keyId.Trim(),
        bundleId.Trim(),
        privateKeyPath.Trim(),
        endpoint);
}

static async Task<IReadOnlyList<QueuedApnsNotification>> QueryQueuedApnsNotificationsAsync(
    MySqlConnection connection,
    int edgeEventId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT notification.id, notification.EdgeEventId, notification.RiskSeverity,
            notification.Title, notification.Message, device.DeviceCode, device.DeviceName,
            appDevice.PushToken
        FROM bee_AppRiskNotification AS notification
        INNER JOIN bee_EdgeDevice AS device ON device.id = notification.EdgeDeviceId
        INNER JOIN bee_AppUserDevice AS appDevice
            ON appDevice.id = (
                SELECT latestDevice.id
                FROM bee_AppUserDevice AS latestDevice
                WHERE latestDevice.ProjectId = notification.ProjectId
                    AND latestDevice.AppUserId = notification.AppUserId
                    AND LOWER(COALESCE(latestDevice.PushProvider, '')) = 'apns'
                    AND COALESCE(latestDevice.PushToken, '') <> ''
                ORDER BY latestDevice.UpdatedAtUtc DESC, latestDevice.id DESC
                LIMIT 1
            )
        WHERE notification.EdgeEventId = @EdgeEventId
            AND notification.PushStatus = 'Queued'
        ORDER BY notification.id
        LIMIT 25;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EdgeEventId", MySqlDbType.Int32).Value = edgeEventId;
    var notifications = new List<QueuedApnsNotification>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        notifications.Add(new QueuedApnsNotification(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetInt32(reader.GetOrdinal("EdgeEventId")),
            reader["DeviceCode"] as string ?? string.Empty,
            reader["DeviceName"] as string ?? string.Empty,
            reader["RiskSeverity"] as string ?? "Real Risk",
            reader["Title"] as string ?? "SentriBee risk alert",
            reader["Message"] as string,
            reader["PushToken"] as string ?? string.Empty));
    }

    return notifications;
}

static async Task<ApnsSendResult> SendApnsNotificationAsync(
    HttpClient httpClient,
    ApnsOptions options,
    QueuedApnsNotification notification,
    CancellationToken cancellationToken)
{
    var jwt = ApnsJwtTokenCache.GetOrCreate(options);
    var payload = new
    {
        aps = new
        {
            alert = new
            {
                title = notification.Title,
                body = NormalizeBounded(notification.Message, 180)
                    ?? $"{notification.RiskSeverity} verified at {notification.DeviceName}."
            },
            sound = "default",
            badge = 1
        },
        type = "risk_notification",
        notificationId = notification.NotificationId,
        eventId = notification.EdgeEventId,
        deviceCode = notification.DeviceCode,
        riskSeverity = notification.RiskSeverity
    };
    var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Endpoint}/3/device/{notification.PushToken}")
    {
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("bearer", jwt);
    request.Headers.TryAddWithoutValidation("apns-topic", options.BundleId);
    request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
    request.Headers.TryAddWithoutValidation("apns-priority", "10");

    try
    {
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        var apnsId = response.Headers.TryGetValues("apns-id", out var values) ? values.FirstOrDefault() : null;
        if (response.IsSuccessStatusCode)
        {
            return new ApnsSendResult(true, apnsId, null);
        }

        return new ApnsSendResult(
            false,
            apnsId,
            $"APNS returned HTTP {(int)response.StatusCode}: {TrimDiagnostic(responseText)}");
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or CryptographicException)
    {
        return new ApnsSendResult(false, null, exception.Message);
    }
}

static async Task UpdateAppPushDeliveryStatusAsync(
    MySqlConnection connection,
    long notificationId,
    ApnsSendResult result,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_AppRiskNotification
        SET PushStatus = @PushStatus,
            PushProviderMessageId = @PushProviderMessageId,
            PushAttemptedAtUtc = UTC_TIMESTAMP(6),
            PushErrorText = @PushErrorText
        WHERE id = @NotificationId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@NotificationId", MySqlDbType.Int64).Value = notificationId;
    command.Parameters.Add("@PushStatus", MySqlDbType.VarChar, 40).Value = result.Success ? "Delivered" : "Failed";
    command.Parameters.Add("@PushProviderMessageId", MySqlDbType.VarChar, 100).Value =
        DbNullable(NormalizeBounded(result.ProviderMessageId, 100));
    command.Parameters.Add("@PushErrorText", MySqlDbType.VarChar, 500).Value =
        DbNullable(NormalizeBounded(result.ErrorText, 500));
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<AppBoundDeviceRef?> FindBoundAppDeviceAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    string deviceCode,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT device.id, device.DeviceCode
        FROM bee_EdgeDevice AS device
        INNER JOIN bee_EdgeDeviceUserBinding AS binding
            ON binding.EdgeDeviceId = device.id AND binding.AppUserId = @AppUserId
        WHERE device.ProjectId = @ProjectId
            AND device.DeviceCode = @DeviceCode
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@DeviceCode", MySqlDbType.VarChar, 40).Value = deviceCode.Trim();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new AppBoundDeviceRef(
        reader.GetInt32(reader.GetOrdinal("id")),
        reader["DeviceCode"] as string ?? string.Empty);
}

static async Task<AppRiskNotificationSettingsResponse> LoadRiskNotificationSettingsAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    int edgeDeviceId,
    string deviceCode,
    CancellationToken cancellationToken)
{
    var settings = DefaultRiskNotificationSettings().ToDictionary(item => item.RiskSeverity, StringComparer.OrdinalIgnoreCase);
    const string sql = """
        SELECT RiskSeverity, PushEnabled
        FROM bee_AppUserRiskNotificationPreference
        WHERE ProjectId = @ProjectId
            AND AppUserId = @AppUserId
            AND EdgeDeviceId = @EdgeDeviceId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    command.Parameters.Add("@EdgeDeviceId", MySqlDbType.Int32).Value = edgeDeviceId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var severity = reader["RiskSeverity"] as string;
        if (!string.IsNullOrWhiteSpace(severity))
        {
            settings[severity] = new AppRiskNotificationSetting(severity, Convert.ToBoolean(reader["PushEnabled"]));
        }
    }

    return new AppRiskNotificationSettingsResponse(deviceCode, settings.Values.ToList());
}

static IReadOnlyList<AppRiskNotificationSetting> NormalizeRiskNotificationSettings(AppRiskNotificationSettingsUpdateRequest payload)
{
    var defaults = DefaultRiskNotificationSettings().ToDictionary(item => item.RiskSeverity, StringComparer.OrdinalIgnoreCase);
    if (payload.Settings is not null)
    {
        foreach (var setting in payload.Settings)
        {
            var severity = NormalizeRiskSeverity(setting.RiskSeverity);
            if (severity is not null && defaults.ContainsKey(severity))
            {
                defaults[severity] = new AppRiskNotificationSetting(severity, setting.PushEnabled);
            }
        }
    }

    if (payload.SevereDangerEnabled.HasValue)
    {
        defaults["Severe Danger"] = new AppRiskNotificationSetting("Severe Danger", payload.SevereDangerEnabled.Value);
    }

    if (payload.OrdinaryRiskEnabled.HasValue)
    {
        defaults["Ordinary Risk"] = new AppRiskNotificationSetting("Ordinary Risk", payload.OrdinaryRiskEnabled.Value);
    }

    if (payload.RealRiskEnabled.HasValue)
    {
        defaults["Real Risk"] = new AppRiskNotificationSetting("Real Risk", payload.RealRiskEnabled.Value);
    }

    return defaults.Values.ToList();
}

static IReadOnlyList<AppRiskNotificationSetting> DefaultRiskNotificationSettings()
{
    return
    [
        new("Severe Danger", true),
        new("Ordinary Risk", false),
        new("Real Risk", false)
    ];
}

static string? NormalizeRiskSeverity(string? riskSeverity)
{
    if (string.Equals(riskSeverity, "Severe Danger", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(riskSeverity, "Severe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(riskSeverity, "High", StringComparison.OrdinalIgnoreCase))
    {
        return "Severe Danger";
    }

    if (string.Equals(riskSeverity, "Ordinary Risk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(riskSeverity, "Ordinary", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(riskSeverity, "Medium", StringComparison.OrdinalIgnoreCase))
    {
        return "Ordinary Risk";
    }

    if (string.Equals(riskSeverity, "Real Risk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(riskSeverity, "Verified Risk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(riskSeverity, "Low", StringComparison.OrdinalIgnoreCase))
    {
        return "Real Risk";
    }

    return null;
}

static async Task<object?> QueryAppEventAnalysisAsync(
    IConfiguration configuration,
    int projectId,
    int appUserId,
    int eventId,
    CancellationToken cancellationToken)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    const string analysisSql = """
        SELECT evt.id, evt.Title, evt.EventTimeUtc, evt.ImageUrl, device.DeviceCode,
            analysis.PeopleCount, analysis.MachineryVehicleCount, analysis.ToolCount,
            analysis.PpeCompliantPeopleCount, analysis.RiskPersonCount, analysis.PpeComplianceRate,
            analysis.RiskCategory, analysis.RiskSeverity, analysis.Summary, analysis.AnalysisJson
        FROM bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        INNER JOIN bee_EdgeDeviceUserBinding AS binding
            ON binding.EdgeDeviceId = device.id AND binding.AppUserId = @AppUserId
        LEFT JOIN bee_EdgeEventAnalysis AS analysis ON analysis.EdgeEventId = evt.id
        WHERE evt.id = @EventId AND device.ProjectId = @ProjectId
        LIMIT 1;
        """;
    await using var analysisCommand = new MySqlCommand(analysisSql, connection);
    analysisCommand.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    analysisCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    analysisCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await analysisCommand.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    var eventResult = new
    {
        eventId = reader.GetInt32(reader.GetOrdinal("id")),
        title = reader["Title"] as string ?? string.Empty,
        deviceCode = reader["DeviceCode"] as string ?? string.Empty,
        eventTimeUtc = reader.GetDateTime(reader.GetOrdinal("EventTimeUtc")),
        imageUrl = string.IsNullOrWhiteSpace(reader["ImageUrl"] as string) ? null : $"/api/app/events/{eventId}/image",
        peopleCount = reader["PeopleCount"] is DBNull ? 0 : reader.GetInt32(reader.GetOrdinal("PeopleCount")),
        machineryVehicleCount = reader["MachineryVehicleCount"] is DBNull ? 0 : reader.GetInt32(reader.GetOrdinal("MachineryVehicleCount")),
        toolCount = reader["ToolCount"] is DBNull ? 0 : reader.GetInt32(reader.GetOrdinal("ToolCount")),
        ppeCompliantPeopleCount = reader["PpeCompliantPeopleCount"] is DBNull ? 0 : reader.GetInt32(reader.GetOrdinal("PpeCompliantPeopleCount")),
        riskPersonCount = reader["RiskPersonCount"] is DBNull ? 0 : reader.GetInt32(reader.GetOrdinal("RiskPersonCount")),
        ppeComplianceRate = reader["PpeComplianceRate"] is DBNull ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("PpeComplianceRate")),
        riskCategory = reader["RiskCategory"] as string,
        riskSeverity = reader["RiskSeverity"] as string,
        summary = reader["Summary"] as string,
        analysis = ParseJsonOrNull(reader["AnalysisJson"] as string)
    };
    await reader.CloseAsync();

    var subjects = await QueryEventSubjectsAsync(connection, eventId, cancellationToken);
    return new { eventResult, subjects };
}

static async Task<IReadOnlyList<object>> QueryEventSubjectsAsync(MySqlConnection connection, int eventId, CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, SubjectKey, SubjectType, TrackingLabel, CropImageUrl, PreviewImageUrl,
            BoundingBoxJson, PpeBoxJson, PpeStatusJson, IsRisk, RiskCategory, RiskSeverity, RiskReason, AnalysisJson
        FROM bee_EdgeEventSubject
        WHERE EdgeEventId = @EventId
        ORDER BY SubjectType, SubjectKey;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    var subjects = new List<object>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        subjects.Add(new
        {
            id = reader.GetInt64(reader.GetOrdinal("id")),
            subjectKey = reader["SubjectKey"] as string ?? string.Empty,
            subjectType = reader["SubjectType"] as string ?? string.Empty,
            trackingLabel = reader["TrackingLabel"] as string,
            cropImageUrl = BuildAppSubjectImageProxyUrl(
                reader.GetInt64(reader.GetOrdinal("id")),
                "crop",
                reader["CropImageUrl"] as string),
            previewImageUrl = BuildAppSubjectImageProxyUrl(
                reader.GetInt64(reader.GetOrdinal("id")),
                "preview",
                reader["PreviewImageUrl"] as string),
            boundingBox = ParseJsonOrNull(reader["BoundingBoxJson"] as string),
            ppeBoxes = ParseJsonOrNull(reader["PpeBoxJson"] as string),
            ppeStatus = ParseJsonOrNull(reader["PpeStatusJson"] as string),
            isRisk = Convert.ToBoolean(reader["IsRisk"]),
            riskCategory = reader["RiskCategory"] as string,
            riskSeverity = reader["RiskSeverity"] as string,
            riskReason = reader["RiskReason"] as string,
            analysis = ParseJsonOrNull(reader["AnalysisJson"] as string)
        });
    }

    return subjects;
}

static string ResolveAppDeviceStatus(string? runtimeStatus, string? deviceStatus, DateTime? reportedAtUtc)
{
    if (string.Equals(deviceStatus, "Remote Device Offline", StringComparison.OrdinalIgnoreCase))
    {
        return "Remote Device Offline";
    }

    if (!reportedAtUtc.HasValue || reportedAtUtc.Value < DateTime.UtcNow.AddSeconds(-90))
    {
        return "Offline";
    }

    return "Online";
}

static async Task<string?> ResolveAppLiveStreamUrlAsync(
    IServerResourceService serverResourceService,
    string? serverResourceInstanceName,
    string deviceCode,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(serverResourceInstanceName))
    {
        return null;
    }

    var resources = await serverResourceService.ListAsync(0, cancellationToken);
    var resource = resources.FirstOrDefault(item =>
        string.Equals(item.InstanceName, serverResourceInstanceName, StringComparison.OrdinalIgnoreCase));
    var host = string.IsNullOrWhiteSpace(resource?.PublicDomain)
        ? resource?.PublicIpAddress
        : resource.PublicDomain;
    return string.IsNullOrWhiteSpace(host)
        ? null
        : $"https://{host}/instances/{Uri.EscapeDataString(deviceCode)}/video/index.m3u8";
}

static int ExtractIntMetric(string? detailJson, params string[] names)
{
    var number = ExtractDecimalMetric(detailJson, names);
    return number.HasValue ? Convert.ToInt32(Math.Round(number.Value, 0, MidpointRounding.AwayFromZero)) : 0;
}

static decimal? ExtractDecimalMetric(string? detailJson, params string[] names)
{
    if (string.IsNullOrWhiteSpace(detailJson))
    {
        return null;
    }

    try
    {
        using var document = JsonDocument.Parse(detailJson);
        foreach (var name in names)
        {
            if (!document.RootElement.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
            {
                return decimalValue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(value.GetString(), out var stringValue))
            {
                return stringValue;
            }
        }
    }
    catch (JsonException)
    {
        return null;
    }

    return null;
}

static int ExtractIntMetricFromJson(JsonElement? detailJson, params string[] names)
{
    var number = ExtractDecimalMetricFromJson(detailJson, names);
    return number.HasValue ? Convert.ToInt32(Math.Round(number.Value, 0, MidpointRounding.AwayFromZero)) : 0;
}

static decimal? ExtractDecimalMetricFromJson(JsonElement? detailJson, params string[] names)
{
    if (detailJson is null)
    {
        return null;
    }

    foreach (var name in names)
    {
        if (TryFindJsonProperty(detailJson.Value, name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
            {
                return decimalValue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(value.GetString(), out var stringValue))
            {
                return stringValue;
            }
        }
    }

    return null;
}

static bool TryFindJsonProperty(JsonElement element, string name, out JsonElement value)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }

            if ((property.Value.ValueKind == JsonValueKind.Object || property.Value.ValueKind == JsonValueKind.Array) &&
                TryFindJsonProperty(property.Value, name, out value))
            {
                return true;
            }
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            if (TryFindJsonProperty(item, name, out value))
            {
                return true;
            }
        }
    }

    value = default;
    return false;
}

static object DbJson(JsonElement? value)
{
    return value.HasValue ? value.Value.GetRawText() : DBNull.Value;
}

static JsonNode? ParseJsonOrNull(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    try
    {
        return JsonNode.Parse(value);
    }
    catch (JsonException)
    {
        return null;
    }
}

static bool IsPersonSubject(string? subjectType)
{
    return string.IsNullOrWhiteSpace(subjectType) ||
        string.Equals(subjectType, "Person", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(subjectType, "Worker", StringComparison.OrdinalIgnoreCase);
}

static string NormalizePhoneNumber(string? phoneNumber)
{
    if (string.IsNullOrWhiteSpace(phoneNumber))
    {
        return string.Empty;
    }

    var normalized = new string(phoneNumber.Trim().Where(ch => char.IsDigit(ch) || ch == '+').ToArray());
    return normalized.Length >= 6 ? normalized : string.Empty;
}

static string NormalizeEmail(string? email)
{
    if (string.IsNullOrWhiteSpace(email))
    {
        return string.Empty;
    }

    var normalized = email.Trim().ToLowerInvariant();
    return normalized.Contains('@', StringComparison.Ordinal) && normalized.Length <= 150
        ? normalized
        : string.Empty;
}

static string NormalizeVerificationPurpose(string? purpose)
{
    return string.Equals(purpose, "Login", StringComparison.OrdinalIgnoreCase)
        ? "Login"
        : "Register";
}

static bool HasUsefulDeviceInfo(AppClientDeviceInfo? device)
{
    return device is not null &&
        (!string.IsNullOrWhiteSpace(device.DeviceIdentifier) ||
         !string.IsNullOrWhiteSpace(device.PushToken) ||
         !string.IsNullOrWhiteSpace(device.DeviceType) ||
         !string.IsNullOrWhiteSpace(device.Platform));
}

static string? NormalizeBounded(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var normalized = value.Trim();
    return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
}

static object DbNullable(string? value)
{
    return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}

static async Task<EdgeEventContext?> FindEdgeEventContextAsync(
    MySqlConnection connection,
    int projectId,
    int eventId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT evt.id, device.DeviceCode
        FROM bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        WHERE evt.id = @EventId AND device.ProjectId = @ProjectId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    return new EdgeEventContext(
        reader.GetInt32(reader.GetOrdinal("id")),
        reader["DeviceCode"] as string ?? string.Empty);
}

static async Task<EdgeEventVideoUpload?> FindVideoUploadAsync(
    MySqlConnection connection,
    int projectId,
    int eventId,
    int videoUploadId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT video.id, video.EdgeEventId, video.S3Key, video.UploadId, video.ContentType,
            video.Status, video.VideoUrl, video.PartEtagsJson
        FROM bee_EdgeEventVideo AS video
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = video.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        WHERE video.id = @VideoUploadId
            AND video.EdgeEventId = @EventId
            AND device.ProjectId = @ProjectId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@VideoUploadId", MySqlDbType.Int32).Value = videoUploadId;
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    var partsJson = reader["PartEtagsJson"] as string;
    var parts = string.IsNullOrWhiteSpace(partsJson)
        ? []
        : JsonSerializer.Deserialize<List<EdgeEventVideoPart>>(partsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    return new EdgeEventVideoUpload(
        reader.GetInt32(reader.GetOrdinal("id")),
        reader.GetInt32(reader.GetOrdinal("EdgeEventId")),
        reader["S3Key"] as string ?? string.Empty,
        reader["UploadId"] as string ?? string.Empty,
        reader["ContentType"] as string ?? "video/mp4",
        reader["Status"] as string ?? "Uploading",
        reader["VideoUrl"] as string,
        parts);
}

static List<EdgeEventVideoPart> UpsertVideoPart(
    IReadOnlyList<EdgeEventVideoPart> existingParts,
    int partNumber,
    string etag)
{
    var parts = existingParts
        .Where(part => part.PartNumber != partNumber)
        .Append(new EdgeEventVideoPart(partNumber, etag))
        .OrderBy(part => part.PartNumber)
        .ToList();
    return parts;
}

static async Task SaveVideoUploadPartsAsync(
    MySqlConnection connection,
    int videoUploadId,
    IReadOnlyList<EdgeEventVideoPart> parts,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_EdgeEventVideo
        SET PartEtagsJson = @PartEtagsJson
        WHERE id = @VideoUploadId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@VideoUploadId", MySqlDbType.Int32).Value = videoUploadId;
    command.Parameters.Add("@PartEtagsJson", MySqlDbType.JSON).Value =
        JsonSerializer.Serialize(parts, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<SpendBeeReceiptMultipartUpload?> FindSpendBeeReceiptUploadAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long receiptUploadId,
    CancellationToken cancellationToken)
{
    const string uploadSql = """
        SELECT id, ProjectId, AppUserId, Status, Timezone, CompletedAtUtc, CancelledAtUtc
        FROM bee_SpendBeeReceiptUpload
        WHERE id = @ReceiptUploadId
            AND ProjectId = @ProjectId
            AND AppUserId = @AppUserId
        LIMIT 1;
        """;
    await using var uploadCommand = new MySqlCommand(uploadSql, connection);
    uploadCommand.Parameters.Add("@ReceiptUploadId", MySqlDbType.Int64).Value = receiptUploadId;
    uploadCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    uploadCommand.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await uploadCommand.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
    {
        return null;
    }

    var upload = new SpendBeeReceiptMultipartUpload(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader.GetInt32(reader.GetOrdinal("ProjectId")),
        reader.GetInt32(reader.GetOrdinal("AppUserId")),
        reader["Status"] as string ?? "Uploading",
        reader["Timezone"] as string,
        reader["CompletedAtUtc"] as DateTime?,
        reader["CancelledAtUtc"] as DateTime?,
        []);
    await reader.DisposeAsync();

    const string imageSql = """
        SELECT id, ReceiptUploadId, S3Key, UploadId, FileName, ContentType, FileSizeBytes, SortOrder,
            Status, ImageUrl, PartEtagsJson, CompletedAtUtc
        FROM bee_SpendBeeReceiptUploadImage
        WHERE ReceiptUploadId = @ReceiptUploadId
        ORDER BY SortOrder, id;
        """;
    await using var imageCommand = new MySqlCommand(imageSql, connection);
    imageCommand.Parameters.Add("@ReceiptUploadId", MySqlDbType.Int64).Value = receiptUploadId;
    await using var imageReader = await imageCommand.ExecuteReaderAsync(cancellationToken);
    var images = new List<SpendBeeReceiptMultipartUploadImage>();
    while (await imageReader.ReadAsync(cancellationToken))
    {
        images.Add(MapSpendBeeReceiptUploadImage(imageReader, upload.Status));
    }

    return upload with { Images = images };
}

static async Task<SpendBeeReceiptMultipartUploadImage?> FindSpendBeeReceiptUploadImageAsync(
    MySqlConnection connection,
    int projectId,
    int appUserId,
    long receiptUploadId,
    long imageUploadId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT image.id, image.ReceiptUploadId, image.S3Key, image.UploadId, image.FileName, image.ContentType,
            image.FileSizeBytes, image.SortOrder, image.Status, image.ImageUrl, image.PartEtagsJson,
            image.CompletedAtUtc, upload.Status AS UploadStatus
        FROM bee_SpendBeeReceiptUploadImage AS image
        INNER JOIN bee_SpendBeeReceiptUpload AS upload ON upload.id = image.ReceiptUploadId
        WHERE image.id = @ImageUploadId
            AND image.ReceiptUploadId = @ReceiptUploadId
            AND upload.ProjectId = @ProjectId
            AND upload.AppUserId = @AppUserId
        LIMIT 1;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ImageUploadId", MySqlDbType.Int64).Value = imageUploadId;
    command.Parameters.Add("@ReceiptUploadId", MySqlDbType.Int64).Value = receiptUploadId;
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@AppUserId", MySqlDbType.Int32).Value = appUserId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    return await reader.ReadAsync(cancellationToken)
        ? MapSpendBeeReceiptUploadImage(reader, reader["UploadStatus"] as string ?? "Uploading")
        : null;
}

static SpendBeeReceiptMultipartUploadImage MapSpendBeeReceiptUploadImage(MySqlDataReader reader, string uploadStatus)
{
    var partsJson = reader["PartEtagsJson"] as string;
    var parts = string.IsNullOrWhiteSpace(partsJson)
        ? []
        : JsonSerializer.Deserialize<List<EdgeEventVideoPart>>(partsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    return new SpendBeeReceiptMultipartUploadImage(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader.GetInt64(reader.GetOrdinal("ReceiptUploadId")),
        reader["S3Key"] as string ?? string.Empty,
        reader["UploadId"] as string ?? string.Empty,
        reader["FileName"] as string,
        reader["ContentType"] as string ?? "image/jpeg",
        reader["FileSizeBytes"] is DBNull ? null : Convert.ToInt64(reader["FileSizeBytes"]),
        reader.GetInt32(reader.GetOrdinal("SortOrder")),
        reader["Status"] as string ?? "Uploading",
        uploadStatus,
        reader["ImageUrl"] as string,
        reader["CompletedAtUtc"] as DateTime?,
        parts);
}

static async Task SaveSpendBeeReceiptUploadImagePartsAsync(
    MySqlConnection connection,
    long imageUploadId,
    IReadOnlyList<EdgeEventVideoPart> parts,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeReceiptUploadImage
        SET PartEtagsJson = @PartEtagsJson
        WHERE id = @ImageUploadId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ImageUploadId", MySqlDbType.Int64).Value = imageUploadId;
    command.Parameters.Add("@PartEtagsJson", MySqlDbType.JSON).Value =
        JsonSerializer.Serialize(parts, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task CompleteSpendBeeReceiptUploadImageAsync(
    MySqlConnection connection,
    long imageUploadId,
    string imageUrl,
    IReadOnlyList<EdgeEventVideoPart> parts,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeReceiptUploadImage
        SET Status = 'Completed',
            ImageUrl = @ImageUrl,
            PartEtagsJson = @PartEtagsJson,
            CompletedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @ImageUploadId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ImageUploadId", MySqlDbType.Int64).Value = imageUploadId;
    command.Parameters.Add("@ImageUrl", MySqlDbType.VarChar, 800).Value = imageUrl;
    command.Parameters.Add("@PartEtagsJson", MySqlDbType.JSON).Value =
        JsonSerializer.Serialize(parts, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task CompleteSpendBeeReceiptUploadAsync(
    MySqlConnection connection,
    long receiptUploadId,
    CancellationToken cancellationToken)
{
    const string sql = """
        UPDATE bee_SpendBeeReceiptUpload
        SET Status = 'Completed',
            CompletedAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @ReceiptUploadId;
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ReceiptUploadId", MySqlDbType.Int64).Value = receiptUploadId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task CancelSpendBeeReceiptUploadAsync(
    MySqlConnection connection,
    long receiptUploadId,
    CancellationToken cancellationToken)
{
    const string imageSql = """
        UPDATE bee_SpendBeeReceiptUploadImage
        SET Status = 'Cancelled'
        WHERE ReceiptUploadId = @ReceiptUploadId
            AND Status = 'Uploading';
        """;
    await using var imageCommand = new MySqlCommand(imageSql, connection);
    imageCommand.Parameters.Add("@ReceiptUploadId", MySqlDbType.Int64).Value = receiptUploadId;
    await imageCommand.ExecuteNonQueryAsync(cancellationToken);

    const string uploadSql = """
        UPDATE bee_SpendBeeReceiptUpload
        SET Status = 'Cancelled',
            CancelledAtUtc = UTC_TIMESTAMP(6)
        WHERE id = @ReceiptUploadId;
        """;
    await using var uploadCommand = new MySqlCommand(uploadSql, connection);
    uploadCommand.Parameters.Add("@ReceiptUploadId", MySqlDbType.Int64).Value = receiptUploadId;
    await uploadCommand.ExecuteNonQueryAsync(cancellationToken);
}

static Uri BuildS3Uri(
    S3StorageOptions options,
    string key,
    IReadOnlyDictionary<string, string>? query = null)
{
    var host = $"{options.Bucket}.s3.{options.Region}.amazonaws.com";
    var escapedKey = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
    var queryString = query is null || query.Count == 0
        ? string.Empty
        : "?" + string.Join('&', query
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => string.IsNullOrEmpty(pair.Value)
                ? Uri.EscapeDataString(pair.Key)
                : $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    return new Uri($"https://{host}/{escapedKey}{queryString}");
}

static string NormalizeImageExtension(string? fileName, string contentType)
{
    var extension = string.IsNullOrWhiteSpace(fileName)
        ? string.Empty
        : Path.GetExtension(fileName.Trim());
    if (!string.IsNullOrWhiteSpace(extension) &&
        extension.Length <= 12 &&
        extension.All(character => char.IsLetterOrDigit(character) || character == '.'))
    {
        return extension.ToLowerInvariant();
    }

    return contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => ".jpg"
    };
}

static string NormalizeVideoExtension(string? fileName, string contentType)
{
    var extension = string.IsNullOrWhiteSpace(fileName)
        ? string.Empty
        : Path.GetExtension(fileName.Trim());
    if (!string.IsNullOrWhiteSpace(extension) &&
        extension.Length <= 12 &&
        extension.All(character => char.IsLetterOrDigit(character) || character == '.'))
    {
        return extension.ToLowerInvariant();
    }

    return contentType.ToLowerInvariant() switch
    {
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "video/x-msvideo" => ".avi",
        _ => ".mp4"
    };
}

static string BuildCompleteMultipartUploadXml(IReadOnlyList<EdgeEventVideoPart> parts)
{
    var elements = parts
        .OrderBy(part => part.PartNumber)
        .Select(part =>
            $"<Part><PartNumber>{part.PartNumber}</PartNumber><ETag>{System.Security.SecurityElement.Escape(part.ETag)}</ETag></Part>");
    return $"<CompleteMultipartUpload>{string.Concat(elements)}</CompleteMultipartUpload>";
}

static void ValidateS3Options(S3StorageOptions options)
{
    var missing = new List<string>();
    if (string.IsNullOrWhiteSpace(options.AccessKeyId)) missing.Add(nameof(options.AccessKeyId));
    if (string.IsNullOrWhiteSpace(options.SecretAccessKey)) missing.Add(nameof(options.SecretAccessKey));
    if (string.IsNullOrWhiteSpace(options.Region)) missing.Add(nameof(options.Region));
    if (string.IsNullOrWhiteSpace(options.Bucket)) missing.Add(nameof(options.Bucket));
    if (missing.Count > 0)
    {
        throw new InvalidOperationException($"S3 storage configuration is missing: {string.Join(", ", missing)}.");
    }
}

static string HashSecret(string secret)
{
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}

static string NormalizeBindingCode(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var chars = value
        .Trim()
        .ToUpperInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray();
    return new string(chars);
}

static string? NormalizeGender(string? value)
{
    var normalized = NormalizeBounded(value, 40);
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return null;
    }

    return normalized.Trim().ToLowerInvariant() switch
    {
        "male" or "m" => "Male",
        "female" or "f" => "Female",
        "non-binary" or "nonbinary" => "Non-binary",
        "prefer-not-to-say" or "prefer not to say" => "Prefer not to say",
        _ => normalized
    };
}

static string NormalizeRequired(string? value, string fallback)
{
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

static string NormalizeHeartbeatDeviceStatus(EdgeHeartbeatPayload payload)
{
    if (payload.RtspReachable == false ||
        IsOfflineStatus(payload.RtspStatus) ||
        IsOfflineStatus(payload.RemoteDeviceStatus) ||
        IsOfflineStatus(payload.DeviceStatus))
    {
        return "Remote Device Offline";
    }

    return NormalizeRequired(payload.DeviceStatus, "Unknown");
}

static bool IsOfflineStatus(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return value.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("not open", StringComparison.OrdinalIgnoreCase);
}

static string? BuildHeartbeatDetailJson(EdgeHeartbeatPayload payload)
{
    JsonObject detail;
    if (payload.DetailJson is { } json)
    {
        try
        {
            detail = JsonNode.Parse(json.GetRawText()) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            detail = [];
        }
    }
    else
    {
        detail = [];
    }

    if (!string.IsNullOrWhiteSpace(payload.ServerStatus))
    {
        detail["serverStatus"] = payload.ServerStatus.Trim();
    }

    if (!string.IsNullOrWhiteSpace(payload.RtspStatus))
    {
        detail["rtspStatus"] = payload.RtspStatus.Trim();
    }

    if (payload.RtspReachable.HasValue)
    {
        detail["rtspReachable"] = payload.RtspReachable.Value;
    }

    if (!string.IsNullOrWhiteSpace(payload.RemoteDeviceStatus))
    {
        detail["remoteDeviceStatus"] = payload.RemoteDeviceStatus.Trim();
    }

    if (!string.IsNullOrWhiteSpace(payload.RemoteDeviceMessage))
    {
        detail["remoteDeviceMessage"] = payload.RemoteDeviceMessage.Trim();
    }

    if (payload.PeopleCount.HasValue)
    {
        detail["peopleCount"] = payload.PeopleCount.Value;
    }

    if (payload.BraceletCount.HasValue)
    {
        detail["braceletCount"] = payload.BraceletCount.Value;
    }

    if (payload.MachineryVehicleCount.HasValue)
    {
        detail["machineryVehicleCount"] = payload.MachineryVehicleCount.Value;
    }

    if (payload.PpeComplianceRate.HasValue)
    {
        detail["ppeComplianceRate"] = payload.PpeComplianceRate.Value;
    }

    return detail.Count == 0 ? null : detail.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

static HttpRequestMessage BuildS3Request(
    HttpMethod method,
    Uri uri,
    string? contentType,
    S3StorageOptions options,
    string payloadHash)
{
    var now = DateTimeOffset.UtcNow;
    var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    var dateStamp = now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
    var credentialScope = $"{dateStamp}/{options.Region}/s3/aws4_request";
    var canonicalPath = uri.AbsolutePath;
    var canonicalQuery = BuildCanonicalQueryString(uri);
    var host = uri.Host;
    var signedHeaders = string.IsNullOrWhiteSpace(contentType)
        ? "host;x-amz-content-sha256;x-amz-date"
        : "content-type;host;x-amz-content-sha256;x-amz-date";
    var canonicalHeaders = string.IsNullOrWhiteSpace(contentType)
        ? string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n")
        : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"content-type:{contentType}\nhost:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n");
    var canonicalRequest = string.Join('\n',
        method.Method,
        canonicalPath,
        canonicalQuery,
        canonicalHeaders,
        signedHeaders,
        payloadHash);
    var stringToSign = string.Join('\n',
        "AWS4-HMAC-SHA256",
        amzDate,
        credentialScope,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());
    var signature = Convert.ToHexString(SignAws(GetS3SigningKey(options, dateStamp), stringToSign)).ToLowerInvariant();
    var authorization =
        $"AWS4-HMAC-SHA256 Credential={options.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

    var request = new HttpRequestMessage(method, uri);
    request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
    request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
    request.Headers.TryAddWithoutValidation("Authorization", authorization);
    return request;
}

static HttpRequestMessage BuildAwsSignedJsonRequest(
    HttpMethod method,
    Uri uri,
    string serviceName,
    string region,
    string accessKeyId,
    string secretAccessKey,
    string payloadJson)
{
    const string contentType = "application/json";
    var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))).ToLowerInvariant();
    var now = DateTimeOffset.UtcNow;
    var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    var dateStamp = now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
    var credentialScope = $"{dateStamp}/{region}/{serviceName}/aws4_request";
    var signedHeaders = "content-type;host;x-amz-content-sha256;x-amz-date";
    var canonicalHeaders = string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"content-type:{contentType}\nhost:{uri.Host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n");
    var canonicalRequest = string.Join('\n',
        method.Method,
        uri.AbsolutePath,
        BuildCanonicalQueryString(uri),
        canonicalHeaders,
        signedHeaders,
        payloadHash);
    var stringToSign = string.Join('\n',
        "AWS4-HMAC-SHA256",
        amzDate,
        credentialScope,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());
    var signature = Convert.ToHexString(SignAws(GetAwsSigningKey(secretAccessKey, region, serviceName, dateStamp), stringToSign)).ToLowerInvariant();
    var authorization =
        $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

    var request = new HttpRequestMessage(method, uri)
    {
        Content = new StringContent(payloadJson, Encoding.UTF8, contentType)
    };
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
    request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
    request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
    request.Headers.TryAddWithoutValidation("Authorization", authorization);
    return request;
}

static string BuildCanonicalQueryString(Uri uri)
{
    if (string.IsNullOrWhiteSpace(uri.Query))
    {
        return string.Empty;
    }

    return string.Join('&', uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part =>
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0]);
            var value = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
            return new KeyValuePair<string, string>(key, value);
        })
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .ThenBy(pair => pair.Value, StringComparer.Ordinal)
        .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
}

static byte[] GetS3SigningKey(S3StorageOptions options, string dateStamp)
{
    return GetAwsSigningKey(options.SecretAccessKey, options.Region, "s3", dateStamp);
}

static byte[] GetAwsSigningKey(string secretAccessKey, string region, string serviceName, string dateStamp)
{
    var dateKey = SignAws(Encoding.UTF8.GetBytes($"AWS4{secretAccessKey}"), dateStamp);
    var dateRegionKey = SignAws(dateKey, region);
    var dateRegionServiceKey = SignAws(dateRegionKey, serviceName);
    return SignAws(dateRegionServiceKey, "aws4_request");
}

static byte[] SignAws(byte[] key, string data)
{
    using var hmac = new HMACSHA256(key);
    return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
}

static string BuildYoloText(EventAnnotationPayload payload)
{
    var lines = payload.Boxes.Select(box =>
    {
        var xCenter = (box.X + box.W / 2m) / payload.ImageWidth;
        var yCenter = (box.Y + box.H / 2m) / payload.ImageHeight;
        var width = box.W / payload.ImageWidth;
        var height = box.H / payload.ImageHeight;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{box.ClassId} {xCenter:0.000000} {yCenter:0.000000} {width:0.000000} {height:0.000000}");
    });

    return string.Join(Environment.NewLine, lines);
}

static object BuildAnnotationActor(ClaimsPrincipal user)
{
    return new
    {
        name = user.FindFirstValue(ClaimTypes.Name) ?? "Unknown user",
        email = user.FindFirstValue(ClaimTypes.Email)
    };
}

static async Task InsertAnnotationOperationLogAsync(
    MySqlConnection connection,
    int projectId,
    string targetType,
    long targetId,
    int? edgeEventId,
    long? edgeEventSubjectId,
    int adminId,
    ClaimsPrincipal user,
    string action,
    int boxCount,
    bool saveAsPendingLearning,
    CancellationToken cancellationToken)
{
    const string sql = """
        INSERT INTO bee_AnnotationOperationLog (
            ProjectId, TargetType, TargetId, EdgeEventId, EdgeEventSubjectId,
            AdminId, AdminName, AdminEmail, Action, BoxCount, SaveAsPendingLearning)
        VALUES (
            @ProjectId, @TargetType, @TargetId, @EdgeEventId, @EdgeEventSubjectId,
            @AdminId, @AdminName, @AdminEmail, @Action, @BoxCount, @SaveAsPendingLearning);
        """;
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@TargetType", MySqlDbType.VarChar, 40).Value = targetType;
    command.Parameters.Add("@TargetId", MySqlDbType.Int64).Value = targetId;
    command.Parameters.Add("@EdgeEventId", MySqlDbType.Int32).Value = (object?)edgeEventId ?? DBNull.Value;
    command.Parameters.Add("@EdgeEventSubjectId", MySqlDbType.Int64).Value = (object?)edgeEventSubjectId ?? DBNull.Value;
    command.Parameters.Add("@AdminId", MySqlDbType.Int32).Value = adminId;
    command.Parameters.Add("@AdminName", MySqlDbType.VarChar, 100).Value = (object?)user.FindFirstValue(ClaimTypes.Name) ?? DBNull.Value;
    command.Parameters.Add("@AdminEmail", MySqlDbType.VarChar, 150).Value = (object?)user.FindFirstValue(ClaimTypes.Email) ?? DBNull.Value;
    command.Parameters.Add("@Action", MySqlDbType.VarChar, 80).Value = action;
    command.Parameters.Add("@BoxCount", MySqlDbType.Int32).Value = Math.Max(0, boxCount);
    command.Parameters.Add("@SaveAsPendingLearning", MySqlDbType.Bit).Value = saveAsPendingLearning;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<IReadOnlyList<object>> LoadAnnotationOperationLogsAsync(
    MySqlConnection connection,
    int eventId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT id, TargetType, TargetId, EdgeEventId, EdgeEventSubjectId,
            AdminId, AdminName, AdminEmail, Action, BoxCount, SaveAsPendingLearning, CreatedAtUtc
        FROM bee_AnnotationOperationLog
        WHERE EdgeEventId = @EventId
        ORDER BY CreatedAtUtc DESC
        LIMIT 20;
        """;
    var logs = new List<object>();
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@EventId", MySqlDbType.Int32).Value = eventId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        logs.Add(new
        {
            id = reader.GetInt64(reader.GetOrdinal("id")),
            targetType = reader["TargetType"] as string ?? string.Empty,
            targetId = reader.GetInt64(reader.GetOrdinal("TargetId")),
            edgeEventId = DbInt(reader, "EdgeEventId"),
            edgeEventSubjectId = reader.IsDBNull(reader.GetOrdinal("EdgeEventSubjectId"))
                ? (long?)null
                : reader.GetInt64(reader.GetOrdinal("EdgeEventSubjectId")),
            adminId = reader.GetInt32(reader.GetOrdinal("AdminId")),
            adminName = reader["AdminName"] as string,
            adminEmail = reader["AdminEmail"] as string,
            action = reader["Action"] as string ?? string.Empty,
            boxCount = reader.GetInt32(reader.GetOrdinal("BoxCount")),
            saveAsPendingLearning = reader.GetBoolean(reader.GetOrdinal("SaveAsPendingLearning")),
            createdAtUtc = reader.GetDateTime(reader.GetOrdinal("CreatedAtUtc"))
        });
    }

    return logs;
}

static string? NormalizeReviewModelKind(string? modelKind)
{
    if (string.Equals(modelKind, "panorama", StringComparison.OrdinalIgnoreCase))
    {
        return "panorama";
    }

    if (string.Equals(modelKind, "subjects", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelKind, "personSlicePpe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelKind, "person-slice-ppe", StringComparison.OrdinalIgnoreCase))
    {
        return "personSlicePpe";
    }

    return null;
}

static object? BuildLastEditorObject(MySqlDataReader reader)
{
    var adminOrdinal = reader.GetOrdinal("AdminId");
    if (reader.IsDBNull(adminOrdinal))
    {
        return null;
    }

    return new
    {
        adminId = reader.GetInt32(adminOrdinal),
        name = reader["AdminName"] as string,
        email = reader["AdminEmail"] as string,
        editedAtUtc = reader.IsDBNull(reader.GetOrdinal("LastEditedAtUtc"))
            ? (DateTime?)null
            : reader.GetDateTime(reader.GetOrdinal("LastEditedAtUtc"))
    };
}

static JsonArray DefaultPersonPpeReviewClasses()
{
    var classes = new JsonArray();
    foreach (var item in YoloYamlFile.DefaultModelClasses())
    {
        classes.Add(new JsonObject { ["id"] = item.Index, ["name"] = item.Name });
    }

    return classes;
}

static async Task<IReadOnlyList<object>> LoadPanoramaPendingReviewItemsAsync(
    MySqlConnection connection,
    int projectId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT evt.id, evt.Title, evt.EventDescription, evt.AnnotationJson, evt.EventTimeUtc,
            device.DeviceName, latest.AdminId, latest.AdminName, latest.AdminEmail, latest.CreatedAtUtc AS LastEditedAtUtc
        FROM bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        LEFT JOIN (
            SELECT log.TargetId, log.AdminId, log.AdminName, log.AdminEmail, log.CreatedAtUtc
            FROM bee_AnnotationOperationLog AS log
            INNER JOIN (
                SELECT TargetId, MAX(id) AS MaxId
                FROM bee_AnnotationOperationLog
                WHERE TargetType = 'Event'
                GROUP BY TargetId
            ) AS grouped ON grouped.MaxId = log.id
        ) AS latest ON latest.TargetId = evt.id
        WHERE device.ProjectId = @ProjectId
          AND COALESCE(evt.LearningStatus, 'None') = 'Pending Learning'
        ORDER BY evt.EventTimeUtc DESC, evt.id DESC;
        """;
    var items = new List<object>();
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var id = reader.GetInt32(reader.GetOrdinal("id"));
        var annotation = ParseJsonNode(reader["AnnotationJson"] as string);
        items.Add(new
        {
            id,
            title = reader["Title"] as string ?? $"Event {id}",
            subtitle = $"{reader["DeviceName"] as string ?? "Device"} | {reader.GetDateTime(reader.GetOrdinal("EventTimeUtc")):yyyy-MM-dd HH:mm}",
            description = reader["EventDescription"] as string,
            imageUrl = $"/api/events/{id}/image",
            targetType = "Event",
            boxes = annotation?["boxes"],
            classes = annotation?["classes"],
            lastEditor = BuildLastEditorObject(reader)
        });
    }

    return items;
}

static async Task<IReadOnlyList<object>> LoadPersonSlicePendingReviewItemsAsync(
    MySqlConnection connection,
    int projectId,
    CancellationToken cancellationToken)
{
    const string sql = """
        SELECT subject.id, subject.EdgeEventId, subject.SubjectKey, subject.TrackingLabel, subject.PpeBoxJson,
            evt.Title, evt.EventTimeUtc, device.DeviceName,
            latest.AdminId, latest.AdminName, latest.AdminEmail, latest.CreatedAtUtc AS LastEditedAtUtc
        FROM bee_EdgeEventSubject AS subject
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        LEFT JOIN (
            SELECT log.TargetId, log.AdminId, log.AdminName, log.AdminEmail, log.CreatedAtUtc
            FROM bee_AnnotationOperationLog AS log
            INNER JOIN (
                SELECT TargetId, MAX(id) AS MaxId
                FROM bee_AnnotationOperationLog
                WHERE TargetType = 'PersonSlicePpe'
                GROUP BY TargetId
            ) AS grouped ON grouped.MaxId = log.id
        ) AS latest ON latest.TargetId = subject.id
        WHERE device.ProjectId = @ProjectId
          AND subject.SubjectType = 'Person'
          AND COALESCE(subject.LearningStatus, 'None') = 'Pending Learning'
        ORDER BY evt.EventTimeUtc DESC, evt.id DESC, subject.SubjectKey, subject.id;
        """;
    var items = new List<object>();
    await using var command = new MySqlCommand(sql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        var id = reader.GetInt64(reader.GetOrdinal("id"));
        items.Add(new
        {
            id,
            title = !string.IsNullOrWhiteSpace(reader["TrackingLabel"] as string)
                ? reader["TrackingLabel"] as string
                : reader["SubjectKey"] as string ?? $"Person slice {id}",
            subtitle = $"{reader["Title"] as string ?? "Event"} | {reader["DeviceName"] as string ?? "Device"} | {reader.GetDateTime(reader.GetOrdinal("EventTimeUtc")):yyyy-MM-dd HH:mm}",
            imageUrl = $"/api/edge-event-subjects/{id}/image/crop",
            targetType = "PersonSlicePpe",
            boxes = ParseJsonNode(reader["PpeBoxJson"] as string),
            classes = DefaultPersonPpeReviewClasses(),
            lastEditor = BuildLastEditorObject(reader)
        });
    }

    return items;
}

static async Task<bool> CancelPanoramaPendingLearningAsync(
    MySqlConnection connection,
    int projectId,
    int reviewerAdminId,
    long targetId,
    CancellationToken cancellationToken)
{
    const string updateSql = """
        UPDATE bee_EdgeEvent AS evt
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        SET evt.LearningStatus = 'None'
        WHERE evt.id = @EventId
          AND device.ProjectId = @ProjectId
          AND COALESCE(evt.LearningStatus, 'None') = 'Pending Learning';
        """;
    await using var updateCommand = new MySqlCommand(updateSql, connection);
    updateCommand.Parameters.Add("@EventId", MySqlDbType.Int64).Value = targetId;
    updateCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
    {
        return false;
    }

    await InsertReviewMistakeAsync(connection, projectId, "Event", targetId, (int)targetId, null, reviewerAdminId, cancellationToken);
    return true;
}

static async Task<bool> CancelPersonSlicePendingLearningAsync(
    MySqlConnection connection,
    int projectId,
    int reviewerAdminId,
    long targetId,
    CancellationToken cancellationToken)
{
    const string updateSql = """
        UPDATE bee_EdgeEventSubject AS subject
        INNER JOIN bee_EdgeEvent AS evt ON evt.id = subject.EdgeEventId
        INNER JOIN bee_EdgeDevice AS device ON device.id = evt.EdgeDeviceId
        SET subject.LearningStatus = 'None'
        WHERE subject.id = @SubjectId
          AND device.ProjectId = @ProjectId
          AND COALESCE(subject.LearningStatus, 'None') = 'Pending Learning';
        """;
    await using var updateCommand = new MySqlCommand(updateSql, connection);
    updateCommand.Parameters.Add("@SubjectId", MySqlDbType.Int64).Value = targetId;
    updateCommand.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    if (await updateCommand.ExecuteNonQueryAsync(cancellationToken) == 0)
    {
        return false;
    }

    const string eventSql = "SELECT EdgeEventId FROM bee_EdgeEventSubject WHERE id = @SubjectId LIMIT 1;";
    await using var eventCommand = new MySqlCommand(eventSql, connection);
    eventCommand.Parameters.Add("@SubjectId", MySqlDbType.Int64).Value = targetId;
    var edgeEventId = Convert.ToInt32(await eventCommand.ExecuteScalarAsync(cancellationToken));
    await InsertReviewMistakeAsync(connection, projectId, "PersonSlicePpe", targetId, edgeEventId, targetId, reviewerAdminId, cancellationToken);
    return true;
}

static async Task InsertReviewMistakeAsync(
    MySqlConnection connection,
    int projectId,
    string targetType,
    long targetId,
    int? edgeEventId,
    long? edgeEventSubjectId,
    int reviewerAdminId,
    CancellationToken cancellationToken)
{
    const string latestSql = """
        SELECT AdminId, AdminName, AdminEmail
        FROM bee_AnnotationOperationLog
        WHERE TargetType = @TargetType AND TargetId = @TargetId
        ORDER BY id DESC
        LIMIT 1;
        """;
    int? editorAdminId = null;
    string? editorName = null;
    string? editorEmail = null;
    await using (var latestCommand = new MySqlCommand(latestSql, connection))
    {
        latestCommand.Parameters.Add("@TargetType", MySqlDbType.VarChar, 40).Value = targetType;
        latestCommand.Parameters.Add("@TargetId", MySqlDbType.Int64).Value = targetId;
        await using var reader = await latestCommand.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            editorAdminId = reader.GetInt32(reader.GetOrdinal("AdminId"));
            editorName = reader["AdminName"] as string;
            editorEmail = reader["AdminEmail"] as string;
        }
    }

    const string insertSql = """
        INSERT INTO bee_AnnotationReviewMistake (
            ProjectId, TargetType, TargetId, EdgeEventId, EdgeEventSubjectId,
            EditorAdminId, EditorName, EditorEmail, ReviewerAdminId)
        VALUES (
            @ProjectId, @TargetType, @TargetId, @EdgeEventId, @EdgeEventSubjectId,
            @EditorAdminId, @EditorName, @EditorEmail, @ReviewerAdminId);
        """;
    await using var command = new MySqlCommand(insertSql, connection);
    command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
    command.Parameters.Add("@TargetType", MySqlDbType.VarChar, 40).Value = targetType;
    command.Parameters.Add("@TargetId", MySqlDbType.Int64).Value = targetId;
    command.Parameters.Add("@EdgeEventId", MySqlDbType.Int32).Value = (object?)edgeEventId ?? DBNull.Value;
    command.Parameters.Add("@EdgeEventSubjectId", MySqlDbType.Int64).Value = (object?)edgeEventSubjectId ?? DBNull.Value;
    command.Parameters.Add("@EditorAdminId", MySqlDbType.Int32).Value = (object?)editorAdminId ?? DBNull.Value;
    command.Parameters.Add("@EditorName", MySqlDbType.VarChar, 100).Value = (object?)editorName ?? DBNull.Value;
    command.Parameters.Add("@EditorEmail", MySqlDbType.VarChar, 150).Value = (object?)editorEmail ?? DBNull.Value;
    command.Parameters.Add("@ReviewerAdminId", MySqlDbType.Int32).Value = reviewerAdminId;
    await command.ExecuteNonQueryAsync(cancellationToken);
}

static async Task<IReadOnlyList<object>> LoadAnnotationMistakeStatsAsync(
    MySqlConnection connection,
    int projectId,
    DateTime fromUtc,
    DateTime toUtc,
    CancellationToken cancellationToken)
{
    var rows = new Dictionary<int, AnnotationMistakeStatsRow>();
    const string savesSql = """
        SELECT AdminId, COALESCE(AdminName, CONCAT('Admin ', AdminId)) AS AdminName, AdminEmail, COUNT(*) AS SaveCount
        FROM bee_AnnotationOperationLog
        WHERE ProjectId = @ProjectId AND CreatedAtUtc >= @FromUtc AND CreatedAtUtc < @ToUtc
        GROUP BY AdminId, AdminName, AdminEmail;
        """;
    await using (var command = new MySqlCommand(savesSql, connection))
    {
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@FromUtc", MySqlDbType.DateTime).Value = fromUtc;
        command.Parameters.Add("@ToUtc", MySqlDbType.DateTime).Value = toUtc;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var adminId = reader.GetInt32(reader.GetOrdinal("AdminId"));
            rows[adminId] = new AnnotationMistakeStatsRow(
                adminId,
                reader["AdminName"] as string ?? $"Admin {adminId}",
                reader["AdminEmail"] as string,
                reader.GetInt32(reader.GetOrdinal("SaveCount")),
                0);
        }
    }

    const string mistakesSql = """
        SELECT EditorAdminId, COALESCE(EditorName, CONCAT('Admin ', EditorAdminId)) AS EditorName, EditorEmail, COUNT(*) AS MistakeCount
        FROM bee_AnnotationReviewMistake
        WHERE ProjectId = @ProjectId AND ReviewedAtUtc >= @FromUtc AND ReviewedAtUtc < @ToUtc AND EditorAdminId IS NOT NULL
        GROUP BY EditorAdminId, EditorName, EditorEmail;
        """;
    await using (var command = new MySqlCommand(mistakesSql, connection))
    {
        command.Parameters.Add("@ProjectId", MySqlDbType.Int32).Value = projectId;
        command.Parameters.Add("@FromUtc", MySqlDbType.DateTime).Value = fromUtc;
        command.Parameters.Add("@ToUtc", MySqlDbType.DateTime).Value = toUtc;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var adminId = reader.GetInt32(reader.GetOrdinal("EditorAdminId"));
            var mistakes = reader.GetInt32(reader.GetOrdinal("MistakeCount"));
            if (rows.TryGetValue(adminId, out var existing))
            {
                rows[adminId] = existing with { MistakeCount = mistakes };
            }
            else
            {
                rows[adminId] = new AnnotationMistakeStatsRow(
                    adminId,
                    reader["EditorName"] as string ?? $"Admin {adminId}",
                    reader["EditorEmail"] as string,
                    0,
                    mistakes);
            }
        }
    }

    return rows.Values
        .OrderByDescending(row => row.MistakeRate)
        .ThenByDescending(row => row.MistakeCount)
        .Select(row => new
        {
            row.AdminId,
            row.Name,
            row.Email,
            row.SaveCount,
            row.MistakeCount,
            mistakeRate = row.MistakeRate
        })
        .ToList();
}

static string BuildSubjectPpeBoxJson(EventAnnotationPayload payload)
{
    var classesById = payload.Classes
        .GroupBy(item => item.Id)
        .ToDictionary(group => group.Key, group => group.First().Name);
    var boxes = new JsonArray();
    foreach (var box in payload.Boxes)
    {
        var label = classesById.TryGetValue(box.ClassId, out var className) && !string.IsNullOrWhiteSpace(className)
            ? className
            : $"class-{box.ClassId}";
        boxes.Add(new JsonObject
        {
            ["label"] = label,
            ["source"] = "console_ppe_annotation",
            ["imageWidth"] = payload.ImageWidth,
            ["imageHeight"] = payload.ImageHeight,
            ["cropBox"] = new JsonObject
            {
                ["x"] = Math.Round(box.X, 2),
                ["y"] = Math.Round(box.Y, 2),
                ["w"] = Math.Round(box.W, 2),
                ["h"] = Math.Round(box.H, 2),
                ["label"] = label,
                ["classId"] = box.ClassId
            }
        });
    }

    return boxes.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

public sealed record EventAnnotationPayload(
    string ImageUrl,
    decimal ImageWidth,
    decimal ImageHeight,
    IReadOnlyList<EventAnnotationClass> Classes,
    IReadOnlyList<EventAnnotationBox> Boxes,
    string? YoloText,
    bool SaveAsPendingLearning = false);

public sealed record PendingLearningReviewCancelPayload(
    string ModelKind,
    long TargetId);

public sealed record AnnotationMistakeStatsRow(
    int AdminId,
    string Name,
    string? Email,
    int SaveCount,
    int MistakeCount)
{
    public decimal MistakeRate => SaveCount <= 0 ? 100m : Math.Round(MistakeCount * 100m / SaveCount, 2);
}

public sealed record EventAnnotationClass(int Id, string Name);

public sealed record EventAnnotationBox(int ClassId, decimal X, decimal Y, decimal W, decimal H);

public sealed record EventAnnotationDocument(
    string? ImageUrl,
    decimal ImageWidth,
    decimal ImageHeight,
    IReadOnlyList<EventAnnotationClass> Classes,
    IReadOnlyList<EventAnnotationBox> Boxes);

public sealed record EdgeAuthPayload(string ApiKey, string? ClientName);

public sealed record EdgeHeartbeatPayload(
    string DeviceCode,
    string? RuntimeStatus,
    string? DeviceStatus,
    DateTime? ReportedAtUtc,
    JsonElement? DetailJson,
    string? ServerStatus = null,
    string? RtspStatus = null,
    bool? RtspReachable = null,
    string? RemoteDeviceStatus = null,
    string? RemoteDeviceMessage = null,
    int? PeopleCount = null,
    int? BraceletCount = null,
    int? MachineryVehicleCount = null,
    decimal? PpeComplianceRate = null);

public sealed record EdgeEventUploadPayload(
    string DeviceCode,
    string Title,
    string? Description,
    DateTime? EventTimeUtc,
    string? ImageUrl,
    string? ImageBase64,
    string? ImageContentType,
    JsonElement? DetectionJson,
    EdgeEventAnalysisPayload? Analysis = null,
    IReadOnlyList<EdgeEventSubjectPayload>? Subjects = null);

public sealed record EdgeEventAnalysisPayload(
    int? PeopleCount = null,
    int? MachineryVehicleCount = null,
    int? ToolCount = null,
    int? PpeCompliantPeopleCount = null,
    int? RiskPersonCount = null,
    decimal? PpeComplianceRate = null,
    string? RiskCategory = null,
    string? RiskSeverity = null,
    string? Summary = null,
    JsonElement? AnalysisJson = null,
    IReadOnlyList<EdgeEventSubjectPayload>? Subjects = null);

public sealed record EdgeEventSubjectPayload(
    string? SubjectKey,
    string? SubjectType = "Person",
    string? TrackingLabel = null,
    string? CropImageUrl = null,
    string? PreviewImageUrl = null,
    JsonElement? BoundingBoxJson = null,
    JsonElement? PpeBoxJson = null,
    JsonElement? PpeStatusJson = null,
    bool IsRisk = false,
    string? RiskCategory = null,
    string? RiskSeverity = null,
    string? RiskReason = null,
    JsonElement? AnalysisJson = null);

public sealed record EdgeEventAnalysisResult(
    int PeopleCount,
    int MachineryVehicleCount,
    int ToolCount,
    int PpeCompliantPeopleCount,
    int RiskPersonCount,
    decimal? PpeComplianceRate,
    string? RiskCategory,
    string RiskSeverity,
    string? Summary,
    string? AnalysisJson,
    IReadOnlyList<EdgeEventSubjectPayload> Subjects);

public sealed record VerifiedEventReview(
    string? Status,
    string? PpeReviewJson,
    string? AnnotationJson);

public sealed record EdgeEventVideoUploadStartPayload(
    string? FileName,
    string? ContentType,
    long? FileSizeBytes);

public sealed record EdgeEventVideoUploadCompletePayload(
    IReadOnlyList<EdgeEventVideoPart>? Parts);

public sealed record EdgeEventVideoPart(int PartNumber, string ETag);

public sealed record EdgeEventContext(int Id, string DeviceCode);

public sealed record EdgeEventVideoUpload(
    int Id,
    int EventId,
    string S3Key,
    string UploadId,
    string ContentType,
    string Status,
    string? VideoUrl,
    IReadOnlyList<EdgeEventVideoPart> Parts);

public sealed record EdgeApiSession(int Id, int ProjectId);

public sealed record AppSmsCodeRequest(int? ProjectId, string PhoneNumber, string? Purpose = null);

public sealed record AppEmailCodeRequest(int? ProjectId, string Email, string? Purpose = null);

public sealed record AppRegisterRequest(
    int? ProjectId,
    string Email,
    string VerificationCode,
    string DisplayName,
    string? FirstName = null,
    string? LastName = null,
    string? Gender = null,
    AppClientDeviceInfo? Device = null);

public sealed record AppLoginRequest(
    int? ProjectId,
    string Email,
    string VerificationCode,
    AppClientDeviceInfo? Device = null);

public sealed record AppClientDeviceInfo(
    string? DeviceIdentifier,
    string? DeviceType,
    string? Platform,
    string? OsVersion,
    string? AppVersion,
    string? PushProvider,
    string? PushToken);

public sealed record AppBindDeviceRequest(string? BindingToken, string? BindingCode = null);

public sealed record AppProfileUpdateRequest(
    string DisplayName,
    string? FirstName = null,
    string? LastName = null,
    string? Gender = null);

public sealed record SpendBeeEmailCodeRequest(string Email, string? Purpose = null);

public sealed record SpendBeeRegisterRequest(
    string Email,
    string VerificationCode,
    string DisplayName,
    string? Gender = null,
    string? AvatarUrl = null,
    string? Bio = null,
    AppClientDeviceInfo? Device = null);

public sealed record SpendBeeLoginRequest(
    string Email,
    string VerificationCode,
    AppClientDeviceInfo? Device = null);

public sealed record SpendBeeAuthUser(
    int Id,
    string DisplayName,
    string? Gender,
    string? AvatarUrl,
    string? Bio);

public sealed record SpendBeeProfileUpdateRequest(
    string DisplayName,
    string? Gender = null,
    string? AvatarUrl = null,
    string? Bio = null);

public sealed record SpendBeeReceiptGroupUpdateRequest(
    string Title,
    string? Description = null);

public sealed record SpendBeeReceiptGroupReceiptAddRequest(
    IReadOnlyList<long>? ReceiptIds = null);

public sealed record SpendBeeReceiptUploadRequest(
    IReadOnlyList<SpendBeeReceiptImageUpload> Images,
    string? Timezone = null);

public sealed record SpendBeeReceiptImageUpload(
    string ImageBase64,
    string? ContentType = null,
    string? FileName = null);

public sealed record SpendBeeReceiptDuplicate(
    long ReceiptId,
    int AppUserId,
    string Status,
    string DisplayName,
    string? Email,
    string? MerchantName,
    string? MerchantAddress,
    DateTime? PurchasedAtUtc,
    decimal? Total,
    string? Currency);

public sealed record SpendBeeMerchantRecord(
    long Id,
    string? GooglePlaceId,
    string Name,
    string? Address,
    string? PrimaryType,
    decimal? Latitude,
    decimal? Longitude,
    string? GooglePhotoUri,
    string? AiCoverImageUrl,
    string? CoverSource,
    string? CoverCategory,
    string? StreetViewImageUrl,
    string SyncStatus);

public sealed record SpendBeeMerchantCoverResult(
    string Url,
    string Source,
    string Category,
    string Prompt,
    string? StreetViewImageUrl,
    decimal? Latitude,
    decimal? Longitude);

public sealed record SpendBeeStreetViewImage(byte[] Bytes, string StoredUrl, decimal? Latitude, decimal? Longitude);

public sealed record SpendBeeEnsureGoogleMerchantRequest(string GooglePlaceId);

public sealed record SpendBeeNearbyLocalMerchant(
    long Id,
    string? GooglePlaceId,
    string Name,
    string? Address,
    string? PrimaryType,
    decimal? Latitude,
    decimal? Longitude,
    double? DistanceMeters,
    decimal? Rating,
    int? UserRatingCount,
    string? CoverImageUrl,
    string? GoogleMapsUri,
    string? WebsiteUrl,
    string? PhoneNumber,
    string SyncStatus);

public sealed record SpendBeeGooglePlace(
    string? PlaceId,
    string? ResourceName,
    string Name,
    string? Address,
    string? PhoneNumber,
    string? WebsiteUrl,
    string? GoogleMapsUri,
    string? PrimaryType,
    string? BusinessStatus,
    decimal? Latitude,
    decimal? Longitude,
    decimal? Rating,
    int? UserRatingCount,
    string? PriceLevel,
    bool? DineIn,
    bool? Takeout,
    string? EditorialSummary,
    string? PhotoName,
    string? PhotoUri,
    string? PhotoAttributionsJson,
    string SourceJson);

public sealed record SpendBeeReceiptMultipartUploadStartRequest(
    IReadOnlyList<SpendBeeReceiptMultipartUploadImageStart> Images,
    string? Timezone = null);

public sealed record SpendBeeReceiptMultipartUploadImageStart(
    string? ContentType = null,
    string? FileName = null,
    long? FileSizeBytes = null);

public sealed record SpendBeeReceiptMultipartUpload(
    long Id,
    int ProjectId,
    int AppUserId,
    string Status,
    string? Timezone,
    DateTime? CompletedAtUtc,
    DateTime? CancelledAtUtc,
    IReadOnlyList<SpendBeeReceiptMultipartUploadImage> Images);

public sealed record SpendBeeReceiptMultipartUploadImage(
    long Id,
    long ReceiptUploadId,
    string S3Key,
    string UploadId,
    string? FileName,
    string ContentType,
    long? FileSizeBytes,
    int SortOrder,
    string Status,
    string UploadStatus,
    string? ImageUrl,
    DateTime? CompletedAtUtc,
    IReadOnlyList<EdgeEventVideoPart> Parts)
{
    public string ImageStatus => Status;
}

public sealed record SpendBeeMerchantPhotoUploadStartRequest(
    long MerchantId,
    string? ContentType = null,
    string? FileName = null,
    long? FileSizeBytes = null,
    string? Category = null,
    string? Caption = null);

public sealed record SpendBeeMerchantPhotoUpload(
    long Id,
    int ProjectId,
    long MerchantId,
    int AppUserId,
    string S3Key,
    string UploadId,
    string? FileName,
    string ContentType,
    long? FileSizeBytes,
    string? Category,
    string? Caption,
    string Status,
    IReadOnlyList<EdgeEventVideoPart> Parts,
    string? OriginalImageUrl,
    long? PhotoId,
    DateTime? CompletedAtUtc,
    DateTime? CancelledAtUtc);

public sealed record SpendBeeLikeRequest(bool? Liked = true);

public sealed record SpendBeePhotoOwner(int AppUserId, long MerchantId);

public sealed record SpendBeeUserPublicProfile(
    int AppUserId,
    string? DisplayName,
    string? AvatarUrl,
    string? Gender);

public sealed record SpendBeePhotoCommentRequest(string Body, long? ParentCommentId = null);

public sealed record SpendBeePhotoCommentReplyRequest(string Body);

public sealed record SpendBeeUploadedReceiptImage(
    string Url,
    string ContentType,
    byte[] Bytes,
    int SortOrder);

public sealed record SpendBeeReceiptRecognition(
    string? ReceiptType,
    string? FulfillmentType,
    SpendBeeReceiptPlatformRecognition? Platform,
    string? MerchantName,
    string? MerchantAddress,
    string? PlatformOrderNumber,
    string? PurchasedAt,
    string? OrderedAt,
    string? PickupAt,
    string? DeliveredAt,
    string? Currency,
    decimal? Subtotal,
    decimal? Tax,
    decimal? DeliveryFee,
    decimal? ServiceFee,
    decimal? PlatformDiscount,
    decimal? Total,
    IReadOnlyList<SpendBeeReceiptLineRecognition> LineItems,
    SpendBeeReceiptQuality Quality);

public sealed record SpendBeeReceiptPlatformRecognition(
    string? Name,
    string? DisplayName,
    string? PlatformType,
    string? WebsiteUrl,
    decimal Confidence);

public sealed record SpendBeeReceiptLineRecognition(
    string Name,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Amount,
    string? Category,
    decimal Confidence);

public sealed record SpendBeeReceiptQuality(
    decimal OverallConfidence,
    decimal EstimatedErrorRate,
    bool NeedsHumanReview,
    IReadOnlyList<string> FailedChecks);

public sealed record AppRiskNotificationSettingsUpdateRequest(
    bool? SevereDangerEnabled = null,
    bool? OrdinaryRiskEnabled = null,
    bool? RealRiskEnabled = null,
    IReadOnlyList<AppRiskNotificationSetting>? Settings = null);

public sealed record AppRiskNotificationSetting(string RiskSeverity, bool PushEnabled);

public sealed record AppRiskNotificationSettingsResponse(
    string DeviceCode,
    IReadOnlyList<AppRiskNotificationSetting> Settings);

public sealed record AppRiskNotificationResponse(
    long Id,
    int EventId,
    string DeviceCode,
    string DeviceName,
    string RiskSeverity,
    string Title,
    string? Message,
    bool IsRead,
    string PushStatus,
    DateTime CreatedAtUtc,
    DateTime? ReadAtUtc);

public sealed record ApnsOptions(
    string TeamId,
    string KeyId,
    string BundleId,
    string PrivateKeyPath,
    string Endpoint);

public sealed record QueuedApnsNotification(
    long NotificationId,
    int EdgeEventId,
    string DeviceCode,
    string DeviceName,
    string RiskSeverity,
    string Title,
    string? Message,
    string PushToken);

public sealed record ApnsSendResult(bool Success, string? ProviderMessageId, string? ErrorText);

public sealed record AppUserProfile(
    int Id,
    int ProjectId,
    string? Email,
    string? PhoneNumber,
    string DisplayName,
    string? FirstName,
    string? LastName,
    string? Gender,
    string? AvatarUrl,
    string? Bio,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record AppApiSession(
    long Id,
    int ProjectId,
    int AppUserId,
    string PhoneNumber,
    string Email,
    string DisplayName,
    string? FirstName,
    string? LastName,
    string? Gender);

public sealed record SmsSendResult(
    bool Success,
    string Message,
    string? ProviderMessageId,
    string? ProviderStatus,
    string? ErrorText,
    string? RawResponseJson);

public sealed record EmailSendResult(bool Success, string Provider, string? ProviderMessageId, string? ErrorText)
{
    public string Message => Success ? "Email sent." : ErrorText ?? "Email delivery failed.";
}

public sealed record AppDeviceSummary(
    int Id,
    string DeviceCode,
    string Name,
    string Address,
    string Status,
    int CameraCount,
    int RiskCount,
    int RecognizableWorkerCount,
    decimal? PpeComplianceRate,
    int HeavyEquipmentCount,
    string? ServerResourceInstanceName,
    decimal? Latitude,
    decimal? Longitude,
    DateTime? LastHeartbeatAtUtc);

public sealed record AppDeviceDailyStat(
    DateOnly Date,
    int PeopleCount,
    int BraceletCount,
    int MachineryVehicleCount,
    decimal? PpeComplianceRate,
    int RiskEventCount,
    int RiskPersonCount,
    string? TopRiskSubjectKey,
    int TopRiskSubjectRiskCount,
    DateTime? LastHeartbeatAtUtc,
    DateTime? LastEventAtUtc);

public sealed record AppRiskSubjectSummary(
    string SubjectKey,
    string? TrackingLabel,
    long? SubjectId,
    int RiskCount,
    string? CropImageUrl,
    string? PreviewImageUrl,
    string? RiskCategory,
    string? RiskSeverity);

public readonly record struct AppBoundDeviceRef(int Id, string DeviceCode);

public static class ApnsJwtTokenCache
{
    private static readonly object SyncRoot = new();
    private static string? _cacheKey;
    private static string? _token;
    private static DateTimeOffset _expiresAtUtc;

    public static string GetOrCreate(ApnsOptions options)
    {
        var now = DateTimeOffset.UtcNow;
        var cacheKey = $"{options.TeamId}|{options.KeyId}|{options.BundleId}|{options.PrivateKeyPath}";
        lock (SyncRoot)
        {
            if (_token is not null && _cacheKey == cacheKey && _expiresAtUtc > now.AddMinutes(5))
            {
                return _token;
            }

            _token = Create(options, now);
            _cacheKey = cacheKey;
            _expiresAtUtc = now.AddMinutes(45);
            return _token;
        }
    }

    private static string Create(ApnsOptions options, DateTimeOffset now)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = options.KeyId }));
        var claims = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = options.TeamId,
            iat = now.ToUnixTimeSeconds()
        }));
        var signingInput = $"{header}.{claims}";
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(options.PrivateKeyPath));
        var signature = ecdsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
