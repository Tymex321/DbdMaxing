namespace DbdMaxing.App.ViewModels;

/// <summary>Entry in the killer picker; a null Id means "any killer".</summary>
public sealed record KillerOption(string? Id, string Name)
{
    public override string ToString() => Name;
}
