namespace Stride.Core.Assets;

/// <summary>
/// Restores semantic aliases to owned objects with the same identifiers after
/// deserialization, before collision analysis can treat aliases as duplicates.
/// Implementations must validate all targets before mutation, reject ambiguous
/// or foreign references, and leave identifiers, files and authored history
/// unchanged. This callback does not mark an asset dirty or upgrade its file.
/// </summary>
public interface IAssetObjectReferenceNormalizer
{
    void NormalizeObjectReferences();
}
