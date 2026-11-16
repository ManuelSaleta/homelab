namespace Jarvis.Sharp.Tests;

using System.Text;
using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Jarvis.Sharp.State;
using Microsoft.Extensions.DependencyInjection;
using SlackNet.Extensions.DependencyInjection;
using Xunit;

public class SecurityAndSanitizerTests
{
    [Fact]
    public void AuthorizationGuard_ShouldFailClosed_WhenWhitelistEmpty()
    {
        var guard = new AuthorizationGuard(string.Empty);
        Assert.False(guard.IsAuthorized("U12345"));
        Assert.Throws<UnauthorizedAccessException>(() => guard.RequireAuthorized("U12345"));
    }

    [Fact]
    public void AuthorizationGuard_ShouldAllowWhitelistedUser()
    {
        var guard = new AuthorizationGuard("U_MANNY, U_ADMIN");
        Assert.True(guard.IsAuthorized("U_MANNY"));
        Assert.True(guard.IsAuthorized("U_ADMIN"));
        Assert.False(guard.IsAuthorized("U_INTRUDER"));
        Assert.Equal("U_MANNY", guard.RequireAuthorized("U_MANNY"));
    }

    [Fact]
    public void HmacSigner_ShouldComputeAndVerifySignature()
    {
        var secret = "super-secret-key-12345";
        var payload = Encoding.UTF8.GetBytes("{\"batch_id\":\"batch-1\",\"items\":[]}");

        var signature = HmacSigner.ComputeSignature(secret, payload);
        Assert.StartsWith("sha256=", signature);
        Assert.True(HmacSigner.VerifySignature(secret, payload, signature));
        Assert.False(HmacSigner.VerifySignature("wrong-secret", payload, signature));
        Assert.False(HmacSigner.VerifySignature(secret, payload, "sha256=invalid"));
    }

    [Fact]
    public void RateLimiter_ShouldBlockExcessiveRequests()
    {
        var limiter = new RateLimiter(maxRequests: 3, windowSeconds: 60.0);
        var now = DateTimeOffset.UtcNow;

        limiter.Check("U_TEST", now);
        limiter.Check("U_TEST", now.AddSeconds(1));
        limiter.Check("U_TEST", now.AddSeconds(2));

        Assert.Throws<RateLimitExceededException>(() => limiter.Check("U_TEST", now.AddSeconds(3)));

        // After window expires, should allow again
        limiter.Check("U_TEST", now.AddSeconds(65));
    }

    [Fact]
    public void InputSanitizer_ShouldValidateK8sIdentifiersAndNamespaces()
    {
        Assert.Equal("valid-pod-1", InputSanitizer.ValidateK8sIdentifier("valid-pod-1"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateK8sIdentifier("invalid_pod!"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateK8sIdentifier("pod; rm -rf /"));

        Assert.Equal("monitoring", InputSanitizer.ValidateNamespace("monitoring"));
        Assert.Equal("media", InputSanitizer.ValidateNamespace("media"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateNamespace("unauthorized-ns"));
    }

    [Fact]
    public void InputSanitizer_ShouldValidateHomelabNodeIps()
    {
        Assert.Equal("192.168.1.200", InputSanitizer.ValidateNodeIp("192.168.1.200"));
        Assert.Equal("192.168.1.210", InputSanitizer.ValidateNodeIp("192.168.1.210"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateNodeIp("10.0.0.1"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateNodeIp("192.168.1.999"));
    }

    [Fact]
    public void InputSanitizer_ShouldValidateLogQlQueries()
    {
        var valid = "{namespace=\"monitoring\"} |= \"error\"";
        Assert.Equal(valid, InputSanitizer.ValidateLogQlQuery(valid));

        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateLogQlQuery(""));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateLogQlQuery("no stream selector here"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateLogQlQuery("{app=\"test\"}\nrm -rf /"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateLogQlQuery("{app=\"test\"}", maxLength: 5));
    }
}

public class StateAndCommandTests
{
    [Fact]
    public void CommandParser_ShouldExtractIndices()
    {
        var indices = CommandParser.ParseDownloadCommand("/download 1 3 6 10");
        Assert.Equal(new[] { 1, 3, 6, 10 }, indices);

        var commaIndices = CommandParser.ParseDownloadCommand("/download 2, 4, 8");
        Assert.Equal(new[] { 2, 4, 8 }, commaIndices);

        Assert.Empty(CommandParser.ParseDownloadCommand("no numbers here"));
        Assert.Empty(CommandParser.ParseDownloadCommand(null));
    }

    [Fact]
    public void CandidateStateStore_ShouldStoreAndResolveBatchWithinTtl()
    {
        var store = new CandidateStateStore(ttl: TimeSpan.FromMinutes(10));
        var items = new List<CandidateItem>
        {
            new(1, "Severance", 2025, "TV"),
            new(2, "Gladiator II", 2024, "Movie"),
            new(3, "Evil Dead Burn", 2026, "Movie")
        };

        var now = DateTimeOffset.UtcNow;
        store.SaveBatch("batch-alpha", items, now);

        var batch = store.GetBatch("batch-alpha", now);
        Assert.NotNull(batch);
        Assert.Equal(3, batch.Items.Count);

        var resolved = store.ResolveIndices("batch-alpha", new[] { 1, 3 }, now);
        Assert.Equal(2, resolved.Count);
        Assert.Equal("Severance", resolved[0].Title);
        Assert.Equal("Evil Dead Burn", resolved[1].Title);

        // After TTL expired
        var expiredBatch = store.GetBatch("batch-alpha", now.AddMinutes(15));
        Assert.Null(expiredBatch);
    }

    [Fact]
    public void SlackNet_DIConfigurationCheck()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSlackNet(c =>
        {
            c.UseApiToken("xoxb-fake");
            c.UseAppLevelToken("xapp-fake");

            c.RegisterSlashCommandHandler<Jarvis.Sharp.Slack.DownloadCommandHandler>("/download");
            c.RegisterSlashCommandHandler<Jarvis.Sharp.Slack.AgyCommandHandler>("/agy");

            c.RegisterBlockActionHandler<SlackNet.Blocks.ButtonAction, Jarvis.Sharp.Slack.RemediationActionHandler>("remediate_restart_deployment");
            c.RegisterBlockActionHandler<SlackNet.Blocks.ButtonAction, Jarvis.Sharp.Slack.RemediationActionHandler>("remediate_view_logs");
            c.RegisterBlockActionHandler<SlackNet.Blocks.ButtonAction, Jarvis.Sharp.Slack.RemediationActionHandler>("remediate_dismiss");
            c.RegisterBlockActionHandler<SlackNet.Blocks.ButtonAction, Jarvis.Sharp.Slack.CandidateActionHandler>();

            c.RegisterEventHandler<SlackNet.Events.MessageEvent, Jarvis.Sharp.Slack.AlertMessageEventHandler>();
            c.RegisterEventHandler<SlackNet.Events.AppMention, Jarvis.Sharp.Slack.AppMentionEventHandler>();
        });

        var provider = services.BuildServiceProvider();
        var socketClient = provider.GetService<SlackNet.ISlackSocketModeClient>();
        Assert.NotNull(socketClient);
    }

    [Fact]
    public void AlertMessageEventHandler_ExtractAlertTargets_ShouldParseAccurately()
    {
        var alert1 = "[FIRING:1] K3s Pod CrashLooping (namespace=media deployment=vpn-media-sync pod=vpn-media-sync-6d7c8f9b-abc12)";
        var (ns1, deploy1, pod1) = Jarvis.Sharp.Slack.AlertMessageEventHandler.ExtractAlertTargets(alert1);
        Assert.Equal("media", ns1);
        Assert.Equal("vpn-media-sync", deploy1);
        Assert.Equal("vpn-media-sync-6d7c8f9b-abc12", pod1);

        var alert2 = "[FIRING:1] Container OOMKilled pod: jellyfin-server-5d6e7f8a-xyz09";
        var (ns2, deploy2, pod2) = Jarvis.Sharp.Slack.AlertMessageEventHandler.ExtractAlertTargets(alert2);
        Assert.Equal("media", ns2);
        Assert.Equal("jellyfin-server", deploy2);
        Assert.Equal("jellyfin-server-5d6e7f8a-xyz09", pod2);

        var alert3 = "Generic alert without metadata";
        var (ns3, deploy3, pod3) = Jarvis.Sharp.Slack.AlertMessageEventHandler.ExtractAlertTargets(alert3);
        Assert.Equal("media", ns3);
        Assert.Equal("vpn-media-sync-0", pod3);
    }

    [Fact]
    public async Task DownloadCommandHandler_ShouldRejectUnauthorizedUser()
    {
        var guard = new AuthorizationGuard("U_AUTHORIZED");
        var limiter = new RateLimiter();
        var store = new CandidateStateStore();
        var dispatcher = new MockDispatcher();
        var handler = new Jarvis.Sharp.Slack.DownloadCommandHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Slack.DownloadCommandHandler>.Instance,
            guard,
            limiter,
            store,
            dispatcher);

        var command = new SlackNet.Interaction.SlashCommand
        {
            UserId = "U_ATTACKER",
            Text = "1 2"
        };

        var response = await handler.Handle(command);
        Assert.Equal(SlackNet.Interaction.ResponseType.Ephemeral, response.ResponseType);
        Assert.Contains("⛔", response.Message.Text);
    }

    [Fact]
    public async Task DownloadCommandHandler_ShouldPromptOnEmptyInput()
    {
        var guard = new AuthorizationGuard("U_AUTHORIZED");
        var limiter = new RateLimiter();
        var store = new CandidateStateStore();
        var dispatcher = new MockDispatcher();
        var handler = new Jarvis.Sharp.Slack.DownloadCommandHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Slack.DownloadCommandHandler>.Instance,
            guard,
            limiter,
            store,
            dispatcher);

        var command = new SlackNet.Interaction.SlashCommand
        {
            UserId = "U_AUTHORIZED",
            Text = "   "
        };

        var response = await handler.Handle(command);
        Assert.Equal(SlackNet.Interaction.ResponseType.Ephemeral, response.ResponseType);
        Assert.Contains("Please specify candidate item numbers", response.Message.Text);
    }

    [Fact]
    public async Task AgyCommandHandler_ShouldRejectUnauthorizedUser()
    {
        var guard = new AuthorizationGuard("U_AUTHORIZED");
        var limiter = new RateLimiter();
        var k8sMock = new KubernetesDiagnostics(new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = "http://localhost:8080" }));
        var bridge = new Jarvis.Sharp.Services.AgentBridge(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Services.AgentBridge>.Instance,
            k8sMock);
        var handler = new Jarvis.Sharp.Slack.AgyCommandHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Slack.AgyCommandHandler>.Instance,
            guard,
            limiter,
            bridge);

        var command = new SlackNet.Interaction.SlashCommand
        {
            UserId = "U_ATTACKER",
            Text = "what is the cluster status?"
        };

        var response = await handler.Handle(command);
        Assert.Equal(SlackNet.Interaction.ResponseType.Ephemeral, response.ResponseType);
        Assert.Contains("⛔", response.Message.Text);
    }

    private class MockDispatcher : IMediaSyncDispatcher
    {
        public Task<DispatchResult> DispatchApprovedCandidatesAsync(
            string? batchId,
            IEnumerable<int> indices,
            string requestedBy,
            string? webhookUrl = null,
            string? webhookSecret = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DispatchResult(
                true,
                batchId ?? "mock-batch",
                Array.Empty<CandidateItem>(),
                Array.Empty<CandidateItem>(),
                "✅ Dispatched mock items"));
        }
    }
}
