namespace Jarvis.Sharp;

using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Jarvis.Sharp.State;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Security & State
        builder.Services.AddSingleton(new AuthorizationGuard());
        builder.Services.AddSingleton(new RateLimiter(maxRequests: 10, windowSeconds: 60.0));
        builder.Services.AddSingleton(new CandidateStateStore(
            stateFilePath: Environment.GetEnvironmentVariable("CANDIDATE_STATE_PATH")));

        // HTTP Client & Services
        builder.Services.AddHttpClient<IPlexService, PlexService>();
        builder.Services.AddHttpClient<IMediaSyncDispatcher, MediaSyncDispatcher>();
        builder.Services.AddHttpClient<IGrafanaLokiService, GrafanaLokiService>();

        // Kubernetes Client with graceful fallback
        builder.Services.AddSingleton<IKubernetes>(_ =>
        {
            try
            {
                var config = KubernetesClientConfiguration.IsInCluster()
                    ? KubernetesClientConfiguration.InClusterConfig()
                    : KubernetesClientConfiguration.BuildConfigFromConfigFile();
                return new Kubernetes(config);
            }
            catch
            {
                return new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" });
            }
        });
        builder.Services.AddSingleton<IKubernetesDiagnostics, KubernetesDiagnostics>();

        // Background Worker Daemon
        builder.Services.AddHostedService<Worker>();

        var host = builder.Build();
        host.Run();
    }
}
