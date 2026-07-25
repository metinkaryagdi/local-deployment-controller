using System.Text;

namespace DeployController.Services;

public static class FileSystemHelpers
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes text with LF endings and no BOM — what Linux containers expect from a mounted .env.</summary>
    public static async Task WriteUnixTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.Length > 0 && !normalized.EndsWith('\n'))
        {
            normalized += "\n";
        }

        await File.WriteAllTextAsync(path, normalized, Utf8NoBom, cancellationToken);
    }

    /// <summary>Resolves a relative path inside <paramref name="root"/>, rejecting traversal outside it.</summary>
    public static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Path must not be empty.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Injected file path '{relativePath}' must be relative to the repository root.");
        }

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath));

        if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Injected file path '{relativePath}' escapes the project directory.");
        }

        return candidate;
    }

    public static bool IsGitRepository(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    public static bool IsEmptyDirectory(string directory) =>
        !Directory.EnumerateFileSystemEntries(directory).Any();

    /// <summary>Removes stale git lock files left behind by an interrupted operation.</summary>
    public static IReadOnlyList<string> RemoveGitLockFiles(string projectDirectory)
    {
        var gitDir = Path.Combine(projectDirectory, ".git");
        if (!Directory.Exists(gitDir))
        {
            return Array.Empty<string>();
        }

        var removed = new List<string>();

        foreach (var lockFile in Directory.EnumerateFiles(gitDir, "*.lock", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(lockFile, FileAttributes.Normal);
                File.Delete(lockFile);
                removed.Add(Path.GetRelativePath(projectDirectory, lockFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process still holds it; git will report the real problem.
            }
        }

        return removed;
    }

    /// <summary>
    /// Deletes a directory tree, clearing the read-only attributes that git sets on
    /// pack files (the usual cause of "access denied" on Windows) and retrying briefly
    /// while antivirus/Docker still hold handles.
    /// </summary>
    public static async Task<bool> ForceDeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken);
            }
        }

        return false;
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) return;

        directory.Attributes = FileAttributes.Directory;

        foreach (var info in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            try
            {
                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    info.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>Parses KEY=VALUE lines out of a .env payload (comments and blanks ignored).</summary>
    public static Dictionary<string, string> ParseEnv(string? content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
        {
            return result;
        }

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');

            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }
}
