using DbdMaxing.Core.Data;
using DbdMaxing.Core.Models;
using DbdMaxing.Vision.Capture;
using DbdMaxing.Vision.Matching;
using OpenCvSharp;

namespace DbdMaxing.Vision;

/// <summary>
/// Background loop: while the game window is the foreground window, capture it (screen pixels only),
/// skip unchanged frames, match perk icons and raise <see cref="PerksDetected"/>.
/// ANTI-CHEAT: no process handle is ever opened; the game is identified by its window's owning pid.
/// </summary>
public sealed class PerkScanner : IPerkScanner
{
    public const string StatusScanning = "Scanning…";
    public const string StatusNotFocused = "Game not focused";
    public const string StatusStopped = "Stopped";
    public const string StatusNoIcons = "No perk icons cached — download icons first";

    private const int ThumbW = 96, ThumbH = 54;
    private static readonly TimeSpan TemplateRetry = TimeSpan.FromSeconds(30);

    private readonly IPerkCatalog _catalog;
    private readonly PerkScannerOptions _opt;
    private readonly WindowCapture _capture = new();
    private readonly object _lifecycle = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private int _processId;

    // Owned by the scanner thread only.
    private PerkIconMatcher? _matcher;
    private DateTime _matcherLoadedAt;
    private volatile bool _reloadRequested;
    private Mat? _lastThumb;
    private int _blackFrames;
    private string _lastStatus = "";

    public PerkScanner(IPerkCatalog catalog, PerkScannerOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _opt = options ?? new PerkScannerOptions();
    }

    public bool IsRunning
    {
        get
        {
            lock (_lifecycle)
                return _thread is not null;
        }
    }

    /// <summary>The last status message raised through <see cref="StatusChanged"/>.</summary>
    public string LastStatus => _lastStatus;

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<PerkDetection>>? PerksDetected;

    /// <summary>Human-readable status for the UI ("Scanning…", "Game not focused", "Capture black…"). Background thread.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Rebuilds the icon templates on the next tick (call after the catalog downloaded new icons).</summary>
    public void ReloadTemplates() => _reloadRequested = true;

    public void Start(int processId)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));

        (Thread? Thread, CancellationTokenSource? Cts) previous;
        lock (_lifecycle)
        {
            if (_thread is not null && _processId == processId)
                return;
            previous = Detach();

            _processId = processId;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var waitFor = previous.Thread;
            _thread = new Thread(() =>
            {
                // Never let two loops share state: wait for the previous one to finish first.
                if (waitFor is not null && waitFor != Thread.CurrentThread)
                    waitFor.Join();
                Loop(token);
            })
            {
                IsBackground = true,
                Name = "DbdMaxing.PerkScanner",
                Priority = ThreadPriority.BelowNormal, // keep the game smooth
            };
            _thread.Start();
        }
        Finish(previous, join: false);
    }

    /// <summary>Stops the loop. Idempotent; safe to call from any thread, including a PerksDetected handler.</summary>
    public void Stop()
    {
        (Thread? Thread, CancellationTokenSource? Cts) previous;
        lock (_lifecycle)
            previous = Detach();
        Finish(previous, join: true);
    }

    private (Thread? Thread, CancellationTokenSource? Cts) Detach()
    {
        var detached = (_thread, _cts);
        _thread = null;
        _cts = null;
        try
        {
            detached._cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        return detached;
    }

    /// <summary>Joins outside the lock, so a handler that marshals to the UI thread cannot deadlock us for long.</summary>
    private void Finish((Thread? Thread, CancellationTokenSource? Cts) previous, bool join)
    {
        if (previous.Thread is null)
            return;
        if (join && previous.Thread != Thread.CurrentThread && !previous.Thread.Join(TimeSpan.FromSeconds(3)))
            _opt.Write("PerkScanner thread still finishing a scan; it exits on its own (already cancelled).");
        // The CTS is only disposed if the thread has finished with its token; otherwise the GC handles it.
        if (!previous.Thread.IsAlive)
            previous.Cts?.Dispose();
        // The loop thread disposes its matcher and thumbnail on exit.
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------------------------------------------

    private void Loop(CancellationToken ct)
    {
        WindowCapture.EnsureDpiAwareness(); // thread-scoped PER_MONITOR_AWARE_V2 so rects are physical pixels
        var interval = _opt.Interval < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : _opt.Interval;
        _blackFrames = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Tick(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _opt.Write("Scan failed: " + ex);
                    SetStatus("Scan error: " + ex.Message);
                }

                if (ct.WaitHandle.WaitOne(interval))
                    break;
            }
        }
        finally
        {
            _lastThumb?.Dispose();
            _lastThumb = null;
            _matcher?.Dispose();
            _matcher = null;
            SetStatus(StatusStopped);
        }
    }

    private void Tick(CancellationToken ct)
    {
        if (!WindowCapture.IsProcessInForeground(_processId, out var hwnd))
        {
            SetStatus(StatusNotFocused);
            return;
        }

        var matcher = EnsureMatcher();
        if (matcher is null || matcher.TemplateCount == 0)
        {
            SetStatus(StatusNoIcons);
            return;
        }

        using var frame = _capture.Capture(hwnd);
        if (frame is null)
        {
            SetStatus(string.IsNullOrEmpty(_capture.LastStatusMessage) ? "Capture failed" : _capture.LastStatusMessage);
            return;
        }

        if (_capture.LastStatus == CaptureStatus.Black)
        {
            if (++_blackFrames >= Math.Max(1, _opt.BlackFramesBeforeWarning))
                SetStatus(WindowCapture.BlackFrameMessage);
            return;
        }
        _blackFrames = 0;
        SetStatus(StatusScanning);

        if (IsUnchanged(frame))
            return;

        ct.ThrowIfCancellationRequested();
        var matches = matcher.MatchDetailed(frame);
        ct.ThrowIfCancellationRequested();

        if (_opt.SaveDebugFrames)
            SaveDebugFrame(frame, matches);

        if (matches.Count == 0)
            return;

        IReadOnlyList<PerkDetection> detections = matches
            .GroupBy(m => m.PerkId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PerkDetection(g.Key, Math.Round(g.Max(m => m.Confidence), 4)))
            .OrderByDescending(d => d.Confidence)
            .ToList();

        var handler = PerksDetected;
        if (handler is null)
            return;
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<IReadOnlyList<PerkDetection>>)d)(this, detections);
            }
            catch (Exception ex)
            {
                _opt.Write("PerksDetected handler threw: " + ex);
            }
        }
    }

    private PerkIconMatcher? EnsureMatcher()
    {
        bool stale = _matcher is not null && _matcher.TemplateCount == 0 && DateTime.UtcNow - _matcherLoadedAt > TemplateRetry;
        if (_matcher is not null && !_reloadRequested && !stale)
            return _matcher;

        _reloadRequested = false;
        PerkIconMatcher? fresh = null;
        try
        {
            SetStatus("Loading perk icons…");
            fresh = new PerkIconMatcher(_catalog, _opt);
        }
        catch (Exception ex)
        {
            _opt.Write("Failed to build perk templates: " + ex);
        }

        _matcherLoadedAt = DateTime.UtcNow;
        if (fresh is not null)
        {
            _matcher?.Dispose();
            _matcher = fresh;
            _lastThumb?.Dispose();
            _lastThumb = null; // force a re-match with the new templates
        }
        return _matcher;
    }

    /// <summary>
    /// Cheap change detection on a 96×54 grey thumbnail: the frame counts as unchanged if at most a couple of
    /// thumbnail pixels moved by more than a small amount. Tolerates DBD's animated menu fog, while a new perk
    /// icon (~5×5 thumbnail pixels) always registers.
    /// </summary>
    private bool IsUnchanged(Mat frame)
    {
        var thumb = new Mat();
        try
        {
            using (var small = new Mat())
            {
                Cv2.Resize(frame, small, new Size(ThumbW, ThumbH), 0, 0, InterpolationFlags.Area);
                if (small.Channels() == 1)
                    small.CopyTo(thumb);
                else
                    Cv2.CvtColor(small, thumb, small.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            }

            bool unchanged = false;
            if (_lastThumb is not null && _lastThumb.Size() == thumb.Size() && _lastThumb.Type() == thumb.Type())
            {
                using var diff = new Mat();
                Cv2.Absdiff(thumb, _lastThumb, diff);
                using var changed = new Mat();
                Cv2.Threshold(diff, changed, 10, 255, ThresholdTypes.Binary);
                unchanged = Cv2.CountNonZero(changed) <= 2;
            }

            if (!unchanged)
            {
                _lastThumb?.Dispose();
                _lastThumb = thumb;
                thumb = null!;
            }
            return unchanged;
        }
        finally
        {
            thumb?.Dispose();
        }
    }

    private void SaveDebugFrame(Mat frame, IReadOnlyList<PerkMatch> matches)
    {
        try
        {
            Directory.CreateDirectory(_opt.DebugDir);
            using var annotated = frame.Clone();
            foreach (var m in matches)
            {
                Cv2.Rectangle(annotated, m.Bounds, Scalar.LimeGreen, 2);
                Cv2.PutText(annotated, $"{m.PerkId} {m.Confidence:0.00}", new Point(m.Bounds.X, Math.Max(12, m.Bounds.Y - 4)),
                    HersheyFonts.HersheySimplex, 0.45, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
            }

            var file = Path.Combine(_opt.DebugDir, $"frame-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{matches.Count}.png");
            Cv2.ImEncode(".png", annotated, out var bytes);
            File.WriteAllBytes(file, bytes);

            var old = new DirectoryInfo(_opt.DebugDir).GetFiles("frame-*.png")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(Math.Max(1, _opt.MaxDebugFrames));
            foreach (var f in old)
            {
                try
                {
                    f.Delete();
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            _opt.Write("Failed to save debug frame: " + ex.Message);
        }
    }

    private void SetStatus(string status)
    {
        if (string.Equals(_lastStatus, status, StringComparison.Ordinal))
            return;
        _lastStatus = status;
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _opt.Write("StatusChanged handler threw: " + ex.Message);
        }
    }
}
