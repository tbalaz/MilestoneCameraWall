using GridLookout.Milestone;
using Xunit;

namespace GridLookout.Tests.Milestone;

/// <summary>
/// Covers <see cref="MilestoneSession.IsLayoutPollAllowed"/> — the one piece of
/// <see cref="MilestoneSession"/> that's pure/SDK-free and therefore unit-testable; every other
/// member needs a live MIP session (<c>VideoOS.Platform.Login.LoginSettingsCache</c>) and is not
/// covered here, same boundary <c>RecorderCatalogTests</c> documents for
/// <see cref="RecorderCatalog.Discover"/>.
/// </summary>
public class MilestoneSessionTests
{
    // --- IsLayoutPollAllowed: FIX 3-lite — refuse a bearer-token REST call over plain http ---------

    [Fact]
    public void IsLayoutPollAllowed_Https_AllowedRegardlessOfFlag()
    {
        var uri = new Uri("https://vms-mgmt.example.local");

        Assert.True(MilestoneSession.IsLayoutPollAllowed(uri, allowInsecureLayoutPoll: false));
        Assert.True(MilestoneSession.IsLayoutPollAllowed(uri, allowInsecureLayoutPoll: true));
    }

    [Fact]
    public void IsLayoutPollAllowed_Http_FlagFalse_Refused()
    {
        var uri = new Uri("http://vms-mgmt.example.local");

        Assert.False(MilestoneSession.IsLayoutPollAllowed(uri, allowInsecureLayoutPoll: false));
    }

    [Fact]
    public void IsLayoutPollAllowed_Http_FlagTrue_Allowed()
    {
        // The insecure OPT-IN — AllowInsecureLayoutPoll=true is what lets a lab/dev deployment run
        // this poll over plain http anyway, exactly mirroring AllowInsecureBasic's identical gate for
        // AuthMode=Basic login (Auth.CredentialFactory.Build).
        var uri = new Uri("http://vms-mgmt.example.local");

        Assert.True(MilestoneSession.IsLayoutPollAllowed(uri, allowInsecureLayoutPoll: true));
    }
}
