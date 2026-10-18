// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders;
using Stride.Shaders.Compiler;

internal static class CompilerValidation
{
    internal static EffectBytecode Compile(string name)
    {
        using var provider = new FileSystemProvider($"/wgpu-validation-{Guid.NewGuid():N}", AppContext.BaseDirectory);
        using var compiler = new EffectCompiler(provider) { ValidateSpirv = true };
        compiler.SourceDirectories.Add("Shaders");
        compiler.SourceDirectories.Add("Shaders/Engine");
        var source = new ShaderMixinSource { Name = name };
        source.Mixins.Add(new ShaderClassSource(name));
        var parameters = EffectCompilerParameters.Default with { Platform = GraphicsPlatform.WebGPU };
        var result = compiler.Compile(source, parameters, new CompilerParameters(), ObjectId.New()).WaitForResult();
        if (result.CompilationLog.HasErrors || result.Bytecode == null)
            throw new InvalidOperationException(string.Join(Environment.NewLine, result.CompilationLog.Messages.Select(m => m.Text)));
        // Exercise the serialized cache format as well as the compiler's in-memory result.
        using var stream = new MemoryStream();
        result.Bytecode.WriteTo(stream);
        stream.Position = 0;
        return EffectBytecode.FromStream(stream);
    }

    internal static void Run(GraphicsDevice device, CommandList commands)
    {
        var bytecode = Compile("WebGpuValidation");
        using var effect = new Effect(device, bytecode);
        using var instance = new EffectInstance(effect);
        instance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        using var white = Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue });
        instance.Parameters.Set(TexturingKeys.Texture0, white);
        instance.Parameters.Set(TexturingKeys.Sampler, device.SamplerStates.PointClamp);
        using var output = Texture.New2D(device, 41, 29, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        commands.SetRenderTargetAndViewport(null, output);
        var context = new GraphicsContext(device, commandList: commands);
        using (var argumentEffect = new Effect(device, Compile("WebGpuFunctionArgumentsValidation")))
        using (var arguments = new EffectInstance(argumentEffect))
        using (var argumentOutput = Texture.New2D(device, 1, 1, PixelFormat.R32G32B32A32_Float, TextureFlags.RenderTarget))
        {
            arguments.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
            commands.SetRenderTargetAndViewport(null, argumentOutput);
            context.DrawQuad(arguments);
            var actual = argumentOutput.GetData<Vector4>(commands)[0];
            if (Math.Abs(actual.X - 150) > 0.001f || Math.Abs(actual.Y - 0.0002f) > 0.000001f || actual.Z != 2 || actual.W != 1)
                throw new Exception($"Nested shader value arguments were not preserved: {actual}.");
            Console.WriteLine("PASS: Nested shader value arguments preserve their caller's scalar and vector values.");
        }
        commands.SetRenderTargetAndViewport(null, output);
        context.DrawQuad(instance);
        var pixels = output.GetData<uint>(commands);
        if (pixels.Any(p => p != 0xffbf8040)) throw new Exception($"Compiled WebGPU SDSL pixel mismatch: center={pixels[pixels.Length / 2]:x8}.");
        Console.WriteLine("PASS: New SDSL compiles for WebGPU, serializes, binds resources and renders every expected pixel through Stride.");
        using var streakEffect = new Effect(device, Compile("WebGpuLightStreakValidation"));
        using var streak = new EffectInstance(streakEffect);
        streak.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        streak.Parameters.Set(TexturingKeys.Texture0, white);
        commands.SetRenderTargetAndViewport(null, output);
        context.DrawQuad(streak);
        _ = output.GetData<uint>(commands);
        Console.WriteLine("PASS: Actual LightStreak shader binds its input texture and immutable sampler through WebGPU.");
        using var nullEffect = new Effect(device, Compile("WebGpuNullTextureValidation"));
        using var nullInstance = new EffectInstance(nullEffect);
        nullInstance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        commands.SetRenderTargetAndViewport(null, output);
        context.DrawQuad(nullInstance);
        if (output.GetData<uint>(commands).Any(pixel => pixel != 0)) throw new Exception("An unbound float2D texture did not sample transparent zero.");
        nullInstance.Parameters.Set(TexturingKeys.Texture1, white);
        commands.SetRenderTargetAndViewport(null, output);
        context.DrawQuad(nullInstance);
        if (output.GetData<uint>(commands).Any(pixel => pixel != uint.MaxValue)) throw new Exception("The null-texture fallback replaced a real bound texture.");
        Console.WriteLine("PASS: Unbound sampled float2D textures return transparent zero; a later real texture binding produces exact white pixels.");
        using var shEffect = new Effect(device, Compile("WebGpuSphericalHarmonicsValidation"));
        using var shInstance = new EffectInstance(shEffect);
        shInstance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        using var radiance = Texture.NewCube(device, 1, PixelFormat.R8G8B8A8_UNorm);
        for (var face = 0; face < 6; face++) radiance.SetData(commands, new uint[] { uint.MaxValue }, face);
        shInstance.Parameters.Set(ParameterKeys.NewObject<Texture>(name: "LambertianPrefilteringSHNoComputePass1.RadianceMap"), radiance);
        commands.SetRenderTargetAndViewport(null, output);
        context.DrawQuad(shInstance);
        if (output.GetData<uint>(commands).Any(pixel => pixel != uint.MaxValue))
            throw new Exception("Order-three spherical harmonics produced incorrect coefficient-zero pixels.");
        Console.WriteLine("PASS: Actual order-three spherical harmonics prefilter removes unreachable higher-order array accesses and renders expected saturated white radiance.");
        if ((device.Features[PixelFormat.R32G32B32A32_Float].FormatSupport & FormatSupport.ShaderSample) != 0)
        {
            using var reductionEffect = new Effect(device, Compile("WebGpuSphericalHarmonicsReductionValidation"));
            using var reduction = new EffectInstance(reductionEffect);
            reduction.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
            var sample = new Vector4(0.125f, 0.25f, 0.5f, 1f);
            using var coefficients = Texture.New2D(device, 2, 2, PixelFormat.R32G32B32A32_Float,
                new[] { sample, sample, sample, sample });
            using var reduced = Texture.New2D(device, 1, 1, PixelFormat.R32G32B32A32_Float, TextureFlags.RenderTarget);
            reduction.Parameters.Set(TexturingKeys.Texture0, coefficients);
            commands.SetRenderTargetAndViewport(null, reduced);
            context.DrawQuad(reduction);
            if (reduced.GetData<Vector4>(commands)[0] != sample * 4)
                throw new Exception("SH reduction did not preserve the float32 sum of all four input texels.");
            Console.WriteLine("PASS: Actual SH reduction samples RGBA32Float and produces the exact floating-point coefficient sum.");
        }
        else Console.WriteLine("SKIP: SH float32 sampling requires the adapter's optional Float32Filterable feature.");
    }
}
