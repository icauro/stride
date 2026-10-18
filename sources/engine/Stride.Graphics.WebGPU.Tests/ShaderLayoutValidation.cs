// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

internal static class ShaderLayoutValidation
{
    public static void Run(GraphicsDevice device, CommandList commands)
    {
        var context = new GraphicsContext(device, commandList: commands);
        using (var effect = new Effect(device, CompilerValidation.Compile("WebGpuTypedBuffers")))
        using (var instance = new EffectInstance(effect))
        using (var input = Buffer.Typed.New(device, Enumerable.Range(1, 8).Select(i => (uint)i).ToArray(), PixelFormat.R32_UInt))
        using (var vectors = Buffer.Typed.New(device, Enumerable.Range(0, 8).Select(i => new Vector4(i, 0, 0, 0)).ToArray(), PixelFormat.R32G32B32A32_Float))
        using (var output = Buffer.Typed.New(device, 8, PixelFormat.R32_UInt, unorderedAccess: true))
        {
            instance.Parameters.Set(ParameterKeys.NewObject<Buffer>(name: "WebGpuTypedBuffers.Input"), input);
            instance.Parameters.Set(ParameterKeys.NewObject<Buffer>(name: "WebGpuTypedBuffers.Vectors"), vectors);
            instance.Parameters.Set(ParameterKeys.NewObject<Buffer>(name: "WebGpuTypedBuffers.Output"), output);
            instance.UpdateEffect(device);
            var state = new MutablePipelineState(device);
            state.State.RootSignature = instance.RootSignature; state.State.EffectBytecode = effect.Bytecode;
            state.Update(); commands.SetPipelineState(state.CurrentState);
            instance.Apply(context); commands.Dispatch(1, 1, 1);
            if (!output.GetData<uint>(commands).SequenceEqual(Enumerable.Range(0, 8).Select(i => (uint)(i * 4 + 27))))
                throw new Exception("Typed buffer storage lowering changed loads, writes or GetDimensions element counts.");
        }
        using (var effect = new Effect(device, CompilerValidation.Compile("WebGpuUniformLayout")))
        using (var instance = new EffectInstance(effect))
        using (var target = Texture.New2D(device, 16, 16, PixelFormat.R32G32B32A32_Float, TextureFlags.RenderTarget))
        {
            instance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
            instance.Parameters.Set(ParameterKeys.NewValue<float>(name: "WebGpuUniformLayout.Bias"), .125f);
            instance.Parameters.Set(ParameterKeys.NewValue<Vector2>(name: "WebGpuUniformLayout.Coordinates"), new Vector2(.25f, .5f));
            instance.Parameters.Set(ParameterKeys.NewValue<Vector3>(name: "WebGpuUniformLayout.Tint"), new Vector3(1, 0, 0));
            instance.Parameters.Set(ParameterKeys.NewValue<float>(name: "WebGpuUniformLayout.AfterVector"), .75f);
            instance.Parameters.Set(ParameterKeys.NewValue<Vector2>(name: "WebGpuUniformLayout.Basis"), new Vector2[] { Vector2.Zero, Vector2.One });
            commands.SetRenderTargetAndViewport(null, target); context.DrawQuad(instance);
            var expected = new Vector4(.25f, .5f, .875f, 1);
            if (target.GetData<Vector4>(commands).Any(pixel => (pixel - expected).Length() > .00001f))
                throw new Exception("WebGPU uniform alignment or padded vector-array upload changed shader values.");
        }
        using (var effect = new Effect(device, CompilerValidation.Compile("WebGpuNullBuffer")))
        using (var instance = new EffectInstance(effect))
        using (var output = Buffer.Structured.New<uint>(device, 8, unorderedAccess: true))
        {
            instance.Parameters.Set(ParameterKeys.NewObject<Buffer>(name: "WebGpuNullBuffer.Output"), output);
            instance.UpdateEffect(device);
            var state = new MutablePipelineState(device);
            state.State.RootSignature = instance.RootSignature; state.State.EffectBytecode = effect.Bytecode;
            state.Update(); commands.SetPipelineState(state.CurrentState);
            instance.Apply(context); commands.Dispatch(1, 1, 1);
            if (output.GetData<uint>(commands).Any(value => value != 5)) throw new Exception("Unbound read-only storage buffer did not read as zero.");
        }
        Console.WriteLine("PASS: Typed Buffer/RWBuffer loads, writes and GetDimensions; strict uniform scalar/vector packing and padded arrays produce exact GPU results.");
        Console.WriteLine("PASS: Unbound read-only storage buffers read zero, including indices beyond the fallback binding.");
    }
}
