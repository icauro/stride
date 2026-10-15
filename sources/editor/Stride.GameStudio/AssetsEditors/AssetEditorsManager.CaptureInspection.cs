using System;
using System.Windows;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;

namespace Stride.GameStudio.AssetsEditors;

internal sealed partial class AssetEditorsManager
{
    internal sealed record CaptureEditorState(string AssetId, string AssetName, string AssetUrl,
        string EditorType, string ViewType, bool InitializationCompleted, bool Visible, bool Selected,
        FrameworkElement Visual);

    // Inspect actual registered owners only. Registration follows successful
    // awaited native InitializeEditor; this does not fabricate a rendered frame.
    internal CaptureEditorState InspectCaptureEditor(AssetViewModel asset)
    {
        if (!openedAssets.TryGetValue(asset, out var editor) || !assetEditors.TryGetValue(editor, out var pane)) return null;
        var view = pane.Content as IEditorView;
        return new(asset.Id.ToString(), asset.Name, asset.Url, editor.GetType().FullName,
            view?.GetType().FullName, view?.EditorInitialization?.IsCompletedSuccessfully == true,
            pane.IsVisible, pane.IsSelected, pane.Content as FrameworkElement);
    }
}
