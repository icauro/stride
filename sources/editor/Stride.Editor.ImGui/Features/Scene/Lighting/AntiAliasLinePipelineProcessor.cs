// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Editor.ImGui;

// Shared native WPF light-probe wireframe pipeline policy, without its presentation assembly dependency.
internal sealed class AntiAliasLinePipelineProcessor : PipelineProcessor
{
    internal RenderStage RenderStage { get; set; } = null!;

    public override void Process(RenderNodeReference renderNodeReference, ref RenderNode renderNode,
        RenderObject renderObject, PipelineStateDescription pipelineState)
    {
        if (renderNode.RenderStage != RenderStage) return;
        pipelineState.BlendState = BlendStates.NonPremultiplied;
        pipelineState.DepthStencilState = DepthStencilStates.DepthRead;
        pipelineState.RasterizerState.MultisampleAntiAliasLine = true;
    }
}
