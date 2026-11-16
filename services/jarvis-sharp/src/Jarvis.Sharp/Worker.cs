namespace Jarvis.Sharp;

using Jarvis.Sharp.Security;
using Jarvis.Sharp.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly CandidateStateStore _stateStore;

    public Worker(
        ILogger<Worker> logger,
        AuthorizationGuard authGuard,
        CandidateStateStore stateStore)
    {
        _logger = logger;
        _authGuard = authGuard;
        _stateStore = stateStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🤖 [Jarvis.Sharp] Daemon initialized successfully.");
        _logger.LogInformation("🛡️ [Jarvis.Sharp] USER_WHITELIST loaded with {Count} authorized user(s).", _authGuard.AllowedUsers.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            var evicted = _stateStore.EvictExpired();
            if (evicted > 0)
            {
                _logger.LogInformation("🧹 [Jarvis.Sharp] Evicted {Count} expired candidate batch(es).", evicted);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        _logger.LogInformation("🛑 [Jarvis.Sharp] Daemon shutting down gracefully.");
    }
}
