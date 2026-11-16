namespace Jarvis.Sharp.Slack;

using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Jarvis.Sharp.State;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Interaction;
using SlackNet.WebApi;

public class CandidateActionPayload
{
    public List<int> Indices { get; set; } = new();
    public string BatchId { get; set; } = string.Empty;
}

public class CandidateActionHandler : IBlockActionHandler<ButtonAction>
{
    private readonly ILogger<CandidateActionHandler> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly RateLimiter _rateLimiter;
    private readonly CandidateStateStore _stateStore;
    private readonly IMediaSyncDispatcher _dispatcher;
    private readonly HttpClient _httpClient;
    private readonly ISlackApiClient _slackClient;

    public CandidateActionHandler(
        ILogger<CandidateActionHandler> logger,
        AuthorizationGuard authGuard,
        RateLimiter rateLimiter,
        CandidateStateStore stateStore,
        IMediaSyncDispatcher dispatcher,
        HttpClient httpClient,
        ISlackApiClient slackClient)
    {
        _logger = logger;
        _authGuard = authGuard;
        _rateLimiter = rateLimiter;
        _stateStore = stateStore;
        _dispatcher = dispatcher;
        _httpClient = httpClient;
        _slackClient = slackClient;
    }

    public async Task Handle(ButtonAction action, BlockActionRequest request)
    {
        if (!(action.ActionId?.StartsWith("media_sync_download_item", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return;
        }

        var userId = request.User?.Id;

        try
        {
            var verifiedUser = _authGuard.RequireAuthorized(userId);
            _rateLimiter.Check(verifiedUser);

            CandidateActionPayload? payload = null;
            var actionValue = action.Value;
            if (!string.IsNullOrWhiteSpace(actionValue))
            {
                payload = JsonSerializer.Deserialize<CandidateActionPayload>(
                    actionValue,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            var indices = payload?.Indices ?? new List<int>();
            var batchId = payload?.BatchId;

            if (indices.Count == 0)
            {
                await SendResponseAsync(request, "⚠️ No item indices attached to this action.", isEphemeral: true);
                return;
            }

            var result = await _dispatcher.DispatchApprovedCandidatesAsync(
                batchId,
                indices,
                requestedBy: $"slack:{verifiedUser}");

            await SendResponseAsync(request, result.SummaryText, isEphemeral: false);
        }
        catch (UnauthorizedAccessException uex)
        {
            _logger.LogWarning("Unauthorized candidate approval attempt from '{UserId}': {Message}", userId, uex.Message);
            await SendResponseAsync(request, $"⛔ {uex.Message}", isEphemeral: true);
        }
        catch (RateLimitExceededException rex)
        {
            _logger.LogWarning("Rate limit exceeded for user '{UserId}': {Message}", userId, rex.Message);
            await SendResponseAsync(request, $"⛔ {rex.Message}", isEphemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing candidate button click");
            await SendResponseAsync(request, $"⚠️ Failed to approve candidate: {ex.Message}", isEphemeral: true);
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
