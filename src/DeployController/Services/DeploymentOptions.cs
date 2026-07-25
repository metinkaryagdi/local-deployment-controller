namespace DeployController.Services;

public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    /// <summary>Root folder that holds one sub-directory per project.</summary>
    public string BaseDirectory { get; set; } = @"C:\Deployments";

    public string GitExecutable { get; set; } = "git";

    public string DockerExecutable { get; set; } = "docker";

    public int GitTimeoutSeconds { get; set; } = 900;

    public int DockerBuildTimeoutSeconds { get; set; } = 3600;

    public int DockerQuickTimeoutSeconds { get; set; } = 180;

    public int MaxJobHistory { get; set; } = 50;

    public int MaxLogLinesPerJob { get; set; } = 20_000;

    /// <summary>Host name/IP used when building the public URL of a deployment (defaults to the request host).</summary>
    public string? PublicHost { get; set; }

    public string StateDirectory => Path.Combine(BaseDirectory, ".state");

    public TimeSpan GitTimeout => TimeSpan.FromSeconds(Math.Max(30, GitTimeoutSeconds));

    public TimeSpan DockerBuildTimeout => TimeSpan.FromSeconds(Math.Max(60, DockerBuildTimeoutSeconds));

    public TimeSpan DockerQuickTimeout => TimeSpan.FromSeconds(Math.Max(15, DockerQuickTimeoutSeconds));
}
