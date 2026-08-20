using GridLookout.Layout;
using Xunit;

namespace GridLookout.Tests.Layout;

public class CameraBindingResolverTests
{
    [Fact]
    public void Resolve_ValidEntry_ParsesAliasAndGuid()
    {
        var guid = Guid.NewGuid();
        var raw = new Dictionary<string, string> { ["front-gate"] = guid.ToString() };

        var result = CameraBindingResolver.Resolve(raw, warn: null);

        Assert.Equal(guid, result["front-gate"]);
    }

    [Fact]
    public void Resolve_AliasLookup_IsCaseInsensitive()
    {
        var guid = Guid.NewGuid();
        var raw = new Dictionary<string, string> { ["Front-Gate"] = guid.ToString() };

        var result = CameraBindingResolver.Resolve(raw, warn: null);

        Assert.Equal(guid, result["front-gate"]);
        Assert.Equal(guid, result["FRONT-GATE"]);
    }

    [Fact]
    public void Resolve_NullOrEmptyInput_ReturnsEmptyMap()
    {
        Assert.Empty(CameraBindingResolver.Resolve(null, warn: null));
        Assert.Empty(CameraBindingResolver.Resolve(new Dictionary<string, string>(), warn: null));
    }

    [Fact]
    public void Resolve_UnparseableGuid_EntryIgnored_WarningRaised()
    {
        var raw = new Dictionary<string, string> { ["front-gate"] = "not-a-guid" };
        var warnings = new List<string>();

        var result = CameraBindingResolver.Resolve(raw, warnings.Add);

        Assert.Empty(result);
        Assert.Single(warnings);
        Assert.Contains("front-gate", warnings[0]);
    }

    [Theory]
    [InlineData("front_gate")]   // underscore not in [a-z0-9-]
    [InlineData("front gate")]   // space
    [InlineData("front.gate")]   // dot
    [InlineData("")]
    public void Resolve_InvalidAliasFormat_EntryIgnored_WarningRaised(string badAlias)
    {
        var raw = new Dictionary<string, string> { [badAlias] = Guid.NewGuid().ToString() };
        var warnings = new List<string>();

        var result = CameraBindingResolver.Resolve(raw, warnings.Add);

        Assert.Empty(result);
        Assert.Single(warnings);
    }

    [Fact]
    public void Resolve_AliasFormat_AllowsDigitsAndHyphens()
    {
        var guid = Guid.NewGuid();
        var raw = new Dictionary<string, string> { ["cam-02-east"] = guid.ToString() };

        var result = CameraBindingResolver.Resolve(raw, warn: null);

        Assert.Equal(guid, result["cam-02-east"]);
    }

    [Fact]
    public void Resolve_DuplicateAliasCaseInsensitive_FirstWins_SecondWarned()
    {
        // "front-gate" and "Front-Gate" are two distinct keys in a plain (case-SENSITIVE)
        // Dictionary<string,string> — exactly what a hand-edited camerawall.json could carry — but
        // they collide once Resolve applies its case-insensitive alias rule.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var raw = new Dictionary<string, string>
        {
            ["front-gate"] = first.ToString(),
            ["Front-Gate"] = second.ToString(),
        };
        var warnings = new List<string>();

        var result = CameraBindingResolver.Resolve(raw, warnings.Add);

        Assert.Single(result);
        Assert.Equal(first, result["front-gate"]);
        Assert.Single(warnings);
        Assert.Contains("duplicate", warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_OneBadEntryAmongGoodOnes_OnlyTheBadOneIsDropped()
    {
        var good = Guid.NewGuid();
        var raw = new Dictionary<string, string>
        {
            ["front-gate"] = good.ToString(),
            ["broken"] = "not-a-guid",
        };
        var warnings = new List<string>();

        var result = CameraBindingResolver.Resolve(raw, warnings.Add);

        Assert.Single(result);
        Assert.Equal(good, result["front-gate"]);
        Assert.Single(warnings);
    }
}
