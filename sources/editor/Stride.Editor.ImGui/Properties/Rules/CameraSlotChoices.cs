using System.Collections.Generic;
using Stride.Assets.Rendering;
using Stride.Core.Assets;
using Stride.Rendering.Compositing;

namespace Stride.Assets.Presentation.NodePresenters.Rules;

internal sealed record CameraSlotChoice(SceneCameraSlotId Value, string Label, int Order);

internal static class CameraSlotChoices
{
    internal static IEnumerable<CameraSlotChoice> Get(Package package)
    {
        foreach (var compositor in package.Assets)
        {
            if (compositor.Asset is not GraphicsCompositorAsset asset) continue;
            var index = 0;
            foreach (var slot in asset.Cameras)
                yield return new CameraSlotChoice(slot.ToSlotId(), compositor.Location.ToString() + " > " + slot.Name, index++);
        }
    }
}
