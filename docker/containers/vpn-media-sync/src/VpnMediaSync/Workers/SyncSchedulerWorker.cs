using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;
using VpnMediaSync.Services;

namespace VpnMediaSync.Workers;

public sealed class SyncSchedulerWorker(
    IOptions<ScheduleConfig> scheduleOptions,
    IServiceScopeFactory scopeFactory,
    ILogger<SyncSchedulerWorker> logger) : BackgroundService
{
    private readonly ScheduleConfig _config = scheduleOptions?.Value ?? throw new ArgumentNullException(nameof(scheduleOptions));

    private readonly Channel<string> _triggerChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.IntervalMinutes));
        logger.LogInformation(
            "Starting vpn-media-sync daemon loop (interval: {Minutes} minutes, webhook port: {WebhookPort}).",
            interval.TotalMinutes,
            _config.WebhookPort);

        // Start webhook listener if configured
        if (_config.WebhookPort > 0)
        {
            _ = RunWebhookListenerAsync(_config.WebhookPort, stoppingToken);
        }

        // Initial cycle run on startup
        await ExecuteCycleSafelyAsync("startup", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(interval);

            string triggerSource;
            try
            {
                if (await _triggerChannel.Reader.WaitToReadAsync(timeoutCts.Token))
                {
                    _triggerChannel.Reader.TryRead(out var reason);
                    triggerSource = $"webhook ({reason})";
                }
                else
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                triggerSource = "periodic timer";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Drain any buffered triggers so we don't immediately repeat the exact same run
            while (_triggerChannel.Reader.TryRead(out _)) { }

            logger.LogInformation("Starting media sync cycle via {Source}.", triggerSource);
            await ExecuteCycleSafelyAsync(triggerSource, stoppingToken);
        }

        logger.LogInformation("vpn-media-sync daemon loop stopped gracefully.");
    }

    private async Task ExecuteCycleSafelyAsync(string source, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<SyncOrchestrator>();
            await orchestrator.RunCycleAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception during media sync cycle ({Source}).", source);
        }
    }

    private async Task RunWebhookListenerAsync(int port, CancellationToken ct)
    {
        var container = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        var root = Environment.UserName == "root";

        // A prefix represents the URI structure you want your app to listen on.
        // Defines which incoming HTTP/HTTPS requests your application will listen for and process.
        // Roughly follows: scheme://host:port/path/
        // Important: prefix must always end with trailing '/' else .Add(prefix) throws ArgumentException
        // TODO: For http(S) requires cert to bound to the strict port on the host OS.
        // When running locally & non-root  bind with a specified host.
        // containers need a permissive prefix

        var localDebugging = !container && !root;
        var specific = $"http://127.0.0.1:{port}/";
        var permissive = $"http://*:{port}/";
        var prefix = localDebugging ? specific : permissive;

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
            logger.LogInformation("Webhook listener started on {Prefix}", prefix);


            using var cancelTokenRegistration = ct.Register(() => { try { listener.Stop(); } catch { } });

            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync();
                _ = ProcessWebhookContextAsync(context, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Normal graceful shutdown
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook listener stopped unexpectedly on port {Port}.", port);
        }
    }

    private async Task ProcessWebhookContextAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            var req = context.Request;
            var path = req.Url?.AbsolutePath ?? "/";
            byte[] responseBytes;
            var statusCode = (int)HttpStatusCode.OK;

            // Health probe endpoint: GET only, no token authentication required
            if (string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                (path.Equals("/health", StringComparison.OrdinalIgnoreCase) || path.Equals("/")))
            {
                responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"healthy\":true}\n");
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(responseBytes, ct);
                context.Response.Close();
                return;
            }

            // Sync triggers: MUST BE HTTP POST
            if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = (int)HttpStatusCode.MethodNotAllowed;
                responseBytes = Encoding.UTF8.GetBytes("{\"error\":\"Method Not Allowed. Sync triggers require HTTP POST.\"}\n");
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(responseBytes, ct);
                context.Response.Close();
                return;
            }

            // Authentication verification if WebhookSecretToken is configured
            var secretToken = !string.IsNullOrWhiteSpace(_config.WebhookSecretToken)
                ? _config.WebhookSecretToken
                : Environment.GetEnvironmentVariable("WEBHOOK_SECRET_TOKEN");

            if (!string.IsNullOrWhiteSpace(secretToken))
            {
                var receivedToken = req.Headers["X-Webhook-Token"];
                if (string.IsNullOrWhiteSpace(receivedToken) && !string.IsNullOrWhiteSpace(req.Headers["Authorization"]))
                {
                    var authHeader = req.Headers["Authorization"]!;
                    if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        receivedToken = authHeader[7..].Trim();
                    }
                }

                if (!string.Equals(secretToken, receivedToken, StringComparison.Ordinal))
                {
                    logger.LogWarning("Unauthorized webhook invocation attempt from {RemoteEndPoint}.", req.RemoteEndPoint);
                    statusCode = (int)HttpStatusCode.Unauthorized;
                    responseBytes = Encoding.UTF8.GetBytes("{\"error\":\"Unauthorized: invalid or missing webhook secret token\"}\n");
                    context.Response.StatusCode = statusCode;
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = responseBytes.Length;
                    await context.Response.OutputStream.WriteAsync(responseBytes, ct);
                    context.Response.Close();
                    return;
                }
            }

            if (path.StartsWith("/webhook", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/sync", StringComparison.OrdinalIgnoreCase))
            {
                _triggerChannel.Writer.TryWrite($"HTTP POST {path}");
                responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"triggered\":true}\n");
            }
            else
            {
                statusCode = (int)HttpStatusCode.NotFound;
                responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"not_found\"}\n");
            }

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes, ct);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error handling webhook HTTP request.");
        }
    }
}
