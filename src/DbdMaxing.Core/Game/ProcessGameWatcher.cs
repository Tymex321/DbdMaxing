using System.Diagnostics;

namespace DbdMaxing.Core.Game;

/// <summary>
/// Polls the OS process list for <see cref="IGameWatcher.ProcessName"/>. It only looks at process
/// names and ids: it never opens the game's memory, injects, or touches network traffic (EAC-safe).
/// </summary>
public sealed class ProcessGameWatcher : IGameWatcher
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TimeSpan _interval;
    private readonly Func<int?> _findProcess;
    private Timer? _timer;
    private int? _currentPid;
    private int _polling; // 0/1 re-entrancy guard for timer callbacks
    private bool _disposed;

    /// <param name="interval">Poll interval; defaults to 2 seconds.</param>
    /// <param name="processFinder">Returns the game's pid or null; defaults to a process-list lookup. Injected by tests.</param>
    public ProcessGameWatcher(TimeSpan? interval = null, Func<int?>? processFinder = null)
    {
        _interval = interval is { } i && i > TimeSpan.Zero ? i : DefaultInterval;
        _findProcess = processFinder ?? FindGameProcess;
    }

    public bool IsGameRunning
    {
        get { lock (_gate) return _currentPid is not null; }
    }

    /// <summary>The pid of the running game, or null.</summary>
    public int? CurrentProcessId
    {
        get { lock (_gate) return _currentPid; }
    }

    public event EventHandler<int>? GameStarted;
    public event EventHandler? GameExited;

    /// <summary>Starts polling. Checks once synchronously so an already-running game raises GameStarted immediately.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null) return;
            _timer = new Timer(_ => Poll(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        Poll();

        lock (_gate)
        {
            _timer?.Change(_interval, _interval);
        }
    }

    /// <summary>Stops polling. The last known state is kept; Start resumes from it.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Runs one check now. Public so tests and the app can force a refresh.</summary>
    public void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            int? pid;
            try { pid = _findProcess(); }
            catch (Exception) { return; } // a failing lookup must never kill the timer thread

            int? previous;
            lock (_gate)
            {
                if (_disposed) return;
                previous = _currentPid;
                if (previous == pid) return;
                _currentPid = pid;
            }

            // Pid changed: the old instance is gone (exit), and/or a new one appeared (start).
            if (previous is not null) Raise(() => GameExited?.Invoke(this, EventArgs.Empty));
            if (pid is { } started) Raise(() => GameStarted?.Invoke(this, started));
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static void Raise(Action raise)
    {
        try { raise(); }
        catch (Exception e) { Debug.WriteLine($"IGameWatcher subscriber threw: {e}"); }
    }

    /// <summary>Default finder: first process named <see cref="IGameWatcher.ProcessName"/>; disposes every Process object.</summary>
    internal static int? FindGameProcess()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(IGameWatcher.ProcessName);
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            int? pid = null;
            foreach (var p in processes)
            {
                try
                {
                    if (pid is null && !p.HasExited) pid = p.Id;
                }
                catch (Exception)
                {
                    // HasExited can throw for access-denied processes; the id is still valid.
                    pid ??= SafeId(p);
                }
            }
            return pid;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static int? SafeId(Process p)
    {
        try { return p.Id; }
        catch (Exception) { return null; }
    }
}
