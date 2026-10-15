using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private AssetId[] SelectedSourceLibraries(UIDocument source, IEnumerable<Guid> ids)
        => ids.Select(source.SourceLibrary).Where(link => link != null && host.CanOpenSourceLibrary(link.BasePartAsset.Id))
            .Select(link => link!.BasePartAsset.Id).Distinct().ToArray();

    private void DrawSelectedLibraryCommands(UIDocument source, Guid[] ids, bool editable)
    {
        var libraries = SelectedSourceLibraries(source, ids);
        if (host.DrawMenuItem("Open library in editor", enabled: libraries.Length != 0))
            QueueUICommand(source, ids, false, (asset, selection) => host.OpenSourceLibraries(SelectedSourceLibraries(asset, selection)));
        if (host.DrawMenuItem("Select library in asset view", enabled: ids.Length == 1 && libraries.Length == 1))
            QueueUICommand(source, ids, false, (asset, selection) =>
            {
                var current = SelectedSourceLibraries(asset, selection);
                if (current.Length == 1) host.SelectSourceLibrary(current[0]);
            });
        if (host.DrawMenuItem("Break link to library", enabled: editable && libraries.Length != 0))
            QueueUICommand(source, ids, true, (asset, selection) => asset.BreakLibraryLinks(selection));
    }
}
