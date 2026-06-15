using System.Security.Cryptography;
using System.Text;

namespace SentribeeConsole.Web.Pages.Crm;

public static class CrmChatResumeLink
{
    public static string CreateToken(string secret, string publicChatPath, long conversationId)
    {
        var normalizedPath = NormalizePublicChatPath(publicChatPath);
        var payload = $"{normalizedPath}:{conversationId}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool IsValidToken(string secret, string publicChatPath, long conversationId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var expected = CreateToken(secret, publicChatPath, conversationId);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(token.Trim().ToLowerInvariant()));
    }

    public static string NormalizePublicChatPath(string publicChatPath)
    {
        return publicChatPath.Trim().Trim('/').ToLowerInvariant();
    }
}
