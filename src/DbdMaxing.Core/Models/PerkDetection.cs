namespace DbdMaxing.Core.Models;

/// <summary>A perk icon recognised on screen by the vision scanner.</summary>
public sealed record PerkDetection(string PerkId, double Confidence);
