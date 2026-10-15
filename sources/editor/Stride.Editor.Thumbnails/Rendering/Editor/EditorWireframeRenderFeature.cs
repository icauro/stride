// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Diagnostics;
using Stride.Core.Mathematics;

using Stride.Graphics;

namespace Stride.Rendering
{
    /// <summary>
    /// Performs wireframe rendering.
    /// </summary>
    public class EditorWireframeRenderFeature : SubRenderFeature
    {
        public Func<bool> IsEnabled { get; set; }
        public Func<bool> DisplaySelectionMask { get; set; }
        public int PreparedNodes { get; private set; }

        private ConstantBufferOffsetReference perDrawData;

        private readonly Stopwatch clockSelection = new Stopwatch();

        // TODO: Make configurable (per object/view/...?)
        private struct PerDraw
        {
            public Color3 FrontColor;
            public float ColorBlend;
            public Color3 BackColor;
            public float AlphaBlend;
        }

        public void NotifySelectionChanged() => clockSelection.Restart();

        /// <inheritdoc/>
        protected override void InitializeCore()
        {
            perDrawData = ((RootEffectRenderFeature)RootRenderFeature).CreateDrawCBufferOffsetSlot(MaterialFrontBackBlendShaderKeys.ColorFront.Name);
        }

        /// <inheritdoc/>
        public override unsafe void Prepare(RenderDrawContext context)
        {
            PreparedNodes = 0;
            if (IsEnabled?.Invoke() == false) return;
            var blendValue = (DisplaySelectionMask?.Invoke() ?? false) ? 1.0f : MathUtil.Clamp((1.0f - (float)clockSelection.Elapsed.TotalSeconds) / 1.0f, 0, 1);
            var perDrawValue = new PerDraw
            {
                FrontColor = ((Color3)Color.FromBgra(0xFFFFDC51)).ToColorSpace(Context.GraphicsDevice.ColorSpace),
                BackColor = ((Color3)Color.FromBgra(0xFFFF8300)).ToColorSpace(Context.GraphicsDevice.ColorSpace),
                ColorBlend = 0.3f * blendValue,
                AlphaBlend = 0.1f * blendValue
            };

            foreach (var renderNode in ((RootEffectRenderFeature)RootRenderFeature).RenderNodes)
            {
                if (!renderNode.RenderEffect.EffectSelector.EffectName.EndsWith(".Wireframe", StringComparison.Ordinal)) continue;
                var perDrawLayout = renderNode.RenderEffect.Reflection?.PerDrawLayout;
                if (perDrawLayout == null)
                    continue;

                var perDrawDataOffset = perDrawLayout.GetConstantBufferOffset(this.perDrawData);
                if (perDrawDataOffset == -1)
                    continue;
                    
                var mappedCB = renderNode.Resources.ConstantBuffer.Data;
                var perDraw = (PerDraw*)((byte*)mappedCB + perDrawDataOffset);
                *perDraw = perDrawValue;
                PreparedNodes++;
            }
        }

        public override void ProcessPipelineState(RenderContext context, RenderNodeReference renderNodeReference, ref RenderNode renderNode, RenderObject renderObject, PipelineStateDescription pipelineState)
        {
            base.ProcessPipelineState(context, renderNodeReference, ref renderNode, renderObject, pipelineState);
            if (!renderNode.RenderEffect.EffectSelector.EffectName.EndsWith(".Wireframe", StringComparison.Ordinal)) return;

            // Check if this is a wireframe rendering
            var perDrawLayout = renderNode.RenderEffect.Reflection?.PerDrawLayout;
            if (perDrawLayout == null)
                return;

            var perDrawDataOffset = perDrawLayout.GetConstantBufferOffset(this.perDrawData);
            if (perDrawDataOffset == -1)
                return;

            // Display using wireframe and without depth-buffer
            pipelineState.BlendState = BlendStates.AlphaBlend;
            pipelineState.RasterizerState = RasterizerStates.Wireframe;
            pipelineState.DepthStencilState.DepthBufferEnable = false;
        }

    }
}
