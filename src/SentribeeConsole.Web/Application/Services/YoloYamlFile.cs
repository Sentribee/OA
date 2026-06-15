using System.Text;
using System.Text.RegularExpressions;
using SentribeeConsole.Web.Domain.Entities;

namespace SentribeeConsole.Web.Application.Services;

public static partial class YoloYamlFile
{
    public static IReadOnlyList<YoloModelClass> DefaultModelClasses()
    {
        return
        [
            new YoloModelClass { Index = 0, Name = "helmet" },
            new YoloModelClass { Index = 1, Name = "gloves" },
            new YoloModelClass { Index = 2, Name = "vest" },
            new YoloModelClass { Index = 3, Name = "boots" },
            new YoloModelClass { Index = 4, Name = "goggles" },
            new YoloModelClass { Index = 5, Name = "none" },
            new YoloModelClass { Index = 6, Name = "Person" },
            new YoloModelClass { Index = 7, Name = "no_helmet" },
            new YoloModelClass { Index = 8, Name = "no_goggle" },
            new YoloModelClass { Index = 9, Name = "no_gloves" },
            new YoloModelClass { Index = 10, Name = "no_boots" },
            new YoloModelClass { Index = 11, Name = "no_vest" },
            new YoloModelClass { Index = 12, Name = "machinery_vehicle" },
            new YoloModelClass { Index = 13, Name = "excavator" },
            new YoloModelClass { Index = 14, Name = "crane" },
            new YoloModelClass { Index = 15, Name = "forklift" },
            new YoloModelClass { Index = 16, Name = "truck" },
            new YoloModelClass { Index = 17, Name = "scaffold" },
            new YoloModelClass { Index = 18, Name = "ladder" },
            new YoloModelClass { Index = 19, Name = "rebar" },
            new YoloModelClass { Index = 20, Name = "uncapped_rebar" },
            new YoloModelClass { Index = 21, Name = "fire_smoke" }
        ];
    }

    public static IReadOnlyList<YoloModelClass> DefaultPanoramaClasses()
    {
        return DefaultModelClasses();
    }

    public static IReadOnlyList<YoloModelClass> DefaultPpeClasses()
    {
        return DefaultModelClasses();
    }

    public static string RewriteClassList(string? content, IReadOnlyList<YoloModelClass> classes)
    {
        return RewriteNames(content, classes);
    }

    public static async Task<(string? Content, IReadOnlyList<YoloModelClass> Classes)> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (null, []);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return (content, ParseClasses(content));
    }

    public static async Task<IReadOnlyList<YoloModelClass>> AddClassAsync(
        string path,
        string className,
        CancellationToken cancellationToken)
    {
        var (content, _) = await ReadAsync(path, cancellationToken);
        var (updatedContent, updatedClasses) = AddClassToContent(content, className);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, updatedContent, cancellationToken);
        return updatedClasses;
    }

    public static (string Content, IReadOnlyList<YoloModelClass> Classes) AddClassToContent(
        string? content,
        string className)
    {
        var normalized = NormalizeClassName(className);
        if (normalized.Length < 2)
        {
            throw new InvalidOperationException("Enter a meaningful class name.");
        }

        var classes = ParseClasses(content);
        if (classes.Any(item => string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("This class already exists in data.yaml.");
        }

        var updatedClasses = classes
            .Append(new YoloModelClass { Index = classes.Count == 0 ? 0 : classes.Max(item => item.Index) + 1, Name = normalized })
            .OrderBy(item => item.Index)
            .ToArray();
        var updatedContent = RewriteNames(content, updatedClasses);
        return (updatedContent, updatedClasses);
    }

    public static IReadOnlyList<YoloModelClass> ParseClasses(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return [];
        }

        var classes = new List<YoloModelClass>();
        var lines = yaml.Split(["\r\n", "\n"], StringSplitOptions.None);
        var inNames = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.StartsWith("names:", StringComparison.OrdinalIgnoreCase))
            {
                inNames = true;
                var inline = trimmed["names:".Length..].Trim();
                if (inline.StartsWith('[') && inline.EndsWith(']'))
                {
                    var values = inline.Trim('[', ']')
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    for (var index = 0; index < values.Length; index++)
                    {
                        classes.Add(new YoloModelClass { Index = index, Name = CleanYamlScalar(values[index]) });
                    }

                    break;
                }

                if (inline.StartsWith('{') && inline.EndsWith('}'))
                {
                    var values = inline.Trim('{', '}')
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    foreach (var value in values)
                    {
                        var inlineMatch = NameLineRegex().Match(value.Trim());
                        if (inlineMatch.Success && int.TryParse(CleanYamlScalar(inlineMatch.Groups["index"].Value), out var inlineIndex))
                        {
                            classes.Add(new YoloModelClass
                            {
                                Index = inlineIndex,
                                Name = CleanYamlScalar(inlineMatch.Groups["name"].Value)
                            });
                        }
                    }

                    break;
                }

                continue;
            }

            if (!inNames)
            {
                continue;
            }

            if (!rawLine.StartsWith(' ') && !rawLine.StartsWith('\t'))
            {
                break;
            }

            var listMatch = ListNameLineRegex().Match(trimmed);
            if (listMatch.Success)
            {
                classes.Add(new YoloModelClass
                {
                    Index = classes.Count,
                    Name = CleanYamlScalar(listMatch.Groups["name"].Value)
                });
                continue;
            }

            var match = NameLineRegex().Match(trimmed);
            if (match.Success && int.TryParse(match.Groups["index"].Value, out var classIndex))
            {
                classes.Add(new YoloModelClass
                {
                    Index = classIndex,
                    Name = CleanYamlScalar(match.Groups["name"].Value)
                });
            }
        }

        return classes
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Index)
            .ToArray();
    }

    private static string RewriteNames(string? content, IReadOnlyList<YoloModelClass> classes)
    {
        var namesBlock = BuildNamesBlock(classes);
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"path: .{Environment.NewLine}{namesBlock}";
        }

        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        var namesIndex = lines.FindIndex(line => line.TrimStart().StartsWith("names:", StringComparison.OrdinalIgnoreCase));
        if (namesIndex < 0)
        {
            if (!content.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                lines.Add(string.Empty);
            }

            lines.AddRange(namesBlock.Split(Environment.NewLine));
            return string.Join(Environment.NewLine, lines);
        }

        var endIndex = namesIndex + 1;
        while (endIndex < lines.Count && (lines[endIndex].StartsWith(' ') || lines[endIndex].StartsWith('\t')))
        {
            endIndex++;
        }

        lines.RemoveRange(namesIndex, endIndex - namesIndex);
        lines.InsertRange(namesIndex, namesBlock.Split(Environment.NewLine));
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildNamesBlock(IReadOnlyList<YoloModelClass> classes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("names:");
        foreach (var item in classes.OrderBy(item => item.Index))
        {
            builder.Append("  ");
            builder.Append(item.Index);
            builder.Append(": ");
            builder.AppendLine(QuoteYamlScalar(item.Name));
        }

        return builder.ToString().TrimEnd();
    }

    private static string NormalizeClassName(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", "_");
    }

    private static string CleanYamlScalar(string value)
    {
        return value.Trim().Trim('"', '\'');
    }

    private static string QuoteYamlScalar(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    [GeneratedRegex(@"^['""]?(?<index>\d+)['""]?\s*:\s*(?<name>.+)$")]
    private static partial Regex NameLineRegex();

    [GeneratedRegex(@"^-\s*(?<name>.+)$")]
    private static partial Regex ListNameLineRegex();
}
