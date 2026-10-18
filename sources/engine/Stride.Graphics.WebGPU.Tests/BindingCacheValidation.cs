// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System.Reflection;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

internal static class BindingCacheValidation
{
    internal static void Run(GraphicsDevice device)
    {
        using var commands = CommandList.New(device);
        using var effect = new Effect(device, CompilerValidation.Compile("WebGpuValidation"));
        using var instance = new EffectInstance(effect);
        using var white = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue });
        using var red = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new uint[] { 0xff0000ff });
        using var first = Texture.New2D(device, 8, 8, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        using var second = Texture.New2D(device, 8, 8, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        using var third = Texture.New2D(device, 8, 8, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
        instance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        instance.Parameters.Set(TexturingKeys.Texture0, white);
        instance.Parameters.Set(TexturingKeys.Sampler, device.SamplerStates.PointClamp);
        commands.SetRenderTargetAndViewport(null, first);
        new GraphicsContext(device, commandList: commands).DrawQuad(instance);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var count = typeof(CommandList).GetField("bindingCacheCount", flags)!;
        int Count() => (int)count.GetValue(commands)!;
        if (Count() != 1) throw new Exception("First draw did not cache its bind group.");

        // Mutate the same CPU descriptor set without reapplying the effect or
        // uploading another constant-buffer slice. Only the texture changes.
        var sets = (DescriptorSet[])typeof(CommandList).GetField("boundSets", flags)!.GetValue(commands)!;
        var textureSet = default(DescriptorSet);
        var textureSlot = -1;
        foreach (var set in sets)
        {
            var heap = (Array?)typeof(DescriptorSet).GetField("HeapObjects", flags)!.GetValue(set);
            if (heap == null) continue;
            var start = (int)typeof(DescriptorSet).GetField("DescriptorStartOffset", flags)!.GetValue(set)!;
            for (var i = start; i < heap.Length; i++)
            {
                var entry = heap.GetValue(i)!;
                if (!ReferenceEquals(entry.GetType().GetField("Value")!.GetValue(entry), white)) continue;
                textureSet = set; textureSlot = i - start; break;
            }
            if (textureSlot >= 0) break;
        }
        if (textureSlot < 0) throw new Exception("Texture descriptor was not found.");
        textureSet.SetShaderResourceView(textureSlot, red);
        commands.SetRenderTargetAndViewport(null, second); commands.DrawQuad();
        if (Count() != 2) throw new Exception("Descriptor mutation reused stale bindings.");
        textureSet.SetShaderResourceView(textureSlot, white);
        commands.SetRenderTargetAndViewport(null, third); commands.DrawQuad();
        if (Count() != 2) throw new Exception("Restored descriptor content did not reuse its cached group.");
        red.Dispose();
        textureSet.SetShaderResourceView(textureSlot, red);
        try { commands.DrawQuad(); throw new Exception("Cached disposed texture was accepted."); }
        catch (ObjectDisposedException) { }
        textureSet.SetShaderResourceView(textureSlot, white);
        if (first.GetData<uint>(commands).Any(p => p != 0xffbf8040)
            || second.GetData<uint>(commands).Any(p => p != 0xff000040)
            || third.GetData<uint>(commands).Any(p => p != 0xffbf8040))
            throw new Exception("Cached A/B/A bindings changed recorded pixels or resource lifetime.");
        if (Count() != 0) throw new Exception("Closed recording retained its binding cache.");
        Console.WriteLine("PASS: Bind-group cache preserves mutable descriptor A/B/A pixels, reuses matching content, rejects disposed resources and releases recording references.");
        ValidateRangesAndEviction(device);
    }

    private static void ValidateRangesAndEviction(GraphicsDevice device)
    {
        using var commands = CommandList.New(device);
        using var effect = new Effect(device, CompilerValidation.Compile("WebGpuBindingRanges"));
        using var instance = new EffectInstance(effect);
        const int ranges = 513;
        var elements = new uint[ranges * 64];
        for (var i = 0; i < ranges; i++) elements[i * 64] = (uint)i;
        using var input = Buffer.Structured.New(device, elements);
        using var output = Buffer.Structured.New<uint>(device, ranges, unorderedAccess: true);
        instance.Parameters.Set(ParameterKeys.NewObject<Buffer>(name: "WebGpuBindingRanges.Input"), input);
        instance.Parameters.Set(ParameterKeys.NewObject<Buffer>(name: "WebGpuBindingRanges.Values"), output);
        instance.UpdateEffect(device);
        var state = new MutablePipelineState(device);
        state.State.RootSignature = instance.RootSignature; state.State.EffectBytecode = effect.Bytecode;
        state.Update(); commands.SetPipelineState(state.CurrentState);
        instance.Apply(new GraphicsContext(device, commandList: commands));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var countField = typeof(CommandList).GetField("bindingCacheCount", flags)!;
        var sets = (DescriptorSet[])typeof(CommandList).GetField("boundSets", flags)!.GetValue(commands)!;
        DescriptorSet inputSet = default;
        var inputSlot = -1;
        foreach (var set in sets)
        {
            var heap = (Array?)typeof(DescriptorSet).GetField("HeapObjects", flags)!.GetValue(set);
            if (heap == null) continue;
            var start = (int)typeof(DescriptorSet).GetField("DescriptorStartOffset", flags)!.GetValue(set)!;
            for (var i = start; i < heap.Length; i++)
            {
                var entry = heap.GetValue(i)!;
                if (!ReferenceEquals(entry.GetType().GetField("Value")!.GetValue(entry), input)) continue;
                inputSet = set; inputSlot = i - start; break;
            }
            if (inputSlot >= 0) break;
        }
        if (inputSlot < 0) throw new Exception("Compute input descriptor was not found.");
        // Descriptor entries carry the same buffer/offset/size triple for storage
        // and uniform resources. Exercise range changes without replacing buffers.
        for (var i = 0; i < ranges; i++)
        {
            inputSet.SetConstantBuffer(inputSlot, input, i * 256, 16);
            commands.Dispatch(1, 1, 1);
            if ((int)countField.GetValue(commands)! != i % 512 + 1)
                throw new Exception($"Cache did not retain/evict the expected number of compute ranges at {i}.");
        }
        inputSet.SetConstantBuffer(inputSlot, input, 0, 16); commands.Dispatch(1, 1, 1);
        if ((int)countField.GetValue(commands)! != 2) throw new Exception("Evicted group was not recreated.");
        inputSet.SetConstantBuffer(inputSlot, input, 0, 32); commands.Dispatch(1, 1, 1);
        if ((int)countField.GetValue(commands)! != 3) throw new Exception("Changed storage size reused a stale group.");
        inputSet.SetConstantBuffer(inputSlot, input, 512 * 256, 16); commands.Dispatch(1, 1, 1);
        if ((int)countField.GetValue(commands)! != 3) throw new Exception("Compute cache did not reuse a matching range.");
        var values = output.GetData<uint>(commands);
        if (values[0] != 8 || values.Skip(1).Any(value => value != 4))
            throw new Exception("Storage range offsets/sizes or eviction lost recorded compute results.");
        Console.WriteLine("PASS: 513 compute binding ranges survive eviction; buffer offsets, sizes, GetDimensions and reuse preserve exact GPU values.");
    }
}
