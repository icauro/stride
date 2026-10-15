using System;
using Stride.Core.Extensions;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Particles;
using Stride.Particles.Initializers;
using Stride.Particles.Modules;

namespace Stride.Assets.Presentation.NodePresenters.Rules;

internal static class ParticleTransformNodeRules
{
    internal static void Apply(INodePresenter root, Type? rootDeclaredType = null)
    {
        if (IsModule(rootDeclaredType ?? root.Type) && root.Value is ParticleTransform) ApplyModule(root);
        foreach (var node in root.Children.BreadthFirst(child => child.Children))
            if (IsModule(node.Type) && node.Value is ParticleTransform) ApplyModule(node);
    }

    private static bool IsModule(Type type) => type == typeof(ParticleInitializer) || type == typeof(ParticleUpdater);

    private static void ApplyModule(INodePresenter node)
    {
        var position = Flag(node, nameof(ParticleTransform.DisplayParticlePosition));
        var rotation = Flag(node, nameof(ParticleTransform.DisplayParticleRotation));
        var scale = Flag(node, nameof(ParticleTransform.DisplayParticleScale));
        var uniform = Flag(node, nameof(ParticleTransform.DisplayParticleScaleUniform));
        Visible(node, nameof(ParticleTransform.Position), position);
        Visible(node, nameof(ParticleTransform.InheritPosition), position);
        Visible(node, nameof(ParticleTransform.Rotation), rotation);
        Visible(node, nameof(ParticleTransform.InheritRotation), rotation);
        Visible(node, nameof(ParticleTransform.Scale), scale);
        Visible(node, nameof(ParticleTransform.ScaleUniform), uniform);
        Visible(node, nameof(ParticleTransform.InheritScale), scale, uniform);
    }

    private static INodePresenter? Flag(INodePresenter parent, string name)
    {
        var flag = parent.TryGetChild(name);
        if (flag != null) flag.IsVisible = false;
        return flag;
    }
    private static void Visible(INodePresenter parent, string name, INodePresenter? flag, INodePresenter? alternate = null)
    {
        var target = parent.TryGetChild(name);
        if (target == null || flag == null && alternate == null) return;
        if (flag != null) target.AddDependency(flag, false);
        if (alternate != null) target.AddDependency(alternate, false);
        target.IsVisible = flag?.Value is true || alternate?.Value is true;
    }
}
