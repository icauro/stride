using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Editor.ImGui;

/// <summary>Consumes Dear ImGui draw data using only Stride graphics resources and commands.</summary>
internal sealed unsafe partial class ImGuiRenderer : IDisposable
{
    // Compute the actual size: the engine's legacy Size constant predates Color4 fields.
    private static readonly int VertexStride = sizeof(VertexPositionColorTextureSwizzle);
    // Reuse Stride's embedded sprite shader, including its per-vertex color input.
    private sealed class ShaderProvider(GraphicsDevice device) : SpriteBatch(device)
    {
        private int releasedEffects;
        public EffectInstance UiEffect => graphicsDevice.ColorSpace == ColorSpace.Linear ? DefaultEffectSRgb : DefaultEffect;

        protected override void Destroy()
        {
            ReleaseEffect(1, () => DefaultEffect.Effect.Dispose());
            ReleaseEffect(2, () => DefaultEffect.Dispose());
            ReleaseEffect(4, () => DefaultEffectSRgb.Effect.Dispose());
            ReleaseEffect(8, () => DefaultEffectSRgb.Dispose());
            ReleaseEffect(16, () => TextureSpriteFontEffect.Effect.Dispose());
            ReleaseEffect(32, () => TextureSpriteFontEffect.Dispose());
            base.Destroy();
        }
        private void ReleaseEffect(int phase, Action release)
        {
            if ((releasedEffects & phase) != 0) return;
            release(); releasedEffects |= phase;
        }
    }

    private readonly GraphicsDevice device;
    private readonly ShaderProvider shader;
    private readonly MutablePipelineState pipeline;
    private Buffer? vertexBuffer;
    private Buffer? indexBuffer;
    private VertexPositionColorTextureSwizzle[] vertices = [];
    private ushort[] indices = [];
    private bool shaderReleased;
    private bool retiring, disposed;

    public ImGuiRenderer(GraphicsDevice device)
    {
        this.device = device;
        shader = new ShaderProvider(device);
        shader.UiEffect.Parameters.Set(TexturingKeys.Sampler, device.SamplerStates.LinearClamp);
        shader.UiEffect.UpdateEffect(device);
        pipeline = new MutablePipelineState(device);
        pipeline.State.SetDefaults();
        pipeline.State.InputElements = VertexPositionColorTextureSwizzle.Layout.CreateInputElements();
        pipeline.State.PrimitiveType = PrimitiveType.TriangleList;
        pipeline.State.BlendState = BlendStates.NonPremultiplied;
        pipeline.State.DepthStencilState = DepthStencilStates.None;
        pipeline.State.RasterizerState = new RasterizerStateDescription(CullMode.None) { ScissorTestEnable = true };
        pipeline.State.RootSignature = shader.UiEffect.RootSignature;
        pipeline.State.EffectBytecode = shader.UiEffect.Effect.Bytecode;
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures;
    }

    public void Render(GraphicsContext context, ImDrawDataPtr data)
    {
        ObjectDisposedException.ThrowIf(retiring, this);
        if (data.IsNull) return;
        var commandList = context.CommandList;
        if (!ReferenceEquals(commandList.GraphicsDevice, device))
            throw new InvalidOperationException("The ImGui render commands belong to another graphics device.");
        ProcessTextureRequests(commandList, data);

        var width = (int)(data.DisplaySize.X * data.FramebufferScale.X);
        var height = (int)(data.DisplaySize.Y * data.FramebufferScale.Y);
        if (width <= 0 || height <= 0 || data.TotalVtxCount == 0) return;
        EnsureBuffers(data.TotalVtxCount, data.TotalIdxCount);
        var vertexOffset = 0;
        var indexOffset = 0;
        for (var i = 0; i < data.CmdListsCount; i++)
        {
            var list = data.CmdLists[i];
            for (var v = 0; v < list.VtxBuffer.Size; v++)
            {
                var source = list.VtxBuffer[v];
                vertices[vertexOffset + v] = new VertexPositionColorTextureSwizzle(
                    new Vector4(source.Pos.X, source.Pos.Y, 0, 1), new Color(source.Col), default,
                    new Vector2(source.Uv.X, source.Uv.Y), SwizzleMode.None);
            }
            new ReadOnlySpan<ushort>(list.IdxBuffer.Data, list.IdxBuffer.Size).CopyTo(indices.AsSpan(indexOffset));
            vertexOffset += list.VtxBuffer.Size;
            indexOffset += list.IdxBuffer.Size;
        }
        if (vertexBuffer!.SizeInBytes < data.TotalVtxCount * VertexStride)
            throw new InvalidOperationException($"Vertex allocation: {vertexBuffer.SizeInBytes} bytes, stride {VertexStride}, capacity {vertices.Length}, requested {data.TotalVtxCount}.");
        vertexBuffer.SetData<VertexPositionColorTextureSwizzle>(commandList, vertices.AsSpan(0, data.TotalVtxCount));
        indexBuffer!.SetData<ushort>(commandList, indices.AsSpan(0, data.TotalIdxCount));
        shader.UiEffect.Parameters.Set(SpriteBaseKeys.MatrixTransform,
            Matrix.OrthoOffCenterLH(data.DisplayPos.X, data.DisplayPos.X + data.DisplaySize.X,
                data.DisplayPos.Y + data.DisplaySize.Y, data.DisplayPos.Y, 0, 1));
        pipeline.State.Output.CaptureState(commandList);
        pipeline.Update();
        commandList.SetViewport(new Viewport(0, 0, width, height));
        vertexOffset = indexOffset = 0;
        for (var i = 0; i < data.CmdListsCount; i++)
        {
            var list = data.CmdLists[i];
            for (var c = 0; c < list.CmdBuffer.Size; c++)
            {
                var draw = list.CmdBuffer.Data + c;
                // Dear ImGui's reset-state sentinel is -8. Every normal draw rebinds our state.
                if (draw->UserCallback != null)
                {
                    if ((nint)draw->UserCallback != -8)
                        ((delegate* unmanaged[Cdecl]<ImDrawList*, ImDrawCmd*, void>)draw->UserCallback)(list.Handle, draw);
                    continue;
                }
                if (draw->ElemCount == 0) continue;
                if ((ulong)draw->IdxOffset + draw->ElemCount > (ulong)list.IdxBuffer.Size
                    || draw->VtxOffset >= (uint)list.VtxBuffer.Size)
                    throw new InvalidOperationException("ImGui draw command exceeds its source buffers.");
                var clip = draw->ClipRect;
                var x1 = Math.Clamp((int)((clip.X - data.DisplayPos.X) * data.FramebufferScale.X), 0, width);
                var y1 = Math.Clamp((int)((clip.Y - data.DisplayPos.Y) * data.FramebufferScale.Y), 0, height);
                var x2 = Math.Clamp((int)MathF.Ceiling((clip.Z - data.DisplayPos.X) * data.FramebufferScale.X), 0, width);
                var y2 = Math.Clamp((int)MathF.Ceiling((clip.W - data.DisplayPos.Y) * data.FramebufferScale.Y), 0, height);
                if (x2 <= x1 || y2 <= y1) continue;
                var textureId = draw->GetTexID().Handle;
                if (!textures.TryGetValue(textureId, out var texture))
                    throw new InvalidOperationException($"ImGui texture {textureId} is not registered.");
                commandList.SetPipelineState(pipeline.CurrentState);
                commandList.SetViewport(new Viewport(0, 0, width, height));
                commandList.SetScissorRectangle(new Rectangle(x1, y1, x2 - x1, y2 - y1));
                commandList.SetVertexBuffer(0, vertexBuffer, 0, VertexStride);
                commandList.SetIndexBuffer(indexBuffer, 0, false);
                shader.UiEffect.Parameters.Set(TexturingKeys.Texture0, texture);
                shader.UiEffect.Apply(context);
                commandList.DrawIndexed(checked((int)draw->ElemCount), checked(indexOffset + (int)draw->IdxOffset), checked(vertexOffset + (int)draw->VtxOffset));
            }
            vertexOffset += list.VtxBuffer.Size;
            indexOffset += list.IdxBuffer.Size;
        }
    }

    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        if (vertexBuffer == null || vertices.Length < vertexCount)
        {
            vertexBuffer?.Dispose();
            vertexBuffer = null;
            var replacement = new VertexPositionColorTextureSwizzle[Math.Max(vertexCount, vertices.Length * 2 + 4096)];
            vertexBuffer = Buffer.New(device, bufferSize: replacement.Length * VertexStride, elementSize: VertexStride,
                bufferFlags: BufferFlags.VertexBuffer, viewFormat: PixelFormat.None, usage: GraphicsResourceUsage.Dynamic);
            vertices = replacement;
        }
        if (indexBuffer == null || indices.Length < indexCount)
        {
            indexBuffer?.Dispose();
            indexBuffer = null;
            var replacement = new ushort[Math.Max(indexCount, indices.Length * 2 + 8192)];
            indexBuffer = Buffer.New(device, bufferSize: replacement.Length * sizeof(ushort), elementSize: sizeof(ushort),
                bufferFlags: BufferFlags.IndexBuffer, viewFormat: PixelFormat.None, usage: GraphicsResourceUsage.Dynamic);
            indices = replacement;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        retiring = true;
        foreach (var id in ownedTextures.ToArray())
        {
            textures[id].Dispose(); textures.Remove(id); ownedTextures.Remove(id);
        }
        textures.Clear();
        ownedTextures.Clear();
        vertexBuffer?.Dispose();
        vertexBuffer = null;
        indexBuffer?.Dispose();
        indexBuffer = null;
        if (!shaderReleased) { shader.Dispose(); shaderReleased = true; }
        vertices = []; indices = []; atlasUpdatePixels = [];
        disposed = true;
    }
}
