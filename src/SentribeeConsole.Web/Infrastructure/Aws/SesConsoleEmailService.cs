using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SentribeeConsole.Web.Application.Contracts;

namespace SentribeeConsole.Web.Infrastructure.Aws;

public sealed class SesConsoleEmailService(
    HttpClient httpClient,
    IConfiguration configuration) : IConsoleEmailService
{
    private const string Provider = "AmazonSes";

    public Task<ConsoleEmailResult> SendProjectInvitationAsync(
        string email,
        string projectName,
        string invitationUrl,
        CancellationToken cancellationToken)
    {
        var subject = $"You're invited to {projectName} on SentriBee";
        var html = BuildInvitationHtml(projectName, invitationUrl);
        var text = BuildInvitationText(projectName, invitationUrl);
        return SendEmailAsync(email, subject, html, text, cancellationToken);
    }

    public Task<ConsoleEmailResult> SendVerificationCodeAsync(
        string email,
        string code,
        CancellationToken cancellationToken)
    {
        const string subject = "Your Sentribee verification code";
        var html = BuildVerificationEmailHtml(code);
        var text = BuildVerificationEmailText(code);
        return SendEmailAsync(email, subject, html, text, cancellationToken);
    }

    public Task<ConsoleEmailResult> SendEmployeeWelcomeAsync(
        string email,
        string companyName,
        string loginUrl,
        string temporaryPassword,
        CancellationToken cancellationToken)
    {
        var subject = $"Your {companyName} Sentribee OA account";
        var html = BuildEmployeeWelcomeHtml(companyName, loginUrl, temporaryPassword);
        var text = BuildEmployeeWelcomeText(companyName, loginUrl, temporaryPassword);
        return SendEmailAsync(email, subject, html, text, cancellationToken);
    }

    private async Task<ConsoleEmailResult> SendEmailAsync(
        string email,
        string subject,
        string html,
        string text,
        CancellationToken cancellationToken)
    {
        var fromAddress = configuration["EmailAuth:FromAddress"];
        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            return new ConsoleEmailResult(false, Provider, null, "EmailAuth from address is not configured.");
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

        if (string.IsNullOrWhiteSpace(accessKeyId) ||
            string.IsNullOrWhiteSpace(secretAccessKey) ||
            string.IsNullOrWhiteSpace(region))
        {
            return new ConsoleEmailResult(false, Provider, null, "Amazon SES access key, secret, and region must be configured.");
        }

        var payload = new
        {
            FromEmailAddress = fromAddress,
            Destination = new { ToAddresses = new[] { email } },
            Content = new
            {
                Simple = new
                {
                    Subject = new { Data = subject, Charset = "UTF-8" },
                    Body = new
                    {
                        Html = new { Data = html, Charset = "UTF-8" },
                        Text = new { Data = text, Charset = "UTF-8" }
                    }
                }
            }
        };
        var payloadJson = JsonSerializer.Serialize(payload);
        var endpoint = new Uri($"https://email.{region}.amazonaws.com/v2/email/outbound-emails");
        using var request = BuildAwsSignedJsonRequest(
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
                return new ConsoleEmailResult(true, Provider, ExtractSesMessageId(responseText), null);
            }

            return new ConsoleEmailResult(
                false,
                Provider,
                null,
                $"Amazon SES returned HTTP {(int)response.StatusCode}: {TrimDiagnostic(responseText)}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new ConsoleEmailResult(false, Provider, null, exception.Message);
        }
    }

    private static HttpRequestMessage BuildAwsSignedJsonRequest(
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
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var credentialScope = $"{dateStamp}/{region}/{serviceName}/aws4_request";
        var signedHeaders = "content-type;host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = string.Create(
            CultureInfo.InvariantCulture,
            $"content-type:{contentType}\nhost:{uri.Host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n");
        var canonicalRequest = string.Join('\n',
            method.Method,
            uri.AbsolutePath,
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            payloadHash);
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());
        var signature = Convert.ToHexString(
            Sign(GetAwsSigningKey(secretAccessKey, region, serviceName, dateStamp), stringToSign))
            .ToLowerInvariant();
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

    private static byte[] GetAwsSigningKey(string secretAccessKey, string region, string serviceName, string dateStamp)
    {
        var dateKey = Sign(Encoding.UTF8.GetBytes($"AWS4{secretAccessKey}"), dateStamp);
        var dateRegionKey = Sign(dateKey, region);
        var dateRegionServiceKey = Sign(dateRegionKey, serviceName);
        return Sign(dateRegionServiceKey, "aws4_request");
    }

    private static byte[] Sign(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string? ExtractSesMessageId(string responseText)
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

    private static string BuildInvitationHtml(string projectName, string invitationUrl)
    {
        var encodedProjectName = WebUtility.HtmlEncode(projectName);
        var encodedUrl = WebUtility.HtmlEncode(invitationUrl);
        return $$"""
            <!doctype html>
            <html>
            <body style="margin:0;background:#f6f7fb;font-family:Arial,Helvetica,sans-serif;color:#172033;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f6f7fb;padding:32px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border:1px solid #e5e7eb;border-radius:14px;overflow:hidden;">
                      <tr>
                        <td style="padding:30px 34px 16px;">
                          <div style="font-size:13px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#6d28d9;">SentriBee</div>
                          <h1 style="margin:14px 0 10px;font-size:24px;line-height:1.25;color:#111827;">Set up your console account</h1>
                          <p style="margin:0;color:#4b5563;font-size:15px;line-height:1.6;">An administrator invited you to join <strong>{{encodedProjectName}}</strong> in SentriBee Console.</p>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:14px 34px 8px;">
                          <a href="{{encodedUrl}}" style="display:inline-block;background:#6d28d9;color:#ffffff;text-decoration:none;font-weight:700;border-radius:10px;padding:13px 20px;">Set password</a>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:18px 34px 30px;">
                          <p style="margin:0 0 10px;color:#6b7280;font-size:13px;line-height:1.6;">This invitation expires in 7 days. If the button does not work, open this link:</p>
                          <p style="margin:0;color:#4f46e5;font-size:13px;line-height:1.6;word-break:break-all;">{{encodedUrl}}</p>
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

    private static string BuildInvitationText(string projectName, string invitationUrl)
    {
        return $"You have been invited to join {projectName} in SentriBee Console. Set your password here: {invitationUrl}. This invitation expires in 7 days.";
    }

    private static string BuildVerificationEmailHtml(string code)
    {
        var encodedCode = WebUtility.HtmlEncode(code);
        return $$"""
            <!doctype html>
            <html>
            <body style="margin:0;background:#f6f7fb;font-family:Arial,Helvetica,sans-serif;color:#172033;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f6f7fb;padding:32px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border:1px solid #e5e7eb;border-radius:14px;overflow:hidden;">
                      <tr>
                        <td style="padding:30px 34px 16px;">
                          <div style="font-size:13px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#6d28d9;">SentriBee</div>
                          <h1 style="margin:14px 0 10px;font-size:24px;line-height:1.25;color:#111827;">Verify your email</h1>
                          <p style="margin:0;color:#4b5563;font-size:15px;line-height:1.6;">Use this code to finish creating your SentriBee OA workspace.</p>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:10px 34px 12px;">
                          <div style="display:inline-block;background:#f3f4f6;border:1px solid #e5e7eb;border-radius:12px;padding:16px 22px;font-size:30px;letter-spacing:8px;font-weight:700;color:#111827;">{{encodedCode}}</div>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:16px 34px 30px;">
                          <p style="margin:0;color:#6b7280;font-size:13px;line-height:1.6;">This code expires in 10 minutes. If you did not request it, you can ignore this email.</p>
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

    private static string BuildVerificationEmailText(string code)
    {
        return $"Your Sentribee verification code is {code}. It expires in 10 minutes.";
    }

    private static string BuildEmployeeWelcomeHtml(string companyName, string loginUrl, string temporaryPassword)
    {
        var encodedCompanyName = WebUtility.HtmlEncode(companyName);
        var encodedUrl = WebUtility.HtmlEncode(loginUrl);
        var encodedPassword = WebUtility.HtmlEncode(temporaryPassword);
        return $$"""
            <!doctype html>
            <html>
            <body style="margin:0;background:#f6f7fb;font-family:Arial,Helvetica,sans-serif;color:#172033;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#f6f7fb;padding:32px 12px;">
                <tr>
                  <td align="center">
                    <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;background:#ffffff;border:1px solid #e5e7eb;border-radius:14px;overflow:hidden;">
                      <tr>
                        <td style="padding:30px 34px 16px;">
                          <div style="font-size:13px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#6d28d9;">Sentribee OA</div>
                          <h1 style="margin:14px 0 10px;font-size:24px;line-height:1.25;color:#111827;">Your staff account is ready</h1>
                          <p style="margin:0;color:#4b5563;font-size:15px;line-height:1.6;">{{encodedCompanyName}} created an OA account for you. Sign in with this email and the temporary password below.</p>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:10px 34px 12px;">
                          <div style="background:#f3f4f6;border:1px solid #e5e7eb;border-radius:12px;padding:16px 18px;color:#111827;">
                            <div style="font-size:12px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#6b7280;">Temporary password</div>
                            <div style="font-size:22px;line-height:1.35;font-weight:700;word-break:break-all;margin-top:6px;">{{encodedPassword}}</div>
                          </div>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:14px 34px 8px;">
                          <a href="{{encodedUrl}}" style="display:inline-block;background:#6d28d9;color:#ffffff;text-decoration:none;font-weight:700;border-radius:10px;padding:13px 20px;">Sign in to Sentribee OA</a>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:18px 34px 30px;">
                          <p style="margin:0 0 10px;color:#6b7280;font-size:13px;line-height:1.6;">On first login you will change the password, then complete your staff profile.</p>
                          <p style="margin:0;color:#4f46e5;font-size:13px;line-height:1.6;word-break:break-all;">{{encodedUrl}}</p>
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

    private static string BuildEmployeeWelcomeText(string companyName, string loginUrl, string temporaryPassword)
    {
        return $"{companyName} created a Sentribee OA staff account for you. Login: {loginUrl}. Temporary password: {temporaryPassword}. On first login, change the password and complete your staff profile.";
    }

    private static string TrimDiagnostic(string? value, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
