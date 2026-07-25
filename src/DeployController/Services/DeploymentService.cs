using System.Text;
using System.Text.Json;
using DeployController.Models;
using Microsoft.Extensions.Options;

namespace DeployController.Services;

public interface IDeploymentService
{
    Task<IReadOnlyList<DeploymentInfo>> ListAsync(CancellationToken cancellationToken);

    Task<DeploymentInfo?> GetAsync(string projectName, CancellationToken cancellationToken);

    Task ExecuteJobAsync(DeploymentJob job, CancellationToken cancellationToken);

    Task<string> GetContainerLogsAsync(string projectName, int lines, CancellationToken cancellationToken);

    Task<CommandResult> RestartAsync(string projectName, CancellationToken cancellationToken);

    Task<(bool ContainersRemoved, bool DirectoryRemoved, string Output)> DeleteAsync(
        string projectName, CancellationToken cancellationToken);

    Task<string> DockerVersionAsync(CancellationToken cancellationToken);

    Task<string> GitVersionAsync(CancellationToken cancellationToken);
}

/// <summary>Raised for an expected, user-facing deployment failure (bad input, failed CLI step).</summary>
public sealed class DeploymentException : Exception
{
    public DeploymentException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class DeploymentService : IDeploymentService
{
    private static readonly string[] ComposeFileNames =
    {
        "docker-compose.yml",
        "docker-compose.yaml",
        "compose.yml",
        "compose.yaml"
    };

    private const string GeneratedComposeFile = "docker-compose.deploycontroller.yml";

    private static readonly JsonSerializerOptions StateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IProcessRunner _runner;
    private readonly DeploymentOptions _options;
    private readonly JobStore _jobs;
    private readonly ILogger<DeploymentService> _logger;

    public DeploymentService(
        IProcessRunner runner,
        IOptions<DeploymentOptions> options,
        JobStore jobs,
        ILogger<DeploymentService> logger)
    {
        _runner = runner;
        _options = options.Value;
        _jobs = jobs;
        _logger = logger;
    }

    public string ProjectDirectory(string projectName) => Path.Combine(_options.BaseDirectory, projectName);

    // ---------------------------------------------------------------- deploy

    public async Task ExecuteJobAsync(DeploymentJob job, CancellationToken cancellationToken)
    {
        var request = job.Request;
        var projectDirectory = ProjectDirectory(request.ProjectName);

        job.MarkRunning();
        job.Append($"Local Deployment Controller — project '{request.ProjectName}'", "system");
        job.Append($"Repository : {request.RepoUrl}", "system");
        job.Append($"Branch     : {request.EffectiveBranch}", "system");
        job.Append($"Directory  : {projectDirectory}", "system");

        var state = ReadState(request.ProjectName) ?? new ProjectState { ProjectName = request.ProjectName };
        state.RepoUrl = request.RepoUrl;
        state.Branch = request.EffectiveBranch;
        state.HostPort = request.HostPort;

        try
        {
            Directory.CreateDirectory(_options.BaseDirectory);
            Directory.CreateDirectory(_options.StateDirectory);

            await SyncRepositoryAsync(job, request, projectDirectory, cancellationToken);

            state.Commit = await ReadCommitAsync(projectDirectory, "%h", cancellationToken);
            state.CommitSubject = await ReadCommitAsync(projectDirectory, "%s", cancellationToken);
            if (state.Commit is not null)
            {
                job.Append($"HEAD is now at {state.Commit} {state.CommitSubject}", "system");
            }

            await InjectConfigurationAsync(job, request, projectDirectory, cancellationToken);

            var composeFile = await ResolveOrGenerateComposeFileAsync(job, request, projectDirectory, cancellationToken);
            state.ComposeFile = composeFile;

            await ComposeUpAsync(job, request.ProjectName, projectDirectory, composeFile, cancellationToken);

            await ReportContainersAsync(job, request.ProjectName, cancellationToken);

            state.LastDeployedAt = DateTimeOffset.Now;
            state.LastResult = "Succeeded";
            WriteState(state);

            job.Append("Deployment finished successfully.", "system");
            job.Complete(JobStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            state.LastResult = "Cancelled";
            WriteState(state);
            throw;
        }
        catch (Exception ex) when (ex is DeploymentException or ProcessLaunchException)
        {
            _logger.LogWarning(ex, "Deployment of {Project} failed.", request.ProjectName);
            job.Append(ex.Message, "error");
            state.LastResult = "Failed";
            WriteState(state);
            job.Complete(JobStatus.Failed, ex.Message);
        }
    }

    private async Task SyncRepositoryAsync(
        DeploymentJob job,
        DeployRequest request,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var branch = request.EffectiveBranch;
        var exists = Directory.Exists(projectDirectory);
        var isRepository = exists && FileSystemHelpers.IsGitRepository(projectDirectory);

        if (exists && !isRepository && !FileSystemHelpers.IsEmptyDirectory(projectDirectory))
        {
            throw new DeploymentException(
                $"'{projectDirectory}' already exists but is not a git repository. Delete it from the dashboard (or by hand) and deploy again.");
        }

        if (!isRepository)
        {
            job.SetStep($"Cloning {request.RepoUrl} ({branch})");

            if (exists && FileSystemHelpers.IsEmptyDirectory(projectDirectory))
            {
                Directory.Delete(projectDirectory); // let git create it so clone does not complain
            }

            await RunGitAsync(
                job,
                workingDirectory: _options.BaseDirectory,
                cancellationToken,
                "-c", "core.longpaths=true",
                "clone", "--progress", "--recurse-submodules",
                "-b", branch,
                request.RepoUrl,
                projectDirectory);

            return;
        }

        job.SetStep($"Updating existing checkout to origin/{branch}");

        var removedLocks = FileSystemHelpers.RemoveGitLockFiles(projectDirectory);
        foreach (var lockFile in removedLocks)
        {
            job.Append($"Removed stale lock file: {lockFile}", "system");
        }

        // Point the checkout at whatever repository the user submitted this time.
        var remote = await _runner.RunAsync(
            GitRequest(projectDirectory, "remote", "get-url", "origin"),
            cancellationToken: cancellationToken);

        if (remote.Success)
        {
            var current = remote.StandardOutput.Trim();
            if (!string.Equals(current, request.RepoUrl, StringComparison.OrdinalIgnoreCase))
            {
                job.Append($"Remote 'origin' changed: {current} -> {request.RepoUrl}", "system");
                await RunGitAsync(job, projectDirectory, cancellationToken, "remote", "set-url", "origin", request.RepoUrl);
            }
        }
        else
        {
            await RunGitAsync(job, projectDirectory, cancellationToken, "remote", "add", "origin", request.RepoUrl);
        }

        await RunGitAsync(job, projectDirectory, cancellationToken, "fetch", "--all", "--prune", "--progress");
        await RunGitAsync(job, projectDirectory, cancellationToken, "checkout", "-B", branch, $"origin/{branch}");
        await RunGitAsync(job, projectDirectory, cancellationToken, "reset", "--hard", $"origin/{branch}");

        // Drop untracked leftovers, but keep ignored artefacts (node_modules, volumes...).
        await RunGitAsync(job, projectDirectory, cancellationToken, "clean", "-fd");

        await _runner.RunAsync(
            GitRequest(projectDirectory, "submodule", "update", "--init", "--recursive"),
            (line, isError) => job.Append(line, isError ? "stderr" : "stdout"),
            cancellationToken);
    }

    private async Task InjectConfigurationAsync(
        DeploymentJob job,
        DeployRequest request,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var wroteSomething = false;

        if (!string.IsNullOrWhiteSpace(request.EnvContent))
        {
            job.SetStep("Injecting configuration files");

            var content = request.EnvContent!;
            var parsed = FileSystemHelpers.ParseEnv(content);

            // Make the requested host port available to compose files that use ${HOST_PORT}.
            if (request.HostPort is > 0 && !parsed.ContainsKey("HOST_PORT"))
            {
                content = content.TrimEnd() + $"\nHOST_PORT={request.HostPort}\n";
            }

            var envPath = Path.Combine(projectDirectory, ".env");
            await FileSystemHelpers.WriteUnixTextAsync(envPath, content, cancellationToken);
            job.Append($"Wrote .env ({parsed.Count} variable(s)) to {envPath}", "system");
            wroteSomething = true;
        }

        foreach (var file in request.Files ?? Array.Empty<InjectedFile>())
        {
            if (!wroteSomething)
            {
                job.SetStep("Injecting configuration files");
                wroteSomething = true;
            }

            string target;
            try
            {
                target = FileSystemHelpers.ResolveInside(projectDirectory, file.Path);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                throw new DeploymentException(ex.Message, ex);
            }

            await FileSystemHelpers.WriteUnixTextAsync(target, file.Content ?? string.Empty, cancellationToken);
            job.Append($"Wrote {file.Path}", "system");
        }

        if (!wroteSomething)
        {
            job.Append("No configuration injected (empty .env).", "system");
        }
    }

    private async Task<string> ResolveOrGenerateComposeFileAsync(
        DeploymentJob job,
        DeployRequest request,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var existing = FindComposeFile(projectDirectory);
        if (existing is not null)
        {
            job.Append($"Using compose file: {existing}", "system");
            return existing;
        }

        var dockerfile = Path.Combine(projectDirectory, "Dockerfile");
        if (!File.Exists(dockerfile))
        {
            throw new DeploymentException(
                "The repository root contains neither a docker-compose file (docker-compose.yml / compose.yml) nor a Dockerfile. " +
                "The deployment contract requires one of them.");
        }

        job.SetStep("Generating a compose file for the repository Dockerfile");

        var env = FileSystemHelpers.ParseEnv(request.EnvContent);
        var containerPort = ResolveContainerPort(env, request.HostPort);
        var hasEnvFile = File.Exists(Path.Combine(projectDirectory, ".env"));

        var yaml = new StringBuilder()
            .AppendLine("# Generated by the Local Deployment Controller because the repository")
            .AppendLine("# ships a Dockerfile but no compose file. Regenerated on every deploy.")
            .AppendLine("services:")
            .AppendLine("  app:")
            .AppendLine("    build:")
            .AppendLine("      context: .")
            .AppendLine("      dockerfile: Dockerfile")
            .AppendLine($"    image: {request.ProjectName}-app:latest")
            .AppendLine($"    container_name: {request.ProjectName}-app")
            .AppendLine("    restart: unless-stopped");

        if (hasEnvFile)
        {
            yaml.AppendLine("    env_file:").AppendLine("      - .env");
        }

        if (request.HostPort is > 0)
        {
            yaml.AppendLine("    ports:").AppendLine($"      - \"{request.HostPort}:{containerPort}\"");
        }

        var generatedPath = Path.Combine(projectDirectory, GeneratedComposeFile);
        await FileSystemHelpers.WriteUnixTextAsync(generatedPath, yaml.ToString(), cancellationToken);

        job.Append($"Generated {GeneratedComposeFile} (host {request.HostPort} -> container {containerPort}).", "system");
        return GeneratedComposeFile;
    }

    private static int ResolveContainerPort(IReadOnlyDictionary<string, string> env, int? hostPort)
    {
        foreach (var key in new[] { "PORT", "APP_PORT", "SERVER_PORT", "CONTAINER_PORT" })
        {
            if (env.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed is > 0 and < 65536)
            {
                return parsed;
            }
        }

        return hostPort is > 0 ? hostPort.Value : 80;
    }

    private async Task ComposeUpAsync(
        DeploymentJob job,
        string projectName,
        string projectDirectory,
        string composeFile,
        CancellationToken cancellationToken)
    {
        job.SetStep("docker compose up -d --build --remove-orphans");

        var result = await _runner.RunAsync(
            ComposeRequest(projectName, projectDirectory, composeFile, _options.DockerBuildTimeout,
                "up", "-d", "--build", "--remove-orphans"),
            (line, isError) => job.Append(line, isError ? "stderr" : "stdout"),
            cancellationToken);

        if (!result.Success)
        {
            throw new DeploymentException(
                $"Docker build/up failed. {result.FailureMessage} " +
                "Check the log above for the failing step.");
        }
    }

    private async Task ReportContainersAsync(DeploymentJob job, string projectName, CancellationToken cancellationToken)
    {
        job.SetStep("Verifying containers");

        var containers = await ListContainersAsync(cancellationToken);
        var mine = containers.TryGetValue(projectName, out var list) ? list : Array.Empty<ContainerInfo>();

        if (mine.Count == 0)
        {
            job.Append("Warning: docker reports no containers for this project.", "stderr");
            return;
        }

        foreach (var container in mine)
        {
            var ports = container.Ports.Count == 0
                ? "no published ports"
                : string.Join(", ", container.Ports.Select(p => $"{p.HostPort}->{p.ContainerPort}/{p.Protocol}"));

            job.Append($"{container.Name}: {container.State} ({container.Status}) — {ports}", "system");
        }
    }

    // ------------------------------------------------------------- inventory

    public async Task<IReadOnlyList<DeploymentInfo>> ListAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.BaseDirectory);

        var containersByProject = await ListContainersAsync(cancellationToken);
        var results = new List<DeploymentInfo>();

        foreach (var directory in Directory.EnumerateDirectories(_options.BaseDirectory))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.')) continue; // .state and friends

            results.Add(BuildInfo(name, directory, containersByProject));
        }

        // Projects whose containers exist but whose folder was removed by hand.
        foreach (var (project, _) in containersByProject)
        {
            if (results.Any(r => string.Equals(r.ProjectName, project, StringComparison.OrdinalIgnoreCase))) continue;
            if (ReadState(project) is null) continue;

            results.Add(BuildInfo(project, ProjectDirectory(project), containersByProject));
        }

        return results.OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DeploymentInfo?> GetAsync(string projectName, CancellationToken cancellationToken)
    {
        var directory = ProjectDirectory(projectName);
        var state = ReadState(projectName);

        if (!Directory.Exists(directory) && state is null)
        {
            return null;
        }

        var containers = await ListContainersAsync(cancellationToken);
        return BuildInfo(projectName, directory, containers);
    }

    private DeploymentInfo BuildInfo(
        string projectName,
        string directory,
        IReadOnlyDictionary<string, IReadOnlyList<ContainerInfo>> containersByProject)
    {
        var state = ReadState(projectName);
        var containers = containersByProject.TryGetValue(projectName, out var list)
            ? list
            : Array.Empty<ContainerInfo>();

        var activeJob = _jobs.ActiveForProject(projectName);

        var running = containers.Count(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase));
        var status = activeJob is not null
            ? "Deploying"
            : containers.Count == 0
                ? (Directory.Exists(directory) ? "Stopped" : "Missing")
                : running == containers.Count
                    ? "Running"
                    : running > 0
                        ? "Partial"
                        : "Stopped";

        var exposedPorts = containers
            .SelectMany(c => c.Ports)
            .Select(p => p.HostPort)
            .Distinct()
            .OrderBy(p => p)
            .ToArray();

        var composeFile = state?.ComposeFile ?? (Directory.Exists(directory) ? FindComposeFile(directory) : null);

        var lastUpdated = Directory.Exists(directory)
            ? new DateTimeOffset(Directory.GetLastWriteTime(directory))
            : state?.LastDeployedAt ?? DateTimeOffset.Now;

        return new DeploymentInfo(
            ProjectName: projectName,
            RepoUrl: state?.RepoUrl ?? string.Empty,
            Branch: state?.Branch ?? "main",
            Status: status,
            HostPort: state?.HostPort,
            ExposedPorts: exposedPorts,
            Containers: containers,
            Commit: state?.Commit,
            CommitSubject: state?.CommitSubject,
            LastDeployedAt: state?.LastDeployedAt,
            LastUpdatedAt: lastUpdated,
            Directory: directory,
            ComposeFile: composeFile,
            HasComposeFile: composeFile is not null && File.Exists(Path.Combine(directory, composeFile)),
            ActiveJobId: activeJob?.Id);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ContainerInfo>>> ListContainersAsync(
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, IReadOnlyList<ContainerInfo>>(StringComparer.OrdinalIgnoreCase);

        CommandResult result;
        try
        {
            result = await _runner.RunAsync(
                DockerRequest(null, _options.DockerQuickTimeout, "ps", "-a", "--no-trunc", "--format", "json"),
                cancellationToken: cancellationToken);
        }
        catch (ProcessLaunchException ex)
        {
            _logger.LogWarning(ex, "Docker CLI is not available.");
            return map;
        }

        if (!result.Success)
        {
            _logger.LogWarning("docker ps failed: {Error}", result.StandardError.Trim());
            return map;
        }

        foreach (var group in DockerOutput.ParsePsOutput(result.StandardOutput)
                     .Select(record => (Project: DockerOutput.ComposeProjectOf(record), Record: record))
                     .Where(x => !string.IsNullOrEmpty(x.Project))
                     .GroupBy(x => x.Project!, StringComparer.OrdinalIgnoreCase))
        {
            map[group.Key] = group.Select(x => DockerOutput.ToContainerInfo(x.Record)).ToArray();
        }

        return map;
    }

    // ------------------------------------------------------------ operations

    public async Task<string> GetContainerLogsAsync(string projectName, int lines, CancellationToken cancellationToken)
    {
        var tail = Math.Clamp(lines, 1, 5000);
        var directory = ProjectDirectory(projectName);
        var composeFile = ReadState(projectName)?.ComposeFile ?? (Directory.Exists(directory) ? FindComposeFile(directory) : null);

        if (Directory.Exists(directory) && composeFile is not null && File.Exists(Path.Combine(directory, composeFile)))
        {
            var result = await _runner.RunAsync(
                ComposeRequest(projectName, directory, composeFile, _options.DockerQuickTimeout,
                    "logs", "--no-color", $"--tail={tail}"),
                cancellationToken: cancellationToken);

            var text = result.StandardOutput + result.StandardError;
            if (result.Success || text.Trim().Length > 0)
            {
                return text;
            }
        }

        // Fallback: the checkout is gone but the containers are still around.
        var containers = await ListContainersAsync(cancellationToken);
        if (!containers.TryGetValue(projectName, out var list) || list.Count == 0)
        {
            return $"No compose file and no containers found for project '{projectName}'.";
        }

        var builder = new StringBuilder();
        foreach (var container in list)
        {
            builder.AppendLine($"===== {container.Name} =====");
            var result = await _runner.RunAsync(
                DockerRequest(null, _options.DockerQuickTimeout, "logs", "--tail", tail.ToString(), container.Id),
                cancellationToken: cancellationToken);

            builder.Append(result.StandardOutput).Append(result.StandardError).AppendLine();
        }

        return builder.ToString();
    }

    public async Task<CommandResult> RestartAsync(string projectName, CancellationToken cancellationToken)
    {
        var directory = ProjectDirectory(projectName);
        var composeFile = ReadState(projectName)?.ComposeFile ?? (Directory.Exists(directory) ? FindComposeFile(directory) : null);

        if (Directory.Exists(directory) && composeFile is not null && File.Exists(Path.Combine(directory, composeFile)))
        {
            return await _runner.RunAsync(
                ComposeRequest(projectName, directory, composeFile, _options.DockerBuildTimeout, "restart"),
                cancellationToken: cancellationToken);
        }

        var containers = await ListContainersAsync(cancellationToken);
        if (!containers.TryGetValue(projectName, out var list) || list.Count == 0)
        {
            throw new DeploymentException($"Nothing to restart: project '{projectName}' has no compose file and no containers.");
        }

        var args = new List<string> { "restart" };
        args.AddRange(list.Select(c => c.Id));

        return await _runner.RunAsync(
            DockerRequest(null, _options.DockerBuildTimeout, args.ToArray()),
            cancellationToken: cancellationToken);
    }

    public async Task<(bool ContainersRemoved, bool DirectoryRemoved, string Output)> DeleteAsync(
        string projectName, CancellationToken cancellationToken)
    {
        var directory = ProjectDirectory(projectName);
        var composeFile = ReadState(projectName)?.ComposeFile ?? (Directory.Exists(directory) ? FindComposeFile(directory) : null);
        var output = new StringBuilder();
        var containersRemoved = false;

        if (Directory.Exists(directory) && composeFile is not null && File.Exists(Path.Combine(directory, composeFile)))
        {
            var down = await _runner.RunAsync(
                ComposeRequest(projectName, directory, composeFile, _options.DockerBuildTimeout,
                    "down", "-v", "--remove-orphans"),
                cancellationToken: cancellationToken);

            output.Append(down.StandardOutput).Append(down.StandardError);
            containersRemoved = down.Success;
        }

        // Whatever compose could not reach, remove by label.
        var containers = await ListContainersAsync(cancellationToken);
        if (containers.TryGetValue(projectName, out var leftovers) && leftovers.Count > 0)
        {
            var args = new List<string> { "rm", "-f", "-v" };
            args.AddRange(leftovers.Select(c => c.Id));

            var rm = await _runner.RunAsync(
                DockerRequest(null, _options.DockerQuickTimeout, args.ToArray()),
                cancellationToken: cancellationToken);

            output.Append(rm.StandardOutput).Append(rm.StandardError);
            containersRemoved = containersRemoved || rm.Success;
        }

        var directoryRemoved = await FileSystemHelpers.ForceDeleteDirectoryAsync(directory, cancellationToken);
        if (!directoryRemoved)
        {
            output.AppendLine($"Could not delete '{directory}' — a file is still locked by another process.");
        }

        var statePath = StatePath(projectName);
        if (File.Exists(statePath))
        {
            try { File.Delete(statePath); } catch (IOException) { /* ignored */ }
        }

        return (containersRemoved, directoryRemoved, output.ToString());
    }

    public async Task<string> DockerVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(
                DockerRequest(null, _options.DockerQuickTimeout, "version", "--format", "{{.Server.Version}}"),
                cancellationToken: cancellationToken);

            return result.Success
                ? result.StandardOutput.Trim()
                : $"unavailable ({result.StandardError.Trim()})";
        }
        catch (ProcessLaunchException ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    public async Task<string> GitVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(
                new ProcessRequest
                {
                    FileName = _options.GitExecutable,
                    Arguments = new[] { "--version" },
                    Timeout = _options.DockerQuickTimeout
                },
                cancellationToken: cancellationToken);

            return result.Success
                ? result.StandardOutput.Trim().Replace("git version ", string.Empty)
                : $"unavailable ({result.StandardError.Trim()})";
        }
        catch (ProcessLaunchException ex)
        {
            return $"unavailable ({ex.Message})";
        }
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<CommandResult> RunGitAsync(
        DeploymentJob job,
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var result = await _runner.RunAsync(
            GitRequest(workingDirectory, arguments),
            (line, isError) => job.Append(line, isError ? "stderr" : "stdout"),
            cancellationToken);

        if (!result.Success)
        {
            throw new DeploymentException($"Git step failed. {result.FailureMessage}");
        }

        return result;
    }

    private ProcessRequest GitRequest(string workingDirectory, params string[] arguments) => new()
    {
        FileName = _options.GitExecutable,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        Timeout = _options.GitTimeout,
        Environment = new Dictionary<string, string?>
        {
            // Never block on an interactive credential prompt for a private repo.
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_ASKPASS"] = "echo",
            ["LC_ALL"] = "C"
        }
    };

    private ProcessRequest DockerRequest(string? workingDirectory, TimeSpan timeout, params string[] arguments) => new()
    {
        FileName = _options.DockerExecutable,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        Timeout = timeout,
        Environment = new Dictionary<string, string?>
        {
            // Plain progress keeps BuildKit output readable in a non-TTY log stream.
            ["BUILDKIT_PROGRESS"] = "plain",
            ["DOCKER_CLI_HINTS"] = "false"
        }
    };

    private ProcessRequest ComposeRequest(
        string projectName,
        string projectDirectory,
        string composeFile,
        TimeSpan timeout,
        params string[] arguments)
    {
        var args = new List<string> { "compose", "--ansi", "never", "-p", projectName, "-f", composeFile };
        args.AddRange(arguments);
        return DockerRequest(projectDirectory, timeout, args.ToArray());
    }

    private static string? FindComposeFile(string projectDirectory)
    {
        foreach (var candidate in ComposeFileNames)
        {
            if (File.Exists(Path.Combine(projectDirectory, candidate)))
            {
                return candidate;
            }
        }

        return File.Exists(Path.Combine(projectDirectory, GeneratedComposeFile)) ? GeneratedComposeFile : null;
    }

    private async Task<string?> ReadCommitAsync(string projectDirectory, string format, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            GitRequest(projectDirectory, "log", "-1", $"--pretty=format:{format}"),
            cancellationToken: cancellationToken);

        var value = result.StandardOutput.Trim();
        return result.Success && value.Length > 0 ? value : null;
    }

    private string StatePath(string projectName) => Path.Combine(_options.StateDirectory, $"{projectName}.json");

    private ProjectState? ReadState(string projectName)
    {
        var path = StatePath(projectName);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(path), StateJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Could not read state for {Project}.", projectName);
            return null;
        }
    }

    private void WriteState(ProjectState state)
    {
        try
        {
            Directory.CreateDirectory(_options.StateDirectory);
            File.WriteAllText(StatePath(state.ProjectName), JsonSerializer.Serialize(state, StateJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist state for {Project}.", state.ProjectName);
        }
    }
}
