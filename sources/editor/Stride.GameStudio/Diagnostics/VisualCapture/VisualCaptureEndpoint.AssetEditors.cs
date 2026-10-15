using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Presentation.Controls;
using Stride.GameStudio.AssetsEditors;
using Stride.GameStudio.ViewModels;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed partial class VisualCaptureEndpoint
{
    private async Task<(object Result, Func<bool> Completed)> OpenCaptureAssetEditor(CaptureRequest request, CaptureTarget target, Window window)
    {
        RequireParameters(request, "id", "name");
        RequireMainNavigation(window);
        if (request.Target != "assets.browser" && !target.SemanticIds.Contains("assets.browser"))
            throw new CaptureEndpointException("action_not_allowed", "Opening an asset editor requires the actual asset browser target.");
        var session = GameStudioViewModel.GameStudio?.Session
            ?? throw new CaptureEndpointException("action_unavailable", "An actual loaded session is required.");
        var id = StringParameter(request, "id"); var name = StringParameter(request, "name");
        if (id == null && name == null)
            throw new CaptureEndpointException("invalid_parameters", "Specify the canonical asset id, name or URL.");
        var matches = session.AllAssets.Where(asset => (id == null || string.Equals(asset.Id.ToString(), id, StringComparison.OrdinalIgnoreCase))
            && (name == null || string.Equals(asset.Name, name, StringComparison.Ordinal) || string.Equals(asset.Url, name, StringComparison.Ordinal))).ToArray();
        if (matches.Length != 1)
            throw new CaptureEndpointException(matches.Length == 0 ? "asset_not_found" : "ambiguous_asset", "Specify one canonical loaded asset id or URL.");
        var asset = matches[0];
        var manager = session.ServiceProvider.Get<IAssetEditorsManager>() as AssetEditorsManager
            ?? throw new CaptureEndpointException("action_unavailable", "The native asset editor manager is unavailable.");
        var reused = manager.InspectCaptureEditor(asset) != null;
        var timeout = Math.Clamp(request.TimeoutMs, 100, 120000);
        var watch = Stopwatch.StartNew();
        // Await on the dispatcher; native initialization can continue on it.
        // Do not update MRU/persisted open-editor settings during capture navigation.
        try
        {
            await manager.OpenAssetEditorWindow(asset, saveSettings: false)
                .WaitAsync(TimeSpan.FromMilliseconds(timeout), stop.Token);
        }
        catch (TimeoutException)
        {
            throw new CaptureEndpointException("editor_initialization_timeout", "The native editor initialization is still pending; no ready state or capture is claimed.");
        }
        var state = manager.InspectCaptureEditor(asset)
            ?? throw new CaptureEndpointException("editor_initialization_failed", "The native manager did not register a successfully initialized editor for this asset.");
        bool Ready()
        {
            var actual = manager.InspectCaptureEditor(asset);
            if (actual is not { InitializationCompleted: true, Visible: true, Selected: true, Visual: { IsVisible: true } visual }
                || visual.ActualWidth <= 0 || visual.ActualHeight <= 0) return false;
            return Descendants(visual).OfType<GameEngineHost>().All(host => host.Handle != IntPtr.Zero && host.IsVisible);
        }
        while (!Ready())
        {
            if (watch.ElapsedMilliseconds >= timeout)
                throw new CaptureEndpointException("editor_presentation_timeout", "The initialized editor has no selected visible realized presentation yet.");
            await Task.Delay(16, stop.Token);
        }
        state = manager.InspectCaptureEditor(asset);
        var nativeHosts = state.Visual == null ? Array.Empty<string>()
            : Descendants(state.Visual).OfType<GameEngineHost>().Select(host => CaptureNativeBounds.Handle(host.Handle)).ToArray();
        return (new { id = state.AssetId, name = state.AssetName, url = state.AssetUrl,
            editorType = state.EditorType, viewType = state.ViewType, reused,
            initializationCompleted = state.InitializationCompleted, nativeHostHwnds = nativeHosts,
            ready = Ready(), engineFramesVerified = false, initialization = "Original awaited native editor/view initialization; rendered GPU frame readiness is separate." }, Ready);
    }
}
