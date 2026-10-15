using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class VisualScriptDocument
{
    internal VisualScriptDocument(string path, AssetItem item, EditorSessionHistory history)
        : this(path, new AssetLoadResult<Asset>(item.Asset, null, false, item.YamlMetadata), [], history, false) { }
}
