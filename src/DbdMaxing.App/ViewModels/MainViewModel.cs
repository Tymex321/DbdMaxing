using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbdMaxing.App.Services;
using DbdMaxing.Core;
using DbdMaxing.Core.Builds;
using DbdMaxing.Core.Data;
using DbdMaxing.Core.Models;
using DbdMaxing.Core.Steam;
using DbdMaxing.Core.Storage;

namespace DbdMaxing.App.ViewModels;

/// <summary>
/// Owns the app state: the current Steam account, its owned perks, and the recommended builds.
/// All public methods must be called on the UI thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int BuildsPerRole = 5;

    private readonly IPerkCatalog _catalog;
    private readonly IBuildEngine _engine;
    private readonly ISteamAccountReader _steam;
    private readonly IProfileStore _store;
    private readonly Dictionary<string, PerkItemViewModel> _items = new();
    private OwnedPerkSet _owned = new();
    private SteamAccount? _account;

    public MainViewModel(IPerkCatalog catalog, IBuildEngine engine, ISteamAccountReader steam, IProfileStore store, AppSettings settings)
    {
        _catalog = catalog;
        _engine = engine;
        _steam = steam;
        _store = store;
        Settings = settings;

        PerksView = CollectionViewSource.GetDefaultView(AllPerks);
        PerksView.Filter = FilterPerk;
        PerksView.SortDescriptions.Add(new SortDescription(nameof(PerkItemViewModel.Name), ListSortDirection.Ascending));
    }

    public AppSettings Settings { get; }

    public ObservableCollection<PerkItemViewModel> AllPerks { get; } = new();
    public ICollectionView PerksView { get; }
    public ObservableCollection<BuildViewModel> SurvivorBuilds { get; } = new();
    public ObservableCollection<BuildViewModel> KillerBuilds { get; } = new();
    public ObservableCollection<KillerOption> Killers { get; } = new();

    [ObservableProperty] private string _accountName = "NO STEAM ACCOUNT";
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private string _gameStatus = "WAITING FOR DEAD BY DAYLIGHT";
    [ObservableProperty] private string _scannerStatus = "Idle";
    [ObservableProperty] private string _lastScanText = "No perks scanned yet";
    [ObservableProperty] private double _iconProgress = 100;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private int _ownedSurvivorCount;
    [ObservableProperty] private int _ownedKillerCount;
    [ObservableProperty] private int _totalSurvivorCount;
    [ObservableProperty] private int _totalKillerCount;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _roleFilter = "All";
    [ObservableProperty] private bool _ownedOnly;
    [ObservableProperty] private KillerOption? _selectedKiller;
    [ObservableProperty] private string _currentPage = "survivor";

    public bool StartWithWindows
    {
        get => Settings.StartWithWindows;
        set
        {
            Settings.StartWithWindows = value;
            StartupRegistration.Apply(value);
            SaveSettings();
        }
    }

    public bool AssumeGeneralPerksOwned
    {
        get => Settings.AssumeGeneralPerksOwned;
        set
        {
            Settings.AssumeGeneralPerksOwned = value;
            SaveSettings();
            RecomputeBuilds();
        }
    }

    public bool ScanEnabled
    {
        get => Settings.ScanEnabled;
        set
        {
            Settings.ScanEnabled = value;
            SaveSettings();
            ScanEnabledChanged?.Invoke(this, value);
        }
    }

    public bool NotifyOnGameStart
    {
        get => Settings.NotifyOnGameStart;
        set { Settings.NotifyOnGameStart = value; SaveSettings(); }
    }

    public bool MinimizeToTray
    {
        get => Settings.MinimizeToTray;
        set { Settings.MinimizeToTray = value; SaveSettings(); }
    }

    public string DataFolder => AppPaths.Root;

    /// <summary>Raised when the user turns screen scanning on or off.</summary>
    public event EventHandler<bool>? ScanEnabledChanged;

    /// <summary>Raised once perk icons are cached and the scanner can start.</summary>
    public event EventHandler? IconsReady;

    public bool AreIconsReady { get; private set; }

    public async Task InitializeAsync()
    {
        IsLoading = true;
        try
        {
            await _catalog.LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Catalog load failed", ex);
        }

        var characterNames = _catalog.Characters.ToDictionary(c => c.Id, c => c.Name);
        AllPerks.Clear();
        _items.Clear();
        foreach (var perk in _catalog.Perks)
        {
            var name = perk.CharacterId is { } id && characterNames.TryGetValue(id, out var n) ? n : null;
            var item = new PerkItemViewModel(perk, name, IconFor(perk), OnPerkToggled);
            _items[perk.Id] = item;
            AllPerks.Add(item);
        }
        TotalSurvivorCount = _catalog.Perks.Count(p => p.Role == Role.Survivor);
        TotalKillerCount = _catalog.Perks.Count(p => p.Role == Role.Killer);

        Killers.Clear();
        Killers.Add(new KillerOption(null, "Any killer"));
        foreach (var killer in _catalog.Characters.Where(c => c.Role == Role.Killer).OrderBy(c => c.Name))
            Killers.Add(new KillerOption(killer.Id, killer.Name));
        SelectedKiller = Killers[0];

        LoadAccount();
        IsLoading = false;

        _ = DownloadIconsAsync();
    }

    /// <summary>Re-reads the Steam account (it may have changed) and reloads its saved perks.</summary>
    public void LoadAccount()
    {
        var account = _steam.GetCurrentAccount();
        if (account is not null && account.SteamId64 == _account?.SteamId64)
            return;

        _account = account;
        AccountName = account is null ? "NO STEAM ACCOUNT" : account.PersonaName.ToUpperInvariant();
        _owned = account is null ? new OwnedPerkSet() : _store.Load(account.SteamId64);

        ApplyOwnedDlc();
        RefreshOwnership();
        RecomputeBuilds();
        Log.Info($"Account: {account?.PersonaName ?? "none"}, {_owned.Perks.Count} owned perks");
    }

    public void OnGameStarted()
    {
        IsGameRunning = true;
        GameStatus = "DEAD BY DAYLIGHT DETECTED";
        LoadAccount();
    }

    public void OnGameExited()
    {
        IsGameRunning = false;
        GameStatus = "WAITING FOR DEAD BY DAYLIGHT";
        ScannerStatus = "Idle";
    }

    public void OnScannerStatus(string status) => ScannerStatus = status;

    public void OnPerksDetected(IReadOnlyList<PerkDetection> detections)
    {
        var added = detections.Where(d => _owned.Add(d.PerkId, PerkSource.Scanned)).Select(d => d.PerkId).ToList();
        if (added.Count == 0)
            return;

        SaveOwned();
        RefreshOwnership();
        RecomputeBuilds();
        var names = added.Select(id => _catalog.Find(id)?.Name ?? id).Take(3);
        LastScanText = $"+{added.Count} new: {string.Join(", ", names)}{(added.Count > 3 ? "…" : "")}";
        Log.Info($"Scanned new perks: {string.Join(", ", added)}");
    }

    [RelayCommand]
    private void Navigate(string page) => CurrentPage = page;

    [RelayCommand]
    private void RecomputeBuilds()
    {
        var owned = _catalog.Perks.Where(IsUsable).ToList();
        Fill(SurvivorBuilds, _engine.Recommend(Role.Survivor, owned, BuildsPerRole));
        Fill(KillerBuilds, _engine.Recommend(Role.Killer, owned, BuildsPerRole, SelectedKiller?.Id));
    }

    [RelayCommand]
    private void MarkAllVisible(string owned)
    {
        var value = owned == "true";
        foreach (var item in PerksView.Cast<PerkItemViewModel>().ToList())
        {
            if (value)
                _owned.Add(item.Perk.Id, PerkSource.Manual);
            else
                _owned.RemoveManually(item.Perk.Id);
        }
        SaveOwned();
        RefreshOwnership();
        RecomputeBuilds();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open data folder", ex);
        }
    }

    [RelayCommand]
    private async Task RedownloadIcons() => await DownloadIconsAsync();

    partial void OnSearchTextChanged(string value) => PerksView.Refresh();
    partial void OnRoleFilterChanged(string value) => PerksView.Refresh();
    partial void OnOwnedOnlyChanged(bool value) => PerksView.Refresh();

    partial void OnSelectedKillerChanged(KillerOption? value)
    {
        if (!IsLoading)
            Fill(KillerBuilds, _engine.Recommend(Role.Killer, _catalog.Perks.Where(IsUsable), BuildsPerRole, value?.Id));
    }

    private async Task DownloadIconsAsync()
    {
        IconProgress = 0;
        var progress = new Progress<double>(p => IconProgress = p * 100);
        try
        {
            await _catalog.EnsureIconsAsync(progress);
        }
        catch (Exception ex)
        {
            Log.Error("Icon download failed", ex);
        }
        IconProgress = 100;
        foreach (var item in AllPerks)
            item.IconSource = IconFor(item.Perk);
        RecomputeBuilds();
        AreIconsReady = true;
        IconsReady?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyOwnedDlc()
    {
        var dlcs = _steam.GetOwnedDlcIds();
        if (dlcs.Count == 0)
            return;

        var owningCharacters = _catalog.Characters.Where(c => c.DlcId is { } d && dlcs.Contains(d)).Select(c => c.Id).ToHashSet();
        var changed = false;
        foreach (var perk in _catalog.Perks.Where(p => p.CharacterId is { } id && owningCharacters.Contains(id)))
            changed |= _owned.Add(perk.Id, PerkSource.Dlc);
        if (changed)
            SaveOwned();
    }

    private void OnPerkToggled(PerkItemViewModel item, bool owned)
    {
        if (owned)
            _owned.Add(item.Perk.Id, PerkSource.Manual);
        else
            _owned.RemoveManually(item.Perk.Id);
        item.SetOwned(owned, owned ? PerkSource.Manual : null);
        SaveOwned();
        UpdateCounts();
        RecomputeBuilds();
    }

    private bool IsUsable(Perk perk) => _owned.Contains(perk.Id) || (Settings.AssumeGeneralPerksOwned && perk.IsGeneral);

    private void RefreshOwnership()
    {
        foreach (var item in AllPerks)
        {
            var has = _owned.Perks.TryGetValue(item.Perk.Id, out var owned);
            item.SetOwned(has, owned?.Source);
        }
        UpdateCounts();
        PerksView.Refresh();
    }

    private void UpdateCounts()
    {
        OwnedSurvivorCount = AllPerks.Count(p => p.IsOwned && p.IsSurvivor);
        OwnedKillerCount = AllPerks.Count(p => p.IsOwned && !p.IsSurvivor);
    }

    private void Fill(ObservableCollection<BuildViewModel> target, IReadOnlyList<Build> builds)
    {
        target.Clear();
        var best = builds.Count > 0 ? builds.Max(b => b.Score) : 0;
        for (var i = 0; i < builds.Count; i++)
            target.Add(new BuildViewModel(builds[i], i + 1, best, IconFor));
    }

    private string? IconFor(Perk perk) => _catalog.GetIconPath(perk.Id) ?? perk.IconUrl;

    private bool FilterPerk(object obj)
    {
        if (obj is not PerkItemViewModel item)
            return false;
        if (OwnedOnly && !item.IsOwned)
            return false;
        if (RoleFilter == "Survivor" && !item.IsSurvivor || RoleFilter == "Killer" && item.IsSurvivor)
            return false;
        return string.IsNullOrWhiteSpace(SearchText)
               || item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
               || item.CharacterName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveOwned()
    {
        if (_account is not null)
            _store.Save(_account.SteamId64, _owned);
    }

    private void SaveSettings() => Settings.Save();
}
