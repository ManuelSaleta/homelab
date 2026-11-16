namespace Jarvis.Sharp.Tests;

using System.Text;
using Jarvis.Sharp.Security;
using Jarvis.Sharp.State;
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

        Assert.Throws<InvalidOperationException>(() => limiter.Check("U_TEST", now.AddSeconds(3)));

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
}
