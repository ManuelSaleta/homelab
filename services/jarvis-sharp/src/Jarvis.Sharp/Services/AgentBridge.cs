namespace Jarvis.Sharp.Services;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

public class AgentBridge : IAgentBridge
{
    private readonly ILogger<AgentBridge> _logger;
    private readonly IKubernetesDiagnostics _k8sDiagnostics;
    private readonly string _agyBinary;
    private readonly TimeSpan _timeout;

    public AgentBridge(
        ILogger<AgentBridge> logger,
        IKubernetesDiagnostics k8sDiagnostics)
    {
        _logger = logger;
        _k8sDiagnostics = k8sDiagnostics;
        _agyBinary = Environment.GetEnvironmentVariable("AGY_BINARY") ?? "agy";
        _timeout = TimeSpan.FromSeconds(90);
    }

    public async Task<string> AskAsync(
        string prompt,
        IDictionary<string, string>? context = null,
        CancellationToken cancellationToken = default)
    {
        var cleanedPrompt = prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanedPrompt))
        {
            return "Please provide a non-empty prompt for Antigravity.";
        }

        var fullPrompt = new StringBuilder();
        if (context is { Count: > 0 })
        {
            fullPrompt.AppendLine("Homelab ChatOps Context:");
            foreach (var kvp in context.OrderBy(k => k.Key))
            {
                fullPrompt.AppendLine($"- {kvp.Key}: {kvp.Value}");
            }
            fullPrompt.AppendLine();
            fullPrompt.AppendLine("User Request:");
        }
        fullPrompt.Append(cleanedPrompt);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _agyBinary,
                Arguments = $"--prompt \"{fullPrompt.ToString().Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return "Failed to start `agy` process.";
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                return stdout;
            }

            return !string.IsNullOrWhiteSpace(stdout)
                ? stdout
                : !string.IsNullOrWhiteSpace(stderr)
                    ? stderr
                    : $"`agy` exited with status {process.ExitCode}";
        }
        catch (OperationCanceledException)
        {
            return $"Antigravity CLI timed out after {(int)_timeout.TotalSeconds}s.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invoke Antigravity CLI binary '{Binary}'", _agyBinary);
            return $"Antigravity CLI (`{_agyBinary}`) invocation notice: {ex.Message}. " +
                   "Ensure `agy` is installed or running in a host-connected bridge environment.";
        }
    }

    public async Task<string> TriageAlertAsync(
        string alertText,
        string @namespace,
        string deployment,
        string pod,
        CancellationToken cancellationToken = default)
    {
        string diagSummary;
        try
        {
            var diag = await _k8sDiagnostics.GetPodDiagnosticsAsync(@namespace, pod, 40, cancellationToken);
            var logsExcerpt = diag.Logs.Length > 1200 ? diag.Logs[^1200..] : diag.Logs;
            diagSummary = $"Pod `{@namespace}/{pod}` status: {diag.StatusPhase}\nLogs excerpt:\n```{logsExcerpt}```";
        }
        catch (Exception ex)
        {
            diagSummary = $"Could not collect live pod diagnostics for `{@namespace}/{pod}`: {ex.Message}";
        }

        var prompt = $"Analyze the following Grafana homelab alert and Kubernetes pod diagnostics. " +
                     $"Provide a concise root-cause summary and recommended remediation.\n\n" +
                     $"Alert:\n{alertText}\n\n{diagSummary}";

        var context = new Dictionary<string, string>
        {
            ["namespace"] = @namespace,
            ["deployment"] = deployment,
            ["pod"] = pod,
        };

        return await AskAsync(prompt, context, cancellationToken);
    }
}
