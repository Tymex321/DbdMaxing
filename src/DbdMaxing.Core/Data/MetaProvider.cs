using System.Text.Json;
using System.Text.Json.Serialization;
using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Data;

/// <summary>
/// Loads the embedded Data/meta.json. If <c>{AppPaths.Root}/meta.override.json</c> exists and parses, it wins.
/// </summary>
public sealed class MetaProvider : IMetaProvider
{
    public const string ResourceName = "DbdMaxing.Core.Data.meta.json";
    public const string OverrideFileName = "meta.override.json";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string? _overridePath;

    /// <param name="overridePath">Override file to check; defaults to AppPaths.Root/meta.override.json.</param>
    public MetaProvider(string? overridePath = null) => _overridePath = overridePath;

    public string OverridePath => _overridePath ?? Path.Combine(AppPaths.Root, OverrideFileName);

    /// <summary>True when the last <see cref="Load"/> used the override file.</summary>
    public bool UsedOverride { get; private set; }

    public MetaData Load()
    {
        UsedOverride = false;
        if (File.Exists(OverridePath))
        {
            try
            {
                var meta = Parse(File.ReadAllText(OverridePath));
                UsedOverride = true;
                return meta;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken override must not break the app; fall back to the embedded meta.
            }
        }
        return LoadEmbedded();
    }

    public static MetaData LoadEmbedded()
    {
        using var stream = typeof(MetaProvider).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static MetaData Parse(string json) =>
        JsonSerializer.Deserialize<MetaData>(json, JsonOptions) ?? new MetaData();
}
