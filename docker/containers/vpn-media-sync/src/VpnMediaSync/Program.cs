using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Plex.Core.Http;
using Plex.Core.Http.Extensions;
using VpnMediaSync.Services;
using VpnMediaSync.Workers;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args
});

// Optionally load explicit CONFIG_PATH or local configuration overlay if present
var customConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
if (string.IsNullOrWhiteSpace(customConfigPath))
{
    var candidates = new[]
    {
        "appsettings.local.json",
        "../appsettings.local.json",
        "../../appsettings.local.json",
        "docker/containers/vpn-media-sync/src/VpnMediaSync/appsettings.local.json",
        Path.Combine(AppContext.BaseDirectory, "appsettings.local.json")
    };

    customConfigPath = candidates.FirstOrDefault(File.Exists);
}

if (!string.IsNullOrWhiteSpace(customConfigPath) && File.Exists(customConfigPath))
{
    customConfigPath = Path.GetFullPath(customConfigPath);
    Console.WriteLine($"Loaded external configuration overlay from '{customConfigPath}'.");
    builder.Configuration.AddJsonFile(customConfigPath, optional: true, reloadOnChange: true);
}

// Map conventional flat container / Kubernetes env vars to standard hierarchical options
var flatEnvMappings = new Dictionary<string, string?>
{
    ["Indexer:ApiKey"] = Environment.GetEnvironmentVariable("PROWLARR_API_KEY"),
    ["Indexer:BaseUrl"] = Environment.GetEnvironmentVariable("PROWLARR_BASE_URL"),
    ["Downloader:Password"] = Environment.GetEnvironmentVariable("QBIT_PASSWORD"),
    ["Downloader:Username"] = Environment.GetEnvironmentVariable("QBIT_USERNAME"),
    ["Downloader:BaseUrl"] = Environment.GetEnvironmentVariable("QBIT_BASE_URL"),
    ["Schedule:WebhookSecretToken"] = Environment.GetEnvironmentVariable("WEBHOOK_SECRET_TOKEN"),
    ["PlexClientOptions:ServerUrl"] = Environment.GetEnvironmentVariable("PLEX_SERVER_URL"),
    ["PlexClientOptions:Token"] = Environment.GetEnvironmentVariable("PLEX_TOKEN"),
    ["Trending:Enabled"] = Environment.GetEnvironmentVariable("ENABLE_TRENDING_FEEDS"),
    ["Trending:PopularMoviesUrl"] = Environment.GetEnvironmentVariable("POPULAR_MOVIES_URL"),
    ["Trending:PopularTvUrl"] = Environment.GetEnvironmentVariable("POPULAR_TV_URL")
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
builder.Services.AddOptions<SchedulingConfigOpts>()
    .BindConfiguration(SchedulingConfigOpts.SectionName);

builder.Services.AddOptions<VpnConfigOpts>()
    .BindConfiguration(VpnConfigOpts.SectionName);

builder.Services.AddOptions<TrendingConfigOpts>()
    .BindConfiguration(TrendingConfigOpts.SectionName);

builder.Services.AddOptions<IndexerConfigOpts>()
    .BindConfiguration(IndexerConfigOpts.SectionName);

builder.Services.AddOptions<FiltersConfigOpts>()
    .BindConfiguration(FiltersConfigOpts.SectionName);

builder.Services.AddOptions<DownloaderConfigOpts>()
    .BindConfiguration(DownloaderConfigOpts.SectionName);

builder.Services.AddOptions<RsyncConfigOpts>()
    .BindConfiguration(RsyncConfigOpts.SectionName);

builder.Services.AddOptions<PlexClientOptions>()
.BindConfiguration("PlexClientOptions");

var flexibleStringConverter = new FlexibleStringJsonConverter();
PlexClient.DefaultJsonOptions.Converters.Add(flexibleStringConverter);

var jsonSerializerOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    Converters = { flexibleStringConverter }
};
builder.Services.AddSingleton(jsonSerializerOptions);

builder.Services.AddSingleton<VpnGuardService>();
builder.Services.AddSingleton<JsonStateStore>();

builder.Services.AddPlexClient(options =>
{
    var serverUrl = builder.Configuration["PlexClientOptions:ServerUrl"];
    if (!string.IsNullOrWhiteSpace(serverUrl))
    {
        options.ServerUrl = serverUrl;
    }

    var token = builder.Configuration["PlexClientOptions:Token"];
    if (!string.IsNullOrWhiteSpace(token))
    {
        options.Token = token;
    }

    options.ClientIdentifier = builder.Configuration["PlexClientOptions:ClientIdentifier"] ?? "plex-media-polling-client";
    options.Product = "Homelab";
    options.Version = "1.0.0";
});

builder.Services.AddHttpClient<TrendingFeedClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("VpnMediaSync/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All
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

builder.Services.AddSingleton<MediaTransferService>();
builder.Services.AddSingleton<MediaPollingService>();
builder.Services.AddSingleton<IMediaPollingService>(sp => sp.GetRequiredService<MediaPollingService>());
builder.Services.AddScoped<SyncOrchestrator>();
builder.Services.AddHostedService<SyncSchedulerWorker>();

using var host = builder.Build();
await host.RunAsync();
return 0;
