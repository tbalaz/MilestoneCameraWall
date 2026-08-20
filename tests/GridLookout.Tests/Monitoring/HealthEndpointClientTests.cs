using GridLookout.Config;
using GridLookout.Monitoring;
using Xunit;

namespace GridLookout.Tests.Monitoring;

/// <summary>
/// Round-5 buyer-review fix: the HTTPS-or-explicit-opt-in gate on the optional health POST — the
/// health-endpoint mirror of <c>MilestoneSession.IsLayoutPollAllowed</c>'s rule for the layout
/// poll. Only the refusal/gate behavior is unit-tested here (it is pure — no socket is ever
/// opened); an actually-successful POST needs a live listener and stays out of the unit surface,
/// same as every other network call in this suite.
/// </summary>
public class HealthEndpointClientTests
{
    private static HealthConfig Health(string endpoint, bool allowInsecure = false) => new()
    {
        Enabled = true,
        Endpoint = endpoint,
        AllowInsecureEndpoint = allowInsecure,
        TimeoutSeconds = 1,
    };

    [Fact]
    public void PostSync_HttpEndpoint_WithoutOptIn_IsRefusedBeforeAnyNetworkIO()
    {
        var (succeeded, error) = HealthEndpointClient.PostSync(Health("http://collector.example.local/health"), "token", "{}");

        Assert.False(succeeded);
        Assert.Contains("refused", error);
        Assert.Contains("AllowInsecureEndpoint", error);
    }

    [Fact]
    public void PostSync_HttpEndpoint_WithOptIn_PassesTheGate_FailureIsNetworkNotRefusal()
    {
        // Loopback port 1: nothing listens there, so the connect fails fast — the point is that
        // with the opt-in set, the failure is a NETWORK error, proving the gate let it through.
        var (succeeded, error) = HealthEndpointClient.PostSync(Health("http://127.0.0.1:1/health", allowInsecure: true), "token", "{}");

        Assert.False(succeeded);
        Assert.DoesNotContain("refused", error);
    }

    [Fact]
    public void PostSync_HttpsEndpoint_PassesTheGateWithoutOptIn_FailureIsNetworkNotRefusal()
    {
        var (succeeded, error) = HealthEndpointClient.PostSync(Health("https://127.0.0.1:1/health"), "token", "{}");

        Assert.False(succeeded);
        Assert.DoesNotContain("refused", error);
    }

    [Fact]
    public void PostSync_MalformedEndpoint_ReportsFailureInsteadOfThrowing()
    {
        var (succeeded, error) = HealthEndpointClient.PostSync(Health("not a uri"), null, "{}");

        Assert.False(succeeded);
        Assert.NotNull(error);
    }
}
