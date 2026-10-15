using Stride.Assets;
using Stride.Core.Assets;
using Stride.Navigation;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private void ApplyNavigationSettings(GameSettingsAsset settings)
    {
        if (navigationWorkspace == null || loadedScene == null || navigationSettings == null) return;
        // The native GameSettingsChanged payload is an AssetCloner snapshot, so
        // runtime configuration retains its previous values during authored edits.
        var next = AssetCloner.Clone(settings.GetOrDefault<NavigationSettings>());
        var previous = navigationSettings;
        try { ConfigureNavigationSettings(next); }
        catch (Exception failure)
        {
            try { ConfigureNavigationSettings(previous); }
            catch (Exception recovery) { throw new AggregateException("Navigation settings update and restoration failed.", failure, recovery); }
            throw;
        }
        navigationSettings = next;
    }

    private void ConfigureNavigationSettings(NavigationSettings settings)
    {
        navigationControlVisual?.Configure(settings.Groups, visibleNavigationGroups);
        foreach (var entry in navigationAssets)
        {
            entry.Visual.Configure(settings.Groups, visibleNavigationGroups);
            entry.Visual.Replace(entry.Mesh, entry.Mesh, SceneOffset(entry.SceneId));
        }
        dynamicNavigation?.InitializeSettingsFromNavigationSettings(settings);
        if (displayedDynamicMesh != null)
        {
            dynamicNavigationVisual?.Configure(settings.Groups, visibleNavigationGroups);
            dynamicNavigationVisual?.Replace(displayedDynamicMesh, displayedDynamicMesh, -loadedScene!.WorldMatrix.TranslationVector);
        }
    }
}
