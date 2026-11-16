namespace Jarvis.Sharp;

using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Jarvis.Sharp.Slack;
using Jarvis.Sharp.State;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;

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
        builder.Services.AddHttpClient();
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
        builder.Services.AddSingleton<IAgentBridge, AgentBridge>();

        // SlackNet Handlers & Socket Mode Configuration
        var botToken = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")?.Trim();
        var appToken = Environment.GetEnvironmentVariable("SLACK_APP_TOKEN")?.Trim();

        builder.Services.AddSlackNet(c =>
        {
            if (!string.IsNullOrWhiteSpace(botToken))
            {
                c.UseApiToken(botToken);
            }
            if (!string.IsNullOrWhiteSpace(appToken))
            {
                c.UseAppLevelToken(appToken);
            }

            // Slash Commands
            c.RegisterSlashCommandHandler<DownloadCommandHandler>("/download");
            c.RegisterSlashCommandHandler<AgyCommandHandler>("/agy");

            // Interactive Block Actions
            c.RegisterBlockActionHandler<ButtonAction, RemediationActionHandler>("remediate_restart_deployment");
            c.RegisterBlockActionHandler<ButtonAction, RemediationActionHandler>("remediate_view_logs");
            c.RegisterBlockActionHandler<ButtonAction, RemediationActionHandler>("remediate_dismiss");
            c.RegisterBlockActionHandler<ButtonAction, CandidateActionHandler>();

            // Event Subscriptions
            c.RegisterEventHandler<MessageEvent, AlertMessageEventHandler>();
            c.RegisterEventHandler<AppMention, AppMentionEventHandler>();
        });

        // Background Worker Daemon
        builder.Services.AddHostedService<Worker>();

        var host = builder.Build();
        host.Run();
    }
}
