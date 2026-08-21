using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using GridLookout.Config;
using GridLookout.Interop;
using GridLookout.Layout;
using GridLookout.Milestone;
using GridLookout.Monitoring;
// System.Windows.Forms.Control (WallForm's base, via Form) declares a PROTECTED INSTANCE
// PROPERTY named "LayoutEngine" (System.Windows.Forms.Layout.LayoutEngine) — inside an instance
// method of a Control subclass, that inherited member shadows any type/alias of the same simple
// name, so a plain "using ... LayoutEngine = ..." alias silently loses to "this.LayoutEngine" and
// "LayoutEngine.Compute(...)" fails to resolve. Alias to a distinct name instead.
using WallLayoutEngine = GridLookout.Layout.LayoutEngine;

namespace GridLookout.UI;

/// <summary>
/// One borderless fullscreen window per targeted monitor. Hosts a grid of live-video tiles built
/// from <see cref="LiveTileSource"/> (a <c>JPEGLiveSource</c> subclass) feeding a
/// <see cref="PictureBox"/> per tile — <c>SDKImageViewerControl</c> is internal to its own
/// assembly and cannot be constructed externally, hence this fallback.
/// </summary>
public sealed class WallForm : Form
{
    private readonly List<ActiveTile> _activeTiles = new();
    private readonly Label _fallbackWarningLabel;
    private readonly bool _taskbarDefault;

    private readonly bool _showHeader;
    private readonly string _recorderName;
    private readonly TableLayoutPanel? _headerStrip;
    private readonly System.Windows.Forms.Timer? _headerClock;
    private readonly Label? _headerNameLabel;
    private readonly int _tileBorderWidth;
    private readonly Color _tileBorderColor;
    private readonly int _staleSeconds;
    private readonly int _tileRecoverSeconds;
    private readonly bool _fitFrameSizeToTile;
    private readonly int _maxFps;
    private readonly TileScaleModeKind _tileScaleMode;

    /// <summary>Program.cs sets this true for the duration of session-level recovery / config-refresh
    /// rebuild teardown (see Program.cs's <c>RecoverSession</c> and the refresh-tick rebuild block)
    /// and false again once the rebuild completes. While true, <see cref="SweepStaleTiles"/>'s
    /// per-tile self-heal must not schedule a per-tile reconnect — a tile-level
    /// <see cref="LiveTileSource"/> replacement racing a whole-wall teardown/relogin would fight the
    /// session-level rebuild for the same tile slot. Defaults false (per-tile recovery active) —
    /// every prior release's behavior before this flag existed. Purely a data flag; this form never
    /// sets it itself, only reads it.</summary>
    public bool SessionRecoveryInProgress { get; set; }

    // --- Page rotation (auto-layout via RenderPagedAutoLayout, matrix via RenderResolvedLayout) ---
    // One timer field shared by both modes — only one mode is ever active at a time (each public
    // Render* entry point stops/disposes it before possibly starting its own), so there is never a
    // conflict over which mode "owns" it. _currentPageIndex/_totalPages are likewise shared; the
    // per-mode-only state (_pagedCameras/_pageSize for auto-layout, _resolvedPages/_cameraCatalog
    // for matrix) is cleared by StopAndDisposePageTimer so a mode switch never reads stale data
    // left over from the other mode.
    private System.Windows.Forms.Timer? _pageTimer;
    private int _currentPageIndex;
    private int _totalPages;

    private IReadOnlyList<CameraInfo>? _pagedCameras;
    private int _pageSize;

    // F3: the $layout{} matrix path now renders from a pre-resolved, camera-id-based plan instead
    // of raw ordinals + a positional camera list — see RenderResolvedLayout/RenderResolvedPage.
    // _cameraCatalog is the FULL (enabled + disabled) camera-id lookup a resolved cell's Guid
    // resolves against, supplied once per render by the caller (Program.cs) from
    // RecorderMatch.AllCameras.
    private IReadOnlyList<ResolvedPage>? _resolvedPages;
    private IReadOnlyDictionary<Guid, CameraInfo>? _cameraCatalog;

    // F3: cells rendered as the UNAVAILABLE placeholder this grid build — see BuildCell and
    // GetHealthSnapshot's WallFormHealth.UnavailableCount. Reset at the top of every BuildGrid call
    // (alongside DisposeTiles/ClearBody), exactly like _activeTiles is rebuilt from scratch each time.
    private int _unavailableCount;

    private static readonly Color UnavailableBackColor = Color.FromArgb(0x1A, 0x1A, 0x1A);
    private static readonly Color UnavailableForeColor = Color.Gainsboro;

    // --- Per-tile rotation (a matrix cell written A(3,4,5) / A(3,@yard-east,@{guid}) instead of a
    // single fixed reference) ---
    // One shared timer flips every rotating tile on the CURRENT page in lockstep; allocated only
    // when RenderResolvedPage's build actually produced at least one rotating tile, disposed by
    // StopAndDisposeTileRotateTimer — called both from StopAndDisposePageTimer's guard (so it
    // never survives a mode switch away from the matrix path) and from RenderResolvedPage itself
    // (so a page flip never advances rotating tiles whose PictureBoxes that flip just disposed).
    private System.Windows.Forms.Timer? _tileRotateTimer;
    private readonly List<RotatingTile> _rotatingTiles = new();
    private int _tileRotateSecondsConfig;

    // Only allocated when the header clock isn't already ticking every second — see the
    // constructor's stale-sweep wiring below.
    private readonly System.Windows.Forms.Timer? _staleSweepTimer;

    // Backs FreshestTileAgeSeconds() (session-loss detection — see Program.cs's refresh timer /
    // GridLookout.Recovery.SessionLossDetector). T1/R1: constructed with DateTime.UtcNow captured
    // ONCE, here in the constructor (not "on first BuildGrid") — the two are equivalent for every
    // real caller (nothing meaningful happens between a WallForm being constructed and its first
    // Render*/BuildGrid call), and fixing it at construction avoids a null-until-first-build edge
    // case for callers that only ever call ShowStatus/ShowNoCameras (no tiles, so it doesn't matter
    // either way, but "the moment this window exists" is the simpler invariant to reason about).
    // See TileFreshnessTracker's own doc comment for why a fold-on-teardown high-water mark is
    // needed at all: a plain "reset on every BuildGrid" baseline (the pre-T1 design) could never
    // accumulate 60+ seconds of staleness on a rotating wall, since every page/matrix flip reset it.
    // Recovery rebuilds construct BRAND NEW WallForm instances (see Program.cs's BuildWallForms,
    // called fresh by both boot and RecoverSession) — never reused across a recovery — so this
    // baseline naturally resets to "now" after every recovery without this type needing to know
    // anything about recovery itself.
    private readonly TileFreshnessTracker _freshnessTracker = new(DateTime.UtcNow);

    private static readonly Color StaleOverlayBackColor = Color.FromArgb(0xB0, 0x00, 0x00);

    // Shared Font instances: WinForms controls do NOT dispose an assigned Font, so allocating a
    // fresh Font per control per grid rebuild leaks one GDI handle each rebuild. These statics
    // live for the process lifetime and are safely shared across all controls/forms.
    private static readonly Font WarningFont = new("Segoe UI", 12, FontStyle.Bold);
    private static readonly Font HeaderFont = new("Segoe UI", 14, FontStyle.Bold);
    private static readonly Font StatusFont = new("Segoe UI", 20);
    private static readonly Font CaptionFont = new("Segoe UI", 10);
    // "Segoe UI" has no glyph for the rotation marker "⟳" (renders as a tofu box — verified via
    // GetFontUnicodeRanges); "Segoe UI Symbol" covers both that codepoint and plain ASCII, so a
    // rotating tile's whole caption switches font rather than mixing fonts within one Label.
    private static readonly Font RotatingCaptionFont = new("Segoe UI Symbol", 10);
    private static readonly Font OverlayFont = new("Segoe UI", 10, FontStyle.Bold);
    private static readonly Font ErrorFont = new("Segoe UI", 12);

    // --- Double-click compact/fullscreen toggle (see ToggleCompact()) ---
    private readonly Rectangle _normalBounds;
    private readonly Rectangle _alternateBounds;
    private bool _compact;

    // S8: config-driven (WallConfig.KioskLock) — gates both input-handling sites below
    // (ProcessCmdKey's Esc branch and ToggleCompact) rather than skipping the MouseDoubleClick
    // wiring at each of the half-dozen child-control call sites; every one of them already funnels
    // through ToggleCompact(), so a single gate there covers all of them with one field.
    private readonly bool _kioskLock;

    // T1/R1: set only by CloseInternal() — lets this codebase's OWN teardown paths (config-refresh
    // rebuild, RecoverSession, LoginRetryLoop's status-card close-on-success — see Program.cs) close
    // a KioskLock'd form despite OnFormClosing's gate below. Never set any other way.
    private bool _allowClose;

    // T1/R1: throttles the "close refused" Warning to once per minute — an operator repeatedly
    // mashing Alt+F4 (or a script spamming WM_CLOSE) against a locked wall must not flood the log.
    private static readonly TimeSpan CloseRefusalWarningThrottle = TimeSpan.FromMinutes(1);
    private DateTime _lastCloseRefusalWarningUtc = DateTime.MinValue;

    public int MonitorNumber { get; }

    /// <param name="recorderName">Shown in the header strip when <paramref name="showHeader"/> is
    /// true; harmless/unused for status-only forms (connecting/error cards, which pass
    /// <c>showHeader: false</c> and don't know the recorder name yet anyway).</param>
    /// <param name="showHeader">Config-driven (<see cref="GridLookout.Config.WallConfig.ShowHeader"/>).
    /// When false: edge-to-edge video, no strips at all.</param>
    /// <param name="tileBorderWidth">Config-driven (<see cref="GridLookout.Config.WallConfig.TileBorderWidth"/>).
    /// Pixel margin around each tile; 0 means tiles sit edge-to-edge (no border).</param>
    /// <param name="tileBorderColor">Config-driven (<see cref="GridLookout.Config.WallConfig.TileBorderColor"/>),
    /// "#RRGGBB". Parsed via <see cref="ColorTranslator.FromHtml"/>; an invalid value falls back
    /// to <see cref="Color.Black"/> rather than throwing, so a bad config value can never crash
    /// startup.</param>
    /// <param name="staleSeconds">Config-driven (<see cref="GridLookout.Config.WallConfig.StaleSeconds"/>).
    /// Seconds of no new live frame before a tile shows the "STALLED" overlay; 0 disables the
    /// feature (no overlay, no sweep timer allocated).</param>
    /// <param name="windowBoundsOverride">Config-driven (<see cref="GridLookout.Config.WallConfig.WindowBounds"/>),
    /// resolved by the caller for the first/default monitor only. Null (default) keeps the normal
    /// fullscreen-on-<paramref name="screen"/> behavior; when set, the form uses exactly these
    /// desktop coordinates/size instead — still borderless, still topmost (those are set
    /// unconditionally below, independent of this override).</param>
    /// <param name="fitFrameSizeToTile">Config-driven (<see cref="GridLookout.Config.WallConfig.FitFrameSizeToTile"/>).
    /// True (default): each tile requests a live-stream frame size matching its actual on-screen
    /// pixel size, computed once at grid-build time — see <see cref="BuildGrid"/>. False: every
    /// tile requests the flat 1280x720 size.</param>
    /// <param name="maxFps">Config-driven (<see cref="GridLookout.Config.WallConfig.MaxFps"/>).
    /// Passed straight through to each tile's <see cref="LiveTileSource"/>; 0 leaves the SDK's FPS
    /// property untouched (server's native/default rate).</param>
    /// <param name="tileScaleMode">Config-driven raw string (<see cref="GridLookout.Config.WallConfig.TileScaleMode"/>),
    /// parsed here via <see cref="TileScaleModeParser"/> — an unrecognized value falls back to Fit
    /// with a logged warning rather than throwing, same resilience rule as <paramref name="tileBorderColor"/>'s
    /// try/catch above.</param>
    /// <param name="kioskLock">Config-driven (<see cref="GridLookout.Config.WallConfig.KioskLock"/>,
    /// S8). Default <c>false</c> (unlocked — every prior release's behavior). When <c>true</c>,
    /// <see cref="ProcessCmdKey"/> no longer treats Esc as exit and <see cref="ToggleCompact"/>
    /// becomes a no-op, so neither a keyboard nor a mouse action can blank or de-kiosk this
    /// window; see the field's own doc comment for the operator-recovery implication.</param>
    /// <param name="tileRecoverSeconds">Config-driven (<see cref="GridLookout.Config.WallConfig.TileRecoverSeconds"/>).
    /// Per-tile self-heal base interval — see <see cref="SweepStaleTiles"/> and
    /// <see cref="GridLookout.Monitoring.TileRecoveryScheduler"/>. <c>0</c> disables per-tile
    /// recovery entirely; a nonzero value below 10 is floored to 10 (same "a reconnect costs real
    /// time" reasoning as <see cref="GridLookout.Config.WallConfig.TileRotateSeconds"/>'s floor).</param>
    public WallForm(int monitorNumber, Screen screen, bool fallbackWarning, string recorderName, bool showHeader,
        int tileBorderWidth = 1, string tileBorderColor = "#404040", int staleSeconds = 10,
        Rectangle? windowBoundsOverride = null, bool fitFrameSizeToTile = true, int maxFps = 12,
        string tileScaleMode = "Fit", bool kioskLock = false, int tileRecoverSeconds = 30)
    {
        MonitorNumber = monitorNumber;
        _recorderName = recorderName;
        _showHeader = showHeader;
        _tileBorderWidth = tileBorderWidth;
        _staleSeconds = staleSeconds;
        _tileRecoverSeconds = tileRecoverSeconds <= 0 ? 0 : Math.Max(tileRecoverSeconds, 10);
        _fitFrameSizeToTile = fitFrameSizeToTile;
        _maxFps = maxFps;
        _kioskLock = kioskLock;
        _tileScaleMode = TileScaleModeParser.Parse(tileScaleMode, out var scaleModeWarning);
        if (scaleModeWarning is not null)
        {
            Milestone.RecorderLocator.Logger?.Warning(scaleModeWarning);
        }
        try
        {
            _tileBorderColor = string.IsNullOrWhiteSpace(tileBorderColor)
                ? Color.Black
                : ColorTranslator.FromHtml(tileBorderColor);
        }
        catch (Exception)
        {
            _tileBorderColor = Color.Black;
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        _normalBounds = windowBoundsOverride ?? screen.Bounds;
        Bounds = _normalBounds;
        // Double-click toggle target — see ToggleCompact()'s doc comment for why the choice of
        // "alternate" depends on whether a WindowBounds override is in play: the goal is always
        // two visibly different sizes, never a no-op toggle.
        _alternateBounds = windowBoundsOverride.HasValue ? screen.Bounds : CenteredSixtyPercent(screen.Bounds);
        WindowState = FormWindowState.Normal;
        TopMost = true;
        BackColor = Color.Black;
        KeyPreview = true;
        _taskbarDefault = fallbackWarning;
        ShowInTaskbar = fallbackWarning; // secondary/fallback windows stay findable; primary hides
        Text = $"GridLookout — Monitor {monitorNumber}";
        // Form.Icon does not inherit the exe's ApplicationIcon — without this, the compact-mode
        // title bar and taskbar show the generic WinForms form icon instead of app.ico.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* unreadable exe icon: keep the WinForms default rather than fail startup */ }

        MouseDoubleClick += (_, _) => ToggleCompact();

        // Esc is handled in ProcessCmdKey, not a KeyDown handler, so it still fires regardless of
        // which (or no) child control has focus. There is deliberately NO minimize hotkey (global
        // F-keys collide with other software) — minimizing is done from compact mode's real title
        // bar instead (double-click → compact → minimize button).

        _fallbackWarningLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            ForeColor = Color.Orange,
            BackColor = Color.Black,
            Font = WarningFont,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = $"Monitor {monitorNumber} not found — showing on primary display",
            Visible = fallbackWarning,
        };
        Controls.Add(_fallbackWarningLabel);

        if (_showHeader)
        {
            _headerStrip = BuildHeaderStrip(_recorderName, ToggleCompact, out _headerClock, out _headerNameLabel);
            Controls.Add(_headerStrip);

            // Dock layout is processed from the BACK of the z-order to the front: the back-most
            // control docks first and claims the top edge; a Dock.Fill control only receives the
            // REMAINING space if it is front-most. Order back→front: fallback warning (very top),
            // header strip (below it), body fill (remainder) — body controls call BringToFront()
            // at their add sites.
            _headerStrip.SendToBack();
            _fallbackWarningLabel.SendToBack();
        }

        // Stale-feed sweep: one shared 1-second tick drives it. Reuse the header clock's timer
        // when it exists (ShowHeader: true already ticks every second) rather than allocate a
        // second WinForms Timer for the same cadence; otherwise stand up a dedicated one. Skipped
        // entirely when BOTH StaleSeconds and TileRecoverSeconds are 0 — no timer, no per-tick
        // work, both the STALLED-overlay feature and per-tile self-heal fully off. StaleSeconds: 0
        // is a documented-valid config (no visual overlay) that must NOT also silently disable
        // per-tile self-heal — the two settings are independent, so either alone being nonzero
        // keeps this timer running.
        if (_staleSeconds > 0 || _tileRecoverSeconds > 0)
        {
            if (_headerClock is not null)
            {
                _headerClock.Tick += (_, _) => SweepStaleTiles();
            }
            else
            {
                _staleSweepTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _staleSweepTimer.Tick += (_, _) => SweepStaleTiles();
                _staleSweepTimer.Start();
            }
        }
    }

    /// <summary>Kiosk hotkeys: Esc exits. That is the only one — minimize/maximize/close live on
    /// compact mode's real title bar (see <see cref="ToggleCompact"/>); no F-key hotkey is used,
    /// since global F-keys tend to collide with other software. S8: when <see cref="_kioskLock"/>
    /// is set, Esc is swallowed instead of exiting — still marked handled (<c>return true</c>) so
    /// it doesn't leak through to a child control either.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (!_kioskLock)
            {
                Application.Exit();
            }

            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>T1/R1: closes this form BYPASSING the KioskLock close-gate (see
    /// <see cref="OnFormClosing"/>). EVERY programmatic close of a WallForm in this codebase must go
    /// through this method instead of a bare <see cref="Form.Close"/> — Program.cs's config-refresh
    /// rebuild teardown, RecoverSession's teardown, and LoginRetryLoop's own status-card
    /// close-on-success all do — or a KioskLock wall would refuse its own internal teardown exactly
    /// like it refuses a passerby's Alt+F4, wedging the recorder-switch/recovery/retry flow it's
    /// called from.</summary>
    public void CloseInternal()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>T1/R1: the actual close-refusal gate. Esc (<see cref="ProcessCmdKey"/>) and the
    /// double-click compact toggle (<see cref="ToggleCompact"/>) were the only two KioskLock gates
    /// before this fix; Alt+F4, a taskbar "Close window" command, and SC_CLOSE from the system menu
    /// all bypassed both and reached <see cref="Form.Close"/> directly, so a locked wall could still
    /// be closed by any of the three — on a multi-monitor wall, closing just one monitor's form
    /// leaves the process alive but that monitor dark, and the watchdog (which only relaunches a
    /// fully-dead process — see scripts/install-kiosk.ps1) never notices. The actual should-cancel
    /// decision is delegated to <see cref="KioskCloseGuard.ShouldCancelClose"/> (SDK/UI-free,
    /// unit-tested there) — this override only wires it to the real <see cref="FormClosingEventArgs"/>
    /// and logs a throttled Warning on refusal.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        if (!e.Cancel && KioskCloseGuard.ShouldCancelClose(_kioskLock, _allowClose, e.CloseReason))
        {
            e.Cancel = true;
            LogRefusedCloseAttempt();
        }
    }

    private void LogRefusedCloseAttempt()
    {
        var now = DateTime.UtcNow;
        if (now - _lastCloseRefusalWarningUtc < CloseRefusalWarningThrottle)
        {
            return;
        }

        _lastCloseRefusalWarningUtc = now;
        Milestone.RecorderLocator.Logger?.Warning($"Monitor {MonitorNumber}: close attempt refused — KioskLock is active.");
    }

    /// <summary>Double-click anywhere on the wall toggles between the form's normal
    /// (construction-time) bounds and an "alternate" size — always two visibly different sizes,
    /// picked at construction time (see the constructor):
    /// <list type="bullet">
    /// <item>No <see cref="GridLookout.Config.WallConfig.WindowBounds"/> override: normal
    /// is fullscreen (<c>screen.Bounds</c>); alternate is a centered rectangle at 60% of the
    /// screen's width/height — a visibly smaller, still-borderless/topmost window.</item>
    /// <item>WindowBounds override configured: normal IS that override (already a
    /// deliberately-sized window, so a further "60% of itself" shrink would be a near-no-op);
    /// alternate is full <c>screen.Bounds</c> instead, so the toggle is still fullscreen ↔
    /// windowed either way.</item>
    /// </list>
    /// Compact mode is a NORMAL window: a sizable border with a real title bar
    /// (minimize/maximize/close buttons), shown in the taskbar, not topmost — the operator can
    /// park, move, resize, or minimize it like any application and get it back from the taskbar.
    /// Fullscreen mode restores the kiosk posture: borderless, topmost, taskbar per
    /// <c>_taskbarDefault</c>. The grid rescales for free because tiles are <c>Dock.Fill</c>
    /// inside TableLayoutPanels that already react to any Bounds/ClientSize change.
    /// S8: a no-op entirely when <see cref="_kioskLock"/> is set — every double-click site across
    /// the form and its tiles funnels through this one method, so gating here (rather than at each
    /// <c>MouseDoubleClick</c> wiring site) covers all of them and can never fall out of sync with
    /// a newly-added tile control that also wires the same event.</summary>
    private void ToggleCompact()
    {
        if (_kioskLock)
        {
            return;
        }

        _compact = !_compact;
        if (_compact)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            TopMost = false;
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Bounds = _alternateBounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = _taskbarDefault;
            WindowState = FormWindowState.Normal;
            Bounds = _normalBounds;
        }
    }

    private static Rectangle CenteredSixtyPercent(Rectangle screenBounds)
    {
        int width = (int)(screenBounds.Width * 0.6);
        int height = (int)(screenBounds.Height * 0.6);
        int x = screenBounds.Left + ((screenBounds.Width - width) / 2);
        int y = screenBounds.Top + ((screenBounds.Height - height) / 2);
        return new Rectangle(x, y, width, height);
    }

    /// <param name="onDoubleClick">Wired to <c>MouseDoubleClick</c> on the strip AND both child
    /// labels — the labels are <c>Dock.Fill</c>/<c>Dock.Fill</c> and cover the strip's entire
    /// client area, so a click always lands on one of them, never on the strip panel itself.</param>
    /// <param name="nameLabel">The recorder-name label — kept as a field
    /// (<see cref="_headerNameLabel"/>) so <see cref="UpdateHeaderPageIndicator"/> can append/clear
    /// the " — page N/M" suffix on each page-rotation flip without rebuilding the strip.</param>
    private static TableLayoutPanel BuildHeaderStrip(string recorderName, Action onDoubleClick, out System.Windows.Forms.Timer clock, out Label nameLabel)
    {
        var strip = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Black,
        };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        var nameLabelLocal = new Label
        {
            Dock = DockStyle.Fill,
            Text = recorderName,
            ForeColor = Color.White,
            BackColor = Color.Black,
            Font = HeaderFont,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };

        var timeLabelLocal = new Label
        {
            Dock = DockStyle.Fill,
            Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ForeColor = Color.White,
            BackColor = Color.Black,
            Font = HeaderFont,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 10, 0),
        };

        strip.Controls.Add(nameLabelLocal, 0, 0);
        strip.Controls.Add(timeLabelLocal, 1, 0);

        strip.MouseDoubleClick += (_, _) => onDoubleClick();
        nameLabelLocal.MouseDoubleClick += (_, _) => onDoubleClick();
        timeLabelLocal.MouseDoubleClick += (_, _) => onDoubleClick();

        // Local (not the out-parameter) so the tick handler can capture it — anonymous functions
        // cannot capture ref/out parameters directly.
        var clockLocal = new System.Windows.Forms.Timer { Interval = 1000 };
        clockLocal.Tick += (_, _) => timeLabelLocal.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        clockLocal.Start();

        clock = clockLocal;
        nameLabel = nameLabelLocal;
        return strip;
    }

    /// <summary>Fullscreen status/error card (startup config error, connect retry, etc.) — never a
    /// modal dialog.</summary>
    public void ShowStatus(string message, bool isError)
    {
        DisposeTiles();
        ClearBody();

        var label = new Label
        {
            Dock = DockStyle.Fill,
            Text = message,
            ForeColor = Color.White,
            BackColor = isError ? Color.FromArgb(96, 0, 0) : Color.FromArgb(0, 0, 64),
            Font = StatusFont,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(label);
        // Front-most => docks last => takes the space remaining below the warning/header strips
        // (see the z-order comment in the constructor).
        label.BringToFront();
    }

    public void ShowNoCameras()
    {
        ShowStatus("No cameras assigned to this recorder", isError: false);
    }

    /// <summary>Sequential row-major fill: camera 1 goes to row 0 / col 0, etc. — used when no
    /// <c>$layout{}</c> matrix applies. Single page, no rotation — see
    /// <see cref="RenderPagedAutoLayout"/> for the paging entry point Program.cs actually calls;
    /// this method stays public (and rotation-free) for direct callers and as the paged method's
    /// own single-page fallback.</summary>
    public void RenderAutoLayout(IReadOnlyList<CameraInfo> cameras)
    {
        // Guard against a leftover page-rotation timer from a PRIOR render of this same form (e.g.
        // a config-refresh rebuild that switched a recorder from paged auto-layout back to a
        // single-page fit, or straight to a $layout{} matrix and back) — never let two timers run.
        StopAndDisposePageTimer();
        RenderAutoLayoutPage(cameras, ordinalOffset: 0);
    }

    /// <summary>Auto-layout entry point with page rotation — what Program.cs's auto-layout call
    /// sites use instead of <see cref="RenderAutoLayout"/>. <paramref name="pageSeconds"/> and
    /// <paramref name="pageSize"/> are clamped here (use-site, per
    /// <see cref="GridLookout.Config.WallConfig.PageSeconds"/> /
    /// <see cref="GridLookout.Config.WallConfig.PageSize"/>'s doc comments): a nonzero
    /// <paramref name="pageSeconds"/> below 10 becomes 10 (a flip tears down and rebuilds live
    /// sources, which costs ~2s — faster would thrash); <paramref name="pageSize"/> below 1
    /// becomes 1. When rotation resolves to a single page (either <paramref name="pageSeconds"/>
    /// is 0 or all cameras already fit in one page) this behaves like
    /// <see cref="RenderAutoLayout"/> — single page, no timer. Otherwise cameras are
    /// split into <c>ceil(n/pageSize)</c> pages in order and a <see cref="System.Windows.Forms.Timer"/>
    /// advances through them cyclically, tearing down the outgoing page and building the incoming
    /// one via the same <see cref="BuildGrid"/> (and therefore the same <see cref="DisposeTiles"/>
    /// / <see cref="ComputeTileRequestSize"/> / MaxFps plumbing) any other grid (re)build uses — so
    /// only the current page's live sources ever exist at once.</summary>
    public void RenderPagedAutoLayout(IReadOnlyList<CameraInfo> cameras, int pageSize, int pageSeconds)
    {
        // Same guard as RenderAutoLayout — this is the OTHER "Render*" entry point a config-refresh
        // rebuild calls, so it must independently stop any timer left running from a prior render.
        StopAndDisposePageTimer();

        int effectivePageSize = Math.Max(pageSize, 1);
        int effectivePageSeconds = pageSeconds <= 0 ? 0 : Math.Max(pageSeconds, 10);

        if (effectivePageSeconds <= 0 || cameras.Count <= effectivePageSize)
        {
            RenderAutoLayoutPage(cameras, ordinalOffset: 0);
            return;
        }

        _pagedCameras = cameras;
        _pageSize = effectivePageSize;
        _currentPageIndex = 0;
        _totalPages = (int)Math.Ceiling(cameras.Count / (double)effectivePageSize);

        RenderCurrentPage();

        _pageTimer = new System.Windows.Forms.Timer { Interval = effectivePageSeconds * 1000 };
        _pageTimer.Tick += (_, _) => AdvancePage();
        _pageTimer.Start();
    }

    /// <summary>Renders one page (a contiguous slice) of the auto-layout grid. Ordinal captions
    /// show the camera's TRUE position in the full recorder list — <paramref name="ordinalOffset"/>
    /// (0 for page 0, pageSize for page 1, etc.) is added to each tile's within-page position — so
    /// e.g. page 2's first tile captions "10: name", never "1: name". This matters because the
    /// caption is the operator's legend for writing <c>$layout{}</c> matrices; a paged wall that
    /// relabeled ordinals from 1 on every page would make that legend lie. Resets the header's page
    /// indicator to plain (no " — page N/M") — the multi-page caller (<see cref="RenderCurrentPage"/>)
    /// overwrites it with the real page number immediately after this returns.</summary>
    private void RenderAutoLayoutPage(IReadOnlyList<CameraInfo> pageCameras, int ordinalOffset)
    {
        UpdateHeaderPageIndicator(pageNumber: null, totalPages: null);

        if (pageCameras.Count == 0)
        {
            ShowNoCameras();
            return;
        }

        var rowCounts = WallLayoutEngine.Compute(pageCameras.Count);
        BuildGrid(rowCounts, (row, col) =>
        {
            int linear = 0;
            for (int i = 0; i < row; i++)
            {
                linear += rowCounts[i];
            }
            linear += col;
            // Auto-layout is grammar-free — no cell here ever rotates or goes unavailable; the
            // caption prefix is just this camera's position in the recorder's sorted list.
            return new CellRenderSpec(pageCameras[linear], (ordinalOffset + linear + 1).ToString(CultureInfo.InvariantCulture), null, null, 0);
        });
    }

    /// <summary>Renders <see cref="_currentPageIndex"/> out of <see cref="_pagedCameras"/> and
    /// updates the header's " — page N/M" indicator. Called for the initial page build and by
    /// <see cref="AdvancePage"/> on every timer flip — deliberately does NOT touch
    /// <see cref="_pageTimer"/> itself (unlike <see cref="RenderAutoLayout"/> / <see cref="RenderResolvedLayout"/>,
    /// this is not a config-refresh entry point; it is the timer's own tick target, so stopping the
    /// timer here would kill the rotation it's supposed to drive).</summary>
    private void RenderCurrentPage()
    {
        if (_pagedCameras is null)
        {
            return;
        }

        int offset = _currentPageIndex * _pageSize;
        var pageCameras = _pagedCameras.Skip(offset).Take(_pageSize).ToList();
        RenderAutoLayoutPage(pageCameras, ordinalOffset: offset);
        UpdateHeaderPageIndicator(_currentPageIndex + 1, _totalPages);
    }

    private void AdvancePage()
    {
        if (_pagedCameras is null || _totalPages <= 1)
        {
            return;
        }

        _currentPageIndex = (_currentPageIndex + 1) % _totalPages;
        try
        {
            RenderCurrentPage();
        }
        catch (Exception ex)
        {
            // T8(b)/R10: a page-flip rebuild failure (e.g. a transient SDK throw while building the
            // incoming page's tiles) must never crash the kiosk or wedge the rotation — log and let
            // this SAME timer's next tick retry with the following page. Note BuildGrid's own
            // teardown-before-build ordering (DisposeTiles/ClearBody run before the new grid is
            // built) means the outgoing page's controls may already be gone by the time an
            // exception surfaces here, so this cannot always literally preserve the previous page's
            // pixels once teardown has started — it stops the crash and keeps rotation alive.
            Milestone.RecorderLocator.Logger?.Warning($"Page-flip rebuild failed — will retry on the next rotation: {ex.Message}");
        }
    }

    /// <summary>Stops and disposes any running page-rotation timer — idempotent (safe to call when
    /// none is running) — and clears BOTH modes' paging state (auto-layout's <see cref="_pagedCameras"/>
    /// and the resolved-plan matrix's <see cref="_resolvedPages"/>/<see cref="_cameraCatalog"/>),
    /// since the timer is shared and a mode switch must never leave the other mode's stale list
    /// around to be read. Also retires the per-tile rotate timer/tiles
    /// (<see cref="StopAndDisposeTileRotateTimer"/>) — a config refresh that drops a recorder's
    /// <c>$layout{}</c> matrix (or its rotating cells) must never leave a rotate timer ticking
    /// against tiles that no longer exist.
    /// Called at the top of <see cref="RenderAutoLayout"/>, <see cref="RenderResolvedLayout"/>, and
    /// (internally) <see cref="RenderPagedAutoLayout"/> — the three public entry points a
    /// config-refresh rebuild can call — so a rebuild that replaces the layout can never leave two
    /// page timers running at once.</summary>
    private void StopAndDisposePageTimer()
    {
        if (_pageTimer is not null)
        {
            _pageTimer.Stop();
            _pageTimer.Dispose();
            _pageTimer = null;
        }

        _pagedCameras = null;
        _resolvedPages = null;
        _cameraCatalog = null;

        StopAndDisposeTileRotateTimer();
    }

    /// <summary>Stops and disposes <see cref="_tileRotateTimer"/> (idempotent) and clears
    /// <see cref="_rotatingTiles"/> — called both by <see cref="StopAndDisposePageTimer"/>'s
    /// broader guard and, independently, by <see cref="RenderResolvedPage"/> itself on every page
    /// flip (which is NOT preceded by that guard — it's the timer's own tick target, same non-guard
    /// rule <see cref="RenderCurrentPage"/> follows for <see cref="_pageTimer"/>).</summary>
    private void StopAndDisposeTileRotateTimer()
    {
        if (_tileRotateTimer is not null)
        {
            _tileRotateTimer.Stop();
            _tileRotateTimer.Dispose();
            _tileRotateTimer = null;
        }

        _rotatingTiles.Clear();
    }

    /// <summary>Appends/clears the " — page N/M" suffix on the header's recorder-name label. A
    /// no-op when <see cref="_showHeader"/> is false (no header, so no indicator — matches the
    /// design's "No header → no indicator (fine)"). Pass null/null to show the plain recorder
    /// name (single page, or a $layout{} matrix, where rotation never applies).</summary>
    private void UpdateHeaderPageIndicator(int? pageNumber, int? totalPages)
    {
        if (!_showHeader || _headerNameLabel is null)
        {
            return;
        }

        _headerNameLabel.Text = pageNumber.HasValue && totalPages.HasValue
            ? $"{_recorderName} — page {pageNumber}/{totalPages}"
            : _recorderName;
    }

    /// <summary>F3 (referentially stable layouts): renders an explicit <c>$layout{}</c> matrix from
    /// a pre-resolved <see cref="ResolvedMonitorPlan"/> (<c>Layout.LayoutResolver</c>'s output) —
    /// replaces the pre-F3 <c>RenderMatrixLayout(MatrixPage[], ordinal camera list)</c>, which
    /// indexed <c>cameras[ordinal - 1]</c> directly and is exactly the referential-stability defect
    /// this feature fixes (a rename/reorder/enable/disable could silently repoint a tile at the
    /// wrong camera). Every reference in <paramref name="plan"/> has ALREADY been resolved to a
    /// stable camera id (or flagged unavailable) by the time it reaches here — this method only
    /// looks cameras up in <paramref name="cameraCatalog"/> by id and renders; it does no ordinal
    /// arithmetic and touches no config.
    ///
    /// <paramref name="plan"/>.Pages is one <see cref="ResolvedPage"/> per <c>|</c>-separated page
    /// segment inside the original token body. A single page renders once, no timer. More than one
    /// page rotates through them with the same page-flip timer mechanism as
    /// <see cref="RenderPagedAutoLayout"/>: <paramref name="pageSeconds"/> is clamped to a 10s floor
    /// when nonzero, but UNLIKE the auto-layout path, <c>0</c> does NOT disable rotation here — an
    /// explicitly multi-page matrix (the operator wrote more than one <c>|</c> segment) means
    /// rotation was requested regardless of <see cref="GridLookout.Config.WallConfig.PageSeconds"/>,
    /// so an unset/0 value falls back to the same 10s floor rather than freezing on page one
    /// forever. <see cref="GridLookout.Config.WallConfig.PageSize"/> does NOT apply here — each
    /// page's cell count is whatever the operator wrote in that page segment, not a configured chunk
    /// size.
    ///
    /// <paramref name="tileRotateSeconds"/> (<see cref="GridLookout.Config.WallConfig.TileRotateSeconds"/>)
    /// drives any cell written in the rotation form (<c>A(3,@yard-east,@{guid})</c> instead of a
    /// fixed single reference) — see <see cref="RenderResolvedPage"/> for how those tiles are built
    /// and flipped. Stored on the form so page flips (which call <see cref="RenderResolvedPage"/>
    /// directly, not through this entry point) still see the configured interval.</summary>
    public void RenderResolvedLayout(ResolvedMonitorPlan plan, IReadOnlyDictionary<Guid, CameraInfo> cameraCatalog, int pageSeconds, int tileRotateSeconds)
    {
        // Same guard as RenderAutoLayout — see its doc comment. A recorder can flip from paged
        // auto-layout to an explicit matrix (or between a single-page and multi-page matrix) on a
        // live config refresh; any running page timer from the prior render must be stopped first.
        StopAndDisposePageTimer();
        _tileRotateSecondsConfig = tileRotateSeconds;
        _cameraCatalog = cameraCatalog;
        UpdateHeaderPageIndicator(pageNumber: null, totalPages: null);

        var pages = plan.Pages;
        if (pages.Count == 0)
        {
            ShowNoCameras();
            return;
        }

        if (pages.Count == 1)
        {
            RenderResolvedPage(pages[0]);
            return;
        }

        _resolvedPages = pages;
        _currentPageIndex = 0;
        _totalPages = pages.Count;

        RenderCurrentResolvedPage();

        // 0/unset PageSeconds still rotates here (unlike auto-layout) — see the doc comment above.
        int effectivePageSeconds = pageSeconds <= 0 ? 10 : Math.Max(pageSeconds, 10);
        _pageTimer = new System.Windows.Forms.Timer { Interval = effectivePageSeconds * 1000 };
        _pageTimer.Tick += (_, _) => AdvanceResolvedPage();
        _pageTimer.Start();
    }

    /// <summary>Renders one resolved page's rows — the body shared by the single-page and
    /// multi-page (rotating) branches of <see cref="RenderResolvedLayout"/>. A cell with more than
    /// one member (<see cref="ResolvedCell.IsRotating"/>) is a ROTATING tile: it initially shows its
    /// first AVAILABLE member (an unavailable member at the front of the list is skipped here
    /// exactly like <see cref="AdvanceOneRotatingTile"/> skips one mid-rotation — only when EVERY
    /// member is unavailable does the cell render the UNAVAILABLE placeholder, same as a fixed
    /// cell's single unavailable member). <see cref="BuildCell"/> registers each rotating tile it
    /// builds into <see cref="_rotatingTiles"/>; once the grid is up, a single shared timer is
    /// (re)started to flip them — called both for the initial/single-page render and on every page
    /// flip, so each page's rotating tiles always start again from their first available member (a
    /// natural consequence of the grid being rebuilt from scratch).</summary>
    private void RenderResolvedPage(ResolvedPage page)
    {
        // Page flips call this directly (not through RenderResolvedLayout's top-level guard), so it
        // must independently retire any rotate timer/tiles left over from the OUTGOING page before
        // building the incoming one.
        StopAndDisposeTileRotateTimer();

        var rows = page.Rows;
        if (rows.Count == 0)
        {
            ShowNoCameras();
            return;
        }

        var catalog = _cameraCatalog;
        if (page.IsUniform)
        {
            // F4 (cell spans): a SIBLING build path, not a branch inside BuildGrid — see
            // BuildSpanGrid's own doc comment for why keeping the two textually separate is what
            // makes "every non-spanned page renders byte-for-byte unchanged" auditable at a glance.
            BuildSpanGrid(page, catalog, _rotatingTiles);
        }
        else
        {
            var rowCounts = rows.Select(r => r.Cells.Count).ToArray();
            BuildGrid(rowCounts, (row, col) => BuildCellRenderSpec(rows[row].Cells[col], catalog), catalog, _rotatingTiles);
        }

        if (_rotatingTiles.Count > 0)
        {
            int intervalSeconds = Math.Max(_tileRotateSecondsConfig, 5);
            _tileRotateTimer = new System.Windows.Forms.Timer { Interval = intervalSeconds * 1000 };
            _tileRotateTimer.Tick += (_, _) => AdvanceRotatingTiles();
            _tileRotateTimer.Start();
        }
    }

    /// <summary>Turns one resolved cell into what <see cref="BuildCell"/> needs to render it — the
    /// rotating/fixed/unavailable decision shared by BOTH grid-build paths
    /// (<see cref="BuildGrid"/> via <see cref="RenderResolvedPage"/>'s legacy branch, and
    /// <see cref="BuildSpanGrid"/> directly). A rotating cell (<see cref="ResolvedCell.IsRotating"/>)
    /// starts on its first AVAILABLE member (an unavailable one at the front of the list is skipped
    /// here exactly like <see cref="AdvanceOneRotatingTile"/> skips one mid-rotation); only when
    /// EVERY member is unavailable does the cell render the UNAVAILABLE placeholder, same as a fixed
    /// cell's single unavailable member.</summary>
    private CellRenderSpec BuildCellRenderSpec(ResolvedCell cell, IReadOnlyDictionary<Guid, CameraInfo>? catalog)
    {
        if (cell.IsRotating)
        {
            var members = cell.Members;
            int startIndex = -1;
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].Available)
                {
                    startIndex = i;
                    break;
                }
            }

            if (startIndex < 0)
            {
                var primary = members[0];
                return new CellRenderSpec(null, primary.RefLabel, primary.UnavailableReason ?? "unavailable", null, 0);
            }

            var chosen = members[startIndex];
            return new CellRenderSpec(LookupCamera(chosen, catalog), chosen.RefLabel, null, members, startIndex);
        }

        var single = cell.Members[0];
        if (!single.Available)
        {
            return new CellRenderSpec(null, single.RefLabel, single.UnavailableReason ?? "unavailable", null, 0);
        }

        return new CellRenderSpec(LookupCamera(single, catalog), single.RefLabel, null, null, 0);
    }

    /// <summary>F4 (cell spans): renders a UNIFORM resolved page as ONE flat TableLayoutPanel — R
    /// row styles, C column styles, all equal percents — using
    /// <see cref="TableLayoutPanel.SetRowSpan"/>/<see cref="TableLayoutPanel.SetColumnSpan"/> for
    /// any cell whose <see cref="ResolvedCell.RowSpan"/>/<see cref="ResolvedCell.ColSpan"/> is
    /// greater than 1. Placeholder positions get no control at all — the TableLayoutPanel's own span
    /// mechanism already reserves that screen area for the spanning control, exactly like an HTML
    /// table's <c>rowspan</c>/<c>colspan</c> — so nothing else needs to occupy it.
    ///
    /// Deliberately a SIBLING of <see cref="BuildGrid"/> (the legacy nested-panel path: an outer
    /// single-column panel of equal-height rows, each hosting its own inner equal-width-column
    /// panel) rather than a branch folded inside it — that nesting is exactly what the "every
    /// non-spanned page renders byte-for-byte unchanged" contract protects, and a branch buried
    /// inside shared code would be the largest regression risk this feature could introduce. The two
    /// paths duplicate only the small per-call bookkeeping (dispose/clear/reset — see
    /// <see cref="BuildGrid"/>'s own copy of the same three lines) and both delegate the actual
    /// per-cell work to <see cref="BuildCellRenderSpec"/>/<see cref="BuildCell"/>.</summary>
    private void BuildSpanGrid(ResolvedPage page, IReadOnlyDictionary<Guid, CameraInfo>? cameraCatalog, List<RotatingTile>? rotatingTilesOut)
    {
        DisposeTiles();
        ClearBody();
        // F3: reset per-build UNAVAILABLE count — see GetHealthSnapshot's WallFormHealth.UnavailableCount.
        // BuildSpanGrid is a SIBLING of BuildGrid (see this method's doc comment), so it needs its
        // own copy of this reset rather than inheriting BuildGrid's.
        _unavailableCount = 0;

        int rowCount = Math.Max(page.Rows.Count, 1);
        int colCount = Math.Max(page.GridColumns, 1);

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = rowCount,
            ColumnCount = colCount,
            BackColor = _tileBorderColor,
        };
        for (int r = 0; r < rowCount; r++)
        {
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rowCount));
        }

        for (int c = 0; c < colCount; c++)
        {
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / colCount));
        }

        for (int r = 0; r < page.Rows.Count; r++)
        {
            foreach (var cell in page.Rows[r].Cells)
            {
                var spec = BuildCellRenderSpec(cell, cameraCatalog);
                var (tileWidth, tileHeight) = ComputeSpannedTileRequestSize(cell.ColSpan, cell.RowSpan, colCount, rowCount);

                // S10/U9/M7/E9 (see BuildGrid's matching comment): identifies the grid cell by its
                // ACTUAL column (cell.Col — list position no longer equals column once placeholders
                // are stripped, see LayoutCell.Col's doc comment), not the camera.
                var tileLabel = $"M{MonitorNumber} R{r + 1}C{cell.Col + 1}";
                var control = BuildCell(spec, tileLabel, tileWidth, tileHeight, cameraCatalog, rotatingTilesOut);

                outer.Controls.Add(control, cell.Col, r);
                if (cell.ColSpan > 1)
                {
                    outer.SetColumnSpan(control, cell.ColSpan);
                }

                if (cell.RowSpan > 1)
                {
                    outer.SetRowSpan(control, cell.RowSpan);
                }
            }
        }

        Controls.Add(outer);
        // Front-most => docks last => takes the space remaining below the warning/header strips
        // (see the z-order comment in the constructor) — same convention as BuildGrid.
        outer.BringToFront();
    }

    /// <summary>Looks up a resolved member's pinned camera in <paramref name="catalog"/> —
    /// <paramref name="member"/>.Available true implies <see cref="ResolvedMember.CameraId"/> is set
    /// (see <c>Layout.LayoutResolver.BuildAvailability</c>), but this never trusts that invariant
    /// blindly across the module boundary: a missing/null id or a catalog miss both degrade to
    /// "no camera" rather than throwing.</summary>
    private static CameraInfo? LookupCamera(ResolvedMember member, IReadOnlyDictionary<Guid, CameraInfo>? catalog)
    {
        if (catalog is null || !member.CameraId.HasValue)
        {
            return null;
        }

        return catalog.TryGetValue(member.CameraId.Value, out var camera) ? camera : null;
    }

    /// <summary>Renders <see cref="_currentPageIndex"/> out of <see cref="_resolvedPages"/> and
    /// updates the header's " — page N/M" indicator — the resolved-plan counterpart of
    /// <see cref="RenderCurrentPage"/>. Same non-guard rule: called by the timer tick itself
    /// (<see cref="AdvanceResolvedPage"/>), so it must not stop the timer driving it.</summary>
    private void RenderCurrentResolvedPage()
    {
        if (_resolvedPages is null)
        {
            return;
        }

        RenderResolvedPage(_resolvedPages[_currentPageIndex]);
        UpdateHeaderPageIndicator(_currentPageIndex + 1, _totalPages);
    }

    private void AdvanceResolvedPage()
    {
        if (_resolvedPages is null || _totalPages <= 1)
        {
            return;
        }

        _currentPageIndex = (_currentPageIndex + 1) % _totalPages;
        try
        {
            RenderCurrentResolvedPage();
        }
        catch (Exception ex)
        {
            // T8(b)/R10 — see AdvancePage's identical catch for the full rationale.
            Milestone.RecorderLocator.Logger?.Warning($"Matrix page-flip rebuild failed — will retry on the next rotation: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the grid as an outer single-column TableLayoutPanel (one row per grid row, equal
    /// height) whose each cell hosts an INNER TableLayoutPanel with that row's own camera count
    /// (equal width within the row). This — not one shared global column count — is what makes
    /// "cells in a row share the row's width equally" hold independently per row (e.g. a 3-camera
    /// row and a 4-camera row both span the full window width).
    /// </summary>
    /// <param name="resolver">Resolves one grid cell to a <see cref="CellRenderSpec"/> —
    /// <see cref="CellRenderSpec.RotationMembers"/> is non-null (and Count &gt; 1) only for a
    /// resolved-plan matrix cell in the rotation form — see <see cref="RenderResolvedPage"/>.
    /// Auto-layout's resolver always returns null there (no per-tile rotation on that path).</param>
    /// <param name="cameraCatalog">The camera-id lookup a rotating tile resolves its next member
    /// against at flip time; required (non-null) whenever any cell can be rotating.</param>
    /// <param name="rotatingTilesOut">Populated with one <see cref="RotatingTile"/> per rotating
    /// cell actually built (skipped/null when the resolver never returns rotation members, e.g. the
    /// auto-layout path) — <see cref="RenderResolvedPage"/> uses this to know whether to (re)start
    /// <see cref="_tileRotateTimer"/> after the grid is up.</param>
    private void BuildGrid(
        int[] rowCounts,
        Func<int, int, CellRenderSpec> resolver,
        IReadOnlyDictionary<Guid, CameraInfo>? cameraCatalog = null,
        List<RotatingTile>? rotatingTilesOut = null)
    {
        // T1/R1: DisposeTiles() below folds every outgoing tile's last-frame timestamp into
        // _freshnessTracker's high-water mark BEFORE tearing the tiles down — see
        // TileFreshnessTracker's doc comment. This is what makes a page/matrix flip (this method's
        // own caller, on every rotation) never reset the staleness signal to zero.
        DisposeTiles();
        ClearBody();
        // F3: reset per-build UNAVAILABLE count — see GetHealthSnapshot's WallFormHealth.UnavailableCount.
        _unavailableCount = 0;

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = Math.Max(rowCounts.Length, 1),
            ColumnCount = 1,
            BackColor = _tileBorderColor,
        };
        for (int r = 0; r < rowCounts.Length; r++)
        {
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rowCounts.Length));
        }

        for (int r = 0; r < rowCounts.Length; r++)
        {
            int cols = Math.Max(rowCounts[r], 1);
            var rowPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = cols,
                BackColor = _tileBorderColor,
            };
            for (int c = 0; c < cols; c++)
            {
                rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            }

            var (tileWidth, tileHeight) = ComputeTileRequestSize(cols, rowCounts.Length);
            for (int c = 0; c < rowCounts[r]; c++)
            {
                var spec = resolver(r, c);
                // S10/U9/M7/E9: identifies the grid cell (not the camera — a $layout{} matrix can
                // legitimately put the same camera in several cells, e.g.
                // $layout{A1,B2,C3,A3,A2,A1}), so each cell's own LiveTileSource can tag its log
                // lines and an operator/on-call reading byte-identical "live status" lines at the
                // same timestamp can tell they're N independent tiles, not a double-fire. 1-based
                // to match the ordinal-numbering convention already used in the tile caption.
                var tileLabel = $"M{MonitorNumber} R{r + 1}C{c + 1}";
                rowPanel.Controls.Add(
                    BuildCell(spec, tileLabel, tileWidth, tileHeight, cameraCatalog, rotatingTilesOut),
                    c, 0);
            }

            outer.Controls.Add(rowPanel, 0, r);
        }

        Controls.Add(outer);
        // Front-most => docks last => takes the space remaining below the warning/header strips
        // (see the z-order comment in the constructor).
        outer.BringToFront();
    }

    /// <summary>Computes the live-stream frame size to request for a tile in a row with
    /// <paramref name="colsInRow"/> columns, out of <paramref name="totalRows"/> equal-height rows
    /// — i.e. the tile's actual on-screen pixel size under the grid's equal-row/equal-column
    /// layout. Thin 1x1-span wrapper over <see cref="ComputeTileRequestSizeCore"/> — see that
    /// method's doc comment for the full sizing rules (computed-once, clamp, even-rounding).</summary>
    private (int width, int height) ComputeTileRequestSize(int colsInRow, int totalRows) =>
        ComputeTileRequestSizeCore(colSpan: 1, rowSpan: 1, totalCols: colsInRow, totalRows: totalRows);

    /// <summary>Computes the live-stream frame size for a tile that occupies
    /// <paramref name="colSpan"/> x <paramref name="rowSpan"/> grid cells out of
    /// <paramref name="totalCols"/> x <paramref name="totalRows"/> total (F4 — cell spans; see
    /// <see cref="BuildSpanGrid"/>) — the tile's actual on-screen footprint scales by those factors
    /// before the same floor/ceiling/even-rounding clamp <see cref="ComputeTileRequestSizeCore"/>
    /// already applies to a plain 1x1 tile.</summary>
    private (int width, int height) ComputeSpannedTileRequestSize(int colSpan, int rowSpan, int totalCols, int totalRows) =>
        ComputeTileRequestSizeCore(colSpan, rowSpan, totalCols, totalRows);

    /// <summary>Shared sizing core for <see cref="ComputeTileRequestSize"/> (colSpan/rowSpan always
    /// 1 — mathematically identical to the pre-F4 flat division, since multiplying by 1 first
    /// changes nothing about an integer division's result) and
    /// <see cref="ComputeSpannedTileRequestSize"/>. Computed ONCE from the form's CURRENT bounds at
    /// grid-build time (normally fullscreen, since walls are built once at startup/config-refresh)
    /// — deliberately NOT re-negotiated live on resize/compact-mode toggle; the PictureBox
    /// zoom-scales whatever frame size was last requested, so a resize after build just scales the
    /// existing frames rather than looking wrong. Returns the flat 1280x720 when
    /// <see cref="_fitFrameSizeToTile"/> is false.</summary>
    private (int width, int height) ComputeTileRequestSizeCore(int colSpan, int rowSpan, int totalCols, int totalRows)
    {
        const int DefaultWidth = 1280;
        const int DefaultHeight = 720;

        if (!_fitFrameSizeToTile)
        {
            return (DefaultWidth, DefaultHeight);
        }

        int chromeHeight = (_fallbackWarningLabel.Visible ? _fallbackWarningLabel.Height : 0)
            + (_showHeader && _headerStrip is not null ? _headerStrip.Height : 0);

        int availableWidth = Math.Max(ClientSize.Width, 1);
        int availableHeight = Math.Max(ClientSize.Height - chromeHeight, 1);

        int tileWidth = availableWidth * Math.Max(colSpan, 1) / Math.Max(totalCols, 1);
        int tileHeight = availableHeight * Math.Max(rowSpan, 1) / Math.Max(totalRows, 1);

        return (ClampEven(tileWidth, 320, DefaultWidth), ClampEven(tileHeight, 180, DefaultHeight));
    }

    /// <summary>Clamps <paramref name="value"/> to [<paramref name="min"/>, <paramref name="max"/>]
    /// then rounds down to an even number — <paramref name="min"/> and <paramref name="max"/> are
    /// always even in this file's callers, so the result never falls back below <paramref name="min"/>.</summary>
    private static int ClampEven(int value, int min, int max)
    {
        int clamped = Math.Max(min, Math.Min(max, value));
        return clamped % 2 == 0 ? clamped : clamped - 1;
    }

    /// <summary>Runs on the UI thread every second (see the constructor's timer wiring). Two jobs,
    /// both driven off each tile's sticky <see cref="ActiveTile.LastRenderedUtc"/> (NOT the current
    /// <see cref="LiveTileSource.LastFrameUtc"/> — see that field's own doc comment and
    /// <see cref="OnFrameReceived"/> for why a per-source property would misclassify a tile mid
    /// self-heal):
    /// <list type="bullet">
    /// <item>STALLED overlay — unchanged semantics from before per-tile self-heal existed: shown
    /// only when <see cref="_staleSeconds"/> is nonzero and a has-rendered tile's last render is
    /// older than that threshold; a tile that has never rendered anything is left alone (it keeps
    /// whatever the SDK live-status handling already shows) UNLESS never-framed self-heal below has
    /// already fired at least one attempt, in which case it gets NO SIGNAL instead.</item>
    /// <item>Per-tile self-heal (new) — when <see cref="_tileRecoverSeconds"/> is nonzero and
    /// session-level recovery isn't currently tearing this wall down
    /// (<see cref="SessionRecoveryInProgress"/>), a stale-with-frames OR never-framed-past-threshold
    /// tile is handed to its own <see cref="GridLookout.Monitoring.TileRecoveryScheduler"/>; when
    /// that scheduler says an attempt is due, <see cref="ReconnectTile"/> tears down and rebuilds
    /// just that one tile's <see cref="LiveTileSource"/>.</item>
    /// </list></summary>
    private void SweepStaleTiles()
    {
        var now = DateTime.UtcNow;

        // StaleSeconds: 0 is a documented-valid config (no STALLED overlay) that must NOT also
        // silently disable per-tile self-heal — TileRecoverSeconds is the dedicated off-switch for
        // that. When StaleSeconds is 0, self-heal still needs SOME staleness threshold to judge a
        // has-framed tile as needing recovery, so it borrows TileRecoverSeconds for that purpose
        // only; the STALLED overlay's own on-screen text/timing (below) always uses the dedicated
        // _staleSeconds threshold directly, never this substituted value.
        int recoveryStaleThresholdSeconds = _staleSeconds > 0 ? _staleSeconds : _tileRecoverSeconds;

        foreach (var tile in _activeTiles)
        {
            if (tile.StaleOverlay.IsDisposed)
            {
                continue;
            }

            bool everRendered = tile.LastRenderedUtc != DateTime.MinValue;
            bool isStale = everRendered && recoveryStaleThresholdSeconds > 0
                && (now - tile.LastRenderedUtc).TotalSeconds > recoveryStaleThresholdSeconds;
            bool isNeverFramed = !everRendered && _tileRecoverSeconds > 0
                && (now - tile.TileStartUtc).TotalSeconds > _tileRecoverSeconds;

            bool reconnectAttemptedThisTick = false;
            if (_tileRecoverSeconds > 0 && !SessionRecoveryInProgress && (isStale || isNeverFramed))
            {
                // The moment this bad spell BEGAN, not "now" — a has-framed tile is eligible from
                // the instant it went stale (lastRendered + threshold), a never-framed tile from its
                // own construction time — see TileRecoveryScheduler.IsAttemptDue's doc comment for
                // why only the FIRST call for a given spell actually consumes this value.
                var eligibleSinceUtc = isStale ? tile.LastRenderedUtc.AddSeconds(recoveryStaleThresholdSeconds) : tile.TileStartUtc;
                if (tile.RecoveryScheduler.IsAttemptDue(now, eligibleSinceUtc))
                {
                    ReconnectTile(tile, now);
                    reconnectAttemptedThisTick = true;
                }
            }

            // STALLED wins whenever a has-rendered tile is stale by the DEDICATED StaleSeconds
            // threshold — independent of whatever recoveryStaleThresholdSeconds substituted above,
            // matching "keep the existing STALLED overlay semantics for has-framed tiles" exactly.
            // NO SIGNAL shows for a never-framed tile once a retry has fired at least once (either
            // just now, or on an earlier tick — AttemptCount persists across ticks until a frame
            // finally arrives, at which point everRendered flips true and this branch stops
            // applying for good).
            bool showStalled = _staleSeconds > 0 && everRendered && (now - tile.LastRenderedUtc).TotalSeconds > _staleSeconds;
            bool showNoSignal = !everRendered && (reconnectAttemptedThisTick || tile.RecoveryScheduler.AttemptCount > 0);

            if (showStalled)
            {
                tile.StaleOverlay.Text = $"STALLED — last frame {tile.LastRenderedUtc.ToLocalTime():HH:mm:ss}";
                tile.StaleOverlay.Visible = true;
            }
            else if (showNoSignal)
            {
                tile.StaleOverlay.Text = "NO SIGNAL";
                tile.StaleOverlay.Visible = true;
            }
            else
            {
                tile.StaleOverlay.Visible = false;
            }
        }
    }

    /// <summary>Per-tile self-heal reconnect — tears down <paramref name="tile"/>'s current
    /// <see cref="LiveTileSource"/> and builds a fresh one for the SAME camera into the SAME
    /// <see cref="PictureBox"/>, mutating <paramref name="tile"/> in place (never replacing the list
    /// entry) so any external bookkeeping keyed off this exact <see cref="ActiveTile"/> instance —
    /// in particular a rotating tile's separate <see cref="RotatingTile"/> entry in
    /// <see cref="_rotatingTiles"/>, which tracks rotation state (Ordinals/CurrentIndex/Badge) —
    /// stays valid across the reconnect without this method needing to know anything about
    /// rotation at all. That is exactly "preserving rotation state": self-heal never touches
    /// <see cref="_rotatingTiles"/>, so a rotating tile's next scheduled flip
    /// (<see cref="AdvanceOneRotatingTile"/>) still finds and advances it normally.</summary>
    private void ReconnectTile(ActiveTile tile, DateTime nowUtc)
    {
        // T1/R1: same fold-before-teardown rule as DisposeTiles/SwapRotatingTileSource — a per-tile
        // self-heal reconnect tears down a live source outside BuildGrid's own disposal loop, so it
        // needs its own fold call to stay consistent with the whole-session freshness signal.
        _freshnessTracker.Fold(tile.Source.LastFrameUtc);

        try
        {
            tile.Source.Shutdown();
        }
        catch
        {
            // Best-effort teardown — same rule as DisposeTiles: a stuck tile must never block a
            // reconnect attempt.
        }

        try
        {
            var previousImage = tile.Box.Image;
            tile.Box.Image = null;
            previousImage?.Dispose();
        }
        catch
        {
            // Same best-effort rule as above.
        }

        tile.RecoveryScheduler.RecordAttempt(nowUtc);
        Milestone.RecorderLocator.Logger?.Info(
            $"[{tile.TileLabel} | {tile.Camera.DisplayName}] per-tile self-heal: reconnect attempt {tile.RecoveryScheduler.AttemptCount}.");

        var box = tile.Box;
        try
        {
            var newSource = new LiveTileSource(tile.Camera.Item, Milestone.RecorderLocator.Logger, tile.RequestedWidth, tile.RequestedHeight, _maxFps, tile.TileLabel, tile.Camera.DisplayName);
            newSource.FrameReceived += bytes => OnFrameReceived(box, newSource, bytes);
            tile.Source = newSource;
            newSource.Init();
        }
        catch (Exception ex)
        {
            // A transient SDK throw here runs on the stale-sweep Timer.Tick callback, which has no
            // caller try/catch above it — never let it escape and crash the kiosk. The next sweep
            // tick's own schedule (already advanced by RecordAttempt above) governs the retry.
            Milestone.RecorderLocator.Logger?.Warning(
                $"[{tile.TileLabel} | {tile.Camera.DisplayName}] per-tile self-heal: reconnect attempt {tile.RecoveryScheduler.AttemptCount} failed to init: {ex.Message}");
        }
    }

    /// <summary>
    /// Seconds since the most recently updated live tile on this form last produced a frame — the
    /// minimal "all tiles stale since N seconds" surface Program.cs's session-loss detector needs
    /// (see <see cref="GridLookout.Recovery.SessionLossDetector"/>). Delegates the actual
    /// computation to <see cref="_freshnessTracker"/> (see <see cref="TileFreshnessTracker"/>'s doc
    /// comment for the T1/R1 page-flip-survives-teardown design and its "null only when this form
    /// has never had any tiles at all" contract).
    /// </summary>
    /// <param name="isRealFrame">Round-3 panel-3 T1: true only when the returned age's baseline is
    /// an actual folded frame timestamp, false when it's measured from this form's wall-shown
    /// baseline alone (no live source has produced a frame since the form was built — e.g. the
    /// first tick or two after a recovery rebuild during a still-ongoing outage). Meaningless when
    /// the return value is null. See <see cref="TileFreshnessTracker.ComputeFreshestAgeSeconds(DateTime, IReadOnlyList{DateTime}, bool, out bool)"/>
    /// and <see cref="GridLookout.Recovery.SessionLossDetector.ShouldMarkHealthy"/> for why callers
    /// must require this before treating a small age as evidence the session has recovered.</param>
    public double? FreshestTileAgeSeconds(out bool isRealFrame)
    {
        var liveTimestamps = _activeTiles.Select(t => t.Source.LastFrameUtc).ToList();
        return _freshnessTracker.ComputeFreshestAgeSeconds(DateTime.UtcNow, liveTimestamps, currentBuildHasTiles: _activeTiles.Count > 0, out isRealFrame);
    }

    /// <summary>
    /// Feature 2 (wall-health monitoring): a per-form snapshot of tile aggregates for Program.cs's
    /// health-write timer to fold into the process-wide <c>health.json</c>. Pure/cheap — no SDK
    /// calls, just a scan of <see cref="_activeTiles"/> — safe to call every health-write tick (5s)
    /// in addition to the existing 1s stale-sweep tick.
    /// </summary>
    public WallFormHealth GetHealthSnapshot()
    {
        var now = DateTime.UtcNow;
        int tilesWithFrames = 0;
        int stalledCount = 0;
        int neverFramedCount = 0;
        DateTime? freshestRenderedUtc = null;

        foreach (var tile in _activeTiles)
        {
            bool everRendered = tile.LastRenderedUtc != DateTime.MinValue;
            if (everRendered)
            {
                tilesWithFrames++;
                if (freshestRenderedUtc is null || tile.LastRenderedUtc > freshestRenderedUtc.Value)
                {
                    freshestRenderedUtc = tile.LastRenderedUtc;
                }

                if (_staleSeconds > 0 && (now - tile.LastRenderedUtc).TotalSeconds > _staleSeconds)
                {
                    stalledCount++;
                }
            }
            else
            {
                // A strict "zero frames ever" count, independent of whether per-tile self-heal
                // (TileRecoverSeconds) is even enabled — see WallFormHealth.NeverFramedCount's own
                // doc comment for why this must not be gated the same way self-heal ELIGIBILITY is.
                neverFramedCount++;
            }
        }

        return new WallFormHealth
        {
            MonitorNumber = MonitorNumber,
            ExpectedTileCount = _activeTiles.Count,
            TilesWithFrames = tilesWithFrames,
            StalledCount = stalledCount,
            NeverFramedCount = neverFramedCount,
            FreshestRenderedAgeSeconds = freshestRenderedUtc is DateTime freshest ? (now - freshest).TotalSeconds : (double?)null,
            // F3: cells rendered as UNAVAILABLE this grid build — never in _activeTiles at all (no
            // LiveTileSource was ever built for them), so they're correctly excluded from every
            // count above already; this is their own dedicated bucket. See WallFormHealth.UnavailableCount.
            UnavailableCount = _unavailableCount,
        };
    }

    /// <summary>
    /// F2 (multi-recorder walls): a trivial per-tile projection for Program.cs's health-write timer
    /// to fold into <c>Monitoring.RecorderHealthAggregator.Aggregate</c> — see that method's own doc
    /// comment for why the actual per-recorder classification logic lives there (pure, unit-tested)
    /// rather than here. This method itself adds NO new logic beyond <see cref="GetHealthSnapshot"/>'s
    /// existing "ever rendered" / "currently stalled" checks (same <see cref="_staleSeconds"/>
    /// threshold, same <c>tile.LastRenderedUtc"</c> reads) — it only reads already-computed per-tile
    /// state and reshapes it. Returns a materialized list (not a lazy iterator) since
    /// <see cref="_activeTiles"/> can be mutated by a later grid rebuild — a caller must never hold a
    /// deferred enumeration across that boundary.
    /// </summary>
    public IReadOnlyList<Monitoring.RecorderTileFact> GetRecorderTileFacts()
    {
        var now = DateTime.UtcNow;
        var facts = new List<Monitoring.RecorderTileFact>(_activeTiles.Count);

        foreach (var tile in _activeTiles)
        {
            bool everRendered = tile.LastRenderedUtc != DateTime.MinValue;
            bool stalled = everRendered && _staleSeconds > 0 && (now - tile.LastRenderedUtc).TotalSeconds > _staleSeconds;
            facts.Add(new Monitoring.RecorderTileFact(tile.Camera.RecorderId, tile.Camera.RecorderName, everRendered, stalled));
        }

        return facts;
    }

    /// <summary>F3: what <see cref="BuildGrid"/>'s resolver produces for one grid cell — either a
    /// live camera tile (<see cref="Camera"/> non-null) or the UNAVAILABLE placeholder
    /// (<see cref="Camera"/> null, <see cref="UnavailableReason"/> set). Replaces the pre-F3
    /// <c>(CameraInfo?, int ordinal, string? error, IReadOnlyList&lt;int&gt;?)</c> tuple: the caption
    /// prefix is now a string (an ordinal number, an alias, or a short guid — see
    /// <see cref="CellMember.RefLabel"/>) instead of an int, and the single <c>error</c> concept is
    /// unified into <see cref="UnavailableReason"/> (F3 rule 5 — this retires the pre-F3 red
    /// "Camera N — invalid" tile in favor of ONE placeholder presentation for every kind of
    /// unresolvable reference, ordinal included).</summary>
    /// <param name="Camera">Null means render the UNAVAILABLE placeholder instead of a live tile.</param>
    /// <param name="CaptionPrefix">The legend text shown before the camera name when
    /// <see cref="Camera"/> is non-null (<c>"{CaptionPrefix}: {Camera.Name}"</c>) — an ordinal
    /// number, an alias, or a short guid depending on how the cell was written; see F3 point 8.</param>
    /// <param name="UnavailableReason">Non-null only when <see cref="Camera"/> is null — the reason
    /// text shown as <c>"UNAVAILABLE — {UnavailableReason}"</c>.</param>
    /// <param name="RotationMembers">Non-null with Count &gt; 1 only for a resolved-plan matrix cell
    /// written in the rotation form — see <see cref="RenderResolvedPage"/>. Auto-layout's resolver
    /// always passes null (no per-tile rotation on that path).</param>
    /// <param name="RotationStartIndex">Index into <see cref="RotationMembers"/> of the member
    /// currently shown (i.e. of <see cref="CaptionPrefix"/>/<see cref="Camera"/>) — computed by the
    /// caller (<see cref="RenderResolvedPage"/>) rather than re-derived here by matching
    /// <see cref="CaptionPrefix"/> text back against the list, which would be ambiguous if two
    /// members happened to share a label (e.g. an alias literally named the same as an ordinal
    /// digit-string).</param>
    private sealed record CellRenderSpec(
        CameraInfo? Camera,
        string CaptionPrefix,
        string? UnavailableReason,
        IReadOnlyList<ResolvedMember>? RotationMembers,
        int RotationStartIndex);

    /// <param name="spec">What to render — see <see cref="CellRenderSpec"/>.</param>
    /// <param name="tileLabel">This cell's grid position (e.g. <c>"M1 R1C2"</c>), computed by the
    /// caller from <see cref="MonitorNumber"/> and the row/col loop indices — passed straight
    /// through to <see cref="LiveTileSource"/> so its log lines can be told apart from another tile
    /// showing the SAME camera (a <c>$layout{}</c> matrix may repeat a reference across cells; see
    /// that constructor's <c>tileLabel</c> doc comment).</param>
    /// <param name="requestedWidth">Live-stream frame width to request for this tile — see
    /// <see cref="ComputeTileRequestSize"/>.</param>
    /// <param name="requestedHeight">Live-stream frame height to request for this tile — see
    /// <see cref="ComputeTileRequestSize"/>.</param>
    /// <param name="cameraCatalog">The camera-id lookup a rotating tile resolves its next member
    /// against at flip time — see <see cref="RotatingTile"/>.</param>
    private Control BuildCell(CellRenderSpec spec, string tileLabel, int requestedWidth, int requestedHeight,
        IReadOnlyDictionary<Guid, CameraInfo>? cameraCatalog, List<RotatingTile>? rotatingTilesOut)
    {
        bool isRotating = spec.RotationMembers is not null && spec.RotationMembers.Count > 1;
        int rotationIndex = spec.RotationStartIndex;
        var cellPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            Margin = new Padding(_tileBorderWidth),
        };
        // Double-click anywhere on a tile toggles fullscreen/compact (see ToggleCompact()). The
        // panel itself rarely receives the click directly (its children are Dock.Fill/Dock.Top and
        // cover the whole area) but every child added below is wired individually anyway.
        cellPanel.MouseDoubleClick += (_, _) => ToggleCompact();

        if (spec.Camera is not null)
        {
            var camera = spec.Camera;
            var pictureBox = new ScalableTilePictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                ScaleMode = _tileScaleMode,
            };
            pictureBox.MouseDoubleClick += (_, _) => ToggleCompact();
            cellPanel.Controls.Add(pictureBox);

            Label? caption = null;
            if (_showHeader)
            {
                caption = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 24,
                    // Reference-label prefix is the operator's legend for writing $layout{} tokens:
                    // an ordinal number, an alias, or a short guid — whatever form the cell was
                    // written in (F3 point 8). Rotation state is NOT part of the caption — it lives
                    // on the always-visible badge below, so it survives ShowHeader: false.
                    // F2 (multi-recorder walls): DisplayName is "RecorderName / Name" in multi mode
                    // (distinguishes duplicate camera names across recorders), plain Name unchanged
                    // in single-recorder mode — see CameraInfo.DisplayName's own doc comment.
                    Text = $"{spec.CaptionPrefix}: {camera.DisplayName}",
                    ForeColor = Color.WhiteSmoke,
                    BackColor = Color.FromArgb(0x20, 0x20, 0x20),
                    Font = CaptionFont,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(6, 0, 0, 0),
                };
                caption.MouseDoubleClick += (_, _) => ToggleCompact();
                cellPanel.Controls.Add(caption);
            }

            // Rotation watermark: a small "⟳ index/total" badge pinned to the tile's top-right
            // corner, deliberately INDEPENDENT of ShowHeader — a public wall with captions off
            // must still disclose which tiles cycle. Anchored right via a Resize handler because
            // the cell's final size doesn't exist yet at build time.
            Label? rotationBadge = null;
            if (isRotating)
            {
                rotationBadge = new Label
                {
                    AutoSize = true,
                    Text = $"⟳ {rotationIndex + 1}/{spec.RotationMembers!.Count}",
                    ForeColor = Color.WhiteSmoke,
                    BackColor = Color.FromArgb(0x30, 0x30, 0x30),
                    Font = RotatingCaptionFont,
                    Padding = new Padding(5, 2, 5, 2),
                };
                rotationBadge.MouseDoubleClick += (_, _) => ToggleCompact();
                cellPanel.Controls.Add(rotationBadge);
                var badge = rotationBadge;
                void PositionBadge() => badge.Location = new Point(
                    Math.Max(0, cellPanel.ClientSize.Width - badge.Width - 8),
                    (_showHeader ? 24 : 0) + 8);
                cellPanel.Resize += (_, _) => PositionBadge();
                badge.SizeChanged += (_, _) => PositionBadge();
                PositionBadge();
                badge.BringToFront();
            }

            // Least-intrusive placement: docked Top like the caption, added after it (or after
            // just the picture box when ShowHeader is off) so it claims the strip immediately
            // below the caption rather than displacing it — same add-order convention that already
            // makes the caption itself render correctly against the Dock.Fill picture box (see the
            // constructor's z-order comment). Hidden by default; SweepStaleTiles toggles it.
            var staleOverlay = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = Color.White,
                BackColor = StaleOverlayBackColor,
                Font = OverlayFont,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false,
            };
            staleOverlay.MouseDoubleClick += (_, _) => ToggleCompact();
            cellPanel.Controls.Add(staleOverlay);

            var source = new LiveTileSource(camera.Item, Milestone.RecorderLocator.Logger, requestedWidth, requestedHeight, _maxFps, tileLabel, camera.DisplayName);
            source.FrameReceived += bytes => OnFrameReceived(pictureBox, source, bytes);
            var activeTile = new ActiveTile(source, pictureBox, staleOverlay, camera, requestedWidth, requestedHeight, tileLabel,
                tileStartUtc: DateTime.UtcNow, recoveryScheduler: new TileRecoveryScheduler(_tileRecoverSeconds));
            _activeTiles.Add(activeTile);
            source.Init();

            if (isRotating && cameraCatalog is not null && rotatingTilesOut is not null)
            {
                rotatingTilesOut.Add(new RotatingTile(spec.RotationMembers!, rotationIndex, pictureBox, caption, rotationBadge, staleOverlay,
                    requestedWidth, requestedHeight, cameraCatalog, tileLabel));
            }
        }
        else
        {
            // F3 rule 5: ONE placeholder presentation for every unresolvable reference — an unknown
            // alias/guid, a still-out-of-range ordinal, or a well-formed reference to a now-missing/
            // disabled camera. Never a live tile, never silently substituting another camera.
            _unavailableCount++;
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = $"UNAVAILABLE — {spec.UnavailableReason ?? spec.CaptionPrefix}",
                ForeColor = UnavailableForeColor,
                BackColor = UnavailableBackColor,
                Font = ErrorFont,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            label.MouseDoubleClick += (_, _) => ToggleCompact();
            cellPanel.Controls.Add(label);
        }

        return cellPanel;
    }

    /// <param name="source">T2/R10: the specific <see cref="LiveTileSource"/> that produced this
    /// frame — captured at subscribe time by both call sites (<see cref="BuildCell"/> and
    /// <see cref="SwapRotatingTileSource"/>) so the guard below can tell a stale frame from an
    /// OUTGOING rotating-tile source apart from a fresh one from the INCOMING source, even though
    /// both target the same <paramref name="pictureBox"/>.</param>
    private void OnFrameReceived(PictureBox pictureBox, LiveTileSource source, byte[] jpegBytes)
    {
        if (pictureBox.IsDisposed)
        {
            return;
        }

        try
        {
            // net48's Control.BeginInvoke(Delegate) has no Action-accepting overload (that was
            // added in .NET Core/5+ WinForms) — a bare lambda doesn't convert to the non-specific
            // `Delegate` parameter type, so it must be wrapped in a concrete delegate type.
            pictureBox.BeginInvoke(new MethodInvoker(() =>
            {
                if (pictureBox.IsDisposed)
                {
                    return;
                }

                // T2/R10: a rotating-tile swap (SwapRotatingTileSource) tears down the OUTGOING
                // source and repoints this SAME PictureBox at a brand new INCOMING source — but
                // LiveTileSource.OnLiveContent runs on an SDK callback thread and can synchronously
                // invoke FrameReceived (queuing this very BeginInvoke) a moment before Shutdown()
                // unhooks it, so a frame from the OUTGOING source can still reach here after the
                // swap has already happened. Look up the box's CURRENT active source in
                // _activeTiles (not a snapshot taken when this frame was queued) and drop the frame
                // if it doesn't match — otherwise the previous camera's last frame paints under the
                // new camera's caption/badge, possibly until the new source's own first frame
                // arrives (or indefinitely, if it never does).
                int activeIndex = _activeTiles.FindIndex(t => ReferenceEquals(t.Box, pictureBox));
                object? currentActiveSource = activeIndex >= 0 ? _activeTiles[activeIndex].Source : null;
                if (TileSourceGuard.ShouldDropFrame(currentActiveSource, source))
                {
                    return;
                }

                try
                {
                    // E7/I9: JpegFrameDecoder.Decode intentionally never disposes its backing
                    // MemoryStream (GDI+ ties an Image built via Image.FromStream to that
                    // stream's lifetime for as long as the Image is used — see the decoder's doc
                    // comment) — safe to keep on pictureBox.Image indefinitely, unlike the old
                    // inline decode here which disposed the MemoryStream out from under the
                    // displayed Image. previous?.Dispose() below still frees this frame's Image
                    // (and, transitively, its now-unreferenced stream) every tick.
                    var image = JpegFrameDecoder.Decode(jpegBytes);
                    var previous = pictureBox.Image;
                    pictureBox.Image = image;
                    previous?.Dispose(); // avoid GDI handle leak on every frame

                    // Feature 2 (wall-health monitoring): LastRenderedUtc is set ONLY here — after
                    // decode AND UI-thread PictureBox assignment both succeeded — unlike
                    // LiveTileSource.LastFrameUtc, which advances on the SDK callback thread before
                    // any paint happens (see that property's own doc comment). This is the signal a
                    // hung UI thread cannot fake: SDK callback threads can keep delivering bytes
                    // while the message pump is wedged, but nothing reaches THIS line if it is.
                    //
                    // Feature 1 (per-tile self-heal) also reads this SAME sticky, per-tile timestamp
                    // (not the current Source's own LastFrameUtc) for its stale/never-framed
                    // classification and STALLED-overlay text — see SweepStaleTiles. Using one
                    // shared value is deliberate: after a self-heal reconnect the freshly-built
                    // Source's own LastFrameUtc resets to MinValue, which would otherwise make a
                    // tile that WAS receiving frames look "never framed" the instant it self-heals,
                    // flipping its overlay from STALLED to NO SIGNAL and corrupting the health
                    // aggregate (TilesWithFrames/NeverFramedCount) for a wall that has merely
                    // recovered once — exactly the bug TileFreshnessTracker's fold-before-teardown
                    // design already had to solve one level up, for the whole-session signal.
                    if (activeIndex >= 0)
                    {
                        var activeTile = _activeTiles[activeIndex];
                        activeTile.LastRenderedUtc = DateTime.UtcNow;
                        if (activeTile.RecoveryScheduler.AttemptCount > 0)
                        {
                            Milestone.RecorderLocator.Logger?.Info(
                                $"[{activeTile.TileLabel} | {activeTile.Camera.DisplayName}] tile recovered after {activeTile.RecoveryScheduler.AttemptCount} attempt(s).");
                        }
                        // Unconditional, not just when AttemptCount > 0 — a tile that never needed
                        // recovery at all just resets an already-empty schedule; a tile that DID
                        // need it must clear its backoff on the FIRST real frame, not merely on a
                        // successful Init() call (the SDK can report success without ever actually
                        // delivering data), so the NEXT unrelated outage starts its own schedule
                        // fresh instead of inheriting wherever this one's backoff had climbed to.
                        activeTile.RecoveryScheduler.Reset();
                    }
                }
                catch
                {
                    // A single corrupt/partial frame must never crash the tile.
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // Handle not created yet / form closing race — drop the frame.
        }
    }

    private void DisposeTiles()
    {
        foreach (var tile in _activeTiles)
        {
            var source = tile.Source;
            var box = tile.Box;

            // T1/R1: fold this tile's last-frame timestamp into the freshness high-water mark
            // BEFORE tearing it down — see TileFreshnessTracker's doc comment. Covers every
            // disposal path through this method: page/matrix flips (BuildGrid), ShowStatus/
            // ShowNoCameras, and OnFormClosed.
            _freshnessTracker.Fold(source.LastFrameUtc);

            try
            {
                source.Shutdown(); // unhooks SDK + FrameReceived events, then closes — see its doc
            }
            catch
            {
                // Best-effort teardown — a stuck tile must never block a grid rebuild.
            }

            try
            {
                // PictureBox.Dispose() does NOT dispose its assigned Image — without this the
                // final decoded frame (~2.7 MB bitmap per 720p tile) leaks on every grid rebuild.
                var lastFrame = box.Image;
                box.Image = null;
                lastFrame?.Dispose();
            }
            catch
            {
                // Same best-effort rule as above.
            }
        }
        _activeTiles.Clear();
    }

    private void ClearBody()
    {
        // The fallback-warning label and (when enabled) the header strip are persistent chrome —
        // only the grid/status content underneath gets torn down and rebuilt.
        var toRemove = Controls.Cast<Control>()
            .Where(c => c != _fallbackWarningLabel && c != _headerStrip)
            .ToList();
        foreach (var control in toRemove)
        {
            Controls.Remove(control);
            control.Dispose();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeTiles();
        _headerClock?.Stop();
        _headerClock?.Dispose();
        _staleSweepTimer?.Stop();
        _staleSweepTimer?.Dispose();
        _pageTimer?.Stop();
        _pageTimer?.Dispose();
        _tileRotateTimer?.Stop();
        _tileRotateTimer?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Runs on <see cref="_tileRotateTimer"/>'s tick — advances every currently-registered
    /// rotating tile by one step (see <see cref="AdvanceOneRotatingTile"/>). All rotating tiles on
    /// the page flip in lockstep since they share this one timer.</summary>
    private void AdvanceRotatingTiles()
    {
        foreach (var tile in _rotatingTiles)
        {
            AdvanceOneRotatingTile(tile);
        }
    }

    /// <summary>Advances one rotating tile to the next AVAILABLE member in its list, wrapping
    /// around; an unavailable member encountered along the way is skipped (logged once per bad
    /// reference per tile — see <see cref="RotatingTile.WarnedUnavailableRefs"/>) rather than
    /// erroring the tile. If every member is unavailable (e.g. every referenced camera vanished on
    /// a live config refresh after this tile was built), the tile is left showing its last good
    /// frame — the next config-refresh rebuild will re-resolve it as the UNAVAILABLE placeholder via
    /// <see cref="RenderResolvedPage"/> the normal way.</summary>
    private void AdvanceOneRotatingTile(RotatingTile tile)
    {
        if (tile.PictureBox.IsDisposed)
        {
            return;
        }

        var members = tile.Members;
        for (int step = 1; step <= members.Count; step++)
        {
            int nextIndex = (tile.CurrentIndex + step) % members.Count;
            var candidate = members[nextIndex];

            if (candidate.Available)
            {
                SwapRotatingTileSource(tile, nextIndex, candidate);
                return;
            }

            if (tile.WarnedUnavailableRefs.Add(candidate.RefLabel))
            {
                Milestone.RecorderLocator.Logger?.Warning(
                    $"Rotating tile: '{candidate.RefLabel}' is unavailable ({candidate.UnavailableReason}) — skipped.");
            }
        }
    }

    /// <summary>Flips <paramref name="tile"/> onto <paramref name="nextMember"/>: tears down its
    /// current <see cref="LiveTileSource"/>, builds a new one for the next camera (same
    /// fit-size/MaxFps parameters the tile was originally built with), rewires
    /// <see cref="LiveTileSource.FrameReceived"/> to the SAME <see cref="PictureBox"/>, updates the
    /// caption, resets the stale overlay, and MUTATES the tile's <see cref="ActiveTile"/> entry in
    /// <see cref="_activeTiles"/> in place (same camera/source/never-framed clock/recovery-schedule
    /// fields <see cref="ReconnectTile"/> mutates) so <see cref="SweepStaleTiles"/> tracks the new
    /// source instead of the one just shut down. <see cref="ActiveTile.LastRenderedUtc"/>,
    /// <see cref="ActiveTile.TileStartUtc"/>, and the tile's <see cref="TileRecoveryScheduler"/> are
    /// all reset here — a rotation swap shows a GENUINELY DIFFERENT camera, so any staleness/backoff
    /// history belonging to the outgoing camera would be actively misleading applied to the
    /// incoming one.</summary>
    private void SwapRotatingTileSource(RotatingTile tile, int nextIndex, ResolvedMember nextMember)
    {
        var pictureBox = tile.PictureBox;
        int activeIndex = _activeTiles.FindIndex(t => ReferenceEquals(t.Box, pictureBox));
        if (activeIndex < 0)
        {
            // Torn down elsewhere (grid rebuild raced the tick) — nothing to flip.
            return;
        }

        // F3: BuildAvailability (Layout.LayoutResolver) only marks a member Available when its
        // catalog lookup already succeeded, so CameraId is expected to be set — but this method
        // runs on a Timer.Tick well after that resolve happened, so re-check rather than trust it
        // blindly across that gap.
        if (!nextMember.CameraId.HasValue || !tile.CameraCatalog.TryGetValue(nextMember.CameraId.Value, out var nextCamera))
        {
            Milestone.RecorderLocator.Logger?.Warning(
                $"Rotating tile: camera for '{nextMember.RefLabel}' is no longer in the catalog — leaving the tile on its last frame.");
            return;
        }

        var activeTile = _activeTiles[activeIndex];
        var oldSource = activeTile.Source;
        var box = activeTile.Box;
        var staleOverlay = activeTile.StaleOverlay;
        // T1/R1: same fold-before-teardown rule as DisposeTiles() — a per-tile rotation swap tears
        // down a live source outside BuildGrid's own disposal loop, so it needs its own fold call to
        // stay consistent with the high-water mark's "survives every teardown" design.
        _freshnessTracker.Fold(oldSource.LastFrameUtc);
        try
        {
            oldSource.Shutdown();
        }
        catch
        {
            // Best-effort teardown — same rule as DisposeTiles: a stuck tile must never block a
            // rotation flip.
        }

        try
        {
            var previousImage = box.Image;
            box.Image = null;
            previousImage?.Dispose();
        }
        catch
        {
            // Same best-effort rule as above.
        }

        try
        {
            var newSource = new LiveTileSource(nextCamera.Item, Milestone.RecorderLocator.Logger, tile.RequestedWidth, tile.RequestedHeight, _maxFps, tile.TileLabel, nextCamera.DisplayName);
            newSource.FrameReceived += bytes => OnFrameReceived(box, newSource, bytes);
            activeTile.Source = newSource;
            activeTile.Camera = nextCamera;
            activeTile.TileStartUtc = DateTime.UtcNow;
            activeTile.LastRenderedUtc = DateTime.MinValue;
            activeTile.RecoveryScheduler.Reset();
            newSource.Init();
        }
        catch (Exception ex)
        {
            // A transient SDK throw here runs on the Timer.Tick callback, which has no caller
            // try/catch above it (unlike a grid (re)build's Init() calls, wrapped by Program.cs's
            // refresh-timer handler) — never let it escape and crash the kiosk. CurrentIndex is
            // deliberately left UNADVANCED so the next tick retries this same member rather than
            // silently skipping it; the tile just sits on its last frame (already cleared above)
            // until a retry succeeds or a config-refresh rebuild replaces it outright.
            Milestone.RecorderLocator.Logger?.Warning(
                $"Rotating tile: failed to start live source for '{nextMember.RefLabel}' ({nextCamera.DisplayName}): {ex.Message}");
            return;
        }

        tile.CurrentIndex = nextIndex;

        if (tile.Caption is not null)
        {
            tile.Caption.Text = $"{nextMember.RefLabel}: {nextCamera.DisplayName}";
        }

        if (tile.Badge is not null)
        {
            // index/total are the tile's rotation-set position, not the camera's ordinal (see
            // BuildCell's rotationIndex comment).
            tile.Badge.Text = $"⟳ {nextIndex + 1}/{tile.Members.Count}";
        }

        staleOverlay.Visible = false;
    }

    /// <summary>One built grid cell showing live video — the state <see cref="SweepStaleTiles"/>,
    /// <see cref="ReconnectTile"/>, <see cref="OnFrameReceived"/>, and <see cref="SwapRotatingTileSource"/>
    /// all read/mutate for a single tile. A CLASS (not the plain tuple this list used before
    /// per-tile self-heal existed) specifically so a reconnect can MUTATE <see cref="Source"/> (and
    /// the other fields a rotation swap also updates) in place rather than replacing the list entry
    /// — mutating in place means any external state keyed off a specific instance (in particular a
    /// rotating tile's separate <see cref="RotatingTile"/> entry, looked up by
    /// <see cref="PictureBox"/> reference, never by holding onto an <see cref="ActiveTile"/> itself)
    /// never goes stale across a reconnect.</summary>
    private sealed class ActiveTile
    {
        public ActiveTile(LiveTileSource source, PictureBox box, Label staleOverlay, CameraInfo camera,
            int requestedWidth, int requestedHeight, string tileLabel, DateTime tileStartUtc, TileRecoveryScheduler recoveryScheduler)
        {
            Source = source;
            Box = box;
            StaleOverlay = staleOverlay;
            Camera = camera;
            RequestedWidth = requestedWidth;
            RequestedHeight = requestedHeight;
            TileLabel = tileLabel;
            TileStartUtc = tileStartUtc;
            RecoveryScheduler = recoveryScheduler;
        }

        /// <summary>The live source currently feeding this tile — reassigned in place by
        /// <see cref="ReconnectTile"/> (per-tile self-heal) and <see cref="SwapRotatingTileSource"/>
        /// (rotation flip); never replaced by discarding and re-adding this <see cref="ActiveTile"/>
        /// itself.</summary>
        public LiveTileSource Source { get; set; }

        public PictureBox Box { get; }

        public Label StaleOverlay { get; }

        /// <summary>The camera CURRENTLY showing in this tile — the same camera for a fixed cell's
        /// entire lifetime, or whichever camera a rotating cell most recently flipped to.</summary>
        public CameraInfo Camera { get; set; }

        public int RequestedWidth { get; }

        public int RequestedHeight { get; }

        public string TileLabel { get; }

        /// <summary>When THIS grid cell (in its CURRENT camera) started — the baseline
        /// <see cref="SweepStaleTiles"/>'s never-framed threshold measures against. Set at
        /// construction (<see cref="BuildCell"/>) and reset by <see cref="SwapRotatingTileSource"/>
        /// on every rotation flip (a genuinely different camera deserves its own fresh grace period)
        /// — deliberately NOT reset by <see cref="ReconnectTile"/> (a per-tile self-heal reconnect
        /// is the SAME camera failing to come back, not a fresh start).</summary>
        public DateTime TileStartUtc { get; set; }

        /// <summary>Sticky per-tile "last successfully rendered" timestamp — set ONLY by
        /// <see cref="OnFrameReceived"/>, after JPEG decode AND UI-thread <see cref="PictureBox"/>
        /// assignment both succeed. <see cref="DateTime.MinValue"/> until the first render. THE
        /// signal both per-tile self-heal (STALLED/NO SIGNAL classification, reconnect eligibility)
        /// and wall-health monitoring (<see cref="GetHealthSnapshot"/>) use — deliberately NOT the
        /// current <see cref="Source"/>'s own <see cref="LiveTileSource.LastFrameUtc"/>, which resets
        /// to <see cref="DateTime.MinValue"/> on every reconnect and would otherwise make a tile that
        /// WAS receiving frames look "never framed" the instant it self-heals.</summary>
        public DateTime LastRenderedUtc { get; set; } = DateTime.MinValue;

        /// <summary>This tile's own per-tile self-heal backoff schedule — see
        /// <see cref="GridLookout.Monitoring.TileRecoveryScheduler"/>. One instance per grid cell,
        /// reused (never replaced) across reconnects so its attempt count/schedule persists exactly
        /// as long as the underlying problem does; reset by <see cref="OnFrameReceived"/> on the
        /// first frame after a recovery and by <see cref="SwapRotatingTileSource"/> on every
        /// rotation flip.</summary>
        public TileRecoveryScheduler RecoveryScheduler { get; }
    }

    /// <summary>F3: one matrix cell written in the rotation form (e.g.
    /// <c>A(3,@yard-east,@{guid})</c>) — tracks enough state for <see cref="_tileRotateTimer"/>'s
    /// tick to flip it: which member it's currently showing (<see cref="CurrentIndex"/> into
    /// <see cref="Members"/>), the controls to update
    /// (<see cref="PictureBox"/>/<see cref="Caption"/>/<see cref="StaleOverlay"/>), the exact
    /// request-size parameters its <see cref="LiveTileSource"/> was originally built with, and the
    /// camera-id catalog to resolve the next member against. Built fresh by <see cref="BuildCell"/>
    /// on every grid (re)build — including every page flip — so a tile's rotation always restarts
    /// from its first available member when its page comes back around.</summary>
    private sealed class RotatingTile
    {
        public RotatingTile(IReadOnlyList<ResolvedMember> members, int currentIndex, PictureBox pictureBox, Label? caption,
            Label? badge, Label staleOverlay, int requestedWidth, int requestedHeight, IReadOnlyDictionary<Guid, CameraInfo> cameraCatalog,
            string tileLabel)
        {
            Members = members;
            CurrentIndex = currentIndex;
            PictureBox = pictureBox;
            Caption = caption;
            Badge = badge;
            StaleOverlay = staleOverlay;
            RequestedWidth = requestedWidth;
            RequestedHeight = requestedHeight;
            CameraCatalog = cameraCatalog;
            TileLabel = tileLabel;
        }

        public IReadOnlyList<ResolvedMember> Members { get; }

        public int CurrentIndex { get; set; }

        public PictureBox PictureBox { get; }

        public Label? Caption { get; }

        /// <summary>The always-visible "⟳ index/total" watermark badge (top-right of the tile) —
        /// present regardless of ShowHeader; the caption only exists when headers are on.</summary>
        public Label? Badge { get; }

        public Label StaleOverlay { get; }

        public int RequestedWidth { get; }

        public int RequestedHeight { get; }

        public IReadOnlyDictionary<Guid, CameraInfo> CameraCatalog { get; }

        /// <summary>This cell's grid position (see <see cref="WallForm.BuildCell"/>'s <c>tileLabel</c>
        /// doc comment) — reused by <see cref="SwapRotatingTileSource"/> so every camera this tile
        /// rotates through is tagged with the SAME cell identity across the swap, not just the one
        /// the tile was originally built with.</summary>
        public string TileLabel { get; }

        /// <summary>Unavailable reference labels already logged for THIS tile instance (a fresh
        /// instance is built on every grid rebuild/page flip, so this naturally resets to "not yet
        /// warned" each time) — caps log spam to one Warning per unavailable reference per build,
        /// not one per tick.</summary>
        public HashSet<string> WarnedUnavailableRefs { get; } = new();
    }
}
