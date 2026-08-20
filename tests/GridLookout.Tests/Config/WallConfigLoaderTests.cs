using System.Security.Cryptography;
using System.Text.Json;
using GridLookout.Config;
using GridLookout.Logging;
using Xunit;

namespace GridLookout.Tests.Config;

public class WallConfigLoaderTests : IDisposable
{
    private readonly string _dir;

    public WallConfigLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "GridLookout.Tests." + Guid.NewGuid());
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

    private static void WriteJson(string path, string json) => File.WriteAllText(path, json);

    [Fact]
    public void LoadOrCreate_MissingFile_ReturnsDefaultConfig()
    {
        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.ManagementServerUri);
        Assert.Equal(AuthMode.Basic, config.AuthMode);
        Assert.Equal(15, config.ReconnectSeconds);
        Assert.Equal(60, config.ConfigRefreshSeconds);
        Assert.Equal(GridLookout.Logging.LogLevel.Info, config.LogLevel);
        Assert.Equal(30, config.LogRetentionDays);
        Assert.False(config.ShowHeader);
        Assert.Equal(1, config.TileBorderWidth);
        Assert.Equal("#404040", config.TileBorderColor);
        Assert.Equal(10, config.StaleSeconds);
        Assert.True(config.FitFrameSizeToTile);
        Assert.Equal(12, config.MaxFps);
        Assert.Equal(0, config.PageSeconds);
        Assert.Equal(9, config.PageSize);
        Assert.Equal(10, config.TileRotateSeconds);
        Assert.Equal("Fit", config.TileScaleMode);
        Assert.True(config.KeepDisplayAwake);
        Assert.False(config.KioskLock);
        Assert.Null(config.WindowBounds);
        Assert.Single(config.Monitors);
        Assert.Empty(config.CameraBindings); // F3: no CameraBindings key at all -> empty dict, not null
    }

    [Fact]
    public void LoadOrCreate_LogLevel_ParsesFromString()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "LogLevel": "Warning" }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(GridLookout.Logging.LogLevel.Warning, config.LogLevel);
    }

    [Fact]
    public void LoadOrCreate_ShowHeader_True_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "ShowHeader": true }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.True(config.ShowHeader);
    }

    [Fact]
    public void LoadOrCreate_KioskLock_True_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "KioskLock": true }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.True(config.KioskLock);
    }

    [Fact]
    public void LoadOrCreate_CameraBindings_RoundTripsRawAliasGuidMap()
    {
        // F3: WallConfigLoader itself does no validation of CameraBindings — it just deserializes
        // the raw section; Layout.CameraBindingResolver (exercised separately, in
        // CameraBindingResolverTests) is what validates alias format and guid parseability. This
        // test only guards that the raw JSON shape round-trips through LoadOrCreate correctly.
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var guid = Guid.NewGuid();
        WriteJson(primaryPath, $$"""{ "CameraBindings": { "front-gate": "{{guid}}" } }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Single(config.CameraBindings);
        Assert.Equal(guid.ToString(), config.CameraBindings["front-gate"]);
    }

    [Fact]
    public void LoadOrCreate_TileBorder_ExplicitValues_RoundTrip()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "TileBorderWidth": 3, "TileBorderColor": "#FF8800" }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(3, config.TileBorderWidth);
        Assert.Equal("#FF8800", config.TileBorderColor);
    }

    [Fact]
    public void LoadOrCreate_StaleSecondsAndKeepDisplayAwake_ExplicitValues_RoundTrip()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "StaleSeconds": 5, "KeepDisplayAwake": false }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(5, config.StaleSeconds);
        Assert.False(config.KeepDisplayAwake);
    }

    [Fact]
    public void LoadOrCreate_StaleSeconds_Zero_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "StaleSeconds": 0 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(0, config.StaleSeconds);
    }

    [Fact]
    public void LoadOrCreate_FitFrameSizeToTileAndMaxFps_ExplicitValues_RoundTrip()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "FitFrameSizeToTile": false, "MaxFps": 24 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.False(config.FitFrameSizeToTile);
        Assert.Equal(24, config.MaxFps);
    }

    [Fact]
    public void LoadOrCreate_MaxFps_Zero_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "MaxFps": 0 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(0, config.MaxFps);
    }

    [Fact]
    public void LoadOrCreate_PageSecondsAndPageSize_ExplicitValues_RoundTrip()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "PageSeconds": 20, "PageSize": 4 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(20, config.PageSeconds);
        Assert.Equal(4, config.PageSize);
    }

    [Fact]
    public void LoadOrCreate_TileRotateSeconds_ExplicitValue_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "TileRotateSeconds": 20 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(20, config.TileRotateSeconds);
    }

    [Fact]
    public void LoadOrCreate_TileScaleMode_ExplicitValue_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "TileScaleMode": "Fill" }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal("Fill", config.TileScaleMode);
    }

    [Fact]
    public void LoadOrCreate_WindowBounds_NestedObject_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """
        {
          "WindowBounds": { "X": 100, "Y": 50, "Width": 1920, "Height": 1080 }
        }
        """);

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.NotNull(config.WindowBounds);
        Assert.Equal(100, config.WindowBounds!.X);
        Assert.Equal(50, config.WindowBounds.Y);
        Assert.Equal(1920, config.WindowBounds.Width);
        Assert.Equal(1080, config.WindowBounds.Height);
    }

    [Fact]
    public void LoadOrCreate_WindowBounds_AbsentFromFile_IsNull()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "ReconnectSeconds": 20 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Null(config.WindowBounds);
    }

    [Fact]
    public void LoadOrCreate_RoundTripsAllFields()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """
        {
          "ManagementServerUri": "http://vms-mgmt.example.local",
          "AuthMode": "Windows",
          "Username": "svc-camerawall",
          "Domain": "EXAMPLE",
          "Password": "",
          "PasswordProtected": "",
          "AllowInsecureBasic": true,
          "AllowInsecureLayoutPoll": true,
          "LayoutRecorder": "REC-02",
          "RecorderNameOverride": "REC-01",
          "ReconnectSeconds": 30,
          "ConfigRefreshSeconds": 120,
          "ShowHeader": true,
          "TileBorderWidth": 2,
          "TileBorderColor": "#112233",
          "StaleSeconds": 20,
          "FitFrameSizeToTile": false,
          "MaxFps": 24,
          "PageSeconds": 15,
          "PageSize": 6,
          "TileRotateSeconds": 8,
          "TileScaleMode": "Stretch",
          "KeepDisplayAwake": false,
          "KioskLock": true,
          "WindowBounds": { "X": 10, "Y": 20, "Width": 800, "Height": 600 },
          "Monitors": [
            { "Monitor": 1, "Cameras": "1-4" },
            { "Monitor": 2, "Cameras": "5,6,7" }
          ]
        }
        """);

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal("http://vms-mgmt.example.local", config.ManagementServerUri);
        Assert.Equal(AuthMode.Windows, config.AuthMode);
        Assert.Equal("svc-camerawall", config.Username);
        Assert.Equal("EXAMPLE", config.Domain);
        Assert.True(config.AllowInsecureBasic);
        Assert.True(config.AllowInsecureLayoutPoll);
        Assert.Equal("REC-02", config.LayoutRecorder);
        Assert.Equal("REC-01", config.RecorderNameOverride);
        Assert.Equal(30, config.ReconnectSeconds);
        Assert.Equal(120, config.ConfigRefreshSeconds);
        Assert.True(config.ShowHeader);
        Assert.Equal(2, config.TileBorderWidth);
        Assert.Equal("#112233", config.TileBorderColor);
        Assert.Equal(20, config.StaleSeconds);
        Assert.False(config.FitFrameSizeToTile);
        Assert.Equal(24, config.MaxFps);
        Assert.Equal(15, config.PageSeconds);
        Assert.Equal(6, config.PageSize);
        Assert.Equal(8, config.TileRotateSeconds);
        Assert.Equal("Stretch", config.TileScaleMode);
        Assert.False(config.KeepDisplayAwake);
        Assert.True(config.KioskLock);
        Assert.NotNull(config.WindowBounds);
        Assert.Equal(10, config.WindowBounds!.X);
        Assert.Equal(20, config.WindowBounds.Y);
        Assert.Equal(800, config.WindowBounds.Width);
        Assert.Equal(600, config.WindowBounds.Height);
        Assert.Equal(2, config.Monitors.Count);
        Assert.Equal("1-4", config.Monitors[0].Cameras);
        Assert.Equal(2, config.Monitors[1].Monitor);
    }

    [Fact]
    public void LoadOrCreate_LocalOverlay_WinsOverPrimaryForPresentKeys()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var overlayPath = Path.Combine(_dir, "camerawall.local.json");

        WriteJson(primaryPath, """
        {
          "ManagementServerUri": "",
          "AuthMode": "Windows",
          "ReconnectSeconds": 15
        }
        """);
        // Overlay sets only ManagementServerUri and AuthMode — ReconnectSeconds must survive
        // from the primary file untouched.
        WriteJson(overlayPath, """
        {
          "ManagementServerUri": "http://dev-mgmt.local",
          "AuthMode": "Basic"
        }
        """);

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir);

        Assert.Equal("http://dev-mgmt.local", config.ManagementServerUri);
        Assert.Equal(AuthMode.Basic, config.AuthMode);
        Assert.Equal(15, config.ReconnectSeconds);
    }

    [Fact]
    public void LoadOrCreate_MissingOverlayFile_DoesNotThrow_UsesPrimaryOnly()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "ReconnectSeconds": 42 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir);

        Assert.Equal(42, config.ReconnectSeconds);
    }

    [Fact]
    public void LoadOrCreate_PlaintextPassword_IsMigratedToProtectedAndBlanked()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """
        {
          "AuthMode": "Basic",
          "Username": "operator",
          "Password": "hunter2"
        }
        """);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.Password);
        Assert.NotEmpty(config.PasswordProtected);
        Assert.Equal(1, protector.ProtectCallCount);
        Assert.Equal("hunter2", protector.Unprotect(config.PasswordProtected));

        // Self-healing: the file on disk is rewritten so a second load never re-migrates.
        var rewritten = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(primaryPath));
        Assert.Equal(string.Empty, rewritten.GetProperty("Password").GetString());
    }

    [Fact]
    public void LoadOrCreate_PasswordInOverlayOnly_RewritesOverlay_PrimaryUntouched()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var overlayPath = Path.Combine(_dir, "camerawall.local.json");

        var primaryOriginal = """
        {
          "ManagementServerUri": "",
          "AuthMode": "Windows",
          "ReconnectSeconds": 15
        }
        """;
        WriteJson(primaryPath, primaryOriginal);
        WriteJson(overlayPath, """
        {
          "ManagementServerUri": "http://dev-mgmt.local",
          "AuthMode": "Basic",
          "Username": "devuser",
          "Password": "dev-secret"
        }
        """);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadOrCreate(_dir);

        Assert.Equal(string.Empty, config.Password);
        Assert.NotEmpty(config.PasswordProtected);

        // Overlay was rewritten: Password blanked, PasswordProtected set, its OTHER keys kept.
        var overlayJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(overlayPath));
        Assert.Equal(string.Empty, overlayJson.GetProperty("Password").GetString());
        Assert.NotEmpty(overlayJson.GetProperty("PasswordProtected").GetString()!);
        Assert.Equal("http://dev-mgmt.local", overlayJson.GetProperty("ManagementServerUri").GetString());
        Assert.Equal("devuser", overlayJson.GetProperty("Username").GetString());

        // Primary file on disk is completely untouched (byte-for-byte).
        Assert.Equal(primaryOriginal, File.ReadAllText(primaryPath));
    }

    [Fact]
    public void LoadOrCreate_PasswordInPrimary_OverlayHasDifferentUri_PrimaryRewrittenWithoutOverlayUri()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var overlayPath = Path.Combine(_dir, "camerawall.local.json");

        WriteJson(primaryPath, """
        {
          "ManagementServerUri": "http://prod-mgmt.local",
          "AuthMode": "Basic",
          "Username": "produser",
          "Password": "prod-secret"
        }
        """);
        var overlayOriginal = """
        {
          "ManagementServerUri": "http://dev-mgmt.local"
        }
        """;
        WriteJson(overlayPath, overlayOriginal);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadOrCreate(_dir);

        // Effective in-memory config still reflects the merge (overlay URI wins for THIS run).
        Assert.Equal("http://dev-mgmt.local", config.ManagementServerUri);

        // Primary file on disk was rewritten (Password migrated) but MUST NOT have picked up the
        // overlay's ManagementServerUri — only its own Password/PasswordProtected keys change.
        var primaryJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(primaryPath));
        Assert.Equal(string.Empty, primaryJson.GetProperty("Password").GetString());
        Assert.NotEmpty(primaryJson.GetProperty("PasswordProtected").GetString()!);
        Assert.Equal("http://prod-mgmt.local", primaryJson.GetProperty("ManagementServerUri").GetString());
        Assert.Equal("produser", primaryJson.GetProperty("Username").GetString());

        // Overlay file untouched.
        Assert.Equal(overlayOriginal, File.ReadAllText(overlayPath));
    }

    [Fact]
    public void LoadOrCreate_NoPlaintextPassword_DoesNotInvokeProtector()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "PasswordProtected": "already-protected-blob" }""");

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(0, protector.ProtectCallCount);
    }

    [Fact]
    public void GetPassword_PrefersProtectedOverPlaintext()
    {
        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = new WallConfig
        {
            Password = "should-not-be-used",
            PasswordProtected = protector.Protect("actual-secret"),
        };

        Assert.Equal("actual-secret", loader.GetPassword(config));
    }

    [Fact]
    public void GetPassword_FallsBackToPlaintext_WhenNoProtectedValue()
    {
        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = new WallConfig { Password = "still-plaintext" };

        Assert.Equal("still-plaintext", loader.GetPassword(config));
    }

    [Fact]
    public void LoadOrCreate_ExplicitStateDirectory_WritableExeDir_BehavesIdenticallyToDefault()
    {
        // T1/B4: passing a StateDirectory explicitly must not change behavior when the exe dir is
        // writable (the overwhelmingly common case) — the whole state-dir mechanism is meant to be
        // a no-op here.
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """
        {
          "AuthMode": "Basic",
          "Username": "operator",
          "Password": "hunter2"
        }
        """);

        var protector = new FakeSecretProtector();
        var stateDirectory = new StateDirectory(Path.Combine(_dir, "unused-programdata-override"));
        var loader = new WallConfigLoader(protector, stateDirectory);
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.Password);
        Assert.NotEmpty(config.PasswordProtected);
        // The migration rewrite went to the exe dir itself, exactly as without a StateDirectory —
        // the ProgramData override was never touched (no state-dir camerawall.json exists).
        Assert.False(File.Exists(Path.Combine(_dir, "unused-programdata-override", "camerawall.json")));
        var rewritten = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(primaryPath));
        Assert.Equal(string.Empty, rewritten.GetProperty("Password").GetString());
    }

    [Fact]
    public void LoadOrCreate_UnwritableExeDir_StateDirCopyExists_MergesFromStateDir_DoesNotReMigrate()
    {
        // T1/B4: simulates a config previously migrated into the ProgramData fallback on an
        // earlier run (see StateDirectoryTests for why exe dir "unwritable" is simulated as a FILE
        // rather than a real ACL-denied directory — same CanWrite() catch-all either way). On this
        // run, the state-dir copy alone (already PasswordProtected, no plaintext Password) must be
        // found and used without invoking the protector again.
        var fakeExeDir = Path.Combine(_dir, "not-actually-a-directory.tmp");
        File.WriteAllText(fakeExeDir, string.Empty);

        var programDataOverride = Path.Combine(_dir, "programdata-override");
        Directory.CreateDirectory(programDataOverride);
        var stateCopyPath = Path.Combine(programDataOverride, "camerawall.json");
        WriteJson(stateCopyPath, """
        {
          "ManagementServerUri": "http://prod-mgmt.local",
          "AuthMode": "Basic",
          "Username": "operator",
          "Password": "",
          "PasswordProtected": "already-migrated-blob"
        }
        """);

        var protector = new FakeSecretProtector();
        var stateDirectory = new StateDirectory(programDataOverride);
        var loader = new WallConfigLoader(protector, stateDirectory);
        var config = loader.LoadOrCreate(fakeExeDir, overlayFileName: null);

        Assert.Equal("http://prod-mgmt.local", config.ManagementServerUri);
        Assert.Equal(string.Empty, config.Password);
        Assert.Equal("already-migrated-blob", config.PasswordProtected);
        Assert.Equal(0, protector.ProtectCallCount);
    }

    [Fact]
    public void DpapiSecretProtector_RealRoundTrip_CurrentUserScope()
    {
        // Machine-local, not mocked — validates the actual DPAPI call shape compiles and works
        // under the CurrentUser account running this test suite (tests run on Windows).
        var protector = new DpapiSecretProtector();
        var protectedValue = protector.Protect("real-dpapi-secret");

        Assert.NotEqual("real-dpapi-secret", protectedValue);
        Assert.Equal("real-dpapi-secret", protector.Unprotect(protectedValue));
    }

    // --- T3/R3: snapshot-shadowing fix ---
    //
    // FakeStateDirectory (not the real StateDirectory) is used throughout this section so
    // "exeDirWritable == false" can be held true while the exe dir itself stays a completely
    // normal, real, writable temp directory — required so the test can freely create
    // camerawall.json and control its last-write time. See FakeStateDirectory's doc comment.

    [Fact]
    public void LoadOrCreate_ExeDirNewerAndConfigured_ReseedsStateDirAndRemigratesPasswordInOneShot()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");

        WriteJson(statePrimaryPath, """
        {
          "ManagementServerUri": "http://old-mgmt.local",
          "AuthMode": "Basic",
          "Username": "old-operator",
          "Password": "",
          "PasswordProtected": "stale-blob"
        }
        """);
        File.SetLastWriteTimeUtc(statePrimaryPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        WriteJson(primaryPath, """
        {
          "ManagementServerUri": "http://new-mgmt.local",
          "AuthMode": "Basic",
          "Username": "new-operator",
          "Password": "new-secret"
        }
        """);
        File.SetLastWriteTimeUtc(primaryPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector, new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Equal("http://new-mgmt.local", config.ManagementServerUri);
        Assert.Equal("new-operator", config.Username);
        Assert.Equal(string.Empty, config.Password);
        Assert.Equal("new-secret", protector.Unprotect(config.PasswordProtected));

        // State-dir copy was reseeded AND migrated in one shot — no intermediate plaintext write.
        var stateJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(statePrimaryPath));
        Assert.Equal("http://new-mgmt.local", stateJson.GetProperty("ManagementServerUri").GetString());
        Assert.Equal(string.Empty, stateJson.GetProperty("Password").GetString());
        Assert.NotEmpty(stateJson.GetProperty("PasswordProtected").GetString()!);
    }

    [Fact]
    public void LoadOrCreate_ExeDirNewerAndConfigured_NoPlaintextPassword_ReseedsStateDirVerbatim()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");

        WriteJson(statePrimaryPath, """{ "ManagementServerUri": "http://old-mgmt.local" }""");
        File.SetLastWriteTimeUtc(statePrimaryPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        WriteJson(primaryPath, """{ "ManagementServerUri": "http://new-mgmt.local", "ReconnectSeconds": 42 }""");
        File.SetLastWriteTimeUtc(primaryPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector, new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Equal("http://new-mgmt.local", config.ManagementServerUri);
        Assert.Equal(42, config.ReconnectSeconds);
        Assert.Equal(0, protector.ProtectCallCount); // nothing to migrate — no plaintext Password anywhere

        var stateJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(statePrimaryPath));
        Assert.Equal("http://new-mgmt.local", stateJson.GetProperty("ManagementServerUri").GetString());
    }

    [Fact]
    public void LoadOrCreate_ExeDirNewerAndConfigured_NoPasswordFieldsAtAll_PreservesExistingStateDirBlob()
    {
        // Regression test for a reviewer-caught bug: an admin who follows T4(a)'s warning and
        // manually removes the stuck plaintext Password from the exe-dir file bumps its mtime past
        // the state-dir copy's — which must NOT let the reseed silently overwrite the state-dir
        // copy's WORKING PasswordProtected blob with nothing (the exe-dir file has neither Password
        // nor PasswordProtected at all, since the app never writes either field there while the exe
        // dir is unwritable).
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");

        WriteJson(statePrimaryPath, """
        {
          "ManagementServerUri": "http://working-kiosk-mgmt.local",
          "Username": "kiosk-operator",
          "Password": "",
          "PasswordProtected": "working-blob"
        }
        """);
        File.SetLastWriteTimeUtc(statePrimaryPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Admin edit: removed the Password key entirely (per T4(a)'s instruction) — no Password,
        // no PasswordProtected, ManagementServerUri unchanged. Newer mtime than the state-dir copy.
        WriteJson(primaryPath, """
        {
          "ManagementServerUri": "http://working-kiosk-mgmt.local",
          "Username": "kiosk-operator"
        }
        """);
        File.SetLastWriteTimeUtc(primaryPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector, new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        // The working credential MUST survive the reseed.
        Assert.Equal("working-blob", config.PasswordProtected);
        Assert.Equal(0, protector.ProtectCallCount); // no plaintext anywhere — no migration ran

        var stateJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(statePrimaryPath));
        Assert.Equal("working-blob", stateJson.GetProperty("PasswordProtected").GetString());
        Assert.Equal("http://working-kiosk-mgmt.local", stateJson.GetProperty("ManagementServerUri").GetString());
    }

    [Fact]
    public void LoadOrCreate_ExeDirNewerButBlankTemplate_KeepsExistingStateDirConfig()
    {
        // Simulates a fresh MSI upgrade laying down the blank cleared-placeholder template over a
        // working kiosk's install dir — the state-dir copy (the kiosk's real, working config) must
        // survive untouched, not be shadowed by the newer-but-empty exe-dir file.
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");

        WriteJson(statePrimaryPath, """
        {
          "ManagementServerUri": "http://working-kiosk-mgmt.local",
          "AuthMode": "Basic",
          "Username": "kiosk-operator",
          "PasswordProtected": "already-migrated-blob"
        }
        """);
        File.SetLastWriteTimeUtc(statePrimaryPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var blankTemplateOriginal = """
        {
          "ManagementServerUri": "",
          "AuthMode": "Basic"
        }
        """;
        WriteJson(primaryPath, blankTemplateOriginal);
        File.SetLastWriteTimeUtc(primaryPath, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector, new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Equal("http://working-kiosk-mgmt.local", config.ManagementServerUri);
        Assert.Equal("kiosk-operator", config.Username);
        Assert.Equal(0, protector.ProtectCallCount);

        var stateJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(statePrimaryPath));
        Assert.Equal("http://working-kiosk-mgmt.local", stateJson.GetProperty("ManagementServerUri").GetString());

        // Exe-dir blank template itself is untouched too.
        Assert.Equal(blankTemplateOriginal, File.ReadAllText(primaryPath));
    }

    [Fact]
    public void LoadOrCreate_StateDirNewerThanExeDir_StateWins()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");

        WriteJson(primaryPath, """{ "ManagementServerUri": "http://exe-dir-template.local" }""");
        File.SetLastWriteTimeUtc(primaryPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        WriteJson(statePrimaryPath, """{ "ManagementServerUri": "http://state-dir-current.local" }""");
        File.SetLastWriteTimeUtc(statePrimaryPath, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Equal("http://state-dir-current.local", config.ManagementServerUri);
    }

    [Fact]
    public void LoadOrCreate_ExeDirAndStateDirEqualTimestamps_StateWins()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");
        var sameInstant = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        WriteJson(primaryPath, """{ "ManagementServerUri": "http://exe-dir-template.local" }""");
        File.SetLastWriteTimeUtc(primaryPath, sameInstant);

        WriteJson(statePrimaryPath, """{ "ManagementServerUri": "http://state-dir-current.local" }""");
        File.SetLastWriteTimeUtc(statePrimaryPath, sameInstant);

        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Equal("http://state-dir-current.local", config.ManagementServerUri);
    }

    // --- T4(a)/R4: plaintext Password that can never be blanked ---

    [Fact]
    public void LoadOrCreate_UnwritableExeDir_PlaintextPasswordInPrimary_LogsWarningEveryStart()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        WriteJson(primaryPath, """{ "AuthMode": "Basic", "Username": "op", "Password": "stuck-secret" }""");

        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath),
            (level, msg) => logs.Add((level, msg)));
        loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Contains(logs, l => l.level == LogLevel.Warning && l.message.Contains(primaryPath));

        // A second "start" (fresh loader instance, same on-disk files) still warns — the exe-dir
        // file's own plaintext was never (and can never be) blanked in place.
        logs.Clear();
        var loader2 = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath),
            (level, msg) => logs.Add((level, msg)));
        loader2.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Contains(logs, l => l.level == LogLevel.Warning && l.message.Contains(primaryPath));
    }

    [Fact]
    public void LoadOrCreate_WritableExeDir_PlaintextPasswordInPrimary_DoesNotLogUnblankableWarning()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "AuthMode": "Basic", "Username": "op", "Password": "gets-blanked-fine" }""");

        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new FakeSecretProtector(), stateDirectory: null,
            log: (level, msg) => logs.Add((level, msg)));
        loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.DoesNotContain(logs, l => l.level == LogLevel.Warning && l.message.Contains("cannot be auto-blanked"));
    }

    // --- T4(b)/R4: DPAPI wedge (protected blob from a different Windows account) ---

    [Fact]
    public void GetPassword_CryptographicException_LogsClearErrorAndRethrows()
    {
        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new CryptographicExceptionSecretProtector(), stateDirectory: null,
            log: (level, msg) => logs.Add((level, msg)));
        var config = new WallConfig { PasswordProtected = "some-blob-from-a-different-account" };

        var exception = Assert.Throws<CryptographicException>(() => loader.GetPassword(config));

        Assert.Contains("Key not valid", exception.Message);
        Assert.Contains(logs, l => l.level == LogLevel.Error
            && l.message.Contains("DIFFERENT Windows account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPassword_CryptographicException_ErrorMessageNamesTheEffectiveConfigPath()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "PasswordProtected": "wedge-blob" }""");

        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new CryptographicExceptionSecretProtector(), stateDirectory: null,
            log: (level, msg) => logs.Add((level, msg)));
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Throws<CryptographicException>(() => loader.GetPassword(config));

        Assert.Contains(logs, l => l.level == LogLevel.Error && l.message.Contains(primaryPath));
    }

    // --- Round-3 panel-3 T5: corrupt (non-base64) PasswordProtected blob ---

    [Fact]
    public void GetPassword_FormatException_LogsCorruptBlobErrorAndRethrows()
    {
        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new FormatExceptionSecretProtector(), stateDirectory: null,
            log: (level, msg) => logs.Add((level, msg)));
        var config = new WallConfig { PasswordProtected = "not-valid-base64!!!" };

        var exception = Assert.Throws<FormatException>(() => loader.GetPassword(config));

        Assert.Contains("Base-64", exception.Message);
        Assert.Contains(logs, l => l.level == LogLevel.Error
            && l.message.Contains("corrupt", StringComparison.OrdinalIgnoreCase)
            && l.message.Contains("not valid base64", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPassword_FormatException_ErrorMessageNamesTheEffectiveConfigPath()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "PasswordProtected": "not-valid-base64!!!" }""");

        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new FormatExceptionSecretProtector(), stateDirectory: null,
            log: (level, msg) => logs.Add((level, msg)));
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Throws<FormatException>(() => loader.GetPassword(config));

        Assert.Contains(logs, l => l.level == LogLevel.Error && l.message.Contains(primaryPath));
    }

    // --- DPAPI-unavailable session (2026-08-19 live incident): Protect throws machine-wide ---

    [Fact]
    public void LoadOrCreate_ProtectThrows_RunsWithPlaintextForThisSession_FileUntouched_LogsWarning()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var originalText = """{ "ManagementServerUri": "http://vms.example.local", "Password": "hunter2xx" }""";
        WriteJson(primaryPath, originalText);

        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new ProtectFailsSecretProtector(), stateDirectory: null,
            log: (level, msg) => logs.Add((level, msg)));

        // Must NOT throw — a broken-DPAPI session degrades, never bricks (north-star self-heal).
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        // In-memory: plaintext kept for this run, nothing pretends to be protected.
        Assert.Equal("hunter2xx", config.Password);
        Assert.True(string.IsNullOrEmpty(config.PasswordProtected));
        Assert.Equal("hunter2xx", loader.GetPassword(config));

        // On disk: byte-identical — no blanking without a stored protected value.
        Assert.Equal(originalText, File.ReadAllText(primaryPath));

        Assert.Contains(logs, l => l.level == LogLevel.Warning
            && l.message.Contains("DPAPI is unavailable", StringComparison.OrdinalIgnoreCase)
            && l.message.Contains("plaintext", StringComparison.OrdinalIgnoreCase));
    }

    // --- T5/R6: overlay plaintext password + unwritable exe dir ---

    [Fact]
    public void LoadOrCreate_UnwritableExeDir_PlaintextPasswordInOverlay_SkipsStateDirOverlayCopy_LogsWarning()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        var overlayPath = Path.Combine(exeDir, "camerawall.local.json");
        WriteJson(primaryPath, """{ "ManagementServerUri": "http://prod.local" }""");
        WriteJson(overlayPath, """{ "Username": "devuser", "Password": "dev-secret" }""");

        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath),
            (level, msg) => logs.Add((level, msg)));
        var config = loader.LoadOrCreate(exeDir);

        // In-memory config for THIS run still gets the protected value.
        Assert.NotEmpty(config.PasswordProtected);

        // No overlay copy written into the state dir — nothing would ever read it back.
        Assert.False(File.Exists(Path.Combine(stateDirPath, "camerawall.local.json")));

        Assert.Contains(logs, l => l.level == LogLevel.Warning && l.message.Contains(overlayPath));

        // The real overlay file on disk keeps its plaintext (never touched).
        Assert.Contains("dev-secret", File.ReadAllText(overlayPath));
    }

    [Fact]
    public void LoadOrCreate_WritableExeDir_PlaintextPasswordInOverlay_StillRewritesOverlayInPlace()
    {
        // Unchanged pre-T5 behavior for the common (writable exe dir) case — no regression.
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var overlayPath = Path.Combine(_dir, "camerawall.local.json");
        WriteJson(primaryPath, """{ "ManagementServerUri": "http://prod.local" }""");
        WriteJson(overlayPath, """{ "Username": "devuser", "Password": "dev-secret" }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        loader.LoadOrCreate(_dir);

        var overlayJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(overlayPath));
        Assert.Equal(string.Empty, overlayJson.GetProperty("Password").GetString());
        Assert.NotEmpty(overlayJson.GetProperty("PasswordProtected").GetString()!);
    }

    // --- EffectiveConfigPath ---

    [Fact]
    public void EffectiveConfigPath_WritableExeDir_IsThePrimaryPath()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "ManagementServerUri": "http://prod.local" }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(primaryPath, loader.EffectiveConfigPath);
    }

    [Fact]
    public void EffectiveConfigPath_UnwritableExeDir_IsTheStateDirCopy()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        WriteJson(primaryPath, """{ "ManagementServerUri": "http://prod.local" }""");

        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath));
        loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Equal(Path.Combine(stateDirPath, "camerawall.json"), loader.EffectiveConfigPath);
    }

    // --- T6: first-run seeding of camerawall.json from camerawall.template.json ---
    //
    // The MSI no longer ships camerawall.json, only camerawall.template.json (see
    // Product.GridLookout.wxs's CAMERAWALL.JSON OWNERSHIP comment) — LoadOrCreate seeds a real
    // camerawall.json from it on first run, copying the template's raw TEXT (comments intact,
    // never round-tripped through JSON parse/reserialize) rather than writing anything itself.

    private const string TemplateJsonWithComments = """
    {
      "ManagementServerUri": "",               // fill this in
      "AuthMode": "Basic",
      "Username": "",
      "Password": "",
      "PasswordProtected": ""
    }
    """;

    [Fact]
    public void LoadOrCreate_MissingPrimaryWithTemplate_WritableExeDir_SeedsExeDirFromTemplate()
    {
        var templatePath = Path.Combine(_dir, "camerawall.template.json");
        WriteJson(templatePath, TemplateJsonWithComments);
        var primaryPath = Path.Combine(_dir, "camerawall.json");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.True(File.Exists(primaryPath));
        Assert.Equal(string.Empty, config.ManagementServerUri); // still "not configured" — unchanged
        Assert.Equal(AuthMode.Basic, config.AuthMode);
        Assert.Equal(primaryPath, loader.EffectiveConfigPath);
    }

    [Fact]
    public void LoadOrCreate_MissingPrimaryWithTemplate_UnwritableExeDir_SeedsStateDir()
    {
        var exeDir = _dir;
        var templatePath = Path.Combine(exeDir, "camerawall.template.json");
        WriteJson(templatePath, TemplateJsonWithComments);
        var stateDirPath = Path.Combine(_dir, "programdata");
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");

        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.False(File.Exists(Path.Combine(exeDir, "camerawall.json"))); // exe dir itself untouched
        Assert.True(File.Exists(statePrimaryPath));
        Assert.Equal(string.Empty, config.ManagementServerUri);
        Assert.Equal(statePrimaryPath, loader.EffectiveConfigPath);
    }

    [Fact]
    public void LoadOrCreate_NoTemplateAndNoPrimary_ReturnsDefaultConfig_SeedsNothing()
    {
        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.ManagementServerUri);
        Assert.False(File.Exists(Path.Combine(_dir, "camerawall.json")));
    }

    [Fact]
    public void LoadOrCreate_ExistingPrimary_NeverOverwrittenEvenWithTemplatePresent()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        const string configuredJson = """{ "ManagementServerUri": "http://already-configured.local" }""";
        WriteJson(primaryPath, configuredJson);
        WriteJson(Path.Combine(_dir, "camerawall.template.json"), TemplateJsonWithComments);

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal("http://already-configured.local", config.ManagementServerUri);
        Assert.Equal(configuredJson, File.ReadAllText(primaryPath)); // byte-for-byte untouched
    }

    [Fact]
    public void LoadOrCreate_MissingPrimaryButStateDirCopyExists_UnwritableExeDir_DoesNotOverwriteStateDirCopy()
    {
        // Edge case distinct from the "existing primary" test above: the EXE-DIR file is missing
        // (so the outer seeding trigger fires) but the STATE-DIR copy — a different file — already
        // holds a real, working kiosk configuration. Seeding must not clobber it.
        var exeDir = _dir;
        WriteJson(Path.Combine(exeDir, "camerawall.template.json"), TemplateJsonWithComments);
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");
        const string workingKioskJson = """{ "ManagementServerUri": "http://working-kiosk.local" }""";
        WriteJson(statePrimaryPath, workingKioskJson);

        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Equal("http://working-kiosk.local", config.ManagementServerUri);
        Assert.Equal(workingKioskJson, File.ReadAllText(statePrimaryPath));
    }

    [Fact]
    public void LoadOrCreate_MissingPrimaryWithTemplate_SeededFileCommentsPreserved()
    {
        var templatePath = Path.Combine(_dir, "camerawall.template.json");
        WriteJson(templatePath, TemplateJsonWithComments);
        var primaryPath = Path.Combine(_dir, "camerawall.json");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        loader.LoadOrCreate(_dir, overlayFileName: null);

        // Raw text comparison, not a JSON parse — the whole point is that comments survive, and
        // ParseObject's JsonCommentHandling.Skip would silently hide a regression that dropped them.
        Assert.Equal(File.ReadAllText(templatePath), File.ReadAllText(primaryPath));
        Assert.Contains("// fill this in", File.ReadAllText(primaryPath));
    }

    [Fact]
    public void LoadOrCreate_MissingPrimaryWithTemplate_LogsInfoLineNamingSourceAndDestination()
    {
        var templatePath = Path.Combine(_dir, "camerawall.template.json");
        WriteJson(templatePath, TemplateJsonWithComments);
        var primaryPath = Path.Combine(_dir, "camerawall.json");

        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new FakeSecretProtector(), log: (level, msg) => logs.Add((level, msg)));
        loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Contains(logs, l => l.level == LogLevel.Info
            && l.message.Contains(templatePath)
            && l.message.Contains(primaryPath));
    }

    // --- T3: comment-preserving password migration (targeted raw-text splice) ---

    [Fact]
    public void LoadOrCreate_PlaintextPassword_TargetedRewrite_PreservesCommentsAndOtherContent_ReplacesOnlyPasswordFields()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var originalJson =
            "{\n" +
            "  \"ManagementServerUri\": \"http://vms-mgmt.example.local\", // primary MS\n" +
            "  \"AuthMode\": \"Basic\",\n" +
            "  \"Username\": \"operator\",\n" +
            "  \"Password\": \"hunter2\",             // fill in plaintext on first run; encrypted on next start\n" +
            "  \"PasswordProtected\": \"\",\n" +
            "  \"ReconnectSeconds\": 15\n" +
            "}\n";
        WriteJson(primaryPath, originalJson);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.Password);
        Assert.Equal("hunter2", protector.Unprotect(config.PasswordProtected));

        var expected =
            "{\n" +
            "  \"ManagementServerUri\": \"http://vms-mgmt.example.local\", // primary MS\n" +
            "  \"AuthMode\": \"Basic\",\n" +
            "  \"Username\": \"operator\",\n" +
            "  \"Password\": \"\",             // fill in plaintext on first run; encrypted on next start\n" +
            $"  \"PasswordProtected\": \"{config.PasswordProtected}\",\n" +
            "  \"ReconnectSeconds\": 15\n" +
            "}\n";
        Assert.Equal(expected, File.ReadAllText(primaryPath));
    }

    [Fact]
    public void LoadOrCreate_PasswordInOverlay_TargetedRewrite_PreservesOverlayComments()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var overlayPath = Path.Combine(_dir, "camerawall.local.json");
        WriteJson(primaryPath, """{ "ManagementServerUri": "http://prod.local" }""");
        var overlayOriginal =
            "{\n" +
            "  \"Username\": \"devuser\", // dev account\n" +
            "  \"Password\": \"dev-secret\",\n" +
            "  \"PasswordProtected\": \"\"\n" +
            "}\n";
        WriteJson(overlayPath, overlayOriginal);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadOrCreate(_dir);

        var expectedOverlay =
            "{\n" +
            "  \"Username\": \"devuser\", // dev account\n" +
            "  \"Password\": \"\",\n" +
            $"  \"PasswordProtected\": \"{config.PasswordProtected}\"\n" +
            "}\n";
        Assert.Equal(expectedOverlay, File.ReadAllText(overlayPath));
    }

    [Fact]
    public void LoadOrCreate_PlaintextPassword_MissingPasswordProtectedKey_FallsBackToReserialize_StillAddsKey()
    {
        // No anchor for a targeted PasswordProtected splice (the key isn't in the raw text at all)
        // — must fall back to the old reserialize path rather than fail outright; that path still
        // correctly ADDS the missing key (just without preserving whatever comments were present).
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """
        {
          "AuthMode": "Basic",
          "Username": "operator",
          "Password": "hunter2"
        }
        """);

        var logs = new List<(LogLevel level, string message)>();
        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector, stateDirectory: null, log: (level, msg) => logs.Add((level, msg)));
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.Password);
        Assert.NotEmpty(config.PasswordProtected);
        Assert.Contains(logs, l => l.level == LogLevel.Warning && l.message.Contains("falling back to a full reserialize"));

        var rewritten = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(primaryPath));
        Assert.Equal(string.Empty, rewritten.GetProperty("Password").GetString());
        Assert.Equal(config.PasswordProtected, rewritten.GetProperty("PasswordProtected").GetString());
    }

    [Fact]
    public void LoadOrCreate_PlaintextPassword_ExistingPasswordProtectedHasEscapedQuote_BailsToReserialize_LogsWarning()
    {
        // PasswordProtected already holds a value containing an escaped quote — not a shape a real
        // DPAPI blob (base64) or FakeSecretProtector's own output can ever produce, but the splice
        // must not corrupt the file if some other value ever ends up there; it must bail instead.
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var originalJson =
            "{\n" +
            "  \"AuthMode\": \"Basic\",\n" +
            "  \"Username\": \"operator\",\n" +
            "  \"Password\": \"hunter2\",\n" +
            "  \"PasswordProtected\": \"weird\\\"value\"\n" +
            "}\n";
        WriteJson(primaryPath, originalJson);

        var logs = new List<(LogLevel level, string message)>();
        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector, stateDirectory: null, log: (level, msg) => logs.Add((level, msg)));
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.Password);
        Assert.Equal("hunter2", protector.Unprotect(config.PasswordProtected));
        Assert.Contains(logs, l => l.level == LogLevel.Warning
            && l.message.Contains("falling back to a full reserialize")
            && l.message.Contains("backslash-escaped"));

        // The fallback reserialize still correctly blanks/protects the fields — just without
        // comment/formatting preservation for this file.
        var rewritten = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(primaryPath));
        Assert.Equal(string.Empty, rewritten.GetProperty("Password").GetString());
        Assert.Equal(config.PasswordProtected, rewritten.GetProperty("PasswordProtected").GetString());
    }

    [Fact]
    public void LoadOrCreate_PlaintextPassword_MissingPrimaryFileAtWriteTarget_FallsBackToReserialize()
    {
        // The migration write TARGET (a fresh state-dir copy) doesn't exist yet — there is no raw
        // text at that path to splice against at all, so this falls back to reserialize. The
        // seed source (exe-dir file) is a completely separate path from the write target here.
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        WriteJson(primaryPath, """{ "AuthMode": "Basic", "Username": "op", "Password": "stuck-secret" }""");

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector, new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Equal("stuck-secret", protector.Unprotect(config.PasswordProtected));
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");
        Assert.True(File.Exists(statePrimaryPath));
        var stateJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(statePrimaryPath));
        Assert.Equal(config.PasswordProtected, stateJson.GetProperty("PasswordProtected").GetString());
    }

    // --- TileRecoverSeconds (per-tile self-heal) ---

    [Fact]
    public void LoadOrCreate_TileRecoverSeconds_DefaultsTo30()
    {
        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(30, config.TileRecoverSeconds);
    }

    [Fact]
    public void LoadOrCreate_TileRecoverSeconds_ExplicitValue_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "TileRecoverSeconds": 45 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(45, config.TileRecoverSeconds);
    }

    [Fact]
    public void LoadOrCreate_TileRecoverSeconds_Zero_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "TileRecoverSeconds": 0 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(0, config.TileRecoverSeconds);
    }

    // --- Health section (wall-health monitoring) ---

    [Fact]
    public void LoadOrCreate_Health_AbsentFromFile_UsesTypeDefaults()
    {
        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.False(config.Health.Enabled);
        Assert.Equal(string.Empty, config.Health.Endpoint);
        Assert.Equal(30, config.Health.StaleAfterSeconds);
        Assert.Equal(5, config.Health.TimeoutSeconds);
        Assert.Equal(string.Empty, config.Health.BearerToken);
        Assert.Equal(string.Empty, config.Health.BearerTokenProtected);
    }

    [Fact]
    public void LoadOrCreate_Health_PartialSection_UnspecifiedFieldsKeepTypeDefaults()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "Health": { "Enabled": true } }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.True(config.Health.Enabled);
        Assert.Equal(30, config.Health.StaleAfterSeconds); // untouched field keeps its own default
        Assert.Equal(5, config.Health.TimeoutSeconds);
    }

    [Fact]
    public void LoadOrCreate_Health_FullSection_RoundTrips()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """
        {
          "Health": {
            "Enabled": true,
            "ControllerId": "wall-07",
            "Endpoint": "https://collector.example.com/health",
            "StaleAfterSeconds": 45,
            "TimeoutSeconds": 8
          }
        }
        """);

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.True(config.Health.Enabled);
        Assert.Equal("wall-07", config.Health.ControllerId);
        Assert.Equal("https://collector.example.com/health", config.Health.Endpoint);
        Assert.Equal(45, config.Health.StaleAfterSeconds);
        Assert.Equal(8, config.Health.TimeoutSeconds);
    }

    // --- Health.BearerToken -> Health.BearerTokenProtected migration (mirrors Password) ---

    [Fact]
    public void LoadOrCreate_PlaintextBearerToken_IsMigratedToProtectedAndBlanked()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """
        {
          "Health": { "Enabled": true, "Endpoint": "https://collector.example.com", "BearerToken": "top-secret-token" }
        }
        """);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.Health.BearerToken);
        Assert.NotEmpty(config.Health.BearerTokenProtected);
        Assert.Equal("top-secret-token", protector.Unprotect(config.Health.BearerTokenProtected));

        // Self-healing: the file on disk is rewritten so a second load never re-migrates.
        var rewritten = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(primaryPath));
        Assert.Equal(string.Empty, rewritten.GetProperty("Health").GetProperty("BearerToken").GetString());
        Assert.Equal(config.Health.BearerTokenProtected, rewritten.GetProperty("Health").GetProperty("BearerTokenProtected").GetString());
    }

    [Fact]
    public void LoadOrCreate_PlaintextBearerToken_TargetedRewrite_PreservesCommentsAndOtherContent()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var originalJson =
            "{\n" +
            "  \"ManagementServerUri\": \"http://vms-mgmt.example.local\",\n" +
            "  \"Health\": {\n" +
            "    \"Enabled\": true,             // health monitoring on\n" +
            "    \"BearerToken\": \"top-secret-token\",\n" +
            "    \"BearerTokenProtected\": \"\"\n" +
            "  }\n" +
            "}\n";
        WriteJson(primaryPath, originalJson);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal("top-secret-token", protector.Unprotect(config.Health.BearerTokenProtected));

        var expected =
            "{\n" +
            "  \"ManagementServerUri\": \"http://vms-mgmt.example.local\",\n" +
            "  \"Health\": {\n" +
            "    \"Enabled\": true,             // health monitoring on\n" +
            "    \"BearerToken\": \"\",\n" +
            $"    \"BearerTokenProtected\": \"{config.Health.BearerTokenProtected}\"\n" +
            "  }\n" +
            "}\n";
        Assert.Equal(expected, File.ReadAllText(primaryPath));
    }

    [Fact]
    public void LoadOrCreate_PlaintextBearerToken_MissingBearerTokenProtectedKey_FallsBackToReserialize_StillAddsKey()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "Health": { "Enabled": true, "BearerToken": "top-secret-token" } }""");

        var logs = new List<(LogLevel level, string message)>();
        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector, stateDirectory: null, log: (level, msg) => logs.Add((level, msg)));
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.Health.BearerToken);
        Assert.NotEmpty(config.Health.BearerTokenProtected);
        Assert.Contains(logs, l => l.level == LogLevel.Warning && l.message.Contains("falling back to a full reserialize"));

        var rewritten = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(primaryPath));
        Assert.Equal(string.Empty, rewritten.GetProperty("Health").GetProperty("BearerToken").GetString());
        Assert.Equal(config.Health.BearerTokenProtected, rewritten.GetProperty("Health").GetProperty("BearerTokenProtected").GetString());
    }

    [Fact]
    public void LoadOrCreate_BearerTokenInOverlayOnly_RewritesOverlay_PrimaryUntouched()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var overlayPath = Path.Combine(_dir, "camerawall.local.json");
        var primaryOriginal = """{ "ManagementServerUri": "" }""";
        WriteJson(primaryPath, primaryOriginal);
        WriteJson(overlayPath, """{ "Health": { "Enabled": true, "BearerToken": "dev-token" } }""");

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadOrCreate(_dir);

        Assert.Equal(string.Empty, config.Health.BearerToken);
        Assert.NotEmpty(config.Health.BearerTokenProtected);

        var overlayJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(overlayPath));
        Assert.Equal(string.Empty, overlayJson.GetProperty("Health").GetProperty("BearerToken").GetString());
        Assert.NotEmpty(overlayJson.GetProperty("Health").GetProperty("BearerTokenProtected").GetString()!);

        // Primary file on disk is completely untouched.
        Assert.Equal(primaryOriginal, File.ReadAllText(primaryPath));
    }

    [Fact]
    public void LoadOrCreate_NoPlaintextBearerToken_DoesNotInvokeProtectorForBearerToken()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "Health": { "BearerTokenProtected": "already-protected-blob" } }""");

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(0, protector.ProtectCallCount);
    }

    [Fact]
    public void LoadOrCreate_UnwritableExeDir_PlaintextBearerTokenInPrimary_LogsWarningEveryStart()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        WriteJson(primaryPath, """{ "Health": { "BearerToken": "stuck-token" } }""");

        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath),
            (level, msg) => logs.Add((level, msg)));
        loader.LoadOrCreate(exeDir, overlayFileName: null);

        Assert.Contains(logs, l => l.level == LogLevel.Warning && l.message.Contains("Health.BearerToken") && l.message.Contains(primaryPath));
    }

    [Fact]
    public void LoadOrCreate_EffectiveConfigPath_SetEvenWhenBearerTokenDpapiUnavailable()
    {
        // Regression guard: EffectiveConfigPath must be set BEFORE the migration blocks run so a
        // DPAPI-unavailable early-return from either Password's or BearerToken's block never
        // leaves it null.
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "Health": { "BearerToken": "stuck-token" } }""");

        var loader = new WallConfigLoader(new ProtectFailsSecretProtector());
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal("stuck-token", config.Health.BearerToken); // degraded — left plaintext for this session
        Assert.Equal(primaryPath, loader.EffectiveConfigPath);
    }

    // --- GetBearerToken (mirrors GetPassword) ---

    [Fact]
    public void GetBearerToken_PrefersProtectedOverPlaintext()
    {
        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var health = new HealthConfig
        {
            BearerToken = "should-not-be-used",
            BearerTokenProtected = protector.Protect("actual-token"),
        };

        Assert.Equal("actual-token", loader.GetBearerToken(health));
    }

    [Fact]
    public void GetBearerToken_FallsBackToPlaintext_WhenNoProtectedValue()
    {
        var loader = new WallConfigLoader(new FakeSecretProtector());
        var health = new HealthConfig { BearerToken = "still-plaintext" };

        Assert.Equal("still-plaintext", loader.GetBearerToken(health));
    }

    [Fact]
    public void GetBearerToken_NoTokenConfiguredAtAll_ReturnsEmpty()
    {
        var loader = new WallConfigLoader(new FakeSecretProtector());
        Assert.Equal(string.Empty, loader.GetBearerToken(new HealthConfig()));
    }

    [Fact]
    public void GetBearerToken_CryptographicException_LogsClearErrorAndRethrows()
    {
        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new CryptographicExceptionSecretProtector(), stateDirectory: null,
            log: (level, msg) => logs.Add((level, msg)));
        var health = new HealthConfig { BearerTokenProtected = "some-blob-from-a-different-account" };

        var exception = Assert.Throws<CryptographicException>(() => loader.GetBearerToken(health));

        Assert.Contains("Key not valid", exception.Message);
        Assert.Contains(logs, l => l.level == LogLevel.Error
            && l.message.Contains("BearerTokenProtected")
            && l.message.Contains("DIFFERENT Windows account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetBearerToken_FormatException_LogsCorruptBlobErrorAndRethrows()
    {
        var logs = new List<(LogLevel level, string message)>();
        var loader = new WallConfigLoader(new FormatExceptionSecretProtector(), stateDirectory: null,
            log: (level, msg) => logs.Add((level, msg)));
        var health = new HealthConfig { BearerTokenProtected = "not-valid-base64!!!" };

        var exception = Assert.Throws<FormatException>(() => loader.GetBearerToken(health));

        Assert.Contains("Base-64", exception.Message);
        Assert.Contains(logs, l => l.level == LogLevel.Error
            && l.message.Contains("BearerTokenProtected")
            && l.message.Contains("corrupt", StringComparison.OrdinalIgnoreCase));
    }

    // --- LoadReadOnly (--health-probe mode: no seeding, no migration, no writes) ---

    [Fact]
    public void LoadReadOnly_PlaintextPassword_NeverMigrated_FileUntouched()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var originalText = """{ "AuthMode": "Basic", "Username": "op", "Password": "hunter2" }""";
        WriteJson(primaryPath, originalText);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadReadOnly(_dir, overlayFileName: null);

        // In-memory: the plaintext is read back as-is — LoadReadOnly never protects it.
        Assert.Equal("hunter2", config.Password);
        Assert.Equal(string.Empty, config.PasswordProtected);
        Assert.Equal(0, protector.ProtectCallCount);

        // On disk: byte-identical — no migration write of any kind.
        Assert.Equal(originalText, File.ReadAllText(primaryPath));
    }

    [Fact]
    public void LoadReadOnly_PlaintextBearerToken_NeverMigrated_FileUntouched()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var originalText = """{ "Health": { "Enabled": true, "BearerToken": "top-secret" } }""";
        WriteJson(primaryPath, originalText);

        var protector = new FakeSecretProtector();
        var loader = new WallConfigLoader(protector);
        var config = loader.LoadReadOnly(_dir, overlayFileName: null);

        Assert.Equal("top-secret", config.Health.BearerToken);
        Assert.Equal(0, protector.ProtectCallCount);
        Assert.Equal(originalText, File.ReadAllText(primaryPath));
    }

    [Fact]
    public void LoadReadOnly_MissingPrimaryWithTemplate_DoesNotSeedAnything()
    {
        var templatePath = Path.Combine(_dir, "camerawall.template.json");
        WriteJson(templatePath, """{ "ManagementServerUri": "" }""");
        var primaryPath = Path.Combine(_dir, "camerawall.json");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadReadOnly(_dir, overlayFileName: null);

        Assert.False(File.Exists(primaryPath)); // LoadOrCreate would have seeded this; LoadReadOnly must not
        Assert.Equal(string.Empty, config.ManagementServerUri);
    }

    [Fact]
    public void LoadReadOnly_UnwritableExeDir_MergesStateDirCopy_NoWritesAnywhere()
    {
        var exeDir = _dir;
        var stateDirPath = Path.Combine(_dir, "programdata");
        Directory.CreateDirectory(stateDirPath);
        var primaryPath = Path.Combine(exeDir, "camerawall.json");
        var statePrimaryPath = Path.Combine(stateDirPath, "camerawall.json");
        WriteJson(primaryPath, """{ "ManagementServerUri": "http://exe-dir.local" }""");
        WriteJson(statePrimaryPath, """{ "ManagementServerUri": "http://state-dir.local", "Health": { "Enabled": true } }""");

        var loader = new WallConfigLoader(new FakeSecretProtector(), new FakeStateDirectory(writable: false, stateDirPath));
        var config = loader.LoadReadOnly(exeDir, overlayFileName: null);

        Assert.Equal("http://state-dir.local", config.ManagementServerUri); // state-dir wins, same precedence as LoadOrCreate
        Assert.True(config.Health.Enabled);

        // Nothing was written anywhere by this call.
        var primaryFilesInExeDir = Directory.GetFiles(exeDir, "*.json");
        Assert.Single(primaryFilesInExeDir); // only the one we wrote ourselves above
    }

    [Fact]
    public void LoadReadOnly_LocalOverlay_WinsOverPrimaryForPresentKeys()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        var overlayPath = Path.Combine(_dir, "camerawall.local.json");
        WriteJson(primaryPath, """{ "ManagementServerUri": "", "ReconnectSeconds": 15 }""");
        WriteJson(overlayPath, """{ "ManagementServerUri": "http://dev-mgmt.local" }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadReadOnly(_dir);

        Assert.Equal("http://dev-mgmt.local", config.ManagementServerUri);
        Assert.Equal(15, config.ReconnectSeconds);
    }

    [Fact]
    public void LoadReadOnly_MissingFile_ReturnsDefaultConfig()
    {
        var loader = new WallConfigLoader(new FakeSecretProtector());
        var config = loader.LoadReadOnly(_dir, overlayFileName: null);

        Assert.Equal(string.Empty, config.ManagementServerUri);
        Assert.False(config.Health.Enabled);
        Assert.Equal(30, config.TileRecoverSeconds);
    }

    // --- M1 (2026-08-21 config-robustness review): one mistyped VALUE never bricks the load —
    // the bad field falls back to its default with a Warning naming it; every healthy field
    // still loads. Pre-fix, each of these threw out of LoadOrCreate and put a kiosk in the
    // config-error-card loop. ---

    private (WallConfigLoader Loader, List<string> Warnings) LoaderWithWarningCapture()
    {
        var warnings = new List<string>();
        var loader = new WallConfigLoader(new FakeSecretProtector(), log: (level, msg) =>
        {
            if (level == GridLookout.Logging.LogLevel.Warning)
            {
                warnings.Add(msg);
            }
        });
        return (loader, warnings);
    }

    [Fact]
    public void LoadOrCreate_MistypedInt_FallsBackToDefault_KeepsEveryOtherField_WarnsNamingIt()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "ManagementServerUri": "http://vms-mgmt.example.local", "MaxFps": "twelve", "StaleSeconds": 20 }""");

        var (loader, warnings) = LoaderWithWarningCapture();
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(12, config.MaxFps); // built-in default, not the garbage
        Assert.Equal("http://vms-mgmt.example.local", config.ManagementServerUri); // healthy fields survive
        Assert.Equal(20, config.StaleSeconds);
        Assert.Contains(warnings, w => w.Contains("MaxFps"));
    }

    [Fact]
    public void LoadOrCreate_UnknownEnumValue_FallsBackToDefault_InsteadOfThrowing()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "AuthMode": "Negotiate", "Username": "svc-wall" }""");

        var (loader, warnings) = LoaderWithWarningCapture();
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(AuthMode.Basic, config.AuthMode);
        Assert.Equal("svc-wall", config.Username);
        Assert.Contains(warnings, w => w.Contains("AuthMode"));
    }

    [Fact]
    public void LoadOrCreate_MistypedNestedHealthField_DropsOnlyThatField_NotTheWholeSection()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "Health": { "Enabled": "yes", "Endpoint": "https://collector.example.local/h", "TimeoutSeconds": 9 } }""");

        var (loader, warnings) = LoaderWithWarningCapture();
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.False(config.Health.Enabled); // the one bad field -> its default
        Assert.Equal("https://collector.example.local/h", config.Health.Endpoint); // siblings survive
        Assert.Equal(9, config.Health.TimeoutSeconds);
        Assert.Contains(warnings, w => w.Contains("Health.Enabled"));
    }

    [Fact]
    public void LoadOrCreate_MistypedMonitorsElement_DropsOnlyThatElement()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "Monitors": [ { "Monitor": 1, "Cameras": "1-6" }, { "Monitor": "two", "Cameras": "7-12" } ] }""");

        var (loader, warnings) = LoaderWithWarningCapture();
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        var kept = Assert.Single(config.Monitors);
        Assert.Equal(1, kept.Monitor);
        Assert.Equal("1-6", kept.Cameras);
        Assert.Contains(warnings, w => w.Contains("Monitors[1]"));
    }

    [Fact]
    public void LoadOrCreate_HealthyFile_ProducesNoMistypedValueWarning()
    {
        var primaryPath = Path.Combine(_dir, "camerawall.json");
        WriteJson(primaryPath, """{ "MaxFps": 8, "AuthMode": "Windows", "Health": { "Enabled": true } }""");

        var (loader, warnings) = LoaderWithWarningCapture();
        var config = loader.LoadOrCreate(_dir, overlayFileName: null);

        Assert.Equal(8, config.MaxFps);
        Assert.Equal(AuthMode.Windows, config.AuthMode);
        Assert.True(config.Health.Enabled);
        Assert.DoesNotContain(warnings, w => w.Contains("wrong type"));
    }

    // --- M2 (same review): config writes are atomic (temp + File.Replace) and a transient
    // filesystem failure during credential migration degrades to a logged warning + in-memory
    // value instead of throwing out of LoadOrCreate. ---

    [Fact]
    public void Save_OverwritesExistingFileAtomically_NoTempFileLeftBehind()
    {
        var path = Path.Combine(_dir, "camerawall.json");
        WriteJson(path, """{ "MaxFps": 1 }""");

        var loader = new WallConfigLoader(new FakeSecretProtector());
        loader.Save(new WallConfig { MaxFps = 7 }, path);

        var reloadOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        reloadOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var reloaded = JsonSerializer.Deserialize<WallConfig>(File.ReadAllText(path), reloadOptions);
        Assert.Equal(7, reloaded!.MaxFps);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void LoadOrCreate_PasswordMigrationWriteBlocked_DegradesWithWarning_InsteadOfThrowing()
    {
        // A read-only config file makes the migration's write fail with UnauthorizedAccessException
        // — pre-fix that escaped LoadOrCreate into the config-error card; now the session runs on
        // the in-memory (already-protected) value and the file is left untouched for a retry.
        var path = Path.Combine(_dir, "camerawall.json");
        WriteJson(path, """{ "Username": "svc-wall", "Password": "hunter2", "PasswordProtected": "" }""");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var (loader, warnings) = LoaderWithWarningCapture();
            var config = loader.LoadOrCreate(_dir, overlayFileName: null);

            Assert.Equal("svc-wall", config.Username);
            Assert.NotEqual(string.Empty, config.PasswordProtected); // in-memory migration still happened
            Assert.Contains(warnings, w => w.Contains("retried on the next start"));
            Assert.Contains("hunter2", File.ReadAllText(path)); // file genuinely untouched
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
