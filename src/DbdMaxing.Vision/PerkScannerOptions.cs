using DbdMaxing.Core;

namespace DbdMaxing.Vision;

/// <summary>Tuning knobs for <see cref="PerkScanner"/> and <see cref="Matching.PerkIconMatcher"/>.</summary>
public sealed class PerkScannerOptions
{
    /// <summary>Time between scans. Clamped to at least 250 ms.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Minimum verified match score (TM_CCOEFF_NORMED on edge features, 0..1) for a detection.
    /// Conservative on purpose: a false positive marks a perk as owned, which is worse than a miss.
    /// </summary>
    public double Threshold { get; set; } = 0.80;

    /// <summary>Score a coarse (downscaled) hit needs to be sent to full-resolution verification.</summary>
    public double CoarseThreshold { get; set; } = 0.50;

    /// <summary>
    /// A detection is rejected when a different perk scores within this margin at the same spot
    /// (ambiguous icons). 0 disables the check.
    /// </summary>
    public double AmbiguityMargin { get; set; } = 0.02;

    /// <summary>Expected perk icon edge length (px) on the loadout grid of a 1080p frame. Scaled by frameHeight/1080.</summary>
    public double ExpectedIconPxAt1080 { get; set; } = 110;

    /// <summary>Candidate icon scales relative to <see cref="ExpectedIconPxAt1080"/>.</summary>
    public IReadOnlyList<double> Scales { get; set; } = new[] { 0.55, 0.7, 0.85, 1.0, 1.2 };

    /// <summary>Icon size (px) used for the fast coarse pass; the frame is downscaled by the same factor.</summary>
    public int CoarseIconPx { get; set; } = 40;

    /// <summary>Max coarse peaks taken per template per scale.</summary>
    public int MaxPeaksPerTemplate { get; set; } = 2;

    /// <summary>Max distinct screen locations verified per frame.</summary>
    public int MaxCandidateLocations { get; set; } = 48;

    /// <summary>Max templates verified per screen location (best coarse scores first).</summary>
    public int MaxTemplatesPerLocation { get; set; } = 4;

    /// <summary>After this many consecutive empty frames at a locked scale, all scales are searched again.</summary>
    public int ScaleUnlockAfterEmptyFrames { get; set; } = 4;

    /// <summary>Worker threads for matching. Defaults to half the cores so the game keeps its frame rate.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Grey level transparent template pixels are composited onto (approximates the dark rarity backgrounds).</summary>
    public byte TemplateBackgroundGray { get; set; } = 60;

    /// <summary>Consecutive black frames before reporting "Capture black" (loading screens fade to black briefly).</summary>
    public int BlackFramesBeforeWarning { get; set; } = 3;

    /// <summary>Saves scanned frames (with detections drawn) to <see cref="DebugDir"/>. Off by default.</summary>
    public bool SaveDebugFrames { get; set; }

    /// <summary>Folder for debug frames. Defaults to AppPaths.Root/debug.</summary>
    public string DebugDir { get; set; } = Path.Combine(AppPaths.Root, "debug");

    /// <summary>Oldest debug frames beyond this count are deleted.</summary>
    public int MaxDebugFrames { get; set; } = 50;

    /// <summary>Optional log sink. Defaults to System.Diagnostics.Trace.</summary>
    public Action<string>? Log { get; set; }

    internal void Write(string message)
    {
        var line = $"[Vision {DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            if (Log is not null)
                Log(line);
            else
                System.Diagnostics.Trace.WriteLine(line);
        }
        catch (Exception)
        {
            // Logging must never throw.
        }
    }
}
