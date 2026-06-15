namespace SentribeeConsole.Web.Application.Services;

public static class ProjectTimeZone
{
    public const string DefaultId = "Pacific/Auckland";

    private static readonly IReadOnlyDictionary<string, string> AlternateIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pacific/Auckland"] = "New Zealand Standard Time",
            ["New Zealand Standard Time"] = "Pacific/Auckland",
            ["Australia/Sydney"] = "AUS Eastern Standard Time",
            ["AUS Eastern Standard Time"] = "Australia/Sydney",
            ["Australia/Melbourne"] = "AUS Eastern Standard Time",
            ["Australia/Brisbane"] = "E. Australia Standard Time",
            ["E. Australia Standard Time"] = "Australia/Brisbane"
        };

    public static IReadOnlyList<ProjectTimeZoneOption> Options { get; } =
    [
        new(DefaultId, "New Zealand - Auckland"),
        new("Australia/Sydney", "Australia - Sydney"),
        new("Australia/Melbourne", "Australia - Melbourne"),
        new("Australia/Brisbane", "Australia - Brisbane"),
        new("UTC", "UTC")
    ];

    public static string Normalize(string? timeZoneId)
    {
        return Options.Any(option => string.Equals(option.Id, timeZoneId, StringComparison.OrdinalIgnoreCase))
            ? timeZoneId!.Trim()
            : DefaultId;
    }

    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        var normalized = Normalize(timeZoneId);
        if (TryFind(normalized, out var timeZone))
        {
            return timeZone;
        }

        if (AlternateIds.TryGetValue(normalized, out var alternateId) && TryFind(alternateId, out timeZone))
        {
            return timeZone;
        }

        return TimeZoneInfo.Utc;
    }

    public static DateTime ConvertUtc(DateTime utc, string? timeZoneId)
    {
        var utcValue = utc.Kind == DateTimeKind.Utc
            ? utc
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utcValue, Resolve(timeZoneId));
    }

    public static DateTime ConvertLocalToUtc(DateTime local, string? timeZoneId)
    {
        var localValue = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localValue, Resolve(timeZoneId));
    }

    public static string Format(DateTime utc, string? timeZoneId, string format = "yyyy-MM-dd HH:mm")
    {
        return ConvertUtc(utc, timeZoneId).ToString(format);
    }

    public static string Format(DateTime? utc, string? timeZoneId, string format = "yyyy-MM-dd HH:mm", string fallback = "-")
    {
        return utc.HasValue ? Format(utc.Value, timeZoneId, format) : fallback;
    }

    private static bool TryFind(string id, out TimeZoneInfo timeZone)
    {
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }
}

public sealed record ProjectTimeZoneOption(string Id, string Label);
