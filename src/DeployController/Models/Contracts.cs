using System.Text.RegularExpressions;

namespace DeployController.Models;

/// <summary>Payload posted to <c>POST /api/deploy</c>.</summary>
public sealed record DeployRequest
{
    public string ProjectName { get; init; } = string.Empty;
    public string RepoUrl { get; init; } = string.Empty;
    public string? Branch { get; init; }
    public int? HostPort { get; init; }

    /// <summary>Raw content written verbatim to <c>.env</c> in the repository root.</summary>
    public string? EnvContent { get; init; }

    /// <summary>Optional extra files injected into the repository before the build.</summary>
    public IReadOnlyList<InjectedFile>? Files { get; init; }

    public string EffectiveBranch => string.IsNullOrWhiteSpace(Branch) ? "main" : Branch.Trim();
}

/// <summary>An additional configuration file injected into the cloned repository.</summary>
public sealed record InjectedFile(string Path, string Content);

public sealed record DeployAccepted(string JobId, string ProjectName, string StreamUrl);

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>A single captured line of CLI output.</summary>
/// <param name="Stream">One of <c>stdout</c>, <c>stderr</c>, <c>system</c>, <c>error</c>.</param>
public sealed record LogLine(long Seq, DateTimeOffset Timestamp, string Stream, string Text);

public sealed record JobSnapshot(
    string Id,
    string ProjectName,
    JobStatus Status,
    string? Step,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

/// <summary>Metadata persisted per project so the dashboard survives restarts.</summary>
public sealed class ProjectState
{
    public string ProjectName { get; set; } = string.Empty;
    public string RepoUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public int? HostPort { get; set; }
    public string? ComposeFile { get; set; }
    public string? Commit { get; set; }
    public string? CommitSubject { get; set; }
    public DateTimeOffset? LastDeployedAt { get; set; }
    public string? LastResult { get; set; }
}

public sealed record PortMapping(string? HostIp, int HostPort, int ContainerPort, string Protocol);

public sealed record ContainerInfo(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    IReadOnlyList<PortMapping> Ports);

public sealed record DeploymentInfo(
    string ProjectName,
    string RepoUrl,
    string Branch,
    string Status,
    int? HostPort,
    IReadOnlyList<int> ExposedPorts,
    IReadOnlyList<ContainerInfo> Containers,
    string? Commit,
    string? CommitSubject,
    DateTimeOffset? LastDeployedAt,
    DateTimeOffset LastUpdatedAt,
    string Directory,
    string? ComposeFile,
    bool HasComposeFile,
    string? ActiveJobId);

public sealed record ContainerLogsResponse(string ProjectName, int Lines, string Logs);

public sealed record ApiError(string Error, string? Detail = null);

public static class NameRules
{
    // Docker Compose project names: lowercase alphanumerics, dashes, underscores and dots.
    private static readonly Regex ProjectNameRegex =
        new("^[a-z0-9][a-z0-9._-]{0,61}[a-z0-9]$|^[a-z0-9]$", RegexOptions.Compiled);

    public static bool IsValidProjectName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ProjectNameRegex.IsMatch(name);

    /// <summary>Rejects URLs that git would interpret as command-line options.</summary>
    public static bool IsValidRepoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (trimmed.StartsWith('-')) return false;
        if (trimmed.Contains('\n') || trimmed.Contains('\r')) return false;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" or "ssh" or "git" or "file";
        }

        // scp-like syntax: git@github.com:user/repo.git
        return Regex.IsMatch(trimmed, @"^[\w.\-]+@[\w.\-]+:.+$");
    }

    public static bool IsValidBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return false;
        var b = branch.Trim();
        if (b.StartsWith('-') || b.Contains("..") || b.Contains(' ')) return false;
        return Regex.IsMatch(b, @"^[\w.\-/]{1,200}$");
    }
}
