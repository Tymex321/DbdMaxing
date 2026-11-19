namespace DbdMaxing.Core.Game;

/// <summary>Watches for the Dead by Daylight process starting and exiting.</summary>
public interface IGameWatcher : IDisposable
{
    public const string ProcessName = "DeadByDaylight-Win64-Shipping";

    bool IsGameRunning { get; }

    /// <summary>Raised (on a background thread) with the process id when the game starts.</summary>
    event EventHandler<int>? GameStarted;

    event EventHandler? GameExited;

    void Start();
    void Stop();
}
