using DbdMaxing.Core.Data;
using DbdMaxing.Core.Models;
using DbdMaxing.Vision.Matching;
using OpenCvSharp;

namespace DbdMaxing.Vision.Diagnostics;

/// <summary>Runs the matcher on saved screenshots, so recognition can be tested without the game running.</summary>
public static class OfflineTester
{
    /// <summary>Loads <paramref name="imagePath"/> (PNG/JPG/BMP, Unicode-safe) and returns the perks recognised in it.</summary>
    public static IReadOnlyList<PerkDetection> RunOnImage(string imagePath, IPerkCatalog catalog, PerkScannerOptions? options = null)
    {
        using var matcher = new PerkIconMatcher(catalog, options);
        using var frame = LoadImage(imagePath);
        return matcher.Match(frame);
    }

    /// <summary>Same as <see cref="RunOnImage"/>, but with locations. Optionally writes an annotated copy to <paramref name="annotatedOutputPath"/>.</summary>
    public static IReadOnlyList<PerkMatch> RunOnImageDetailed(string imagePath, IPerkCatalog catalog,
        PerkScannerOptions? options = null, string? annotatedOutputPath = null)
    {
        using var matcher = new PerkIconMatcher(catalog, options);
        using var frame = LoadImage(imagePath);
        var matches = matcher.MatchDetailed(frame);

        if (!string.IsNullOrEmpty(annotatedOutputPath))
        {
            foreach (var m in matches)
            {
                Cv2.Rectangle(frame, m.Bounds, Scalar.LimeGreen, 2);
                Cv2.PutText(frame, $"{m.PerkId} {m.Confidence:0.00}", new Point(m.Bounds.X, Math.Max(12, m.Bounds.Y - 4)),
                    HersheyFonts.HersheySimplex, 0.45, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
            }
            var dir = Path.GetDirectoryName(Path.GetFullPath(annotatedOutputPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(annotatedOutputPath);
            Cv2.ImEncode(string.IsNullOrEmpty(ext) ? ".png" : ext, frame, out var bytes);
            File.WriteAllBytes(annotatedOutputPath, bytes);
        }
        return matches;
    }

    private static Mat LoadImage(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        var bytes = File.ReadAllBytes(imagePath);
        var frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame is null || frame.Empty())
        {
            frame?.Dispose();
            throw new InvalidDataException($"Could not decode image '{imagePath}'.");
        }
        return frame;
    }
}
