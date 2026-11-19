using DbdMaxing.Core.Game;

namespace DbdMaxing.Core.Tests;

public class GameWatcherTests
{
    private sealed class Recorder
    {
        public readonly List<string> Events = new();

        public void Attach(IGameWatcher w)
        {
            w.GameStarted += (_, pid) => { lock (Events) Events.Add($"start:{pid}"); };
            w.GameExited += (_, _) => { lock (Events) Events.Add("exit"); };
        }
    }

    // Long interval so only explicit Poll() calls drive the state in deterministic tests.
    private static readonly TimeSpan Manual = TimeSpan.FromHours(1);

    [Fact]
    public void Raises_started_immediately_if_running_at_start()
    {
        using var w = new ProcessGameWatcher(Manual, () => 1234);
        var r = new Recorder();
        r.Attach(w);

        w.Start();

        Assert.Equal(new[] { "start:1234" }, r.Events);
        Assert.True(w.IsGameRunning);
        Assert.Equal(1234, w.CurrentProcessId);
    }

    [Fact]
    public void Nothing_raised_when_not_running()
    {
        using var w = new ProcessGameWatcher(Manual, () => null);
        var r = new Recorder();
        r.Attach(w);
        w.Start();
        w.Poll();
        Assert.Empty(r.Events);
        Assert.False(w.IsGameRunning);
    }

    [Fact]
    public void Raises_on_transitions_only()
    {
        int? pid = null;
        using var w = new ProcessGameWatcher(Manual, () => pid);
        var r = new Recorder();
        r.Attach(w);
        w.Start();

        pid = 10; w.Poll(); w.Poll();
        pid = null; w.Poll(); w.Poll();
        pid = 20; w.Poll();

        Assert.Equal(new[] { "start:10", "exit", "start:20" }, r.Events);
    }

    [Fact]
    public void Pid_change_between_polls_is_exit_then_start()
    {
        int? pid = 1;
        using var w = new ProcessGameWatcher(Manual, () => pid);
        var r = new Recorder();
        r.Attach(w);
        w.Start();
        pid = 2; w.Poll();
        Assert.Equal(new[] { "start:1", "exit", "start:2" }, r.Events);
    }

    [Fact]
    public void Throwing_finder_or_subscriber_is_tolerated()
    {
        var calls = 0;
        using var w = new ProcessGameWatcher(Manual, () => ++calls == 1 ? throw new InvalidOperationException() : 5);
        w.GameStarted += (_, _) => throw new Exception("subscriber bug");
        w.Start(); // finder throws
        Assert.False(w.IsGameRunning);
        w.Poll(); // subscriber throws
        Assert.True(w.IsGameRunning);
    }

    [Fact]
    public async Task Timer_polls_on_interval()
    {
        int? pid = null;
        using var w = new ProcessGameWatcher(TimeSpan.FromMilliseconds(20), () => pid);
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        w.GameStarted += (_, p) => started.TrySetResult(p);
        w.Start();

        pid = 77;
        var winner = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(started.Task, winner);
        Assert.Equal(77, await started.Task);
    }

    [Fact]
    public async Task Stop_halts_polling()
    {
        int? pid = null;
        using var w = new ProcessGameWatcher(TimeSpan.FromMilliseconds(20), () => pid);
        var r = new Recorder();
        r.Attach(w);
        w.Start();
        w.Stop();
        pid = 1;
        await Task.Delay(150);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void Dispose_is_idempotent_and_blocks_restart()
    {
        var w = new ProcessGameWatcher(Manual, () => 1);
        w.Start();
        w.Dispose();
        w.Dispose();
        w.Stop();
        Assert.Throws<ObjectDisposedException>(w.Start);
    }

    [Fact]
    public void Default_interval_is_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ProcessGameWatcher.DefaultInterval);
    }

    [Fact]
    public void Real_process_finder_does_not_throw()
    {
        // The game isn't running on the build machine; this just exercises the Process path.
        Assert.Null(ProcessGameWatcher.FindGameProcess());
    }

    [Fact]
    public void Foreground_helpers_are_safe_for_unknown_pid()
    {
        Assert.False(ForegroundWindow.IsProcessInForeground(-1));
        Assert.Equal(IntPtr.Zero, ForegroundWindow.GetMainWindowHandle(-1));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(ForegroundWindow.IsProcessInForeground(Environment.ProcessId));
            Assert.Equal(IntPtr.Zero, ForegroundWindow.GetMainWindowHandle(Environment.ProcessId));
        }
    }
}
