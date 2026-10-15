using System;
using System.Linq;
using System.Reflection;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Presentation.Controls;
using Stride.Editor.Preview;
using Stride.Editor.Preview.View;
using Stride.GameStudio.ViewModels;
using Stride.GameStudio.AssetsEditors;
using Stride.Assets.Entities;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed partial class VisualCaptureEndpoint
{
    private static object CaptureSelectionMetadata()
    {
        var session = GameStudioViewModel.GameStudio?.Session;
        var nativeEditors = session?.ServiceProvider.TryGet<IAssetEditorsManager>() as AssetEditorsManager;
        var sceneEditors = session == null || nativeEditors == null ? Array.Empty<object>()
            : session.AllAssets.Where(asset => asset.Asset is SceneAsset)
                .Select(asset => new { Asset = asset, State = nativeEditors.InspectCaptureEditor(asset) })
                .Where(item => item.State != null).Select(item =>
                {
                    var state = item.State;
                    var nativeHosts = state.Visual == null ? Array.Empty<GameEngineHost>() : Descendants(state.Visual).OfType<GameEngineHost>().ToArray();
                    var viewportReady = state.InitializationCompleted && state.Visible && state.Selected
                        && state.Visual is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }
                        && nativeHosts.Length > 0 && nativeHosts.All(host => host.IsVisible && host.Handle != IntPtr.Zero);
                    return (object)new
                    {
                        assetId = state.AssetId, path = item.Asset.AssetItem.FullPath.ToOSPath(),
                        initialized = state.InitializationCompleted, visible = state.Visible, selected = state.Selected,
                        viewportReady, nativeHostHwnds = nativeHosts.Select(host => CaptureNativeBounds.Handle(host.Handle)).ToArray(),
                        nativeViewportPixels = nativeHosts.Select(host => new
                        {
                            width = (int)Math.Round(host.ActualWidth * System.Windows.Media.VisualTreeHelper.GetDpi(host).DpiScaleX),
                            height = (int)Math.Round(host.ActualHeight * System.Windows.Media.VisualTreeHelper.GetDpi(host).DpiScaleY),
                        }).ToArray(),
                        engineFramesVerified = false,
                    };
                }).ToArray();
        var selected = session?.ActiveAssetView?.SelectedAssets.Select(asset => asset.Id.ToString()).ToArray() ?? [];
        var properties = session?.ActiveProperties;
        var related = properties?.Selection == null ? Array.Empty<string>()
            : properties.GetRelatedAssets().Where(asset => asset != null).Select(asset => asset.Id.ToString()).Distinct().ToArray();
        // Identity comes from the actual adopted view's native preview owner,
        // never the browser selection or service's requested build queue.
        var previewView = GameStudioViewModel.GameStudio?.Preview?.PreviewObject as StridePreviewView;
        var preview = previewView == null ? null
            : typeof(StridePreviewView).GetField("previewer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(previewView) as IAssetPreview;
        var previewAsset = preview?.AssetViewModel;
        var previewService = session?.ServiceProvider.TryGet<IAssetPreviewService>();
        var previewInitialized = preview?.IsInitialized().IsCompletedSuccessfully == true;
        var previewVisible = previewView is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 };
        var hosts = previewView == null ? Array.Empty<GameEngineHost>() : Descendants(previewView).OfType<GameEngineHost>().ToArray();
        var previewReady = previewInitialized && previewVisible && previewAsset != null
            && ReferenceEquals(previewService?.GetCurrentPreviewView(), previewView)
            && ReferenceEquals(previewView.DataContext, preview.PreviewViewModel)
            && hosts.Length > 0 && hosts.All(host => host.IsVisible && host.Handle != IntPtr.Zero);
        return new
        {
            projectReady = session?.IsEditorInitialized == true,
            projectPath = session?.SessionFilePath?.ToOSPath(),
            projectAssetCount = session?.AllAssets.Count() ?? 0,
            sceneEditors,
            matchedBenchmarkGeometry = matchedBenchmarkGeometryEvidence,
            readinessBoundary = "Actual initialized package/session and selected visible native scene editor/host; rendered engine frame readiness is separate.",
            selectedAssetId = selected.Length == 1 ? selected[0] : null,
            selectedAssetIds = selected,
            propertyAssetId = related.Length == 1 ? related[0] : null,
            propertyAssetIds = related,
            propertyGraphReady = properties?.CanDisplayProperties == true && properties.ViewModel != null,
            propertyOwnerType = properties?.GetType().FullName,
            propertySelectionTypes = properties?.Selection?.Select(provider => provider.GetType().FullName).ToArray() ?? [],
            propertySelectionCount = properties?.Selection?.Count ?? 0,
            propertyFallbackMessage = properties?.FallbackMessage,
            propertyName = properties?.Name,
            propertyTypeDescription = properties?.TypeDescription,
            previewAssetId = previewAsset?.Id.ToString(),
            previewAssetPath = previewAsset?.AssetItem.FullPath.ToOSPath(),
            previewUrl = previewAsset?.Url,
            previewAssetType = previewAsset?.Asset.GetType().FullName,
            previewType = preview?.GetType().FullName,
            previewViewType = previewView?.GetType().FullName,
            previewInitialized, previewVisible, previewReady,
            previewNativeHostHwnds = hosts.Select(host => CaptureNativeBounds.Handle(host.Handle)).ToArray(),
            previewEngineFramesVerified = false,
        };
    }
}
