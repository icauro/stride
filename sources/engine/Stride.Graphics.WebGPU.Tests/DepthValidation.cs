// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders.Spirv.Tools;
using System.Reflection;

internal static class DepthValidation
{
    private delegate byte[] PrepareShader(ReadOnlySpan<byte> source, ISet<uint> bindings, IReadOnlyDictionary<uint, PixelFormat>? storageFormats);

    public static void Run(GraphicsDevice device, CommandList commands)
    {
        using var output = Texture.New2D(device, 16, 16, PixelFormat.R32G32B32A32_Float, TextureFlags.RenderTarget);
        var samplerDescription = new SamplerStateDescription(TextureFilter.ComparisonPoint, TextureAddressMode.Clamp) { CompareFunction = CompareFunction.LessEqual };
        using var comparison = SamplerState.New(device, in samplerDescription);
        using var white = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue });
        var context = new GraphicsContext(device, commandList: commands);
        foreach (var format in new[] { PixelFormat.D32_Float, PixelFormat.D16_UNorm, PixelFormat.D24_UNorm_S8_UInt })
        {
            using var depth = Texture.New2D(device, 16, 16, format, TextureFlags.DepthStencil | TextureFlags.ShaderResource);
            commands.Clear(depth, DepthStencilClearOptions.DepthBuffer, .25f);
            foreach (var compare in new[] { false, true })
            {
                var name = compare ? "WebGpuDepthComparison" : "WebGpuDepthSampling";
                var bytecode = CompilerValidation.Compile(name);
                var binding = bytecode.Reflection.ResourceGroups.SelectMany(group => group.Entries).Single(entry => entry.KeyInfo.KeyName == name + ".DepthMap").SlotStart;
                var prepare = typeof(GraphicsDevice).Assembly.GetType("Stride.Graphics.WebGpuShaderBytecode")!.GetMethod("Prepare", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<PrepareShader>();
                foreach (var stage in bytecode.Stages)
                {
                    var validation = Spv.ValidateBinary(prepare(stage.Data, new HashSet<uint> { (uint)binding }, null), targetVulkan: true);
                    if (!validation.IsValid) throw new Exception("Depth specialization produced invalid SPIR-V: " + validation.Output);
                }
                using var effect = new Effect(device, bytecode);
                using var instance = new EffectInstance(effect);
                instance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
                instance.Parameters.Set(ParameterKeys.NewObject<Texture>(name: name + ".DepthMap"), depth);
                if (!compare) instance.Parameters.Set(ParameterKeys.NewObject<Texture>(name: name + ".ColorMap"), white);
                instance.Parameters.Set(ParameterKeys.NewObject<SamplerState>(name: name + ".DepthSampler"), compare ? comparison : device.SamplerStates.PointClamp);
                commands.SetRenderTargetAndViewport(null, output);
                context.DrawQuad(instance);
                var expected = compare ? new Vector4(1, 0, 0, 1) : new Vector4(.25f, .25f, 1, 1);
                foreach (var pixel in output.GetData<Vector4>(commands))
                    if ((pixel - expected).Length() > .0001f) throw new Exception($"{format} {name}: {pixel}, expected {expected}.");
                if (!compare)
                {
                    using var readOnly = depth.ToDepthStencilReadOnlyTexture();
                    try { commands.Clear(readOnly, DepthStencilClearOptions.DepthBuffer); throw new Exception("Read-only depth clear was accepted."); }
                    catch (ArgumentException) { }
                    instance.UpdateEffect(device);
                    var pipeline = new MutablePipelineState(device);
                    pipeline.State.RootSignature = instance.RootSignature;
                    pipeline.State.EffectBytecode = effect.Bytecode;
                    pipeline.State.InputElements = PrimitiveQuad.VertexDeclaration.CreateInputElements();
                    pipeline.State.PrimitiveType = PrimitiveType.TriangleList;
                    pipeline.State.DepthStencilState.DepthBufferEnable = true;
                    pipeline.State.DepthStencilState.DepthBufferWriteEnable = true;
                    pipeline.State.DepthStencilState.DepthBufferFunction = CompareFunction.Always;
                    var access = typeof(CommandList).GetProperty("DepthStencilAccess", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    foreach (var attachment in new[] { readOnly, depth })
                    {
                        commands.SetRenderTargetAndViewport(attachment, output);
                        if (ReferenceEquals(attachment, depth)) access.SetValue(commands, Enum.Parse(access.PropertyType, "Read"));
                        pipeline.State.Output.CaptureState(commands);
                        pipeline.Update(); commands.SetPipelineState(pipeline.CurrentState);
                        instance.Apply(context);
                        // A second draw observes any depth write accidentally made by the first.
                        commands.DrawQuad(); commands.DrawQuad();
                        foreach (var pixel in output.GetData<Vector4>(commands))
                            if ((pixel - expected).Length() > .0001f) throw new Exception($"{format}: read-only depth view/access override did not suppress pipeline writes.");
                    }
                }
            }
            Console.WriteLine($"PASS: {format} ordinary sampling/load alongside a color texture, comparison sampling, and simultaneous read-only depth attachment.");
        }
        Console.WriteLine("PASS: D16/D24S8/D32 depth sampling, load and comparison produce expected values through compiled SDSL.");
    }
}
