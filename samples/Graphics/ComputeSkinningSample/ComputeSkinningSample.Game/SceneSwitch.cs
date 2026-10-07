using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine;

namespace ComputeSkinningSample;

static class SceneSwitch
{
    // Replaces the running root scene; the UrlReference also makes the asset compiler include the target scene.
    public static void Load(ScriptComponent script, UrlReference<Scene> target)
    {
        if (target == null || target.IsEmpty) return;
        var sceneInstance = script.SceneSystem.SceneInstance;
        var previous = sceneInstance.RootScene;
        Scene scene;
        try
        {
            scene = script.Content.Load(target);
        }
        catch (ContentManagerException e)
        {
            // A moved or renamed scene leaves a stale URL in the menu; stay in the current scene.
            Stride.Core.Diagnostics.GlobalLogger.GetLogger(nameof(SceneSwitch)).Error($"Cannot open scene '{target.Url}'.", e);
            return;
        }
        sceneInstance.RootScene = scene;
        if (previous != null) script.Content.Unload(previous);
    }
}
