using Stride.Core.Extensions;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Engine;
using Stride.Engine.Processors;

namespace Stride.Assets.Presentation.NodePresenters.Rules;

internal static class CameraComponentNodeRules
{
    internal static void Apply(INodePresenter root)
    {
        foreach (var node in root.Children.BreadthFirst(x => x.Children))
        {
            if (node.Parent?.Value is not CameraComponent) continue;
            if (node.Name == nameof(CameraComponent.VerticalFieldOfView))
            {
                node.AddDependency(node.Parent[nameof(CameraComponent.Projection)], false);
                node.IsVisible = (CameraProjectionMode)node.Parent[nameof(CameraComponent.Projection)].Value == CameraProjectionMode.Perspective;
            }
            if (node.Name == nameof(CameraComponent.OrthographicSize))
            {
                node.AddDependency(node.Parent[nameof(CameraComponent.Projection)], false);
                node.IsVisible = (CameraProjectionMode)node.Parent[nameof(CameraComponent.Projection)].Value == CameraProjectionMode.Orthographic;
            }
            if (node.Name == nameof(CameraComponent.AspectRatio))
            {
                node.AddDependency(node.Parent[nameof(CameraComponent.UseCustomAspectRatio)], false);
                node.IsVisible = (bool)node.Parent[nameof(CameraComponent.UseCustomAspectRatio)].Value;
            }
        }
    }
}
