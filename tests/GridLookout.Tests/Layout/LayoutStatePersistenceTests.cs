using System.Text.Json;
using GridLookout.Layout;
using GridLookout.Monitoring;
using GridLookout.Tests.Config;
using Xunit;

namespace GridLookout.Tests.Layout;

/// <summary>
/// Round-trips <see cref="LayoutStateFile"/> through the SAME <c>Monitoring.AtomicStateStore</c>
/// mechanism <c>health.json</c> already uses, and through <see cref="LayoutJsonOptions"/> directly —
/// covers F3 point 6's "layout-state.json ... in the same state directory as health.json" contract
/// and guards the on-disk shape (nullable <see cref="Guid"/> pins, the monitor-keyed dictionary)
/// against a silent serialization regression.
/// </summary>
public class LayoutStatePersistenceTests : IDisposable
{
    private readonly string _dir;

    public LayoutStatePersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests.LayoutState." + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static LayoutStateFile SampleState()
    {
        var pinnedId = Guid.NewGuid();
        return new LayoutStateFile
        {
            SchemaVersion = 1,
            MonitorFingerprints = new Dictionary<string, string> { ["1"] = "abc123" },
            ResolvedPlan = new Dictionary<string, List<PersistedPage>>
            {
                ["1"] = new List<PersistedPage>
                {
                    new PersistedPage
                    {
                        Rows = new List<PersistedRow>
                        {
                            new PersistedRow
                            {
                                Cells = new List<PersistedCell>
                                {
                                    new PersistedCell
                                    {
                                        Members = new List<PersistedMember>
                                        {
                                            new PersistedMember { RefKind = "Ordinal", RefLabel = "1", CameraId = pinnedId },
                                            new PersistedMember { RefKind = "Alias", RefLabel = "broken", CameraId = null },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    [Fact]
    public void JsonRoundTrip_PreservesPinnedAndUnpinnedMembers()
    {
        var original = SampleState();

        var json = JsonSerializer.Serialize(original, LayoutJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<LayoutStateFile>(json, LayoutJsonOptions.Default);

        Assert.NotNull(restored);
        Assert.Equal(original.SchemaVersion, restored!.SchemaVersion);
        Assert.Equal(original.MonitorFingerprints["1"], restored.MonitorFingerprints["1"]);

        var restoredMembers = restored.ResolvedPlan["1"][0].Rows[0].Cells[0].Members;
        Assert.Equal(2, restoredMembers.Count);
        Assert.Equal("Ordinal", restoredMembers[0].RefKind);
        Assert.Equal("1", restoredMembers[0].RefLabel);
        Assert.Equal(original.ResolvedPlan["1"][0].Rows[0].Cells[0].Members[0].CameraId, restoredMembers[0].CameraId);
        Assert.Null(restoredMembers[1].CameraId); // never-pinned member stays null, not e.g. Guid.Empty
    }

    [Fact]
    public void AtomicStateStore_WriteThenRead_RoundTripsLayoutState()
    {
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, _dir), _dir);
        var original = SampleState();

        store.Write("layout-state.json", JsonSerializer.Serialize(original, LayoutJsonOptions.Default));

        var raw = store.Read("layout-state.json");
        Assert.NotNull(raw);
        var restored = JsonSerializer.Deserialize<LayoutStateFile>(raw!, LayoutJsonOptions.Default);

        Assert.NotNull(restored);
        Assert.Equal(original.MonitorFingerprints["1"], restored!.MonitorFingerprints["1"]);
        Assert.True(restored.ResolvedPlan.ContainsKey("1"));
    }

    [Fact]
    public void AtomicStateStore_MissingFile_ReadsAsNull_TreatedAsColdStartByCallers()
    {
        var store = new AtomicStateStore(new FakeStateDirectory(writable: true, _dir), _dir);

        Assert.Null(store.Read("layout-state.json"));
    }

    [Fact]
    public void JsonRoundTrip_EmptyResolvedPlan_Survives()
    {
        // F3's "persist even when the plan is empty" fix (see LayoutResolverTests'
        // TokensRemovedThenReAdded regression test) relies on an empty ResolvedPlan being a
        // perfectly normal, round-trippable state — not a special/omitted case. MonitorFingerprints
        // (buyer-review defects #4/#5/#7 fix) is symmetric: an empty dictionary is the correct
        // "no monitor currently has a token" reading, not a special/omitted case either.
        var state = new LayoutStateFile();

        var json = JsonSerializer.Serialize(state, LayoutJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<LayoutStateFile>(json, LayoutJsonOptions.Default);

        Assert.NotNull(restored);
        Assert.Empty(restored!.ResolvedPlan);
        Assert.Empty(restored.MonitorFingerprints);
    }

    // --- F4 (cell spans): PersistedPage.IsUniform/GridColumns, PersistedCell.Col/RowSpan/ColSpan ---

    [Fact]
    public void JsonRoundTrip_SpanFields_Survive()
    {
        var pinnedId = Guid.NewGuid();
        var state = new LayoutStateFile
        {
            SchemaVersion = 1,
            MonitorFingerprints = new Dictionary<string, string> { ["1"] = "span123" },
            ResolvedPlan = new Dictionary<string, List<PersistedPage>>
            {
                ["1"] = new List<PersistedPage>
                {
                    new PersistedPage
                    {
                        IsUniform = true,
                        GridColumns = 2,
                        Rows = new List<PersistedRow>
                        {
                            new PersistedRow
                            {
                                Cells = new List<PersistedCell>
                                {
                                    new PersistedCell
                                    {
                                        Col = 0,
                                        RowSpan = 2,
                                        ColSpan = 1,
                                        Members = new List<PersistedMember>
                                        {
                                            new PersistedMember { RefKind = "Ordinal", RefLabel = "1", CameraId = pinnedId },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(state, LayoutJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<LayoutStateFile>(json, LayoutJsonOptions.Default);

        Assert.NotNull(restored);
        var page = restored!.ResolvedPlan["1"][0];
        Assert.True(page.IsUniform);
        Assert.Equal(2, page.GridColumns);

        var cell = page.Rows[0].Cells[0];
        Assert.Equal(0, cell.Col);
        Assert.Equal(2, cell.RowSpan);
        Assert.Equal(1, cell.ColSpan);
    }

    [Fact]
    public void JsonDeserialize_OlderFileWithoutSpanFields_ReadsBackAsNonUniform_LegacyPathRenders()
    {
        // A layout-state.json written BEFORE F4 has no "IsUniform"/"GridColumns"/"Col"/"RowSpan"/
        // "ColSpan" properties at all — and (buyer-review defects #4/#5/#7 fix) predates
        // MonitorFingerprints too, still carrying the OLD "DescriptionFingerprint" property this
        // fix removed. This is the exact shape from JsonRoundTrip_PreservesPinnedAndUnpinnedMembers's
        // SampleState(), predating both fixes — pinned here as a literal JSON string (not built from
        // the current C# types) so a future accidental rename of these properties can't quietly make
        // this test start passing for the wrong reason. Deserializing it (rather than erroring on the
        // now-unmapped "DescriptionFingerprint" key) IS this test's other half: System.Text.Json
        // silently ignores unmapped properties by default, which is what lets an old file migrate
        // gracefully — see LayoutStateFile.MonitorFingerprints' own doc comment.
        const string oldFileJson = """
        {
          "SchemaVersion": 1,
          "DescriptionFingerprint": "abc123",
          "ResolvedPlan": {
            "1": [
              {
                "Rows": [
                  {
                    "Cells": [
                      {
                        "Members": [
                          { "RefKind": "Ordinal", "RefLabel": "1", "CameraId": "11111111-1111-1111-1111-111111111111" }
                        ]
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        var restored = JsonSerializer.Deserialize<LayoutStateFile>(oldFileJson, LayoutJsonOptions.Default);

        Assert.NotNull(restored);

        // The now-removed "DescriptionFingerprint" key is silently ignored (unmapped property),
        // and MonitorFingerprints — absent from this old file entirely — reads back empty, exactly
        // "no monitor has a per-monitor fingerprint recorded yet," which is what makes
        // LayoutResolver.Resolve treat every monitor in ResolvedPlan as needing one re-resolve.
        Assert.Empty(restored!.MonitorFingerprints);

        var page = restored.ResolvedPlan["1"][0];

        // The property that decides "render through BuildSpanGrid or the legacy BuildGrid path"
        // (WallForm.RenderResolvedPage) defaults to false on a file that predates F4 — exactly
        // "this page never used spans," the correct backward-compatible reading. Defaulting
        // GridColumns to 1 instead of 0 would have been the wrong choice here — see PersistedPage's
        // doc comment for why.
        Assert.False(page.IsUniform);
        Assert.Equal(0, page.GridColumns);

        // Per-cell fields DO default to "spans default to 1" (a plain 1x1 cell) — the rule an older
        // file's cells already satisfied implicitly.
        var cell = page.Rows[0].Cells[0];
        Assert.Equal(0, cell.Col);
        Assert.Equal(1, cell.RowSpan);
        Assert.Equal(1, cell.ColSpan);
    }

    // --- Bug fix: LayoutStateFile.CarriedForwardMonitors ---

    [Fact]
    public void JsonRoundTrip_CarriedForwardMonitors_Survives()
    {
        var state = SampleState();
        state.CarriedForwardMonitors = new List<string> { "2" };

        var json = JsonSerializer.Serialize(state, LayoutJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<LayoutStateFile>(json, LayoutJsonOptions.Default);

        Assert.NotNull(restored);
        Assert.Equal(new[] { "2" }, restored!.CarriedForwardMonitors);
    }

    [Fact]
    public void JsonDeserialize_OlderFileWithoutCarriedForwardMonitors_ReadsBackAsEmpty_NothingMarkedStale()
    {
        // A layout-state.json written before this fix has no "CarriedForwardMonitors" property at
        // all — same backward-read pattern as F4's span fields above (pinned as a literal JSON
        // string, not built from the current C# types, for the same reason). Reading it back as an
        // empty list — "nothing here is a carry-forward" — is the correct interpretation: every
        // entry in an older file's ResolvedPlan is, from this fix's point of view, indistinguishable
        // from a genuine pin, which is exactly what rule 6a already assumed pre-fix.
        const string oldFileJson = """
        {
          "SchemaVersion": 1,
          "DescriptionFingerprint": "abc123",
          "ResolvedPlan": {
            "1": [
              {
                "Rows": [
                  {
                    "Cells": [
                      {
                        "Members": [
                          { "RefKind": "Ordinal", "RefLabel": "1", "CameraId": "11111111-1111-1111-1111-111111111111" }
                        ]
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        var restored = JsonSerializer.Deserialize<LayoutStateFile>(oldFileJson, LayoutJsonOptions.Default);

        Assert.NotNull(restored);
        Assert.Empty(restored!.CarriedForwardMonitors);
        Assert.Empty(restored.MonitorFingerprints); // same graceful-migration reading — see the F4 test above
    }
}
