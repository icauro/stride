// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_WEBGPU

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Shaders;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

public unsafe partial class PipelineState
{
    internal WGPURenderPipeline NativePipeline;
    internal WGPUComputePipeline NativeComputePipeline;
    internal bool IsCompute => NativeComputePipeline.Handle != 0;
    internal WGPUBindGroupLayout NativeBindGroupLayout;
    internal readonly List<WebGpuBinding> Bindings = new();
    internal int[] VertexStrides = Array.Empty<int>();
    internal bool ScissorEnabled;
    private readonly PipelineStateDescription webGpuDescription;
    private readonly ISet<uint> webGpuDepthBindings;
    private readonly IReadOnlyDictionary<uint, PixelFormat> webGpuStorageFormats;
    private readonly Dictionary<string, PipelineState> depthVariants = new();

    internal readonly record struct WebGpuBinding(int SourceSet, int SourceSlot, uint Binding, SamplerState ImmutableSampler, uint StructureStride, bool ReadOnlyStorage, string Name, bool NullableFloat2D, bool IsSampler);

    private PipelineState(GraphicsDevice device, PipelineStateDescription description, ISet<uint> depthBindings = null, IReadOnlyDictionary<uint, PixelFormat> storageFormats = null) : base(device)
    {
        webGpuDescription = description.Clone();
        webGpuDepthBindings = depthBindings;
        webGpuStorageFormats = storageFormats;
        // The device's default state has no shader/root signature.
        if (description.RootSignature == null) return;
        var effect = description.EffectBytecode ?? throw new ArgumentException("A shader effect is required.", nameof(description));
        if (effect.Stages.Length == 1 && effect.Stages[0].Stage == ShaderStage.Compute)
        {
            CreateComputePipeline(description);
            return;
        }
        if (effect.Stages.Any(s => s.Stage != ShaderStage.Vertex && s.Stage != ShaderStage.Pixel))
            throw new NotSupportedException("This WebGPU pipeline currently supports vertex and pixel shaders.");
        var vertexStage = effect.Stages.Single(s => s.Stage == ShaderStage.Vertex);
        var fragmentStage = effect.Stages.SingleOrDefault(s => s.Stage == ShaderStage.Pixel);
        var modules = new List<WGPUShaderModule>();
        try
        {
            var vertexModule = CreateModule(vertexStage.Data); modules.Add(vertexModule);
            var fragmentModule = fragmentStage == null ? default : vertexStage.Data.AsSpan().SequenceEqual(fragmentStage.Data) ? vertexModule : CreateModule(fragmentStage.Data);
            if (fragmentModule.Handle != 0 && fragmentModule != vertexModule) modules.Add(fragmentModule);
            var inputs = description.InputElements ?? Array.Empty<InputElementDescription>();
            if (inputs.Length > device.WebGpuContext.Limits.maxVertexAttributes || inputs.Any(e => e.InputSlot < 0))
                throw new NotSupportedException("This WebGPU vertex input layout exceeds device limits or uses a negative slot.");
            var slotCount = inputs.Length == 0 ? 0 : inputs.Max(e => e.InputSlot) + 1;
            if (slotCount > device.WebGpuContext.Limits.maxVertexBuffers) throw new NotSupportedException("Too many WebGPU vertex buffers.");
            VertexStrides = new int[slotCount];
            var layouts = stackalloc WGPUVertexBufferLayout[slotCount];
            var attributes = stackalloc WGPUVertexAttribute[inputs.Length];
            var attributeIndex = 0;
            for (var slot = 0; slot < slotCount; slot++)
            {
                layouts[slot] = WGPUVertexBufferLayout.CreateDefault();
                layouts[slot].stepMode = WGPUVertexStepMode.Vertex;
                layouts[slot].attributes = attributes + attributeIndex;
                foreach (var input in inputs.Where(e => e.InputSlot == slot))
                {
                    if (input.InstanceDataStepRate > 1 || input.AlignedByteOffset < 0) throw new NotSupportedException("This WebGPU input layout requires explicit offsets and instance step rates of one.");
                    layouts[slot].stepMode = input.InputSlotClass == InputClassification.Vertex ? WGPUVertexStepMode.Vertex : WGPUVertexStepMode.Instance;
                    VertexStrides[slot] = Math.Max(VertexStrides[slot], input.AlignedByteOffset + input.Format.SizeInBytes);
                    var reflected = effect.Reflection.InputAttributes.FirstOrDefault(a => a.SemanticName == input.SemanticName && a.SemanticIndex == input.SemanticIndex);
                    if (reflected.SemanticName == null) continue;
                    attributes[attributeIndex++] = new WGPUVertexAttribute { format = VertexFormat(input.Format), offset = (ulong)input.AlignedByteOffset, shaderLocation = (uint)reflected.Location };
                    layouts[slot].attributeCount++;
                }
                layouts[slot].arrayStride = (ulong)VertexStrides[slot];
            }

            var targets = stackalloc WGPUColorTargetState[description.Output.RenderTargetCount];
            var blends = stackalloc WGPUBlendState[description.Output.RenderTargetCount];
            for (var i = 0; i < description.Output.RenderTargetCount; i++)
            {
                var blend = description.BlendState.RenderTargets[description.BlendState.IndependentBlendEnable ? i : 0];
                targets[i] = WGPUColorTargetState.CreateDefault();
                targets[i].format = WebGpuConvert.TextureFormat(description.Output.RenderTargetFormats[i]);
                targets[i].writeMask = (WGPUColorWriteMask)(uint)blend.ColorWriteChannels;
                if (blend.BlendEnable)
                {
                    blends[i] = new WGPUBlendState
                    {
                        color = new WGPUBlendComponent { operation = (WGPUBlendOperation)blend.ColorBlendFunction, srcFactor = BlendFactor(blend.ColorSourceBlend), dstFactor = BlendFactor(blend.ColorDestinationBlend) },
                        alpha = new WGPUBlendComponent { operation = (WGPUBlendOperation)blend.AlphaBlendFunction, srcFactor = BlendFactor(blend.AlphaSourceBlend), dstFactor = BlendFactor(blend.AlphaDestinationBlend) }
                    };
                    targets[i].blend = blends + i;
                }
            }
            var rasterizer = description.RasterizerState;
            if (rasterizer.FillMode != FillMode.Solid)
                throw new NotSupportedException($"WebGPU fill mode {rasterizer.FillMode} is not supported.");
            // Match Stride's Vulkan rasterizer: the editor's line-antialias hint
            // does not select a separate line mode. Actual multisampling follows
            // the output attachment's sample count below.
            if (!rasterizer.DepthClipEnable && !device.WebGpuContext.HasDepthClipControl)
                throw new NotSupportedException("Disabling depth clipping requires the adapter's optional WebGPU DepthClipControl feature.");
            ScissorEnabled = rasterizer.ScissorTestEnable;
            var depth = WGPUDepthStencilState.CreateDefault();
            var depthState = description.DepthStencilState;
            if (description.Output.DepthStencilFormat != PixelFormat.None)
            {
                depth.format = WebGpuConvert.TextureFormat(description.Output.DepthStencilFormat);
                depth.depthWriteEnabled = depthState.DepthBufferEnable && depthState.DepthBufferWriteEnable ? WGPUOptionalBool.True : WGPUOptionalBool.False;
                depth.depthCompare = depthState.DepthBufferEnable ? (WGPUCompareFunction)depthState.DepthBufferFunction : WGPUCompareFunction.Always;
                if (depthState.StencilEnable)
                {
                    depth.stencilFront = StencilFace(depthState.FrontFace);
                    depth.stencilBack = StencilFace(depthState.BackFace);
                    depth.stencilReadMask = depthState.StencilMask;
                    depth.stencilWriteMask = depthState.StencilWriteMask;
                }
                depth.depthBias = rasterizer.DepthBias;
                depth.depthBiasClamp = rasterizer.DepthBiasClamp;
                depth.depthBiasSlopeScale = rasterizer.SlopeScaleDepthBias;
            }
            var descriptor = WGPURenderPipelineDescriptor.CreateDefault();
            descriptor.vertex.module = vertexModule;
            descriptor.vertex.bufferCount = (nuint)slotCount;
            descriptor.vertex.buffers = layouts;
            if (description.PrimitiveType is not (PrimitiveType.PointList or PrimitiveType.LineList or PrimitiveType.TriangleList))
                throw new NotSupportedException("This WebGPU primitive topology is not implemented.");
            descriptor.primitive.topology = (WGPUPrimitiveTopology)description.PrimitiveType;
            descriptor.primitive.frontFace = rasterizer.FrontFaceCounterClockwise ? WGPUFrontFace.CCW : WGPUFrontFace.CW;
            descriptor.primitive.cullMode = rasterizer.CullMode switch { CullMode.None => WGPUCullMode.None, CullMode.Front => WGPUCullMode.Front, CullMode.Back => WGPUCullMode.Back, _ => throw new NotSupportedException("Unknown cull mode.") };
            descriptor.primitive.unclippedDepth = rasterizer.DepthClipEnable ? 0u : 1u;
            descriptor.multisample.count = (uint)Math.Max(1, (int)description.Output.MultisampleCount);
            descriptor.multisample.mask = description.SampleMask;
            descriptor.multisample.alphaToCoverageEnabled = description.BlendState.AlphaToCoverageEnable ? 1u : 0u;
            if (description.Output.DepthStencilFormat != PixelFormat.None) descriptor.depthStencil = &depth;
            var fragment = WGPUFragmentState.CreateDefault();
            fragment.module = fragmentModule;
            fragment.targetCount = (nuint)description.Output.RenderTargetCount;
            fragment.targets = targets;
            descriptor.fragment = fragmentStage == null ? null : &fragment;
            fixed (byte* vertexEntry = vertexStage.EntryPoint)
            fixed (byte* fragmentEntry = fragmentStage?.EntryPoint)
            {
                descriptor.vertex.entryPoint = new WGPUStringView { data = vertexEntry, length = EntryLength(vertexStage.EntryPoint) };
                fragment.entryPoint = new WGPUStringView { data = fragmentEntry, length = fragmentStage == null ? 0 : EntryLength(fragmentStage.EntryPoint) };
                NativePipeline = Api.wgpuDeviceCreateRenderPipeline(device.WebGpuContext.Device, &descriptor);
            }
            device.WebGpuContext.ThrowIfFailed();
            BuildBindings(description);
            if (Bindings.Count != 0)
            {
                NativeBindGroupLayout = Api.wgpuRenderPipelineGetBindGroupLayout(NativePipeline, 0);
                device.WebGpuContext.ThrowIfFailed();
            }
        }
        catch { ReleasePipeline(); throw; }
        finally { foreach (var module in modules) Api.wgpuShaderModuleRelease(module); }
    }

    private static nuint EntryLength(byte[] entry) => (nuint)(entry.Length > 0 && entry[^1] == 0 ? entry.Length - 1 : entry.Length);

    private void CreateComputePipeline(PipelineStateDescription description)
    {
        var stage = description.EffectBytecode.Stages[0];
        var module = CreateModule(stage.Data);
        try
        {
            var descriptor = WGPUComputePipelineDescriptor.CreateDefault();
            descriptor.compute.module = module;
            fixed (byte* entry = stage.EntryPoint)
            {
                descriptor.compute.entryPoint = new WGPUStringView { data = entry, length = EntryLength(stage.EntryPoint) };
                NativeComputePipeline = Api.wgpuDeviceCreateComputePipeline(GraphicsDevice.WebGpuContext.Device, &descriptor);
            }
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            BuildBindings(description);
            if (Bindings.Count != 0)
            {
                NativeBindGroupLayout = Api.wgpuComputePipelineGetBindGroupLayout(NativeComputePipeline, 0);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
            }
        }
        catch { ReleasePipeline(); throw; }
        finally { Api.wgpuShaderModuleRelease(module); }
    }

    private WGPUShaderModule CreateModule(byte[] bytecode)
    {
        if (OperatingSystem.IsBrowser() || WebGpuShaderPackage.IsPackage(bytecode))
        {
            var wgsl = System.Text.Encoding.UTF8.GetBytes(WebGpuShaderPackage.SelectWgsl(bytecode,
                WebGpuShaderBytecode.WgslVariantKey(bytecode, webGpuDepthBindings, webGpuStorageFormats)));
            fixed (byte* code = wgsl)
            {
                var source = WGPUShaderSourceWGSL.CreateDefault();
                source.code = new WGPUStringView { data = code, length = (nuint)wgsl.Length };
                var descriptor = WGPUShaderModuleDescriptor.CreateDefault(); descriptor.nextInChain = &source.chain;
                var module = Api.wgpuDeviceCreateShaderModule(GraphicsDevice.WebGpuContext.Device, &descriptor);
                try { GraphicsDevice.WebGpuContext.ThrowIfFailed(); return module; }
                catch { if (module.Handle != 0) Api.wgpuShaderModuleRelease(module); throw; }
            }
        }
        var prepared = WebGpuShaderBytecode.Prepare(bytecode, webGpuDepthBindings, webGpuStorageFormats);
        fixed (byte* code = prepared)
        {
            var spirv = WGPUShaderSourceSPIRV.CreateDefault();
            spirv.code = (uint*)code; spirv.codeSize = (uint)(prepared.Length / 4);
            var descriptor = WGPUShaderModuleDescriptor.CreateDefault(); descriptor.nextInChain = &spirv.chain;
            var module = Api.wgpuDeviceCreateShaderModule(GraphicsDevice.WebGpuContext.Device, &descriptor);
            try { GraphicsDevice.WebGpuContext.ThrowIfFailed(); return module; }
            catch { if (module.Handle != 0) Api.wgpuShaderModuleRelease(module); throw; }
        }
    }

    internal PipelineState WithDepthBindings(ISet<uint> bindings, bool readOnlyDepth, IReadOnlyDictionary<uint, PixelFormat> storageFormats)
    {
        if (bindings.Count == 0 && !readOnlyDepth && storageFormats.Count == 0) return this;
        var key = (readOnlyDepth ? "read:" : "write:") + string.Join(",", bindings.OrderBy(binding => binding)) + ";storage:" + string.Join(",", storageFormats.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        lock (depthVariants)
        {
            if (!depthVariants.TryGetValue(key, out var variant))
            {
                var description = webGpuDescription.Clone();
                if (readOnlyDepth)
                {
                    description.DepthStencilState.DepthBufferWriteEnable = false;
                    description.DepthStencilState.StencilWriteMask = 0;
                }
                variant = new PipelineState(GraphicsDevice, description, bindings, storageFormats);
                // The parent owns this variant. Avoid independently enumerating
                // and destroying it in GraphicsDevice's resource registry.
                lock (GraphicsDevice.Resources) GraphicsDevice.Resources.Remove(variant);
                depthVariants.Add(key, variant);
            }
            return variant;
        }
    }

    private void BuildBindings(PipelineStateDescription description)
    {
        var reflection = description.RootSignature.EffectDescriptorSetReflection;
        var destinations = description.EffectBytecode.Reflection.ResourceGroups.SelectMany(g => g.Entries).ToDictionary(e => e.KeyInfo.KeyName);
        var strides = WebGpuShaderBytecode.StorageBufferStrides(description.EffectBytecode.Stages[0].Data);
        var usedBindings = new HashSet<uint>();
        var nullableTextures = new HashSet<uint>();
        foreach (var stage in description.EffectBytecode.Stages) nullableTextures.UnionWith(WebGpuShaderBytecode.NullableFloat2DTextures(stage.Data));
        foreach (var stage in description.EffectBytecode.Stages) usedBindings.UnionWith(WebGpuShaderBytecode.ResourceBindings(stage.Data));
        for (var set = 0; set < reflection.Layouts.Count; set++)
        {
            var entries = reflection.Layouts[set].Layout?.Entries;
            if (entries == null) continue;
            var slot = 0;
            foreach (var entry in entries)
            {
                if (destinations.TryGetValue(entry.Key.Name, out var destination))
                {
                    var binding = (uint)destination.SlotStart;
                    if (!usedBindings.Contains(binding)) { slot += entry.ArraySize; continue; }
                    if (entry.ArraySize != 1 || destination.SlotCount != 1) throw new NotSupportedException("WebGPU binding arrays are not implemented.");
                    strides.TryGetValue(binding, out var stride);
                    Bindings.Add(new WebGpuBinding(set, slot, binding, entry.ImmutableSampler, stride,
                        stride != 0 && destination.Class == EffectParameterClass.ShaderResourceView, entry.Key.Name,
                        destination.Class == EffectParameterClass.ShaderResourceView && nullableTextures.Contains(binding), entry.Class == EffectParameterClass.Sampler));
                }
                slot += entry.ArraySize;
            }
        }
    }

    private static WGPUVertexFormat VertexFormat(PixelFormat format) => format switch
    {
        PixelFormat.R32_Float => WGPUVertexFormat.Float32,
        PixelFormat.R32G32_Float => WGPUVertexFormat.Float32x2,
        PixelFormat.R32G32B32_Float => WGPUVertexFormat.Float32x3,
        PixelFormat.R32G32B32A32_Float => WGPUVertexFormat.Float32x4,
        PixelFormat.R32_UInt => WGPUVertexFormat.Uint32,
        PixelFormat.R32G32_UInt => WGPUVertexFormat.Uint32x2,
        PixelFormat.R32G32B32_UInt => WGPUVertexFormat.Uint32x3,
        PixelFormat.R32G32B32A32_UInt => WGPUVertexFormat.Uint32x4,
        PixelFormat.R32_SInt => WGPUVertexFormat.Sint32,
        PixelFormat.R32G32_SInt => WGPUVertexFormat.Sint32x2,
        PixelFormat.R32G32B32_SInt => WGPUVertexFormat.Sint32x3,
        PixelFormat.R32G32B32A32_SInt => WGPUVertexFormat.Sint32x4,
        PixelFormat.R8G8_UNorm => WGPUVertexFormat.Unorm8x2,
        PixelFormat.R8G8_SNorm => WGPUVertexFormat.Snorm8x2,
        PixelFormat.R8G8_UInt => WGPUVertexFormat.Uint8x2,
        PixelFormat.R8G8_SInt => WGPUVertexFormat.Sint8x2,
        PixelFormat.R16G16_UNorm => WGPUVertexFormat.Unorm16x2,
        PixelFormat.R16G16_SNorm => WGPUVertexFormat.Snorm16x2,
        PixelFormat.R16G16_UInt => WGPUVertexFormat.Uint16x2,
        PixelFormat.R16G16_SInt => WGPUVertexFormat.Sint16x2,
        PixelFormat.R16G16_Float => WGPUVertexFormat.Float16x2,
        PixelFormat.R16G16B16A16_UNorm => WGPUVertexFormat.Unorm16x4,
        PixelFormat.R16G16B16A16_SNorm => WGPUVertexFormat.Snorm16x4,
        PixelFormat.R16G16B16A16_UInt => WGPUVertexFormat.Uint16x4,
        PixelFormat.R16G16B16A16_SInt => WGPUVertexFormat.Sint16x4,
        PixelFormat.R16G16B16A16_Float => WGPUVertexFormat.Float16x4,
        PixelFormat.R8G8B8A8_UNorm => WGPUVertexFormat.Unorm8x4,
        PixelFormat.R8G8B8A8_SNorm => WGPUVertexFormat.Snorm8x4,
        PixelFormat.R8G8B8A8_UInt => WGPUVertexFormat.Uint8x4,
        PixelFormat.R8G8B8A8_SInt => WGPUVertexFormat.Sint8x4,
        _ => throw new NotSupportedException($"WebGPU vertex format {format} is not implemented.")
    };

    private static WGPUBlendFactor BlendFactor(Blend blend) => blend switch
    {
        Blend.Zero => WGPUBlendFactor.Zero, Blend.One => WGPUBlendFactor.One,
        Blend.SourceColor => WGPUBlendFactor.Src, Blend.InverseSourceColor => WGPUBlendFactor.OneMinusSrc,
        Blend.SourceAlpha => WGPUBlendFactor.SrcAlpha, Blend.InverseSourceAlpha => WGPUBlendFactor.OneMinusSrcAlpha,
        Blend.DestinationColor => WGPUBlendFactor.Dst, Blend.InverseDestinationColor => WGPUBlendFactor.OneMinusDst,
        Blend.DestinationAlpha => WGPUBlendFactor.DstAlpha, Blend.InverseDestinationAlpha => WGPUBlendFactor.OneMinusDstAlpha,
        Blend.SourceAlphaSaturate => WGPUBlendFactor.SrcAlphaSaturated,
        Blend.BlendFactor => WGPUBlendFactor.Constant, Blend.InverseBlendFactor => WGPUBlendFactor.OneMinusConstant,
        _ => throw new NotSupportedException($"WebGPU blend factor {blend} is not implemented.")
    };

    private static WGPUStencilFaceState StencilFace(DepthStencilStencilOpDescription face) => new()
    {
        compare = (WGPUCompareFunction)face.StencilFunction,
        failOp = StencilOp(face.StencilFail), depthFailOp = StencilOp(face.StencilDepthBufferFail), passOp = StencilOp(face.StencilPass)
    };

    private static WGPUStencilOperation StencilOp(StencilOperation operation) => operation switch
    {
        StencilOperation.Keep => WGPUStencilOperation.Keep, StencilOperation.Zero => WGPUStencilOperation.Zero,
        StencilOperation.Replace => WGPUStencilOperation.Replace, StencilOperation.Invert => WGPUStencilOperation.Invert,
        StencilOperation.IncrementSaturation => WGPUStencilOperation.IncrementClamp, StencilOperation.DecrementSaturation => WGPUStencilOperation.DecrementClamp,
        StencilOperation.Increment => WGPUStencilOperation.IncrementWrap, StencilOperation.Decrement => WGPUStencilOperation.DecrementWrap,
        _ => throw new NotSupportedException("Unknown WebGPU stencil operation.")
    };

    private void ReleasePipeline()
    {
        if (NativeBindGroupLayout.Handle != 0) Api.wgpuBindGroupLayoutRelease(NativeBindGroupLayout);
        if (NativePipeline.Handle != 0) Api.wgpuRenderPipelineRelease(NativePipeline);
        if (NativeComputePipeline.Handle != 0) Api.wgpuComputePipelineRelease(NativeComputePipeline);
        NativeBindGroupLayout = default; NativePipeline = default; NativeComputePipeline = default;
    }

    protected internal override void OnDestroyed(bool immediately = false)
    {
        foreach (var variant in depthVariants.Values) variant.Dispose();
        depthVariants.Clear();
        ReleasePipeline(); base.OnDestroyed(immediately);
    }
}

#endif
