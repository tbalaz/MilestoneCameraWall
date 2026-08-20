using GridLookout.Logging;
using VideoOS.Platform;
using VideoOS.Platform.Live;

namespace GridLookout.UI;

/// <summary>
/// Live-video source per tile, built on the public <see cref="JPEGLiveSource"/> —
/// <c>SDKImageViewerControl</c> is internal to its assembly and cannot be constructed externally,
/// hence this fallback. Frames arrive via the public <c>LiveContentEvent</c>; <c>LiveStatusEvent</c>
/// and the event args' public <c>Exception</c> field (fields, not properties) are logged so a dark
/// tile is always explained in the log instead of failing silently. Requested frame size and FPS
/// cap are both plain SDK properties set before <c>Init()</c> (see the constructor) — no
/// client-side throttling needed for FPS; the SDK's own <c>FPS</c> property does the job.
/// </summary>
public sealed class LiveTileSource : JPEGLiveSource
{
    private readonly string _cameraName;
    private readonly string _logPrefix;
    private readonly FileLogger? _logger;
    private long _frameCount;
    private long _lastFrameUtcTicks;

    public event Action<byte[]>? FrameReceived;

    /// <summary>UTC timestamp of the most recently received live frame; <see cref="DateTime.MinValue"/>
    /// until the first frame has arrived. Frames land on SDK callback threads (not the UI thread),
    /// so the backing tick count is written/read via <see cref="System.Threading.Interlocked"/>
    /// rather than a lock — a plain <c>volatile DateTime</c> isn't legal (DateTime isn't a
    /// reference/primitive type volatile supports), and Interlocked on the raw ticks gives the same
    /// tear-free read/write guarantee.</summary>
    public DateTime LastFrameUtc =>
        new(System.Threading.Interlocked.Read(ref _lastFrameUtcTicks), DateTimeKind.Utc);

    /// <param name="width">Requested live-stream frame width in pixels. Default 1280 (the flat
    /// size) for callers that don't care; <c>WallForm</c> passes the tile's actual on-screen size
    /// when <see cref="GridLookout.Config.WallConfig.FitFrameSizeToTile"/> is true.</param>
    /// <param name="height">Requested live-stream frame height in pixels. Default 720.</param>
    /// <param name="maxFps">Caps the requested frame rate via the SDK's own <see cref="VideoOS.Platform.Live.VideoLiveSource.FPS"/>
    /// property (declared on the <c>JPEGLiveSource</c> base class, public read/write, doc'd
    /// "Possible downscale of FPS" — this sets the SDK's own throttle rather than a client-side
    /// one). 0 (default) leaves <c>FPS</c> untouched, i.e. the server's native/default rate
    /// applies.</param>
    /// <param name="tileLabel">Identifies which grid cell this source belongs to (e.g.
    /// <c>"M1 R1C2"</c>) — see <see cref="GridLookout.UI.WallForm.BuildCell"/>. A <c>$layout{}</c>
    /// matrix can legitimately show the SAME camera in multiple tiles (duplicate ordinals, e.g.
    /// <c>$layout{A1,B2,C3,A3,A2,A1}</c>), each owning its own <see cref="LiveTileSource"/> — with
    /// no tile identity in the log line, that produces log entries that are byte-identical down to
    /// the millisecond and read as a double-fire bug when it's actually N independent tiles each
    /// reporting their own (genuinely separate) status. Null (default) falls back to the pre-tile-label
    /// format, unchanged, for any caller that doesn't have a grid position to offer.</param>
    /// <param name="displayName">F2 (multi-recorder walls): the log-label text to use in place of
    /// <paramref name="item"/>.Name — <c>WallForm</c> passes <c>CameraInfo.DisplayName</c>
    /// ("RecorderName / Name" in multi-recorder mode, plain Name in single-recorder mode). Null
    /// (default) falls back to <paramref name="item"/>.Name unchanged, exactly the pre-F2
    /// behavior.</param>
    public LiveTileSource(Item item, FileLogger? logger = null, int width = 1280, int height = 720, int maxFps = 0, string? tileLabel = null, string? displayName = null) : base(item)
    {
        _cameraName = displayName ?? item.Name;
        _logPrefix = tileLabel is null ? $"[{_cameraName}]" : $"[{tileLabel} | {_cameraName}]";
        _logger = logger;

        LiveModeStart = true;
        // 0x0 asks the server for native resolution; some drivers refuse it. Request a concrete
        // size and let the server letterbox — the PictureBox zoom-scales the delivered frame
        // anyway, so the exact number only bounds bandwidth/decode cost.
        Width = width;
        Height = height;
        SetKeepAspectRatio(true, false);
        Compression = 75;
        if (maxFps > 0)
        {
            FPS = maxFps;
        }

        LiveContentEvent += OnLiveContent;
        LiveStatusEvent += OnLiveStatus;
    }

    private void OnLiveContent(object sender, EventArgs e)
    {
        var args = e as LiveContentEventArgs;
        if (args is null)
        {
            return;
        }

        if (args.Exception is not null)
        {
            _logger?.Warning($"{_logPrefix} live content error: {args.Exception.Message}");
            return;
        }

        var bytes = args.LiveContent?.Content;
        if (bytes is { Length: > 0 })
        {
            System.Threading.Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
            long n = System.Threading.Interlocked.Increment(ref _frameCount);
            if (n == 1)
            {
                _logger?.Info($"{_logPrefix} first live frame received ({bytes.Length} bytes, {args.LiveContent!.Width}x{args.LiveContent.Height}).");
            }
            else if (n % 300 == 0)
            {
                _logger?.Debug($"{_logPrefix} {n} frames received.");
            }

            try
            {
                // Defensive copy: the SDK may pool/reuse the content buffer after the callback.
                FrameReceived?.Invoke((byte[])bytes.Clone());
            }
            catch
            {
                // A single bad/partial frame must never take down the tile or the callback thread.
            }
        }
    }

    /// <summary>Teardown for grid rebuilds: unhook the SDK events and drop all
    /// <see cref="FrameReceived"/> subscribers BEFORE closing, so neither the SDK's event lists
    /// nor the tile's PictureBox closure keep this source (and its live session) rooted after the
    /// tile is gone — <c>Close()</c> alone does not remove managed event subscriptions, which is
    /// a per-rebuild leak of the whole source object graph.</summary>
    public void Shutdown()
    {
        LiveContentEvent -= OnLiveContent;
        LiveStatusEvent -= OnLiveStatus;
        FrameReceived = null;
        Close();
    }

    /// <summary>Bounded retry count for the LiveModeStart "kick" below — a one-shot attempt burned
    /// itself even on a thrown exception (the toggle never actually ran), so a single transient SDK
    /// hiccup on the FIRST status callback permanently gave up on a tile that a later attempt would
    /// have fixed. Capped (not unlimited) so a camera that is genuinely, persistently down doesn't
    /// get an endless stream of kick attempts on every LiveStatusEvent for as long as the tile
    /// exists — three tries, naturally spaced by however often the SDK raises status events, is
    /// enough to recover a transient session-establishment hiccup without masking a real outage.</summary>
    private const int MaxKickAttempts = 3;
    private int _kickCount;

    private void OnLiveStatus(object sender, EventArgs e)
    {
        if (e is LiveStatusEventArgs status)
        {
            _logger?.Info($"{_logPrefix} live status: current={status.CurrentStatusFlags}, changed={status.ChangedStatusFlags}");

            // Some cameras reach LiveFeed while this client session stays ClientLiveStopped and no
            // content events arrive. Toggling LiveModeStart re-sends the start-live command on the
            // established session — do it up to MaxKickAttempts times, spaced by whichever status
            // events show the feed still up with zero frames delivered.
            if (_kickCount < MaxKickAttempts
                && System.Threading.Interlocked.Read(ref _frameCount) == 0
                && status.CurrentStatusFlags.HasFlag(VideoOS.Platform.Live.StatusFlags.LiveFeed))
            {
                _logger?.Info($"{_logPrefix} LiveFeed up but no frames — re-toggling LiveModeStart (attempt {_kickCount + 1}/{MaxKickAttempts}).");
                try
                {
                    LiveModeStart = false;
                    LiveModeStart = true;
                    // Only counts as a consumed attempt once the toggle itself actually ran without
                    // throwing — an exception here means the SDK never even attempted the restart,
                    // so it must not burn one of the bounded MaxKickAttempts tries; the very next
                    // status event showing LiveFeed-up-but-no-frames gets a fresh, un-throttled
                    // attempt instead of one fewer chance than a healthy session gets.
                    _kickCount++;
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"{_logPrefix} LiveModeStart toggle failed: {ex.Message}");
                }
            }
        }
    }
}
