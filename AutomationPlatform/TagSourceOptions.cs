using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutomationPlatform;

public sealed class TagSourceOptions
{
    public const string SectionName = "TagSources";

    public int RequestTimeoutSeconds { get; set; } = 15;
    public List<TagSource> Sources { get; set; } = [];

    public static bool HasValidSources(TagSourceOptions options)
    {
        return options.Sources is not null &&
            options.Sources.All(source => source is not null &&
                !string.IsNullOrWhiteSpace(source.Id) &&
                Regex.IsMatch(source.Id, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\z") &&
                !string.IsNullOrWhiteSpace(source.Name) &&
                Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) &&
                string.IsNullOrEmpty(uri.Fragment)) &&
            options.Sources.Select(source => source.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                options.Sources.Count;
    }
}

public sealed class TagSource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed record TagSourceSummary(string Id, string Name);

public sealed record TagSourceSnapshot(string SourceId, string SourceName, DateTimeOffset FetchedAtUtc,
    IReadOnlyList<PlcTag> Items);

public sealed class PlcTag
{
    [JsonPropertyName("Device")]
    public required string Device { get; init; }

    [JsonPropertyName("Group")]
    public required string Group { get; init; }

    [JsonPropertyName("Table")]
    public required string Table { get; init; }

    [JsonPropertyName("Name")]
    public required string Name { get; init; }

    [JsonPropertyName("DataType")]
    public required string DataType { get; init; }

    [JsonPropertyName("Address")]
    public required string Address { get; init; }
}
