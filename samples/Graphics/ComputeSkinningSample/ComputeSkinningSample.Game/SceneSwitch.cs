using Stride.Core.Serialization;
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
        sceneInstance.RootScene = script.Content.Load(target);
        if (previous != null) script.Content.Unload(previous);
    }
}
