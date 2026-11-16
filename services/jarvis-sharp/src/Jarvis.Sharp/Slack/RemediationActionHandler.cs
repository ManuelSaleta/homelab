namespace Jarvis.Sharp.Slack;

using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using SlackNet.WebApi;

public class RemediationActionPayload
{
    public string Namespace { get; set; } = "media";
    public string Deployment { get; set; } = string.Empty;
    public string Pod { get; set; } = string.Empty;
}

public class RemediationActionHandler : IBlockActionHandler<ButtonAction>
{
    private readonly ILogger<RemediationActionHandler> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly RateLimiter _rateLimiter;
    private readonly IKubernetesDiagnostics _k8sDiagnostics;
    private readonly HttpClient _httpClient;
    private readonly ISlackApiClient _slackClient;

    public RemediationActionHandler(
        ILogger<RemediationActionHandler> logger,
        AuthorizationGuard authGuard,
        RateLimiter rateLimiter,
        IKubernetesDiagnostics k8sDiagnostics,
        HttpClient httpClient,
        ISlackApiClient slackClient)
    {
        _logger = logger;
        _authGuard = authGuard;
        _rateLimiter = rateLimiter;
        _k8sDiagnostics = k8sDiagnostics;
        _httpClient = httpClient;
        _slackClient = slackClient;
    }

    public async Task Handle(ButtonAction action, BlockActionRequest request)
    {
        var actionId = action.ActionId ?? string.Empty;
        var userId = request.User?.Id;

        try
        {
            var verifiedUser = _authGuard.RequireAuthorized(userId);
            _rateLimiter.Check(verifiedUser);

            RemediationActionPayload? payload = null;
            var actionValue = action.Value;
            if (!string.IsNullOrWhiteSpace(actionValue))
            {
                try
                {
                    payload = JsonSerializer.Deserialize<RemediationActionPayload>(
                        actionValue,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize action value payload: '{Value}'", actionValue);
                }
            }

            var ns = payload?.Namespace ?? "media";
            var deployment = payload?.Deployment ?? string.Empty;
            var pod = payload?.Pod ?? string.Empty;

            string replyText;

            switch (actionId)
            {
                case "remediate_restart_deployment":
                {
                    var result = await _k8sDiagnostics.RestartDeploymentAsync(ns, deployment);
                    var statusEmoji = result.Restarted ? "✅" : "❌";
                    replyText = $"{statusEmoji} *Deployment Restart* triggered by <@{verifiedUser}> for `{ns}/{deployment}`:\n```{result.Message}```";
                    break;
                }
                case "remediate_view_logs":
                {
                    var diag = await _k8sDiagnostics.GetPodDiagnosticsAsync(ns, pod, 100);
                    var logsExcerpt = diag.Logs.Length > 2500 ? diag.Logs[^2500..] : (string.IsNullOrWhiteSpace(diag.Logs) ? "No logs returned." : diag.Logs);
                    replyText = $"📜 *Logs for `{ns}/{pod}`* (requested by <@{verifiedUser}>):\n```{logsExcerpt}```";
                    break;
                }
                case "remediate_dismiss":
                {
                    replyText = $"🔕 Alert triage dismissed by <@{verifiedUser}>.";
                    break;
                }
                default:
                    replyText = $"⚠️ Unknown remediation action '{actionId}'.";
                    break;
            }

            await SendResponseAsync(request, replyText, isEphemeral: false);
        }
        catch (UnauthorizedAccessException uex)
        {
            _logger.LogWarning("Unauthorized remediation attempt from '{UserId}': {Message}", userId, uex.Message);
            await SendResponseAsync(request, $"⛔ {uex.Message}", isEphemeral: true);
        }
        catch (RateLimitExceededException rex)
        {
            _logger.LogWarning("Rate limit exceeded for user '{UserId}': {Message}", userId, rex.Message);
            await SendResponseAsync(request, $"⛔ {rex.Message}", isEphemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing remediation action '{ActionId}'", actionId);
            await SendResponseAsync(request, $"⚠️ Remediation failed: {ex.Message}", isEphemeral: true);
        }
    }

    private async Task SendResponseAsync(BlockActionRequest request, string text, bool isEphemeral)
    {
        if (!string.IsNullOrWhiteSpace(request.ResponseUrl))
        {
            try
            {
                var payload = new
                {
                    text,
                    response_type = isEphemeral ? "ephemeral" : "in_channel",
                    replace_original = false
                };
                await _httpClient.PostAsJsonAsync(request.ResponseUrl, payload);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to respond via ResponseUrl '{Url}'", request.ResponseUrl);
            }
        }

        if (request.Channel?.Id != null)
        {
            try
            {
                if (isEphemeral && request.User?.Id != null)
                {
                    await _slackClient.Chat.PostEphemeral(request.User.Id, new Message { Text = text, Channel = request.Channel.Id });
                }
                else
                {
                    await _slackClient.Chat.PostMessage(new Message { Text = text, Channel = request.Channel.Id });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post Slack message to channel '{Channel}'", request.Channel.Id);
            }
        }
    }
}
