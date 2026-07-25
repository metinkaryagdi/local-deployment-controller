using System.Collections.Concurrent;
using System.Threading.Channels;
using DeployController.Models;

namespace DeployController.Services;

/// <summary>
/// A single deployment run: its status, its full log buffer and its live subscribers.
/// Every mutation happens under one lock so a subscriber can atomically grab the
/// backlog and register for new lines without losing or duplicating output.
/// </summary>
public sealed class DeploymentJob
{
    private readonly object _gate = new();
    private readonly List<LogLine> _lines = new();
    private readonly List<Channel<LogLine>> _subscribers = new();
    private readonly int _maxLines;
    private long _seq;

    public DeploymentJob(DeployRequest request, int maxLines)
    {
        Request = request;
        _maxLines = Math.Max(500, maxLines);
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public DeployRequest Request { get; }
    public string ProjectName => Request.ProjectName;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public JobStatus Status { get; private set; } = JobStatus.Queued;
    public string? Step { get; private set; }
    public string? Error { get; private set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public bool IsFinished => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    public void MarkRunning()
    {
        lock (_gate)
        {
            Status = JobStatus.Running;
            StartedAt = DateTimeOffset.Now;
        }
    }

    public void SetStep(string step)
    {
        lock (_gate) { Step = step; }
        Append($"==> {step}", "system");
    }

    public void Append(string text, string stream = "stdout")
    {
        LogLine line;
        Channel<LogLine>[] subscribers;

        lock (_gate)
        {
            line = new LogLine(++_seq, DateTimeOffset.Now, stream, text);
            _lines.Add(line);

            if (_lines.Count > _maxLines)
            {
                _lines.RemoveRange(0, _lines.Count - _maxLines);
            }

            subscribers = _subscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryWrite(line);
        }
    }

    public void Complete(JobStatus status, string? error = null)
    {
        Channel<LogLine>[] subscribers;

        lock (_gate)
        {
            if (IsFinished) return;

            Status = status;
            Error = error;
            CompletedAt = DateTimeOffset.Now;
            Step = null;
            subscribers = _subscribers.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryComplete();
        }
    }

    public JobSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new JobSnapshot(Id, ProjectName, Status, Step, CreatedAt, StartedAt, CompletedAt, Error);
        }
    }

    public IReadOnlyList<LogLine> AllLines()
    {
        lock (_gate) { return _lines.ToArray(); }
    }

    /// <summary>
    /// Registers a live subscriber. The returned backlog plus everything read from the
    /// channel is the complete, in-order log. If the job has already finished, the
    /// channel is pre-completed so the caller drains the backlog and stops.
    /// </summary>
    public LogSubscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<LogLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        LogLine[] backlog;

        lock (_gate)
        {
            backlog = _lines.ToArray();

            if (IsFinished)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
        }

        return new LogSubscription(this, channel, backlog);
    }

    internal void Unsubscribe(Channel<LogLine> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }
}

public sealed class LogSubscription : IDisposable
{
    private readonly DeploymentJob _job;
    private readonly Channel<LogLine> _channel;
    private bool _disposed;

    internal LogSubscription(DeploymentJob job, Channel<LogLine> channel, IReadOnlyList<LogLine> backlog)
    {
        _job = job;
        _channel = channel;
        Backlog = backlog;
    }

    public IReadOnlyList<LogLine> Backlog { get; }
    public ChannelReader<LogLine> Reader => _channel.Reader;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job.Unsubscribe(_channel);
    }
}

/// <summary>In-memory registry of recent deployment jobs.</summary>
public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, DeploymentJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly DeploymentOptions _options;

    public JobStore(Microsoft.Extensions.Options.IOptions<DeploymentOptions> options) => _options = options.Value;

    public DeploymentJob Create(DeployRequest request)
    {
        var job = new DeploymentJob(request, _options.MaxLogLinesPerJob);
        _jobs[job.Id] = job;
        _order.Enqueue(job.Id);
        Trim();
        return job;
    }

    public DeploymentJob? Find(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public DeploymentJob? LatestForProject(string projectName) =>
        _jobs.Values
            .Where(j => string.Equals(j.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefault();

    public DeploymentJob? ActiveForProject(string projectName) =>
        _jobs.Values
            .Where(j => !j.IsFinished && string.Equals(j.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefault();

    public IReadOnlyList<JobSnapshot> Recent(int count) =>
        _jobs.Values
            .OrderByDescending(j => j.CreatedAt)
            .Take(count)
            .Select(j => j.Snapshot())
            .ToArray();

    private void Trim()
    {
        while (_jobs.Count > _options.MaxJobHistory && _order.TryDequeue(out var id))
        {
            if (_jobs.TryGetValue(id, out var job) && !job.IsFinished)
            {
                // Never evict a job that is still streaming; push it to the back.
                _order.Enqueue(id);
                return;
            }

            _jobs.TryRemove(id, out _);
        }
    }
}
