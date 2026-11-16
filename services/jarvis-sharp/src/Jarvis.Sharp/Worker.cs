namespace Jarvis.Sharp;

using Jarvis.Sharp.Security;
using Jarvis.Sharp.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.SocketMode;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly CandidateStateStore _stateStore;
    private readonly ISlackSocketModeClient? _socketModeClient;

    public Worker(
        ILogger<Worker> logger,
        AuthorizationGuard authGuard,
        CandidateStateStore stateStore,
        ISlackSocketModeClient? socketModeClient = null)
    {
        _logger = logger;
        _authGuard = authGuard;
        _stateStore = stateStore;
        _socketModeClient = socketModeClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🤖 [Jarvis.Sharp] Daemon initialized successfully.");
        _logger.LogInformation("🛡️ [Jarvis.Sharp] USER_WHITELIST loaded with {Count} authorized user(s).", _authGuard.AllowedUsers.Count);

        var appToken = Environment.GetEnvironmentVariable("SLACK_APP_TOKEN")?.Trim();
        var botToken = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")?.Trim();

        if (!string.IsNullOrWhiteSpace(appToken) && appToken.StartsWith("xapp-") &&
            !string.IsNullOrWhiteSpace(botToken) && _socketModeClient != null)
        {
            try
            {
                _logger.LogInformation("🔌 [Jarvis.Sharp] Connecting to Slack via Socket Mode WebSocket...");
                await _socketModeClient.Connect(new SocketModeConnectionOptions(), stoppingToken);
                _logger.LogInformation("⚡ [Jarvis.Sharp] Slack Socket Mode listener connected successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [Jarvis.Sharp] Failed to establish Slack Socket Mode connection. Continuing background tasks...");
            }
        }
        else
        {
            _logger.LogInformation("ℹ️ [Jarvis.Sharp] Slack Socket Mode tokens (SLACK_APP_TOKEN / SLACK_BOT_TOKEN) not configured or in mock mode. Standalone daemon running.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var evicted = _stateStore.EvictExpired();
            if (evicted > 0)
            {
                _logger.LogInformation("🧹 [Jarvis.Sharp] Evicted {Count} expired candidate batch(es).", evicted);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_socketModeClient != null && _socketModeClient.Connected)
        {
            try
            {
                await _socketModeClient.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during Slack Socket Mode disconnection.");
            }
        }

        _logger.LogInformation("🛑 [Jarvis.Sharp] Daemon shutting down gracefully.");
    }
}
