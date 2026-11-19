namespace DbdMaxing.Core.Models;

/// <summary>A survivor or killer. <see cref="DlcId"/> is null for base-game characters.</summary>
public sealed record GameCharacter(string Id, string Name, Role Role, string? DlcId);
