// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using Stride.Shaders.Compiler;
using StrideEffects;

namespace Stride.Rendering
{
    /// <summary>Native hierarchy-editor mesh shader and fallback parameter policy.</summary>
    public static class EditorMeshEffects
    {
        public const string ForwardShadingEffect = "StrideEditorForwardShadingEffect";

        public static void SetFallbackParameters(RenderEffect effect, RenderEffectState state, CompilerParameters parameters)
        {
            var name = effect.EffectSelector.EffectName;
            // Editor feedback/picking must not become the material compiling/error visualization.
            if (name.EndsWith(".Wireframe", StringComparison.Ordinal)
                || name.EndsWith(".Highlight", StringComparison.Ordinal)
                || name.EndsWith(".Picking", StringComparison.Ordinal)) return;
            if (state == RenderEffectState.Compiling) parameters.Set(SceneEditorParameters.IsEffectCompiling, true);
            else if (state == RenderEffectState.Error) parameters.Set(SceneEditorParameters.IsEffectError, true);
        }
    }

    /// <summary>Preserves actual stage membership while enabling original editor shader mixins.</summary>
    public sealed class EditorMeshRenderStageSelector : RenderStageSelector
    {
        public override void Process(RenderObject renderObject)
        {
            for (var index = 0; index < renderObject.ActiveRenderStages.Length; index++)
            {
                if (!renderObject.ActiveRenderStages[index].Active) continue;
                var name = renderObject.ActiveRenderStages[index].EffectSelector.EffectName;
                if (name == "StrideForwardShadingEffect" || name.StartsWith("StrideForwardShadingEffect.", StringComparison.Ordinal))
                    renderObject.ActiveRenderStages[index].EffectSelector = new EffectSelector(
                        name.Replace("StrideForwardShadingEffect", EditorMeshEffects.ForwardShadingEffect));
            }
        }
    }
}
