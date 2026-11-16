namespace Jarvis.Sharp.Slack;

using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Jarvis.Sharp.State;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Interaction;
using SlackNet.WebApi;

public class DownloadCommandHandler : ISlashCommandHandler
{
    private readonly ILogger<DownloadCommandHandler> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly RateLimiter _rateLimiter;
    private readonly CandidateStateStore _stateStore;
    private readonly IMediaSyncDispatcher _dispatcher;

    public DownloadCommandHandler(
        ILogger<DownloadCommandHandler> logger,
        AuthorizationGuard authGuard,
        RateLimiter rateLimiter,
        CandidateStateStore stateStore,
        IMediaSyncDispatcher dispatcher)
    {
        _logger = logger;
        _authGuard = authGuard;
        _rateLimiter = rateLimiter;
        _stateStore = stateStore;
        _dispatcher = dispatcher;
    }

    public async Task<SlashCommandResponse> Handle(SlashCommand command)
    {
        var userId = command.UserId;
        try
        {
            var verifiedUser = _authGuard.RequireAuthorized(userId);
            _rateLimiter.Check(verifiedUser);

            var indices = CommandParser.ParseDownloadCommand(command.Text);
            if (indices.Count == 0)
            {
                return new SlashCommandResponse
                {
                    ResponseType = ResponseType.Ephemeral,
                    Message = new Message { Text = "⚠️ Please specify candidate item numbers to approve (e.g. `/download 1 3 6`)." }
                };
            }

            var latestBatch = _stateStore.GetLatestBatch();
            if (latestBatch == null)
            {
                return new SlashCommandResponse
                {
                    ResponseType = ResponseType.Ephemeral,
                    Message = new Message { Text = "⚠️ No active candidate batch found or candidate window has expired (24h TTL)." }
                };
            }

            var result = await _dispatcher.DispatchApprovedCandidatesAsync(
                latestBatch?.BatchId,
                indices,
                requestedBy: $"slack:{verifiedUser}");

            return new SlashCommandResponse
            {
                ResponseType = ResponseType.InChannel,
                Message = new Message { Text = result.SummaryText }
            };
        }
        catch (UnauthorizedAccessException uex)
        {
            _logger.LogWarning("Unauthorized /download command attempt from user '{UserId}': {Message}", userId, uex.Message);
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
            _logger.LogError(ex, "Unexpected error processing /download command from user '{UserId}'", userId);
            return new SlashCommandResponse
            {
                ResponseType = ResponseType.Ephemeral,
                Message = new Message { Text = $"⚠️ Failed to process /download command: {ex.Message}" }
            };
        }
    }
}
