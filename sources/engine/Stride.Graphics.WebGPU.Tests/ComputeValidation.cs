// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;
using Stride.Core.Mathematics;

internal static class ComputeValidation
{
    internal static void Run(GraphicsDevice device, CommandList commands)
    {
        var bytecode = CompilerValidation.Compile("WebGpuCompute");
        using var effect = new Effect(device, bytecode);
        using var instance = new EffectInstance(effect);
        using var values = Buffer.Structured.New(device, 64, sizeof(uint), unorderedAccess: true);
        instance.Parameters.Set(ParameterKeys.NewObject<Buffer>(name: "WebGpuCompute.Values"), values);
        instance.UpdateEffect(device);
        var state = new MutablePipelineState(device);
        state.State.RootSignature = instance.RootSignature;
        state.State.EffectBytecode = bytecode;
        state.Update();
        commands.SetPipelineState(state.CurrentState!);
        var context = new GraphicsContext(device, commandList: commands);
        instance.Apply(context);
        commands.Dispatch(8, 1, 1);
        var expected = Enumerable.Range(0, 64).Select(i => (uint)(i * 3 + 7)).ToArray();
        if (!values.GetData<uint>(commands).SequenceEqual(expected)) throw new Exception("Compiled SDSL compute dispatch returned incorrect buffer values.");
        values.SetData(commands, new uint[64]);
        using var indirect = Buffer.New(device, new uint[] { 8, 1, 1 }, BufferFlags.ArgumentBuffer, GraphicsResourceUsage.Default);
        commands.SetPipelineState(state.CurrentState!);
        instance.Apply(context);
        commands.Dispatch(indirect, 0);
        if (!values.GetData<uint>(commands).SequenceEqual(expected)) throw new Exception("Indirect compute dispatch returned incorrect buffer values.");
        Console.WriteLine("PASS: New SDSL compute shader, writable structured-buffer binding, direct and indirect dispatch with exact GPU results.");
        ValidateStructuredLayout(device, commands, context);
    }

    private static void ValidateStructuredLayout(GraphicsDevice device, CommandList commands, GraphicsContext context)
    {
        var bytecode = CompilerValidation.Compile("WebGpuStructuredLayout");
        using var effect = new Effect(device, bytecode);
        using var instance = new EffectInstance(effect);
        using var output = Buffer.Structured.New<Vector4>(device, 8, unorderedAccess: true);
        var inputKey = ParameterKeys.NewObject<Buffer>(name: "WebGpuStructuredLayout.Input");
        instance.Parameters.Set(ParameterKeys.NewObject<Buffer>(name: "WebGpuStructuredLayout.Output"), output);
        var inputs = Enumerable.Range(0, 8).Select(i => new Vector4(i + 1, i + 2, i + 3, 999)).ToArray();
        using var paddedInput = Buffer.Structured.New(device, inputs);
        instance.Parameters.Set(inputKey, paddedInput);
        instance.UpdateEffect(device);
        var state = new MutablePipelineState(device);
        state.State.RootSignature = instance.RootSignature;
        state.State.EffectBytecode = bytecode;
        state.Update();
        commands.SetPipelineState(state.CurrentState!);
        instance.Apply(context);
        commands.Dispatch(1, 1, 1);
        var expected = inputs.Select(v => new Vector4(v.X, v.Y, v.Z, 1));
        if (!output.GetData<Vector4>(commands).SequenceEqual(expected)) throw new Exception("Padded float3 structured buffer read incorrect values.");
        using var tightInput = Buffer.Structured.New(device, inputs.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray());
        instance.Parameters.Set(inputKey, tightInput);
        instance.Apply(context);
        try { commands.Dispatch(1, 1, 1); throw new Exception("Incompatible structured buffer stride was accepted."); }
        catch (NotSupportedException ex) when (ex.Message.Contains("requires a structured buffer stride of 16")) { }
        Console.WriteLine("PASS: Padded float3 storage data reads correctly; incompatible 12-byte CPU stride is rejected before dispatch.");
    }
}
