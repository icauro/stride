// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

internal static class DepthCopyValidation
{
    internal static void Run(GraphicsDevice device, CommandList commands)
    {
        using var sampledEffect = new Effect(device, CompilerValidation.Compile("WebGpuDepthSampling"));
        using var sampled = new EffectInstance(sampledEffect);
        using var white = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue });
        using var output = Texture.New2D(device, 16, 16, PixelFormat.R32G32B32A32_Float, TextureFlags.RenderTarget);
        sampled.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        sampled.Parameters.Set(ParameterKeys.NewObject<Texture>(name: "WebGpuDepthSampling.ColorMap"), white);
        sampled.Parameters.Set(ParameterKeys.NewObject<SamplerState>(name: "WebGpuDepthSampling.DepthSampler"), device.SamplerStates.PointClamp);
        var context = new GraphicsContext(device, commandList: commands);
        foreach (var format in new[] { PixelFormat.D16_UNorm, PixelFormat.D24_UNorm_S8_UInt, PixelFormat.D32_Float })
        {
            var flags = TextureFlags.DepthStencil | TextureFlags.ShaderResource;
            using var source = Texture.New2D(device, 8, 8, 3, format, textureData: null, textureFlags: flags, arraySize: 2);
            using var destination = Texture.New2D(device, 8, 8, 3, format, textureData: null, textureFlags: flags, arraySize: 2);
            var expected = new float[6];
            var stencil = new byte[6];
            var options = format == PixelFormat.D24_UNorm_S8_UInt ? DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil : DepthStencilClearOptions.DepthBuffer;
            for (var layer = 0; layer < 2; layer++)
            for (var mip = 0; mip < 3; mip++)
            {
                var index = layer * 3 + mip;
                expected[index] = (index + 1) * .125f; stencil[index] = (byte)(index + 3);
                using var view = View(source, layer, mip);
                commands.Clear(view, options, expected[index], stencil[index]);
            }
            commands.Copy(source, destination);
            for (var layer = 0; layer < 2; layer++)
            for (var mip = 0; mip < 3; mip++)
            {
                using var view = View(source, layer, mip);
                commands.Clear(view, options, .875f, 101);
            }
            // Overwrite one destination layer and copy between distinct layers
            // of the same texture. Other layers/mips must retain the full copy.
            commands.CopyRegion(source, 0, null, destination, 3);
            expected[3] = .875f; stencil[3] = 101;
            commands.CopyRegion(destination, 1, null, destination, 4);
            expected[4] = expected[1]; stencil[4] = stencil[1];
            Reject<ArgumentException>(() => commands.Copy(destination, destination));
            Reject<ArgumentException>(() => commands.CopyRegion(destination, 0, null, destination, 0));
            Reject<NotSupportedException>(() => commands.CopyRegion(source, 0, new ResourceRegion(0, 0, 0, 4, 8, 1), destination, 0));
            using var larger = Texture.New2D(device, 16, 8, format, flags);
            Reject<NotSupportedException>(() => commands.CopyRegion(source, 0, null, larger, 0));
            using var readback = source.ToStaging();
            if (format == PixelFormat.D32_Float)
            {
                commands.Copy(source, readback);
                for (var layer = 0; layer < 2; layer++)
                for (var mip = 0; mip < 3; mip++)
                {
                    var pixels = readback.GetData<float>(commands, arrayIndex: layer, mipLevel: mip);
                    if (pixels.Length != (8 >> mip) * (8 >> mip) || pixels.Any(pixel => pixel != .875f))
                        throw new Exception($"D32 depth readback is incorrect at layer {layer}, mip {mip}.");
                }
            }
            else
                Reject<NotSupportedException>(() => commands.Copy(source, readback));
            for (var layer = 0; layer < 2; layer++)
            for (var mip = 0; mip < 3; mip++)
            {
                using var view = View(destination, layer, mip);
                sampled.Parameters.Set(ParameterKeys.NewObject<Texture>(name: "WebGpuDepthSampling.DepthMap"), view);
                commands.SetRenderTargetAndViewport(null, output); context.DrawQuad(sampled);
                var value = new Vector4(expected[layer * 3 + mip], expected[layer * 3 + mip], 1, 1);
                if (output.GetData<Vector4>(commands).Any(pixel => (pixel - value).Length() > .0001f))
                    throw new Exception($"{format}: copied depth is incorrect at layer {layer}, mip {mip}.");
            }
            if (format == PixelFormat.D24_UNorm_S8_UInt)
            {
                CheckStencil(device, commands, context, source, white, Enumerable.Repeat((byte)101, 6).ToArray());
                CheckStencil(device, commands, context, destination, white, stencil);
            }
            Console.WriteLine($"PASS: {format} full depth copy, complete-subresource region copy and same-texture disjoint-layer copy preserve array/mip depth values and ordering.");
        }
    }

    private static void CheckStencil(GraphicsDevice device, CommandList commands, GraphicsContext context, Texture texture, Texture white, byte[] stencil)
    {
        using var effect = new Effect(device, CompilerValidation.Compile("WebGpuValidation"));
        using var instance = new EffectInstance(effect);
        instance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        instance.Parameters.Set(TexturingKeys.Texture0, white); instance.Parameters.Set(TexturingKeys.Sampler, device.SamplerStates.PointClamp);
        instance.UpdateEffect(device);
        using var output = Texture.New2D(device, 8, 8, PixelFormat.R32G32B32A32_Float, TextureFlags.RenderTarget);
        var state = new MutablePipelineState(device);
        state.State.RootSignature = instance.RootSignature; state.State.EffectBytecode = effect.Bytecode;
        state.State.InputElements = PrimitiveQuad.VertexDeclaration.CreateInputElements(); state.State.PrimitiveType = PrimitiveType.TriangleList;
        state.State.DepthStencilState.DepthBufferEnable = false; state.State.DepthStencilState.DepthBufferWriteEnable = false;
        state.State.DepthStencilState.StencilEnable = true; state.State.DepthStencilState.StencilWriteMask = 0;
        state.State.DepthStencilState.FrontFace.StencilFunction = CompareFunction.Equal;
        state.State.DepthStencilState.BackFace.StencilFunction = CompareFunction.Equal;
        for (var layer = 0; layer < 2; layer++)
        {
            using var view = View(texture, layer, 0);
            commands.SetRenderTargetAndViewport(view, output); state.State.Output.CaptureState(commands); state.Update();
            commands.SetPipelineState(state.CurrentState); instance.Apply(context);
            foreach (var matches in new[] { true, false })
            {
                commands.Clear(output, new Color4(0, 0, 0, 0));
                commands.SetStencilReference(stencil[layer * 3] + (matches ? 0 : 1)); commands.DrawQuad();
                var expected = matches ? new Vector4(.25f, .5f, .75f, 1) : Vector4.Zero;
                var actual = output.GetData<Vector4>(commands);
                if (actual.Any(pixel => pixel != expected))
                {
                    throw new Exception($"D24S8 layer {layer}, ref {stencil[layer * 3] + (matches ? 0 : 1)}, matches={matches}: center {actual[actual.Length / 2]}, expected {expected}.");
                }
            }
        }
        Console.WriteLine("PASS: D24S8 stencil values accept the matching reference and reject a different reference with exact draw pixels.");
    }

    private static Texture View(Texture texture, int layer, int mip) => texture.ToTextureView(new TextureViewDescription { Type = ViewType.Single, ArraySlice = layer, MipLevel = mip });
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
