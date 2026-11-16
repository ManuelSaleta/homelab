namespace Jarvis.Sharp.Security;

using System.Collections.Concurrent;

public class RateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _userEvents = new(StringComparer.Ordinal);

    public RateLimiter(int maxRequests = 10, double windowSeconds = 60.0)
    {
        if (maxRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRequests), "maxRequests must be positive.");
        }
        if (windowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), "windowSeconds must be positive.");
        }

        _maxRequests = maxRequests;
        _window = TimeSpan.FromSeconds(windowSeconds);
    }

    public void Check(string userId, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var current = now ?? DateTimeOffset.UtcNow;
        var cutoff = current - _window;

        var queue = _userEvents.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= _maxRequests)
            {
                throw new InvalidOperationException(
                    $"Rate limit exceeded for user '{userId}' ({_maxRequests} requests per {(int)_window.TotalSeconds}s).");
            }

            queue.Enqueue(current);
        }
    }
}
