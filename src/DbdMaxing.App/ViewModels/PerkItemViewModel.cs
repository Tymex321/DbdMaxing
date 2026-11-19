using CommunityToolkit.Mvvm.ComponentModel;
using DbdMaxing.Core.Models;

namespace DbdMaxing.App.ViewModels;

/// <summary>One row in the "My Perks" list. Toggling IsOwned is a manual override.</summary>
public sealed partial class PerkItemViewModel : ObservableObject
{
    private readonly Action<PerkItemViewModel, bool> _onToggled;
    private bool _suppressCallback;

    public PerkItemViewModel(Perk perk, string? characterName, string? iconSource, Action<PerkItemViewModel, bool> onToggled)
    {
        Perk = perk;
        CharacterName = characterName ?? "General perk";
        IconSource = iconSource;
        _onToggled = onToggled;
    }

    public Perk Perk { get; }
    public string Name => Perk.Name;
    public string CharacterName { get; }
    public string? Description => Perk.Description;
    public bool IsSurvivor => Perk.Role == Role.Survivor;

    [ObservableProperty]
    private string? _iconSource;

    [ObservableProperty]
    private bool _isOwned;

    [ObservableProperty]
    private string _sourceLabel = "";

    /// <summary>Updates state from the model without treating it as a user toggle.</summary>
    public void SetOwned(bool owned, PerkSource? source)
    {
        _suppressCallback = true;
        IsOwned = owned;
        _suppressCallback = false;
        SourceLabel = owned && source is { } s ? s switch
        {
            PerkSource.Scanned => "SCANNED",
            PerkSource.Dlc => "DLC",
            _ => "MANUAL",
        } : "";
    }

    partial void OnIsOwnedChanged(bool value)
    {
        if (!_suppressCallback)
            _onToggled(this, value);
    }
}
