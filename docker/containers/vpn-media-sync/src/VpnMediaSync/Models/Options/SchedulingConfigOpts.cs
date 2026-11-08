namespace VpnMediaSync.Models.Options;

public class SchedulingConfigOpts
{
    public const string SectionName = "Schedule";

    /// <summary>
    /// Periodic execution interval in minutes for the background sync daemon loop.
    /// </summary>
    public int IntervalMinutes { get; set; }

    /// <summary>
    /// Standard 5-field cron schedule expression for orchestrating periodic synchronization runs.
    /// </summary>
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>
    /// File system path to the exclusive lock file preventing concurrent orchestrator executions.
    /// </summary>
    public string LockFile { get; set; } = string.Empty;

    /// <summary>
    /// Port listened on by <c>SyncSchedulerWorker</c> when running as a hosted service in daemon mode for on-demand sync triggers and health probes.
    /// </summary>
    public int WebhookPort { get; set; }

    /// <summary>
    /// Optional shared secret token required in X-Webhook-Token or Bearer Authorization header to trigger sync runs.
    /// </summary>
    public string? WebhookSecretToken { get; set; }

    /// <summary>
    /// Local file system path to the persistent JSON state database storing acquired release history.
    /// </summary>
    public string StateFilePath { get; set; } = string.Empty;
}
