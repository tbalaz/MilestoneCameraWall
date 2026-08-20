using GridLookout.Layout;
using Xunit;

namespace GridLookout.Tests.Layout;

public class CameraBindingsExporterTests
{
    [Fact]
    public void BuildSkeleton_SimpleName_KebabCases()
    {
        var id = Guid.NewGuid();
        var skeleton = CameraBindingsExporter.BuildSkeleton(new[] { ("Front Gate", id) });

        var entry = Assert.Single(skeleton);
        Assert.Equal("front-gate", entry.Alias);
        Assert.Equal(id, entry.CameraId);
        Assert.Equal("Front Gate", entry.CameraName);
    }

    [Fact]
    public void BuildSkeleton_PunctuationAndMixedCase_CollapsesToAliasCharset()
    {
        var id = Guid.NewGuid();
        var skeleton = CameraBindingsExporter.BuildSkeleton(new[] { ("Cam #02 (East)!!", id) });

        var entry = Assert.Single(skeleton);
        // Every character must be in [a-z0-9-] — the SAME charset LayoutSpecParser's alias grammar
        // accepts, so a generated alias is always pasteable straight into a $layout{} token.
        Assert.Matches("^[a-z0-9-]+$", entry.Alias);
    }

    [Fact]
    public void BuildSkeleton_NameThatKebabifiesToEmpty_FallsBackToGenericAlias()
    {
        var id = Guid.NewGuid();
        var skeleton = CameraBindingsExporter.BuildSkeleton(new[] { ("###", id) });

        var entry = Assert.Single(skeleton);
        Assert.Equal("camera", entry.Alias);
    }

    [Fact]
    public void BuildSkeleton_CollidingNames_GetIncrementingSuffixes()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var skeleton = CameraBindingsExporter.BuildSkeleton(new[]
        {
            ("Front Gate", id1),
            ("Front Gate", id2),
            ("Front Gate", id3),
        });

        Assert.Equal(3, skeleton.Count);
        var aliases = skeleton.Select(s => s.Alias).ToList();
        Assert.Equal(new[] { "front-gate", "front-gate-2", "front-gate-3" }, aliases);
        // Every camera still gets exactly one entry — never silently dropped for colliding.
        Assert.Equal(new HashSet<Guid> { id1, id2, id3 }, skeleton.Select(s => s.CameraId).ToHashSet());
    }

    [Fact]
    public void BuildSkeleton_OrdersByNameThenId_Deterministically()
    {
        var idB = Guid.NewGuid();
        var idA = Guid.NewGuid();
        var skeleton = CameraBindingsExporter.BuildSkeleton(new[]
        {
            ("Zebra Cam", idB),
            ("Alpha Cam", idA),
        });

        Assert.Equal("alpha-cam", skeleton[0].Alias);
        Assert.Equal("zebra-cam", skeleton[1].Alias);
    }

    [Fact]
    public void RenderJson_ProducesPasteableCameraBindingsFragment()
    {
        var id = Guid.NewGuid();
        var skeleton = CameraBindingsExporter.BuildSkeleton(new[] { ("Front Gate", id) });

        var json = CameraBindingsExporter.RenderJson(skeleton);

        Assert.Contains("\"CameraBindings\"", json);
        Assert.Contains($"\"front-gate\": \"{id}\"", json);
        Assert.Contains("Front Gate", json); // the inline // comment naming the camera
    }

    [Fact]
    public void RenderJson_MultipleEntries_LastHasNoTrailingComma()
    {
        var skeleton = CameraBindingsExporter.BuildSkeleton(new[]
        {
            ("Front Gate", Guid.NewGuid()),
            ("Loading Dock", Guid.NewGuid()),
        });

        var json = CameraBindingsExporter.RenderJson(skeleton);
        var lines = json.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // The last binding entry's line must not end its JSON portion with a comma before the //
        // comment — find it and check.
        var lastBindingLine = lines.Last(l => l.Contains("loading-dock"));
        Assert.DoesNotContain("\",  //", lastBindingLine); // comma-then-comment would mean trailing comma
    }

    [Fact]
    public void RenderJson_EmptySkeleton_StillProducesValidBracketStructure()
    {
        var json = CameraBindingsExporter.RenderJson(Array.Empty<(string, Guid, string)>());

        Assert.Contains("\"CameraBindings\": {", json);
        Assert.Contains("}", json);
    }
}
