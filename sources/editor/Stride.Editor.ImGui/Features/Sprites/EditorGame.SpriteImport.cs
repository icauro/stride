using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly record struct SpriteFileDropTarget(AssetMetadataDocument Document, NVector2 Min, NVector2 Max, int Index);
    private readonly List<SpriteFileDropTarget> spriteFileDropTargets = [];
    private WindowsFileDrop? spriteNativeFileDrop;
    private void InitializeSpriteFileDrops() => spriteNativeFileDrop = new WindowsFileDrop(Window.NativeWindow.Handle);
    private void DisposeSpriteFileDrops() { spriteNativeFileDrop?.Dispose(); spriteNativeFileDrop = null; spriteFileDropTargets.Clear(); }
    private void AddSpriteFileDropTarget(AssetMetadataDocument document, int insertion)
    { spriteFileDropTargets.Add(new(document, Gui.GetItemRectMin(), Gui.GetItemRectMax(), insertion)); }
    private void PumpSpriteFileDrops()
    {
        var blocked = projectLoading != null || assetPreviewBuild != null || IsAuthoringPipelineUpdating || closeRequested || closePromptOpen
            || RecoveryTransitionBusy || editorExtensionsStopping;
        while (spriteNativeFileDrop?.TryDequeue(out var drop) == true && drop != null)
        {
            if (blocked) continue;
            var pointer = platformViewports?.MainClientToScreen(drop.X, drop.Y)
                ?? Gui.GetMainViewport().Pos + new NVector2(drop.X, drop.Y);
            if (TryHandleTemplateReferenceFileDrop(drop.Files, pointer)
                || contentReferencePicker?.TryHandleNativeFileDrop(drop.Files, pointer) == true
                || Gui.IsPopupOpen("", Hexa.NET.ImGui.ImGuiPopupFlags.AnyPopupId | Hexa.NET.ImGui.ImGuiPopupFlags.AnyPopupLevel)) continue;
            if (!TryHandleSpriteFileDrop(drop.Files, pointer) && !TryHandlePropertyPathFileDrop(drop.Files, pointer))
                TryHandleAssetBrowserFileDrop(drop.Files, pointer);
        }
        while (platformViewports?.TryDequeueFileDrop(out var childDrop) == true && childDrop != null)
        {
            if (blocked) continue;
            var pointer = new NVector2(childDrop.X, childDrop.Y);
            if (TryHandleTemplateReferenceFileDrop(childDrop.Files, pointer)
                || contentReferencePicker?.TryHandleNativeFileDrop(childDrop.Files, pointer) == true
                || Gui.IsPopupOpen("", Hexa.NET.ImGui.ImGuiPopupFlags.AnyPopupId | Hexa.NET.ImGui.ImGuiPopupFlags.AnyPopupLevel)) continue;
            if (!TryHandleSpriteFileDrop(childDrop.Files, pointer) && !TryHandlePropertyPathFileDrop(childDrop.Files, pointer))
                TryHandleAssetBrowserFileDrop(childDrop.Files, pointer);
        }
    }
    private bool TryHandleSpriteFileDrop(string[] files, NVector2 pointer)
    {
        foreach (var target in spriteFileDropTargets)
                if (target.Document.CanEditSpriteSettings && pointer.X >= target.Min.X && pointer.Y >= target.Min.Y && pointer.X <= target.Max.X && pointer.Y <= target.Max.Y)
                {
                    UseTextureSettingsCommandContext(); Execute(() => target.Document.ImportSpriteFiles(files, target.Index)); return true;
                }
        return false;
    }
    private void ChooseSpriteSourceFiles(AssetMetadataDocument document)
    {
        var extensions = AssetRegistry.RegisteredImporters.Where(importer => importer.RootAssetTypes.Contains(typeof(SpriteSheetAsset)))
            .SelectMany(importer => importer.SupportedFileExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var mask = string.Join(';', extensions.Select(extension => "*" + extension));
        var paths = WindowsFileDialogs.OpenMany(GetCurrentDialogOwner(), null, "Images\0" + mask + "\0All files\0*.*\0\0", Path.GetDirectoryName(document.Path));
        if (paths.Length > 0) document.ImportSpriteFiles(paths);
    }
}
