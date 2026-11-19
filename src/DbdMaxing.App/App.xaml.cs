using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DbdMaxing.App.Services;
using DbdMaxing.App.ViewModels;
using DbdMaxing.Core.Builds;
using DbdMaxing.Core.Data;
using DbdMaxing.Core.Game;
using DbdMaxing.Core.Steam;
using DbdMaxing.Core.Storage;
using DbdMaxing.Vision;

namespace DbdMaxing.App;

/// <summary>
/// Composition root: wires the game watcher, screen scanner, tray icon and main window together.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private HttpClient? _http;
    private TrayIcon? _tray;
    private IGameWatcher? _watcher;
    private IPerkScanner? _scanner;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private int? _gamePid;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "DbdMaxing.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("DbdMaxing is already running. Look for the purple diamond in your system tray.", "DbdMaxing");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        Log.Info("Starting DbdMaxing");

        var settings = AppSettings.Load();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DbdMaxing/0.1 (+https://github.com/Tymex321/DbdMaxing)");

        var catalog = new PerkCatalog(_http);
        var engine = new BuildEngine(new MetaProvider().Load());
        _vm = new MainViewModel(catalog, engine, new SteamAccountReader(), new JsonProfileStore(), settings);
        _scanner = new PerkScanner(catalog, new PerkScannerOptions { Log = Log.Info });
        _watcher = new ProcessGameWatcher();

        _window = new MainWindow(_vm);
        _tray = new TrayIcon();
        _tray.OpenRequested += (_, _) => _window.BringToFront();
        _tray.ExitRequested += (_, _) => ExitApp();

        var startInTray = e.Args.Contains("--tray");
        if (!startInTray)
            _window.Show();

        _scanner.PerksDetected += (_, detections) => Ui(() => _vm.OnPerksDetected(detections));
        if (_scanner is PerkScanner concrete)
            concrete.StatusChanged += (_, status) => Ui(() => _vm.OnScannerStatus(status));
        _watcher.GameStarted += (_, pid) => Ui(() => OnGameStarted(pid));
        _watcher.GameExited += (_, _) => Ui(OnGameExited);
        _vm.ScanEnabledChanged += (_, _) => UpdateScanner();
        _vm.IconsReady += (_, _) =>
        {
            // Pick up icons downloaded after the scanner first loaded its templates.
            (_scanner as PerkScanner)?.ReloadTemplates();
            UpdateScanner();
        };

        await _vm.InitializeAsync();
        _watcher.Start();
    }

    private void OnGameStarted(int pid)
    {
        _gamePid = pid;
        _vm!.OnGameStarted();
        _tray!.SetTooltip($"DbdMaxing: DBD running ({_vm.AccountName})");
        if (_vm.Settings.NotifyOnGameStart)
            _tray.Notify("Dead by Daylight detected", $"Account: {_vm.AccountName}. Open your loadout to scan perks.");
        UpdateScanner();
    }

    private void OnGameExited()
    {
        _gamePid = null;
        _vm!.OnGameExited();
        _tray!.SetTooltip("DbdMaxing");
        UpdateScanner();
    }

    /// <summary>Runs the scanner only while the game is running, scanning is enabled, and icons are cached.</summary>
    private void UpdateScanner()
    {
        if (_scanner is null || _vm is null)
            return;
        var shouldRun = _gamePid is not null && _vm.Settings.ScanEnabled && _vm.AreIconsReady;
        try
        {
            if (shouldRun && !_scanner.IsRunning)
                _scanner.Start(_gamePid!.Value);
            else if (!shouldRun && _scanner.IsRunning)
                _scanner.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Scanner start/stop failed", ex);
        }
        if (!shouldRun)
            _vm.OnScannerStatus(_gamePid is null ? "Idle (game not running)" : _vm.Settings.ScanEnabled ? "Preparing icons…" : "Scanning disabled");
    }

    private void ExitApp()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        _scanner?.Dispose();
        _tray?.Dispose();
        _http?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void Ui(Action action) => Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
}
