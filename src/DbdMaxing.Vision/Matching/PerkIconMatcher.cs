using System.Collections.Concurrent;
using DbdMaxing.Core.Data;
using DbdMaxing.Core.Models;
using OpenCvSharp;

namespace DbdMaxing.Vision.Matching;

/// <summary>One recognised icon with its on-screen location (frame pixel coordinates).</summary>
public sealed record PerkMatch(string PerkId, double Confidence, Rect Bounds, double Scale);

/// <summary>
/// Finds perk icons in a frame by template matching against the cached icon PNGs.
///
/// <para><b>Features.</b> Templates and frame are both converted to <i>gradient magnitude</i> (Sobel on a lightly
/// blurred greyscale image). In game the icon art is drawn over a coloured, vignetted rarity background
/// (brown/yellow/green/purple/pink) that the wiki PNGs don't have. Gradient magnitude ignores the flat tint and
/// brightness offsets of that backdrop, and the silhouette edge of the (light) art against a (darker) backdrop
/// exists for every rarity. TM_CCOEFF_NORMED then cancels the remaining global contrast difference. Plain
/// greyscale NCC over the full icon square scored the background as heavily as the art and was rarity-sensitive.</para>
///
/// <para><b>Mask.</b> The PNG alpha (dilated slightly so the silhouette edge is included), intersected with the
/// inscribed diamond, is the match mask. That excludes the diamond frame and the UI around it, which differ by
/// screen and rarity. Icons without alpha fall back to a central diamond mask.</para>
///
/// <para><b>Search.</b> (1) Coarse: the frame is downscaled so that an expected-size icon is ~<see cref="PerkScannerOptions.CoarseIconPx"/>
/// px, and every template is matched unmasked (fast DFT path) at each candidate scale. The best peaks per template go on.
/// (2) Peaks are clustered by location, and only the top few templates per location are kept.
/// (3) Fine: each survivor is re-matched at full resolution (masked, ±7 % around its scale) in a small ROI.
/// (4) Non-maximum suppression keeps one perk per location, and ambiguous locations (two perks within
/// <see cref="PerkScannerOptions.AmbiguityMargin"/>) are dropped, because false positives are worse than misses.
/// Once a scale produces confident hits it is locked for subsequent frames of the same height.</para>
///
/// Thread-safe: calls to <see cref="Match"/> are serialised.
/// </summary>
public sealed class PerkIconMatcher : IDisposable
{
    private const int MaxSourcePx = 256;
    private const int MinTemplatePx = 12;
    private const int MaxFineCacheEntries = 512;
    private static readonly double[] SubScales = { 0.93, 1.0, 1.07 };

    private readonly PerkScannerOptions _opt;
    private readonly double[] _scales;
    private readonly List<IconTemplate> _templates = new();
    private readonly ConcurrentDictionary<(int Template, int Size), Lazy<FineTemplate?>> _fineCache = new();
    private readonly object _gate = new();

    private int? _lockedScale;
    private int _lockedFrameHeight;
    private int _emptyFramesAtLock;
    private bool _disposed;

    /// <summary>Loads a template for every perk in <paramref name="catalog"/> whose icon is cached locally.</summary>
    public PerkIconMatcher(IPerkCatalog catalog, PerkScannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _opt = options ?? new PerkScannerOptions();
        _scales = SanitizeScales(_opt.Scales);

        int missing = 0, failed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var perk in catalog.Perks)
        {
            if (perk is null || string.IsNullOrWhiteSpace(perk.Id) || !seen.Add(perk.Id))
                continue;
            string? path;
            try
            {
                path = catalog.GetIconPath(perk.Id);
            }
            catch (Exception)
            {
                path = null;
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                missing++;
                continue;
            }

            try
            {
                using var src = LoadIcon(path);
                if (src is null)
                {
                    failed++;
                    continue;
                }
                AddTemplate(perk.Id, src, path, inMemory: null);
            }
            catch (Exception ex)
            {
                failed++;
                _opt.Write($"Failed to load icon for '{perk.Id}' ({path}): {ex.Message}");
            }
        }

        _opt.Write($"PerkIconMatcher: {_templates.Count} templates loaded, {missing} icons missing, {failed} failed.");
    }

    /// <summary>
    /// Builds templates from in-memory icons (BGRA with alpha, BGR or grey), mainly for unit tests.
    /// The matcher copies what it needs; the caller keeps ownership of the Mats.
    /// </summary>
    public PerkIconMatcher(IReadOnlyDictionary<string, Mat> icons, PerkScannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(icons);
        _opt = options ?? new PerkScannerOptions();
        _scales = SanitizeScales(_opt.Scales);
        foreach (var (id, icon) in icons)
        {
            if (string.IsNullOrWhiteSpace(id) || icon is null || icon.Empty())
                continue;
            var src = NormalizeIcon(icon);
            if (src is null)
                continue;
            try
            {
                AddTemplate(id, src, path: null, inMemory: src);
            }
            catch
            {
                src.Dispose();
                throw;
            }
        }
    }

    public int TemplateCount => _templates.Count;

    /// <summary>The scale (relative to the expected icon size) currently locked in, if any.</summary>
    public double? LockedScale
    {
        get
        {
            lock (_gate)
                return _lockedScale is { } i ? _scales[i] : null;
        }
    }

    /// <summary>Forgets the locked scale, so the next frame searches all scales.</summary>
    public void ResetScaleLock()
    {
        lock (_gate)
            _lockedScale = null;
    }

    /// <summary>Perks recognised in <paramref name="frame"/> (BGR, BGRA or grey), one entry per perk id, best confidence.</summary>
    public IReadOnlyList<PerkDetection> Match(Mat frame)
    {
        return MatchDetailed(frame)
            .GroupBy(m => m.PerkId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PerkDetection(g.Key, Math.Round(g.Max(m => m.Confidence), 4)))
            .OrderByDescending(d => d.Confidence)
            .ToList();
    }

    /// <summary>Like <see cref="Match"/>, but returns every accepted location (a perk may appear more than once).</summary>
    public IReadOnlyList<PerkMatch> MatchDetailed(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.IsDisposed || frame.Empty() || _templates.Count == 0)
                return Array.Empty<PerkMatch>();

            if (_fineCache.Count > MaxFineCacheEntries)
                ClearFineCache();

            using var gray = ToGray8(frame);
            if (gray is null || gray.Rows < 32 || gray.Cols < 32)
                return Array.Empty<PerkMatch>();

            var result = Run(gray);
            UpdateScaleLock(gray.Rows, result);
            return result;
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Pipeline
    // ------------------------------------------------------------------------------------------------------

    private List<PerkMatch> Run(Mat gray)
    {
        int width = gray.Cols, height = gray.Rows;
        double expectedPx = _opt.ExpectedIconPxAt1080 * height / 1080.0;
        double factor = Math.Clamp(_opt.CoarseIconPx / expectedPx, 0.05, 2.0);
        int coarseW = Math.Max(1, (int)Math.Round(width * factor));
        int coarseH = Math.Max(1, (int)Math.Round(height * factor));
        double fx = width / (double)coarseW, fy = height / (double)coarseH;

        if (_lockedScale is not null && _lockedFrameHeight != height)
            _lockedScale = null;
        int[] scaleIdx = _lockedScale is { } locked
            ? new[] { locked }
            : Enumerable.Range(0, _scales.Length).ToArray();

        // --- (1) coarse pass -------------------------------------------------------------------------------
        var coarse = new ConcurrentBag<CoarseHit>();
        using (var coarseGray = new Mat())
        {
            Cv2.Resize(gray, coarseGray, new Size(coarseW, coarseH), 0, 0,
                factor < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
            using var coarseFeat = ComputeFeatures(coarseGray);
            double thr = _opt.CoarseThreshold;
            int maxPeaks = Math.Max(1, _opt.MaxPeaksPerTemplate);

            Parallel.For(0, _templates.Count, ParallelOpts(), ti =>
            {
                var t = _templates[ti];
                using var res = new Mat();
                foreach (var si in scaleIdx)
                {
                    var tf = t.Coarse[si];
                    if (tf is null || tf.Rows > coarseFeat.Rows || tf.Cols > coarseFeat.Cols)
                        continue;
                    Cv2.MatchTemplate(coarseFeat, tf, res, TemplateMatchModes.CCoeffNormed);
                    Sanitize(res);
                    int suppress = Math.Max(2, (int)(tf.Cols * 0.6));
                    for (int k = 0; k < maxPeaks; k++)
                    {
                        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out Point loc);
                        if (maxVal < thr)
                            break;
                        coarse.Add(new CoarseHit(ti, si,
                            (loc.X + tf.Cols / 2.0) * fx,
                            (loc.Y + tf.Rows / 2.0) * fy,
                            maxVal));
                        var sup = new Rect(loc.X - suppress, loc.Y - suppress, 2 * suppress + 1, 2 * suppress + 1)
                                  & new Rect(0, 0, res.Cols, res.Rows);
                        if (sup.Width <= 0 || sup.Height <= 0)
                            break;
                        using var roi = new Mat(res, sup);
                        roi.SetTo(Scalar.All(-1));
                    }
                }
            });
        }

        if (coarse.IsEmpty)
            return new List<PerkMatch>();

        // --- (2) cluster coarse hits by location ----------------------------------------------------------
        var clusters = new List<Cluster>();
        foreach (var hit in coarse.OrderByDescending(h => h.Score))
        {
            double size = expectedPx * _scales[hit.Scale];
            Cluster? home = null;
            foreach (var c in clusters)
            {
                double dx = c.X - hit.X, dy = c.Y - hit.Y;
                double r = 0.5 * Math.Max(size, c.Size);
                if (dx * dx + dy * dy < r * r)
                {
                    home = c;
                    break;
                }
            }

            if (home is null)
            {
                if (clusters.Count >= _opt.MaxCandidateLocations)
                    continue;
                home = new Cluster(hit.X, hit.Y, size);
                clusters.Add(home);
            }

            if (home.Hits.Count < Math.Max(1, _opt.MaxTemplatesPerLocation)
                && home.Hits.All(h => h.Template != hit.Template))
                home.Hits.Add(hit);
        }

        var toVerify = clusters.SelectMany(c => c.Hits).ToArray();

        // --- (3) full-resolution verification -------------------------------------------------------------
        var verified = new ConcurrentBag<PerkMatch>();
        using (var fullFeat = ComputeFeatures(gray))
        {
            var frameRect = new Rect(0, 0, width, height);
            Parallel.For(0, toVerify.Length, ParallelOpts(), i =>
            {
                var v = Verify(toVerify[i], fullFeat, frameRect, expectedPx);
                if (v is not null)
                    verified.Add(v);
            });
        }

        // --- (4) NMS + ambiguity rejection ---------------------------------------------------------------
        return Select(verified.OrderByDescending(v => v.Confidence).ToList());
    }

    private PerkMatch? Verify(CoarseHit hit, Mat fullFeat, Rect frameRect, double expectedPx)
    {
        double best = double.NegativeInfinity;
        Rect bestRect = default;
        foreach (var sub in SubScales)
        {
            int size = (int)Math.Round(expectedPx * _scales[hit.Scale] * sub);
            if (size < MinTemplatePx || size > frameRect.Width || size > frameRect.Height)
                continue;
            var ft = GetFine(hit.Template, size);
            if (ft is null)
                continue;

            int margin = Math.Max(4, (int)Math.Round(size * 0.2));
            var roiRect = new Rect(
                (int)Math.Round(hit.X - size / 2.0 - margin),
                (int)Math.Round(hit.Y - size / 2.0 - margin),
                size + 2 * margin,
                size + 2 * margin) & frameRect;
            if (roiRect.Width < size || roiRect.Height < size)
                continue;

            using var img = new Mat(fullFeat, roiRect);
            using var res = new Mat();
            Cv2.MatchTemplate(img, ft.Features, res, TemplateMatchModes.CCoeffNormed, ft.MaskF);
            Sanitize(res);
            Cv2.MinMaxLoc(res, out _, out double val, out _, out Point loc);
            if (val <= best)
                continue;

            // Guard against masked NCC blowing up on nearly flat patches: the patch must carry a plausible
            // amount of edge energy compared with the template.
            var patchRect = new Rect(roiRect.X + loc.X, roiRect.Y + loc.Y, size, size);
            using var patch = new Mat(fullFeat, patchRect);
            double energy = Cv2.Mean(patch, ft.Mask8).Val0;
            double ratio = ft.MeanEnergy > 1e-6 ? energy / ft.MeanEnergy : 0;
            if (ratio < 0.3 || ratio > 3.5)
                continue;

            best = val;
            bestRect = patchRect;
        }

        if (double.IsNegativeInfinity(best))
            return null;
        return new PerkMatch(_templates[hit.Template].PerkId, best, bestRect, _scales[hit.Scale]);
    }

    private List<PerkMatch> Select(List<PerkMatch> sorted)
    {
        var accepted = new List<PerkMatch>();
        var blocked = new List<Rect>();
        foreach (var m in sorted)
        {
            if (m.Confidence < _opt.Threshold)
                break; // sorted descending: nothing further can pass
            if (accepted.Any(a => Overlaps(a.Bounds, m.Bounds)) || blocked.Any(b => Overlaps(b, m.Bounds)))
                continue;

            if (_opt.AmbiguityMargin > 0)
            {
                double runnerUp = sorted
                    .Where(o => !ReferenceEquals(o, m)
                                && !string.Equals(o.PerkId, m.PerkId, StringComparison.OrdinalIgnoreCase)
                                && Overlaps(o.Bounds, m.Bounds))
                    .Select(o => o.Confidence)
                    .DefaultIfEmpty(double.NegativeInfinity)
                    .Max();
                if (runnerUp > m.Confidence - _opt.AmbiguityMargin)
                {
                    blocked.Add(m.Bounds);
                    continue;
                }
            }

            accepted.Add(m);
        }
        return accepted;
    }

    private void UpdateScaleLock(int frameHeight, List<PerkMatch> accepted)
    {
        if (accepted.Count == 0)
        {
            if (_lockedScale is not null && ++_emptyFramesAtLock >= Math.Max(1, _opt.ScaleUnlockAfterEmptyFrames))
                _lockedScale = null;
            return;
        }

        _emptyFramesAtLock = 0;
        if (_lockedScale is not null)
            return;
        if (accepted.Count < 2 && accepted.Max(a => a.Confidence) < 0.9)
            return; // one marginal hit is not enough evidence to narrow the search

        var mode = accepted.GroupBy(a => a.Scale).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(a => a.Confidence)).First().Key;
        int idx = Array.IndexOf(_scales, mode);
        if (idx < 0)
            return;
        _lockedScale = idx;
        _lockedFrameHeight = frameHeight;
        _opt.Write($"Perk icon scale locked at {mode:0.##} for {frameHeight}p frames.");
    }

    // ------------------------------------------------------------------------------------------------------
    // Templates
    // ------------------------------------------------------------------------------------------------------

    private void AddTemplate(string perkId, Mat srcBgra, string? path, Mat? inMemory)
    {
        var coarse = new Mat?[_scales.Length];
        try
        {
            for (int si = 0; si < _scales.Length; si++)
            {
                int size = Math.Max(MinTemplatePx, (int)Math.Round(_opt.CoarseIconPx * _scales[si]));
                var (feat, mask) = BuildFeatures(srcBgra, size);
                using (mask)
                using (var inv = new Mat())
                {
                    // Coarse pass is unmasked (fast path), so zero the features outside the mask instead.
                    Cv2.BitwiseNot(mask, inv);
                    feat.SetTo(Scalar.All(0), inv);
                }
                coarse[si] = feat;
            }
        }
        catch
        {
            foreach (var m in coarse)
                m?.Dispose();
            throw;
        }

        _templates.Add(new IconTemplate(perkId, path, inMemory, coarse));
    }

    private FineTemplate? GetFine(int template, int size)
    {
        var lazy = _fineCache.GetOrAdd((template, size),
            key => new Lazy<FineTemplate?>(() => BuildFine(key.Template, key.Size), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private FineTemplate? BuildFine(int template, int size)
    {
        var t = _templates[template];
        Mat? src = null;
        try
        {
            src = t.InMemory is not null ? t.InMemory.Clone() : (t.Path is not null ? LoadIcon(t.Path) : null);
            if (src is null)
                return null;
            var (feat, mask8) = BuildFeatures(src, size);
            var maskF = new Mat();
            mask8.ConvertTo(maskF, MatType.CV_32F, 1.0 / 255.0);
            double meanEnergy = Cv2.Mean(feat, mask8).Val0;
            return new FineTemplate(feat, mask8, maskF, meanEnergy);
        }
        catch (Exception ex)
        {
            _opt.Write($"Failed to build {size}px template for '{t.PerkId}': {ex.Message}");
            return null;
        }
        finally
        {
            src?.Dispose();
        }
    }

    /// <summary>Resizes a BGRA icon to size×size, composites it on the template background and returns (features, 8-bit mask).</summary>
    private (Mat Features, Mat Mask) BuildFeatures(Mat bgra, int size)
    {
        using var resized = new Mat();
        Cv2.Resize(bgra, resized, new Size(size, size), 0, 0,
            size < bgra.Cols ? InterpolationFlags.Area : InterpolationFlags.Linear);
        var ch = Cv2.Split(resized);
        try
        {
            using var bgr = new Mat();
            Cv2.Merge(new[] { ch[0], ch[1], ch[2] }, bgr);
            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

            // composite = (gray - bg) * alpha + bg
            double bg = _opt.TemplateBackgroundGray;
            using var diff = new Mat();
            gray.ConvertTo(diff, MatType.CV_32F, 1.0, -bg);
            using var alpha = new Mat();
            ch[3].ConvertTo(alpha, MatType.CV_32F, 1.0 / 255.0);
            using var blended = new Mat();
            Cv2.Multiply(diff, alpha, blended);
            using var composite = new Mat();
            blended.ConvertTo(composite, MatType.CV_8U, 1.0, bg);

            var features = ComputeFeatures(composite);
            try
            {
                return (features, BuildMask(ch[3], size));
            }
            catch
            {
                features.Dispose();
                throw;
            }
        }
        finally
        {
            foreach (var c in ch)
                c.Dispose();
        }
    }

    private static Mat BuildMask(Mat alpha8, int size)
    {
        var mask = new Mat();
        Cv2.Threshold(alpha8, mask, 64, 255, ThresholdTypes.Binary);
        int r = Math.Max(1, (int)Math.Round(size * 0.06));
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1)))
            Cv2.Dilate(mask, mask, kernel);
        using (var diamond = DiamondMask(size, 1.08))
            Cv2.BitwiseAnd(mask, diamond, mask);

        int nonZero = Cv2.CountNonZero(mask);
        if (nonZero < 0.08 * size * size)
        {
            // No usable alpha (or nearly empty art): fall back to the central diamond.
            mask.Dispose();
            return DiamondMask(size, 0.92);
        }
        return mask;
    }

    private static Mat DiamondMask(int size, double scale)
    {
        var mask = new Mat(size, size, MatType.CV_8UC1, Scalar.All(0));
        double c = (size - 1) / 2.0, r = c * scale;
        var pts = new[]
        {
            new Point((int)Math.Round(c), (int)Math.Round(c - r)),
            new Point((int)Math.Round(c + r), (int)Math.Round(c)),
            new Point((int)Math.Round(c), (int)Math.Round(c + r)),
            new Point((int)Math.Round(c - r), (int)Math.Round(c)),
        };
        Cv2.FillConvexPoly(mask, pts, Scalar.All(255));
        return mask;
    }

    // ------------------------------------------------------------------------------------------------------
    // Image helpers
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Gradient magnitude (CV_32F) of a lightly blurred 8-bit grey image. See class remarks for why.</summary>
    internal static Mat ComputeFeatures(Mat gray8)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(gray8, blur, new Size(3, 3), 0);
        using var dx = new Mat();
        using var dy = new Mat();
        Cv2.Sobel(blur, dx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(blur, dy, MatType.CV_32F, 0, 1, 3);
        var mag = new Mat();
        Cv2.Magnitude(dx, dy, mag);
        return mag;
    }

    /// <summary>Converts any 1/3/4-channel frame of any depth to 8-bit grey (new Mat, caller disposes).</summary>
    internal static Mat? ToGray8(Mat frame)
    {
        using var depth8 = To8Bit(frame);
        var gray = new Mat();
        switch (depth8.Channels())
        {
            case 1:
                depth8.CopyTo(gray);
                break;
            case 3:
                Cv2.CvtColor(depth8, gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(depth8, gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                gray.Dispose();
                return null;
        }
        return gray;
    }

    private static Mat To8Bit(Mat m)
    {
        var depth = m.Depth();
        if (depth == MatType.CV_8U)
            return m.Clone();
        var dst = new Mat();
        if (depth == MatType.CV_16U)
            m.ConvertTo(dst, MatType.CV_8U, 1.0 / 257.0);
        else if (depth == MatType.CV_32F || depth == MatType.CV_64F)
            m.ConvertTo(dst, MatType.CV_8U, 255.0); // assume 0..1 floats
        else
            m.ConvertTo(dst, MatType.CV_8U);
        return dst;
    }

    /// <summary>Reads a PNG (alpha preserved) in a Unicode-path-safe way and normalises it to square BGRA.</summary>
    internal static Mat? LoadIcon(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0)
            return null;
        using var raw = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (raw is null || raw.Empty())
            return null;
        return NormalizeIcon(raw);
    }

    /// <summary>Any icon → 8-bit BGRA, square (transparent padding), at most <see cref="MaxSourcePx"/> px.</summary>
    internal static Mat? NormalizeIcon(Mat icon)
    {
        using var m8 = To8Bit(icon);
        var bgra = new Mat();
        try
        {
            switch (m8.Channels())
            {
                case 1:
                    Cv2.CvtColor(m8, bgra, ColorConversionCodes.GRAY2BGRA);
                    break;
                case 2:
                {
                    var ch = Cv2.Split(m8);
                    try
                    {
                        Cv2.Merge(new[] { ch[0], ch[0], ch[0], ch[1] }, bgra);
                    }
                    finally
                    {
                        foreach (var c in ch)
                            c.Dispose();
                    }
                    break;
                }
                case 3:
                    Cv2.CvtColor(m8, bgra, ColorConversionCodes.BGR2BGRA);
                    break;
                case 4:
                    m8.CopyTo(bgra);
                    break;
                default:
                    bgra.Dispose();
                    return null;
            }

            if (bgra.Rows != bgra.Cols)
            {
                int side = Math.Max(bgra.Rows, bgra.Cols);
                int top = (side - bgra.Rows) / 2, left = (side - bgra.Cols) / 2;
                var padded = new Mat();
                Cv2.CopyMakeBorder(bgra, padded, top, side - bgra.Rows - top, left, side - bgra.Cols - left,
                    BorderTypes.Constant, Scalar.All(0));
                bgra.Dispose();
                bgra = padded;
            }

            if (bgra.Rows > MaxSourcePx)
            {
                var small = new Mat();
                Cv2.Resize(bgra, small, new Size(MaxSourcePx, MaxSourcePx), 0, 0, InterpolationFlags.Area);
                bgra.Dispose();
                bgra = small;
            }

            return bgra;
        }
        catch
        {
            bgra.Dispose();
            throw;
        }
    }

    /// <summary>Masked NCC can yield NaN/±Inf on flat windows; zero them so they never win.</summary>
    private static void Sanitize(Mat res)
    {
        Cv2.PatchNaNs(res, 0);
        Cv2.Threshold(res, res, 1.0001, 0, ThresholdTypes.TozeroInv); // > 1 (incl. +Inf) → 0
        Cv2.Max(res, Scalar.All(-1), res); // -Inf → -1
    }

    private static bool Overlaps(Rect a, Rect b)
    {
        var inter = a & b;
        if (inter.Width <= 0 || inter.Height <= 0)
            return false;
        double ia = (double)inter.Width * inter.Height;
        double minArea = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
        return minArea > 0 && ia / minArea > 0.3;
    }

    private static double[] SanitizeScales(IReadOnlyList<double>? scales)
    {
        var s = (scales ?? Array.Empty<double>())
            .Where(x => x > 0.05 && x < 10 && double.IsFinite(x))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        return s.Length > 0 ? s : new[] { 0.55, 0.7, 0.85, 1.0, 1.2 };
    }

    private ParallelOptions ParallelOpts() => new() { MaxDegreeOfParallelism = Math.Max(1, _opt.MaxDegreeOfParallelism) };

    private void ClearFineCache()
    {
        foreach (var lazy in _fineCache.Values)
        {
            if (lazy.IsValueCreated)
                lazy.Value?.Dispose();
        }
        _fineCache.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            ClearFineCache();
            foreach (var t in _templates)
                t.Dispose();
            _templates.Clear();
        }
    }

    // ------------------------------------------------------------------------------------------------------

    private sealed record CoarseHit(int Template, int Scale, double X, double Y, double Score);

    private sealed class Cluster(double x, double y, double size)
    {
        public double X { get; } = x;
        public double Y { get; } = y;
        public double Size { get; } = size;
        public List<CoarseHit> Hits { get; } = new();
    }

    private sealed class IconTemplate(string perkId, string? path, Mat? inMemory, Mat?[] coarse) : IDisposable
    {
        public string PerkId { get; } = perkId;
        public string? Path { get; } = path;
        public Mat? InMemory { get; } = inMemory;
        public Mat?[] Coarse { get; } = coarse;

        public void Dispose()
        {
            InMemory?.Dispose();
            foreach (var m in Coarse)
                m?.Dispose();
        }
    }

    private sealed class FineTemplate(Mat features, Mat mask8, Mat maskF, double meanEnergy) : IDisposable
    {
        public Mat Features { get; } = features;
        public Mat Mask8 { get; } = mask8;
        public Mat MaskF { get; } = maskF;
        public double MeanEnergy { get; } = meanEnergy;

        public void Dispose()
        {
            Features.Dispose();
            Mask8.Dispose();
            MaskF.Dispose();
        }
    }
}
