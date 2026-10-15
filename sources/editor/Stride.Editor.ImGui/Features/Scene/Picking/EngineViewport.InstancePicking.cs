using Stride.Engine;
using Stride.Core;
using Stride.Engine.Design;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    internal Entity? ResolvePickedSelection(EditorPickingRenderer picker)
    {
        var entity = picker.Result;
        if (entity?.Get<ModelComponent>() is { } model && RuntimeIdHelper.ToRuntimeId(model) == picker.ComponentId
            && entity.Get<InstancingComponent>() is { Enabled: true, Type: InstancingEntityTransform instancing } && picker.InstanceId >= 0)
            entity = instancing.TryGetInstanceAt(picker.InstanceId, out var instance) ? instance.Entity ?? entity : entity;
        return entity;
    }
}
