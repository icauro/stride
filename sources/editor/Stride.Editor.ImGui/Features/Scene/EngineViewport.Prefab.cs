using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Lights;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private static void AddPrefabEditorLights(Scene scene)
    {
        static Entity Add(Scene target, string name, ILight type, float intensity)
        {
            var entity = new Entity(name) { new LightComponent { Type = type, Intensity = intensity } };
            target.Entities.Add(entity);
            return entity;
        }
        Add(scene, "Prefab Editor Ambient Light", new LightAmbient(), 0.3f);
        var first = Add(scene, "Prefab Editor Ambient Light1", new LightDirectional(), 2.5f);
        first.Transform.Rotation = Quaternion.RotationYawPitchRoll(MathUtil.Pi * 1.125f, MathUtil.Pi * -0.125f, 0);
        var second = Add(scene, "Prefab Editor Ambient Light2", new LightDirectional(), 2.5f);
        second.Transform.Rotation = Quaternion.RotationYawPitchRoll(MathUtil.Pi * 0.125f, MathUtil.Pi * -0.25f, 0);
    }
}
