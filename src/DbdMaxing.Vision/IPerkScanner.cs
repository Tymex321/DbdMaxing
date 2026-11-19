using DbdMaxing.Core.Models;

namespace DbdMaxing.Vision;

/// <summary>
/// Periodically screenshots the game window while it is in the foreground and recognises perk icons.
/// Read-only screen capture only: never reads or writes game memory.
/// </summary>
public interface IPerkScanner : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Raised (on a background thread) with the perks recognised in one frame, if any.</summary>
    event EventHandler<IReadOnlyList<PerkDetection>>? PerksDetected;

    /// <summary>Begins scanning the main window of the given process. Icons must already be cached.</summary>
    void Start(int processId);

    void Stop();
}
