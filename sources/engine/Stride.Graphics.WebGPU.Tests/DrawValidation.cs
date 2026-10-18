// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

internal static class DrawValidation
{
    internal static void Run(GraphicsDevice device, CommandList commands)
    {
        var bytecode = CompilerValidation.Compile("WebGpuRaster");
        using var effect = new Effect(device, bytecode);
        using var instance = new EffectInstance(effect);
        instance.UpdateEffect(device);
        var context = new GraphicsContext(device, commandList: commands);
        using var output = Texture.New2D(device, 32, 32, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        using var depth = Texture.New2D(device, 32, 32, PixelFormat.D24_UNorm_S8_UInt, TextureFlags.DepthStencil);
        // Prefix elements exercise baseVertex and byte offsets independently.
        using var vertices = Buffer.New(device, new Vector2[] { new(99, 99), new(-1, -1), new(1, -1), new(1, 1), new(-1, 1) }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        using var indices = Buffer.New(device, new ushort[] { 999, 0, 1, 2, 0, 2, 3 }, BufferFlags.IndexBuffer, GraphicsResourceUsage.Default);
        var pipeline = new MutablePipelineState(device);
        pipeline.State.RootSignature = instance.RootSignature;
        pipeline.State.EffectBytecode = bytecode;
        pipeline.State.InputElements = [new InputElementDescription { SemanticName = "POSITION", Format = PixelFormat.R32G32_Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.Vertex }];
        pipeline.State.PrimitiveType = PrimitiveType.TriangleList;
        pipeline.State.RasterizerState.CullMode = CullMode.None;
        var tint = ParameterKeys.NewValue<Vector4>(name: "WebGpuRaster.Tint");
        var z = ParameterKeys.NewValue<float>(name: "WebGpuRaster.Depth");
        var scale = ParameterKeys.NewValue<float>(name: "WebGpuRaster.Scale");
        var offset = ParameterKeys.NewValue<Vector2>(name: "WebGpuRaster.Offset");
        var spacing = ParameterKeys.NewValue<float>(name: "WebGpuRaster.InstanceSpacing");
        instance.Parameters.Set(scale, 1f); instance.Parameters.Set(offset, Vector2.Zero); instance.Parameters.Set(spacing, 0f);

        commands.SetRenderTargetAndViewport(depth, output);
        commands.Clear(output, new Color4(0, 0, 1, 1));
        commands.Clear(depth, DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil);
        pipeline.State.Output.CaptureState(commands);
        Apply(new Vector4(0, 1, 0, 1), .8f); commands.DrawIndexed(6, baseVertexLocation: 1);
        Apply(new Vector4(1, 0, 0, 1), .2f); commands.DrawIndexed(6, baseVertexLocation: 1);
        Apply(new Vector4(0, 1, 0, 1), .8f); commands.DrawIndexed(6, baseVertexLocation: 1);
        Check(p => p == 0xff0000ff, "Indexed rendering with buffer offsets/baseVertex and depth rejection");

        commands.SetRenderTargetAndViewport(null, output);
        pipeline.State.Output.CaptureState(commands);
        pipeline.State.DepthStencilState.DepthBufferEnable = false;
        pipeline.State.DepthStencilState.DepthBufferWriteEnable = false;
        pipeline.State.RasterizerState.ScissorTestEnable = true;
        commands.SetScissorRectangle(new Rectangle(8, 4, 16, 20));
        commands.Clear(output, new Color4(0, 0, 1, 1));
        Apply(new Vector4(1, 0, 0, 1), .2f); commands.DrawIndexed(6, baseVertexLocation: 1);
        CheckRegion((x, y) => x >= 8 && x < 24 && y >= 4 && y < 24, "Scissor clipping");

        pipeline.State.RasterizerState.ScissorTestEnable = false;
        pipeline.State.Output.CaptureState(commands); // a retained rectangle must not enable a disabled rasterizer scissor
        pipeline.State.BlendState = BlendStates.NonPremultiplied;
        commands.Clear(output, new Color4(0, 0, 1, 1));
        Apply(new Vector4(1, 0, 0, .5f), .2f); commands.DrawIndexed(6, baseVertexLocation: 1);
        // NonPremultiplied uses SrcAlpha for alpha as well: .5*.5 + 1*.5 = .75.
        Check(p => p == 0xbf800080, "Source-alpha blending");

        pipeline.State.BlendState = BlendStates.Opaque;
        instance.Parameters.Set(scale, .25f); instance.Parameters.Set(offset, new Vector2(-.5f, 0)); instance.Parameters.Set(spacing, 1f);
        commands.Clear(output, new Color4(0, 0, 1, 1));
        Apply(new Vector4(1, 0, 0, 1), .2f);
        commands.DrawIndexedInstanced(6, 2, baseVertexLocation: 1);
        CheckRegion((x, y) => ((x >= 4 && x < 12) || (x >= 20 && x < 28)) && y >= 12 && y < 20, "Indexed instancing and SV_InstanceID");

        // Repeat with a 32-bit index buffer and a vertex byte offset instead of baseVertex.
        using var indices32 = Buffer.New(device, new uint[] { 0, 1, 2, 0, 2, 3 }, BufferFlags.IndexBuffer, GraphicsResourceUsage.Default);
        instance.Parameters.Set(scale, 1f); instance.Parameters.Set(offset, Vector2.Zero); instance.Parameters.Set(spacing, 0f);
        Apply(new Vector4(1, 0, 0, 1), .2f);
        commands.SetVertexBuffer(0, vertices, 8, 8);
        commands.SetIndexBuffer(indices32, 0, is32bits: true);
        commands.DrawIndexed(6);
        Check(p => p == 0xff0000ff, "32-bit indices and vertex byte offset");

        using var indexedArguments = Buffer.New(device, new uint[] { 6, 1, 0, 1, 0 }, BufferFlags.ArgumentBuffer, GraphicsResourceUsage.Default);
        commands.Clear(output, new Color4(0, 0, 1, 1));
        Apply(new Vector4(1, 0, 0, 1), .2f);
        commands.DrawIndexedInstanced(indexedArguments);
        Check(p => p == 0xff0000ff, "Indexed indirect draw");

        using var fullscreen = Buffer.New(device, new Vector2[] { new(-1, -1), new(3, -1), new(-1, 3) }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        using var drawArguments = Buffer.New(device, new uint[] { 3, 1, 0, 0 }, BufferFlags.ArgumentBuffer, GraphicsResourceUsage.Default);
        commands.Clear(output, new Color4(0, 0, 1, 1));
        Apply(new Vector4(1, 0, 0, 1), .2f);
        commands.SetVertexBuffer(0, fullscreen, 0, 8);
        commands.DrawInstanced(drawArguments);
        Check(p => p == 0xff0000ff, "Non-indexed indirect draw");
        commands.SetScissorRectangle(default);

        using var multisampled = Texture.New2D(device, 32, 32, 1, PixelFormat.R8G8B8A8_UNorm, textureData: null,
            textureFlags: TextureFlags.RenderTarget, multisampleCount: MultisampleCount.X4);
        using var multisampledDepth = Texture.New2D(device, 32, 32, 1, PixelFormat.D24_UNorm_S8_UInt, textureData: null,
            textureFlags: TextureFlags.DepthStencil, multisampleCount: MultisampleCount.X4);
        commands.SetRenderTargetAndViewport(multisampledDepth, multisampled);
        commands.Clear(multisampled, new Color4(0, 0, 1, 1));
        commands.Clear(multisampledDepth, DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil);
        pipeline.State.Output.CaptureState(commands);
        Apply(new Vector4(1, 0, 0, 1), .2f);
        commands.DrawIndexed(6, baseVertexLocation: 1);
        commands.CopyMultisample(multisampled, 0, output, 0);
        Check(p => p == 0xff0000ff, "Four-sample color/depth rendering and native resolve");

        // Editor overlays enable the legacy line-antialias hint even on
        // single-sample targets. It must not prevent line pipelines from drawing.
        using var lineVertices = Buffer.New(device, new Vector2[] { new(-.75f, -.5f), new(.5f, .75f) }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        pipeline.State.PrimitiveType = PrimitiveType.LineList;
        pipeline.State.DepthStencilState.DepthBufferEnable = false;
        pipeline.State.DepthStencilState.DepthBufferWriteEnable = false;
        foreach (var target in new[] { output, multisampled })
        {
            commands.SetRenderTargetAndViewport(null, target);
            pipeline.State.Output.CaptureState(commands);
            uint[] baseline = null;
            foreach (var hint in new[] { false, true })
            {
                commands.Clear(target, new Color4(0, 0, 1, 1));
                pipeline.State.RasterizerState.MultisampleAntiAliasLine = hint;
                Apply(new Vector4(1, 0, 0, 1), .2f);
                commands.SetVertexBuffer(0, lineVertices, 0, 8);
                commands.Draw(2);
                if (target != output) commands.CopyMultisample(target, 0, output, 0);
                var pixels = output.GetData<uint>(commands);
                if (pixels.Length != 32 * 32 || !pixels.Any(pixel => (pixel & 0xff) != 0) || !pixels.Any(pixel => pixel == 0xffff0000))
                    throw new Exception("Line rasterization must produce both foreground and background pixels.");
                if (baseline != null && !pixels.SequenceEqual(baseline))
                    throw new Exception("The legacy line-antialias hint changed WebGPU output unexpectedly.");
                baseline = pixels;
            }
        }
        Console.WriteLine("PASS: Editor line-antialias hint accepts LineList pipelines and preserves rendered pixels on single-sample and four-sample targets.");

        void Apply(Vector4 color, float depthValue)
        {
            instance.Parameters.Set(tint, color); instance.Parameters.Set(z, depthValue);
            pipeline.Update(); commands.SetPipelineState(pipeline.CurrentState!);
            instance.Apply(context);
            commands.SetVertexBuffer(0, vertices, 0, 8);
            commands.SetIndexBuffer(indices, 2, is32bits: false);
        }

        void Check(Func<uint, bool> valid, string name)
        {
            var pixels = output.GetData<uint>(commands);
            if (!pixels.All(valid)) throw new Exception($"{name}: center={pixels[pixels.Length / 2]:x8}.");
            Console.WriteLine($"PASS: {name}, exact pixels.");
        }

        void CheckRegion(Func<int, int, bool> red, string name)
        {
            var pixels = output.GetData<uint>(commands);
            for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                if (pixels[y * 32 + x] != (red(x, y) ? 0xff0000ff : 0xffff0000))
                    throw new Exception($"{name}: pixel ({x},{y})={pixels[y * 32 + x]:x8}.");
            Console.WriteLine($"PASS: {name}, exact foreground/background pixels.");
        }
    }
}
