namespace Jarvis.Sharp.Slack;

using System.Text.RegularExpressions;
using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;

public class AppMentionEventHandler : IEventHandler<AppMention>
{
    private static readonly Regex MentionPrefixRegex = new(@"^<@[A-Z0-9]+>\s*", RegexOptions.Compiled);

    private readonly ILogger<AppMentionEventHandler> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly RateLimiter _rateLimiter;
    private readonly IAgentBridge _agentBridge;
    private readonly ISlackApiClient _slackClient;

    public AppMentionEventHandler(
        ILogger<AppMentionEventHandler> logger,
        AuthorizationGuard authGuard,
        RateLimiter rateLimiter,
        IAgentBridge agentBridge,
        ISlackApiClient slackClient)
    {
        _logger = logger;
        _authGuard = authGuard;
        _rateLimiter = rateLimiter;
        _agentBridge = agentBridge;
        _slackClient = slackClient;
    }

    public async Task Handle(AppMention slackEvent)
    {
        var userId = slackEvent.User;
        var threadTs = !string.IsNullOrWhiteSpace(slackEvent.ThreadTs) ? slackEvent.ThreadTs : slackEvent.Ts;

        try
        {
            var verifiedUser = _authGuard.RequireAuthorized(userId);
            _rateLimiter.Check(verifiedUser);

            var rawText = slackEvent.Text ?? string.Empty;
            var prompt = MentionPrefixRegex.Replace(rawText, string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                await _slackClient.Chat.PostMessage(new Message
                {
                    Channel = slackEvent.Channel,
                    ThreadTs = threadTs,
                    Text = "👋 How can I help you? Ask any question or command."
                });
                return;
            }

            var context = new Dictionary<string, string>
            {
                ["slack_user"] = verifiedUser,
                ["channel_id"] = slackEvent.Channel ?? "direct",
                ["thread_ts"] = threadTs
            };

            var reply = await _agentBridge.AskAsync(prompt, context);

            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = slackEvent.Channel,
                ThreadTs = threadTs,
                Text = reply
            });
        }
        catch (UnauthorizedAccessException uex)
        {
            _logger.LogWarning("Unauthorized app_mention attempt from '{UserId}': {Message}", userId, uex.Message);
            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = slackEvent.Channel,
                ThreadTs = threadTs,
                Text = $"⛔ {uex.Message}"
            });
        }
        catch (RateLimitExceededException rex)
        {
            _logger.LogWarning("Rate limit exceeded for user '{UserId}': {Message}", userId, rex.Message);
            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = slackEvent.Channel,
                ThreadTs = threadTs,
                Text = $"⛔ {rex.Message}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle app_mention event for user '{UserId}'", userId);
            await _slackClient.Chat.PostMessage(new Message
            {
                Channel = slackEvent.Channel,
                ThreadTs = threadTs,
                Text = $"⚠️ Failed to process request: {ex.Message}"
            });
        }
    }
}
