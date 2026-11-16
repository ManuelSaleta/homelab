namespace Jarvis.Sharp.Services;

using System.Text.Json;
using Jarvis.Sharp.Security;
using k8s;
using k8s.Models;

public record PodDiagnosticsResult(
    string Namespace,
    string Pod,
    bool Success,
    string StatusPhase,
    string Logs
);

public record RestartDeploymentResult(
    string Namespace,
    string Deployment,
    bool Restarted,
    string Message
);

public interface IKubernetesDiagnostics
{
    Task<PodDiagnosticsResult> GetPodDiagnosticsAsync(
        string ns,
        string pod,
        int tailLines = 80,
        CancellationToken cancellationToken = default);

    Task<RestartDeploymentResult> RestartDeploymentAsync(
        string ns,
        string deployment,
        CancellationToken cancellationToken = default);
}

public class KubernetesDiagnostics : IKubernetesDiagnostics
{
    private readonly IKubernetes _k8sClient;

    public KubernetesDiagnostics(IKubernetes k8sClient)
    {
        _k8sClient = k8sClient;
    }

    public async Task<PodDiagnosticsResult> GetPodDiagnosticsAsync(
        string ns,
        string pod,
        int tailLines = 80,
        CancellationToken cancellationToken = default)
    {
        var cleanNs = InputSanitizer.ValidateNamespace(ns);
        var cleanPod = InputSanitizer.ValidateK8sIdentifier(pod, "pod");
        var safeTail = Math.Clamp(tailLines, 1, 500);

        try
        {
            var podObj = await _k8sClient.CoreV1.ReadNamespacedPodAsync(cleanPod, cleanNs, cancellationToken: cancellationToken);
            var phase = podObj.Status?.Phase ?? "Unknown";

            string logs = string.Empty;
            try
            {
                using var logStream = await _k8sClient.CoreV1.ReadNamespacedPodLogAsync(
                    cleanPod,
                    cleanNs,
                    tailLines: safeTail,
                    cancellationToken: cancellationToken);

                using var reader = new StreamReader(logStream);
                logs = await reader.ReadToEndAsync(cancellationToken);
            }
            catch (Exception logEx)
            {
                logs = $"[Unable to retrieve pod logs: {logEx.Message}]";
            }

            return new PodDiagnosticsResult(cleanNs, cleanPod, true, phase, logs.Trim());
        }
        catch (Exception ex)
        {
            return new PodDiagnosticsResult(cleanNs, cleanPod, false, "Error", ex.Message);
        }
    }

    public async Task<RestartDeploymentResult> RestartDeploymentAsync(
        string ns,
        string deployment,
        CancellationToken cancellationToken = default)
    {
        var cleanNs = InputSanitizer.ValidateNamespace(ns);
        var cleanDeploy = InputSanitizer.ValidateK8sIdentifier(deployment, "deployment");

        try
        {
            var patchJson = JsonSerializer.Serialize(new
            {
                spec = new
                {
                    template = new
                    {
                        metadata = new
                        {
                            annotations = new Dictionary<string, string>
                            {
                                ["kubectl.kubernetes.io/restartedAt"] = DateTimeOffset.UtcNow.ToString("o")
                            }
                        }
                    }
                }
            });

            var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);
            await _k8sClient.AppsV1.PatchNamespacedDeploymentAsync(patch, cleanDeploy, cleanNs, cancellationToken: cancellationToken);

            return new RestartDeploymentResult(cleanNs, cleanDeploy, true, $"Successfully patched rollout restart on deployment/{cleanDeploy}");
        }
        catch (Exception ex)
        {
            return new RestartDeploymentResult(cleanNs, cleanDeploy, false, $"Failed to rollout restart deployment: {ex.Message}");
        }
    }
}
