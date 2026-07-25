using System.Threading.Channels;

namespace DeployController.Services;

/// <summary>
/// Hands deployment jobs off to a background worker so the HTTP request that
/// triggered the build returns immediately and no request thread is ever blocked
/// on <c>git clone</c> / <c>docker build</c>.
/// </summary>
public sealed class DeploymentQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public void Enqueue(string jobId)
    {
        if (!_channel.Writer.TryWrite(jobId))
        {
            throw new InvalidOperationException("Deployment queue is no longer accepting work.");
        }
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Drains the queue one job at a time. Serial execution is deliberate: concurrent
/// <c>docker compose up --build</c> runs on a single Docker Desktop/WSL2 backend
/// fight over the build cache and the daemon, and interleaved logs are unreadable.
/// </summary>
public sealed class DeploymentWorker : BackgroundService
{
    private readonly DeploymentQueue _queue;
    private readonly JobStore _jobs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeploymentWorker> _logger;

    public DeploymentWorker(
        DeploymentQueue queue,
        JobStore jobs,
        IServiceScopeFactory scopeFactory,
        ILogger<DeploymentWorker> logger)
    {
        _queue = queue;
        _jobs = jobs;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Deployment worker started.");

        await foreach (var jobId in _queue.ReadAllAsync(stoppingToken))
        {
            var job = _jobs.Find(jobId);
            if (job is null)
            {
                _logger.LogWarning("Job {JobId} disappeared before it could run.", jobId);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IDeploymentService>();
                await service.ExecuteJobAsync(job, linked.Token);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                job.Append("Deployment cancelled by user.", "error");
                job.Complete(Models.JobStatus.Cancelled, "Cancelled by user.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Append("Deployment aborted: the controller is shutting down.", "error");
                job.Complete(Models.JobStatus.Cancelled, "Controller shutting down.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled failure while deploying {Project}.", job.ProjectName);
                job.Append($"FATAL: {ex.Message}", "error");
                job.Complete(Models.JobStatus.Failed, ex.Message);
            }
        }

        _logger.LogInformation("Deployment worker stopped.");
    }
}
