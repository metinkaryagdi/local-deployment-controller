using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeployController.Models;

namespace DeployController.Services;

/// <summary>Shape of one NDJSON record emitted by <c>docker ps --format json</c>.</summary>
public sealed class DockerPsRecord
{
    [JsonPropertyName("ID")] public string? Id { get; set; }
    [JsonPropertyName("Names")] public string? Names { get; set; }
    [JsonPropertyName("Image")] public string? Image { get; set; }
    [JsonPropertyName("State")] public string? State { get; set; }
    [JsonPropertyName("Status")] public string? Status { get; set; }
    [JsonPropertyName("Ports")] public string? Ports { get; set; }
    [JsonPropertyName("Labels")] public string? Labels { get; set; }
    [JsonPropertyName("CreatedAt")] public string? CreatedAt { get; set; }
}

public static class DockerOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex ComposeProjectLabel =
        new(@"com\.docker\.compose\.project=([^,]+)", RegexOptions.Compiled);

    // 0.0.0.0:8080->80/tcp, [::]:8080->80/tcp, 8080/tcp (unpublished)
    private static readonly Regex PortMappingRegex =
        new(@"(?:(?<ip>\[[^\]]+\]|[0-9.]+):)?(?<host>\d+)->(?<container>\d+)/(?<proto>\w+)", RegexOptions.Compiled);

    /// <summary>Parses the newline-delimited JSON produced by <c>docker ps --format json</c>.</summary>
    public static IReadOnlyList<DockerPsRecord> ParsePsOutput(string stdout)
    {
        var records = new List<DockerPsRecord>();
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return records;
        }

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;

            try
            {
                var record = JsonSerializer.Deserialize<DockerPsRecord>(trimmed, JsonOptions);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // A malformed line should never take the whole listing down.
            }
        }

        return records;
    }

    public static string? ComposeProjectOf(DockerPsRecord record)
    {
        if (string.IsNullOrEmpty(record.Labels)) return null;

        var match = ComposeProjectLabel.Match(record.Labels);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static IReadOnlyList<PortMapping> ParsePorts(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports))
        {
            return Array.Empty<PortMapping>();
        }

        var seen = new HashSet<(int, int, string)>();
        var mappings = new List<PortMapping>();

        foreach (Match match in PortMappingRegex.Matches(ports))
        {
            if (!int.TryParse(match.Groups["host"].Value, out var hostPort)) continue;
            if (!int.TryParse(match.Groups["container"].Value, out var containerPort)) continue;

            var protocol = match.Groups["proto"].Value;
            if (!seen.Add((hostPort, containerPort, protocol))) continue;

            var ip = match.Groups["ip"].Success ? match.Groups["ip"].Value : null;
            mappings.Add(new PortMapping(ip, hostPort, containerPort, protocol));
        }

        return mappings;
    }

    public static ContainerInfo ToContainerInfo(DockerPsRecord record) => new(
        record.Id ?? string.Empty,
        record.Names ?? string.Empty,
        record.Image ?? string.Empty,
        record.State ?? "unknown",
        record.Status ?? string.Empty,
        ParsePorts(record.Ports));
}
