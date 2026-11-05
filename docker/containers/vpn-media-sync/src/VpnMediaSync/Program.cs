using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnMediaSync.Models;
using VpnMediaSync.Services;
using VpnMediaSync.Workers;

var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "/app/config.json";
var runOnce = false;
var dryRun = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" or "-c" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--once" or "run-once":
            runOnce = true;
            break;
        case "--dry-run":
            dryRun = true;
            break;
        case "--daemon" or "daemon":
            runOnce = false;
            break;
    }
}

if (!File.Exists(configPath))
{
    var candidates = new[]
    {
        "config.local.json",
        "config.json",
        "../config.local.json",
        "../config.json",
        "../../config.local.json",
        "../../config.json",
        "docker/containers/vpn-media-sync/config.local.json",
        "docker/containers/vpn-media-sync/config.json",
        Path.Combine(AppContext.BaseDirectory, "config.local.json"),
        Path.Combine(AppContext.BaseDirectory, "config.json")
    };

    var fallback = candidates.FirstOrDefault(File.Exists);
    if (fallback != null)
    {
        configPath = Path.GetFullPath(fallback);
    }
}

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Configuration file not found at '{configPath}'.");
    return 1;
}

configPath = Path.GetFullPath(configPath);
Console.WriteLine($"Loaded configuration from '{configPath}'.");

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args
});

builder.Configuration.AddJsonFile(configPath, optional: false, reloadOnChange: true);

// Map conventional flat container / Kubernetes env vars to standard hierarchical options
var flatEnvMappings = new Dictionary<string, string?>
{
    ["Indexer:ApiKey"] = Environment.GetEnvironmentVariable("PROWLARR_API_KEY"),
    ["Indexer:BaseUrl"] = Environment.GetEnvironmentVariable("PROWLARR_BASE_URL"),
    ["Downloader:Password"] = Environment.GetEnvironmentVariable("QBIT_PASSWORD"),
    ["Downloader:Username"] = Environment.GetEnvironmentVariable("QBIT_USERNAME"),
    ["Downloader:BaseUrl"] = Environment.GetEnvironmentVariable("QBIT_BASE_URL"),
    ["Schedule:WebhookSecretToken"] = Environment.GetEnvironmentVariable("WEBHOOK_SECRET_TOKEN")
};

var activeOverrides = flatEnvMappings
    .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
    .ToDictionary(kv => kv.Key, kv => kv.Value);

if (activeOverrides.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(activeOverrides!);
}

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// Configure strongly-typed options
builder.Services.Configure<AppConfig>(builder.Configuration);
builder.Services.Configure<ScheduleConfig>(builder.Configuration.GetSection("Schedule"));
builder.Services.Configure<VpnConfig>(builder.Configuration.GetSection("Vpn"));
builder.Services.Configure<TrendingConfig>(builder.Configuration.GetSection("Trending"));
builder.Services.Configure<IndexerConfig>(builder.Configuration.GetSection("Indexer"));
builder.Services.Configure<FiltersConfig>(builder.Configuration.GetSection("Filters"));
builder.Services.Configure<DownloaderConfig>(builder.Configuration.GetSection("Downloader"));
builder.Services.Configure<RsyncConfig>(builder.Configuration.GetSection("Rsync"));

builder.Services.AddSingleton<VpnGuardService>();
builder.Services.AddSingleton<JsonStateStore>();

builder.Services.AddHttpClient<TrendingFeedClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("VpnMediaSync/1.0");
});

builder.Services.AddHttpClient<ProwlarrClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<QBitClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    UseCookies = true,
    CookieContainer = new CookieContainer()
});

builder.Services.AddSingleton<RsyncTransferService>();
builder.Services.AddScoped<SyncOrchestrator>();

if (!runOnce)
{
    builder.Services.AddHostedService<SyncSchedulerWorker>();
}

using var host = builder.Build();

if (runOnce)
{
    await using var scope = host.Services.CreateAsyncScope();
    var orchestrator = scope.ServiceProvider.GetRequiredService<SyncOrchestrator>();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await orchestrator.RunCycleAsync(dryRun, cts.Token);
    return 0;
}

await host.RunAsync();
return 0;
