// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;
using Stride.Core.Mathematics;
using Stride.Rendering;
using Stride.Shaders;

internal static class WgslValidation
{
    internal static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "WebGpuValidation", "WebGpuCompute" })
        {
            using var output = File.Create(Path.Combine(directory, name + ".effect"));
            CompilerValidation.Compile(name).WriteTo(output);
        }
    }

    internal static void Run(GraphicsDevice device, string directory)
    {
        using var commands = CommandList.New(device);
        using var texturedEffect = new Effect(device, Load("WebGpuValidation"));
        using var textured = new EffectInstance(texturedEffect);
        textured.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        using var white = Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue });
        textured.Parameters.Set(TexturingKeys.Texture0, white);
        textured.Parameters.Set(TexturingKeys.Sampler, device.SamplerStates.PointClamp);
        using var target = Texture.New2D(device, 19, 11, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        commands.SetRenderTargetAndViewport(null, target);
        var graphics = new GraphicsContext(device, commandList: commands);
        graphics.DrawQuad(textured);
        var pixels = target.GetData<uint>(commands);
        if (pixels.Any(pixel => pixel != 0xffbf8040))
            throw new Exception("Offline WGSL textured shader did not preserve exact pixels: " + string.Join(",", pixels.GroupBy(pixel => pixel).Select(group => $"{group.Key:x8}:{group.Count()}")));
        using var pattern = Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm, new uint[] { 0xffffffff, 0xff0000ff, 0xff00ff00, 0xffff0000 });
        textured.Parameters.Set(TexturingKeys.Texture0, pattern);
        using var corners = Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        commands.SetRenderTargetAndViewport(null, corners);
        graphics.DrawQuad(textured);
        if (!corners.GetData<uint>(commands).SequenceEqual(new uint[] { 0xffbf8040, 0xff000040, 0xff008000, 0xffbf0000 }))
            throw new Exception("Offline WGSL changed texture orientation or winding.");
        using var computeEffect = new Effect(device, Load("WebGpuCompute"));
        using var compute = new EffectInstance(computeEffect);
        using var values = Stride.Graphics.Buffer.Structured.New(device, 64, sizeof(uint), unorderedAccess: true);
        compute.Parameters.Set(ParameterKeys.NewObject<Stride.Graphics.Buffer>(name: "WebGpuCompute.Values"), values);
        compute.UpdateEffect(device);
        var pipeline = new MutablePipelineState(device);
        pipeline.State.RootSignature = compute.RootSignature;
        pipeline.State.EffectBytecode = computeEffect.Bytecode;
        pipeline.Update();
        commands.SetPipelineState(pipeline.CurrentState!);
        compute.Apply(graphics);
        commands.Dispatch(8, 1, 1);
        if (!values.GetData<uint>(commands).SequenceEqual(Enumerable.Range(0, 64).Select(index => (uint)(index * 3 + 7))))
            throw new Exception("Offline WGSL compute output did not preserve exact buffer values.");
        Console.WriteLine($"PASS: offline SDSL→SPIR-V→WGSL texture pixels and compute buffer values on {device.RendererName}.");

        EffectBytecode Load(string name)
        {
            using var input = File.OpenRead(Path.Combine(directory, name + ".effect"));
            var effect = EffectBytecode.FromStream(input);
            if (effect.Stages.Any(stage => !WebGpuShaderPackage.IsPackage(stage.Data)))
                throw new Exception("WGSL fixture contains unpackaged shader stages.");
            return effect;
        }
    }
}
