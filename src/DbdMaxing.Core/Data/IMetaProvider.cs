using DbdMaxing.Core.Models;

namespace DbdMaxing.Core.Data;

public interface IMetaProvider
{
    /// <summary>Loads the embedded meta.json (or an override file in AppPaths.Root if present).</summary>
    MetaData Load();
}
