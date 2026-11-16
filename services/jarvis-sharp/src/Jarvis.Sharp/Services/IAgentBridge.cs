namespace Jarvis.Sharp.Services;

public interface IAgentBridge
{
    Task<string> AskAsync(string prompt, IDictionary<string, string>? context = null, CancellationToken cancellationToken = default);
    Task<string> TriageAlertAsync(string alertText, string @namespace, string deployment, string pod, CancellationToken cancellationToken = default);
}
