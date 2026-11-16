namespace Jarvis.Sharp.Slack;

using System.Text.RegularExpressions;
using Jarvis.Sharp.Services;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

public class AlertMessageEventHandler : IEventHandler<MessageEvent>
{
    private static readonly Regex AlertNamespaceRegex = new(@"namespace\s*[=:]\s*([a-z0-9-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AlertDeploymentRegex = new(@"deployment\s*[=:]\s*([a-z0-9.-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AlertPodRegex = new(@"pod\s*[=:]\s*([a-z0-9.-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PodSuffixRegex = new(@"-[a-z0-9]{5,10}-[a-z0-9]{5}$", RegexOptions.Compiled);

    private readonly ILogger<AlertMessageEventHandler> _logger;
    private readonly IAgentBridge _agentBridge;
    private readonly ISlackApiClient _slackClient;
    private readonly string _alertsChannelId;

    public AlertMessageEventHandler(
        ILogger<AlertMessageEventHandler> logger,
        IAgentBridge agentBridge,
        ISlackApiClient slackClient)
    {
        _logger = logger;
        _agentBridge = agentBridge;
        _slackClient = slackClient;
        _alertsChannelId = (Environment.GetEnvironmentVariable("SLACK_ALERTS_CHANNEL") ?? string.Empty).Trim();
    }

    public static (string Namespace, string Deployment, string Pod) ExtractAlertTargets(string alertText)
    {
        var text = alertText ?? string.Empty;
        var nsMatch = AlertNamespaceRegex.Match(text);
        var deployMatch = AlertDeploymentRegex.Match(text);
        var podMatch = AlertPodRegex.Match(text);

        var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "media";
        var pod = podMatch.Success ? podMatch.Groups[1].Value : "vpn-media-sync-0";
        var deployment = deployMatch.Success
            ? deployMatch.Groups[1].Value
            : PodSuffixRegex.Replace(pod, string.Empty);

        return (ns, deployment, pod);
    }

    public async Task Handle(MessageEvent slackEvent)
    {
        if (string.IsNullOrWhiteSpace(_alertsChannelId) || slackEvent.Channel != _alertsChannelId)
        {
            return;
        }

        // Only intercept root alert notifications, not thread replies
        if (!string.IsNullOrWhiteSpace(slackEvent.ThreadTs))
        {
            return;
        }

        var text = slackEvent.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var targets = ExtractAlertTargets(text);
            _logger.LogInformation("🚨 Intercepted Grafana alert for {Namespace}/{Deployment}/{Pod}. Initiating triage...",
                targets.Namespace, targets.Deployment, targets.Pod);

            var diagnosis = await _agentBridge.TriageAlertAsync(text, targets.Namespace, targets.Deployment, targets.Pod);
            var blocks = BlockKitBuilder.BuildAlertTriageBlocks(text, targets.Namespace, targets.Deployment, targets.Pod, diagnosis);

            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = slackEvent.Channel,
                ThreadTs = slackEvent.Ts,
                Text = diagnosis,
                Blocks = blocks
            });

            _logger.LogInformation("✅ Posted automated triage blocks to alert thread {Ts}", slackEvent.Ts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to triage alert message {Ts} in channel {Channel}", slackEvent.Ts, slackEvent.Channel);
        }
    }
}
