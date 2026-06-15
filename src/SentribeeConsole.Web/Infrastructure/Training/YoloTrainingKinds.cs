namespace SentribeeConsole.Web.Infrastructure.Training;

public static class YoloTrainingKinds
{
    public const string Panorama = "Panorama";

    public const string PersonSlicePpe = "PersonSlicePpe";

    public static bool IsSupported(string modelKind)
    {
        return string.Equals(modelKind, Panorama, StringComparison.OrdinalIgnoreCase)
            || string.Equals(modelKind, PersonSlicePpe, StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string modelKind)
    {
        if (string.Equals(modelKind, PersonSlicePpe, StringComparison.OrdinalIgnoreCase))
        {
            return PersonSlicePpe;
        }

        return Panorama;
    }
}
