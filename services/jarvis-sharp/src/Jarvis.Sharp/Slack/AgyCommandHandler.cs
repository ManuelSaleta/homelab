namespace Jarvis.Sharp.Slack;

using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Interaction;
using SlackNet.WebApi;

public class AgyCommandHandler : ISlashCommandHandler
{
    private readonly ILogger<AgyCommandHandler> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly RateLimiter _rateLimiter;
    private readonly IAgentBridge _agentBridge;

    public AgyCommandHandler(
        ILogger<AgyCommandHandler> logger,
        AuthorizationGuard authGuard,
        RateLimiter rateLimiter,
        IAgentBridge agentBridge)
    {
        _logger = logger;
        _authGuard = authGuard;
        _rateLimiter = rateLimiter;
        _agentBridge = agentBridge;
    }

    public async Task<SlashCommandResponse> Handle(SlashCommand command)
    {
        var userId = command.UserId;
        try
        {
            var verifiedUser = _authGuard.RequireAuthorized(userId);
            _rateLimiter.Check(verifiedUser);

            var prompt = command.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new SlashCommandResponse
                {
                    ResponseType = ResponseType.Ephemeral,
                    Message = new Message { Text = "⚠️ Please provide a prompt for Antigravity (e.g. `/agy check cluster status`)." }
                };
            }

            var context = new Dictionary<string, string>
            {
                ["slack_user"] = verifiedUser,
                ["channel_id"] = command.ChannelId ?? "direct",
                ["command"] = "/agy"
            };

            var reply = await _agentBridge.AskAsync(prompt, context);

            return new SlashCommandResponse
            {
                ResponseType = ResponseType.InChannel,
                Message = new Message { Text = reply }
            };
        }
        catch (UnauthorizedAccessException uex)
        {
            _logger.LogWarning("Unauthorized /agy command attempt from user '{UserId}': {Message}", userId, uex.Message);
            return new SlashCommandResponse
            {
                ResponseType = ResponseType.Ephemeral,
                Message = new Message { Text = $"⛔ {uex.Message}" }
            };
        }
        catch (RateLimitExceededException rex)
        {
            _logger.LogWarning("Rate limit exceeded for user '{UserId}': {Message}", userId, rex.Message);
            return new SlashCommandResponse
            {
                ResponseType = ResponseType.Ephemeral,
                Message = new Message { Text = $"⛔ {rex.Message}" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing /agy command from user '{UserId}'", userId);
            return new SlashCommandResponse
            {
                ResponseType = ResponseType.Ephemeral,
                Message = new Message { Text = $"⚠️ Error communicating with Antigravity: {ex.Message}" }
            };
        }
    }
}
