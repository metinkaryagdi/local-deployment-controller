using System.Text.Json;
using System.Text.Json.Serialization;
using DeployController.Models;
using DeployController.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

// A published install is started from a shortcut, a scheduled task or the SCM, none of
// which guarantee a working directory — anchor the content root (and therefore wwwroot
// and appsettings.json) to the folder the executable actually lives in. During `dotnet run`
// there is no wwwroot next to the assembly, so the normal project content root is kept.
var contentRoot = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
    ? AppContext.BaseDirectory
    : Directory.GetCurrentDirectory();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot
});

// No-op when launched from a console; when started by the SCM this switches the
// lifetime over to the service control manager and repoints the content root at
// the install folder (a service starts with cwd = C:\Windows\System32).
builder.Host.UseWindowsService(options => options.ServiceName = "LocalDeploymentController");

builder.Services.Configure<DeploymentOptions>(builder.Configuration.GetSection(DeploymentOptions.SectionName));

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<DeploymentQueue>();
builder.Services.AddSingleton<IDeploymentService, DeploymentService>();
builder.Services.AddHostedService<DeploymentWorker>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

var sseJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

var deploymentOptions = app.Services.GetRequiredService<IOptions<DeploymentOptions>>().Value;
Directory.CreateDirectory(deploymentOptions.BaseDirectory);
Directory.CreateDirectory(deploymentOptions.StateDirectory);
app.Logger.LogInformation("Deployment root: {BaseDirectory}", deploymentOptions.BaseDirectory);

// ---------------------------------------------------------------- meta

app.MapGet("/api/health", async (IDeploymentService service, IOptions<DeploymentOptions> options, CancellationToken ct) =>
    Results.Ok(new
    {
        status = "ok",
        baseDirectory = options.Value.BaseDirectory,
        publicHost = options.Value.PublicHost,
        dockerVersion = await service.DockerVersionAsync(ct),
        gitVersion = await service.GitVersionAsync(ct),
        machine = Environment.MachineName,
        serverTime = DateTimeOffset.Now
    }));

// ------------------------------------------------------------ deployments

app.MapGet("/api/deployments", async (IDeploymentService service, CancellationToken ct) =>
    Results.Ok(await service.ListAsync(ct)));

app.MapGet("/api/deployments/{projectName}", async (string projectName, IDeploymentService service, CancellationToken ct) =>
{
    if (!NameRules.IsValidProjectName(projectName))
    {
        return Results.BadRequest(new ApiError("Invalid project name."));
    }

    var info = await service.GetAsync(projectName, ct);
    return info is null
        ? Results.NotFound(new ApiError($"Project '{projectName}' was not found."))
        : Results.Ok(info);
});

app.MapPost("/api/deploy", (DeployRequest request, JobStore jobs, DeploymentQueue queue, HttpContext http) =>
{
    var projectName = request.ProjectName?.Trim().ToLowerInvariant() ?? string.Empty;

    if (!NameRules.IsValidProjectName(projectName))
    {
        return Results.BadRequest(new ApiError(
            "Invalid project name.",
            "Use a lowercase slug: letters, digits, '-', '_' or '.', starting and ending with a letter or digit."));
    }

    if (!NameRules.IsValidRepoUrl(request.RepoUrl))
    {
        return Results.BadRequest(new ApiError(
            "Invalid repository URL.",
            "Expected an http(s)/ssh git URL such as https://github.com/user/repo.git."));
    }

    var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim();
    if (!NameRules.IsValidBranch(branch))
    {
        return Results.BadRequest(new ApiError("Invalid branch name."));
    }

    if (request.HostPort is not null and (< 1 or > 65535))
    {
        return Results.BadRequest(new ApiError("Host port must be between 1 and 65535."));
    }

    var active = jobs.ActiveForProject(projectName);
    if (active is not null)
    {
        return Results.Conflict(new ApiError(
            $"A deployment for '{projectName}' is already running.",
            $"Job {active.Id} is still in progress."));
    }

    var normalized = request with
    {
        ProjectName = projectName,
        RepoUrl = request.RepoUrl.Trim(),
        Branch = branch
    };

    var job = jobs.Create(normalized);
    queue.Enqueue(job.Id);

    var streamUrl = $"/api/jobs/{job.Id}/stream-build-logs";
    http.Response.Headers.Location = streamUrl;

    return Results.Accepted(streamUrl, new DeployAccepted(job.Id, projectName, streamUrl));
});

app.MapGet("/api/deployments/{projectName}/logs", async (
    string projectName,
    int? lines,
    IDeploymentService service,
    CancellationToken ct) =>
{
    if (!NameRules.IsValidProjectName(projectName))
    {
        return Results.BadRequest(new ApiError("Invalid project name."));
    }

    var tail = Math.Clamp(lines ?? 100, 1, 5000);

    try
    {
        var logs = await service.GetContainerLogsAsync(projectName, tail, ct);
        return Results.Ok(new ContainerLogsResponse(projectName, tail, logs));
    }
    catch (Exception ex) when (ex is DeploymentException or ProcessLaunchException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/deployments/{projectName}/restart", async (
    string projectName,
    IDeploymentService service,
    JobStore jobs,
    CancellationToken ct) =>
{
    if (!NameRules.IsValidProjectName(projectName))
    {
        return Results.BadRequest(new ApiError("Invalid project name."));
    }

    if (jobs.ActiveForProject(projectName) is { } active)
    {
        return Results.Conflict(new ApiError($"Deployment {active.Id} is currently running for '{projectName}'."));
    }

    try
    {
        var result = await service.RestartAsync(projectName, ct);
        var output = (result.StandardOutput + result.StandardError).Trim();

        return result.Success
            ? Results.Ok(new { projectName, restarted = true, output })
            : Results.Problem(
                detail: string.IsNullOrWhiteSpace(output) ? result.FailureMessage : output,
                title: "Restart failed",
                statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception ex) when (ex is DeploymentException or ProcessLaunchException)
    {
        return Results.Problem(ex.Message, title: "Restart failed", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapDelete("/api/deployments/{projectName}", async (
    string projectName,
    IDeploymentService service,
    JobStore jobs,
    CancellationToken ct) =>
{
    if (!NameRules.IsValidProjectName(projectName))
    {
        return Results.BadRequest(new ApiError("Invalid project name."));
    }

    if (jobs.ActiveForProject(projectName) is { } active)
    {
        return Results.Conflict(new ApiError($"Deployment {active.Id} is currently running for '{projectName}'."));
    }

    try
    {
        var (containersRemoved, directoryRemoved, output) = await service.DeleteAsync(projectName, ct);

        return Results.Ok(new
        {
            projectName,
            containersRemoved,
            directoryRemoved,
            output = output.Trim()
        });
    }
    catch (Exception ex) when (ex is DeploymentException or ProcessLaunchException)
    {
        return Results.Problem(ex.Message, title: "Delete failed", statusCode: StatusCodes.Status502BadGateway);
    }
});

// ------------------------------------------------------------------- jobs

app.MapGet("/api/jobs", (int? count, JobStore jobs) =>
    Results.Ok(jobs.Recent(Math.Clamp(count ?? 20, 1, 200))));

app.MapGet("/api/jobs/{jobId}", (string jobId, JobStore jobs) =>
{
    var job = jobs.Find(jobId);
    return job is null
        ? Results.NotFound(new ApiError($"Job '{jobId}' was not found."))
        : Results.Ok(new { job = job.Snapshot(), lines = job.AllLines() });
});

app.MapPost("/api/jobs/{jobId}/cancel", (string jobId, JobStore jobs) =>
{
    var job = jobs.Find(jobId);
    if (job is null)
    {
        return Results.NotFound(new ApiError($"Job '{jobId}' was not found."));
    }

    if (job.IsFinished)
    {
        return Results.Conflict(new ApiError($"Job '{jobId}' has already finished ({job.Status})."));
    }

    job.Cancellation.Cancel();
    return Results.Ok(new { jobId, cancelling = true });
});

// -------------------------------------------------------- server-sent events

app.MapGet("/api/jobs/{jobId}/stream-build-logs", async (
    string jobId,
    JobStore jobs,
    HttpContext http,
    CancellationToken ct) =>
{
    var job = jobs.Find(jobId);
    if (job is null)
    {
        await Results.NotFound(new ApiError($"Job '{jobId}' was not found.")).ExecuteAsync(http);
        return;
    }

    await StreamJobLogsAsync(http, job, sseJson, ct);
});

app.MapGet("/api/deployments/{projectName}/stream-build-logs", async (
    string projectName,
    JobStore jobs,
    HttpContext http,
    CancellationToken ct) =>
{
    if (!NameRules.IsValidProjectName(projectName))
    {
        await Results.BadRequest(new ApiError("Invalid project name.")).ExecuteAsync(http);
        return;
    }

    var job = jobs.ActiveForProject(projectName) ?? jobs.LatestForProject(projectName);
    if (job is null)
    {
        await Results.NotFound(new ApiError($"No build has been started for '{projectName}' in this session."))
            .ExecuteAsync(http);
        return;
    }

    await StreamJobLogsAsync(http, job, sseJson, ct);
});

app.Run();

// Streams a job's log buffer and every subsequent line as SSE, then closes with an
// `end` event. Response buffering is disabled and a comment heartbeat keeps idle
// connections (long docker build steps) alive through proxies.
static async Task StreamJobLogsAsync(
    HttpContext http,
    DeploymentJob job,
    JsonSerializerOptions json,
    CancellationToken cancellationToken)
{
    var response = http.Response;
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache,no-store";
    response.Headers["X-Accel-Buffering"] = "no";
    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

    using var subscription = job.Subscribe();

    try
    {
        await WriteEventAsync(response, "status", JsonSerializer.Serialize(job.Snapshot(), json), cancellationToken);

        foreach (var line in subscription.Backlog)
        {
            await WriteEventAsync(response, "log", JsonSerializer.Serialize(line, json), cancellationToken);
        }

        Task<bool>? pending = null;

        while (true)
        {
            pending ??= subscription.Reader.WaitToReadAsync(cancellationToken).AsTask();

            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(TimeSpan.FromSeconds(15), heartbeat.Token);
            var winner = await Task.WhenAny(pending, delay);

            if (winner != pending)
            {
                await response.WriteAsync(": heartbeat\n\n", cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
                continue;
            }

            heartbeat.Cancel(); // stop the pending delay timer
            var hasData = await pending;
            pending = null;

            if (!hasData)
            {
                break;
            }

            while (subscription.Reader.TryRead(out var line))
            {
                await WriteEventAsync(response, "log", JsonSerializer.Serialize(line, json), cancellationToken);
            }
        }

        await WriteEventAsync(response, "status", JsonSerializer.Serialize(job.Snapshot(), json), cancellationToken);
        await WriteEventAsync(response, "end", JsonSerializer.Serialize(job.Snapshot(), json), cancellationToken);
    }
    catch (OperationCanceledException)
    {
        // Browser navigated away or closed the EventSource — nothing to do.
    }
}

static async Task WriteEventAsync(HttpResponse response, string eventName, string data, CancellationToken cancellationToken)
{
    // `data` is always JSON, so it can never contain a raw newline that would break framing.
    await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}
