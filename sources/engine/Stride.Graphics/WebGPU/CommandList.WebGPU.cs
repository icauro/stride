// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_WEBGPU

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Stride.Graphics.WebGPU;
using Stride.Core.Mathematics;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics
{
    public unsafe partial class CommandList
    {
        private WGPUCommandEncoder encoder;
        private readonly List<string> profileScopes = new();
        private readonly HashSet<QueryPool> timestampPools = new();
        private readonly Dictionary<GraphicsResource, WGPUBuffer> mappedBuffers = new();
        private readonly Dictionary<GraphicsResource, (nint Pointer, int Offset, int Length)> writeMaps = new();
        private PipelineState activePipeline;
        private DescriptorSet[] boundSets = Array.Empty<DescriptorSet>();
        private readonly Dictionary<int, (Buffer Buffer, int Offset, int Stride)> vertexBuffers = new();
        private (Buffer Buffer, int Offset, bool Is32) indexBuffer;
        private uint stencilReference;

        public static CommandList New(GraphicsDevice device)
        {
            return new CommandList(device);
        }
        private CommandList(GraphicsDevice device) : base(device)
        {
            Reset();
        }
        public unsafe partial void Reset()
        {
            if (encoder.Handle != 0) return;
            var descriptor = WGPUCommandEncoderDescriptor.CreateDefault();
            encoder = Api.wgpuDeviceCreateCommandEncoder(GraphicsDevice.WebGpuContext.Device, &descriptor);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            DebugScopeResetForNewRecording();
            foreach (var name in profileScopes) PushNativeProfile(name);
        }
        public partial CompiledCommandList Close()
        {
            EnsureRecording();
            // Stride can submit while a logical profile scope is still active
            // (for example, a readback inside a renderer). Native groups must
            // be balanced per encoder; Reset reopens the logical scopes.
            for (var i = profileScopes.Count; i > 0; i--) Api.wgpuCommandEncoderPopDebugGroup(encoder);
            var descriptor = WGPUCommandBufferDescriptor.CreateDefault();
            var buffer = Api.wgpuCommandEncoderFinish(encoder, &descriptor);
            Api.wgpuCommandEncoderRelease(encoder);
            encoder = default;
            ClearBindingCache();
            WGPUCommandBuffer readback = default;
            try
            {
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                if (timestampPools.Count > 0)
                {
                    var copyDescriptor = WGPUCommandEncoderDescriptor.CreateDefault();
                    var copyEncoder = Api.wgpuDeviceCreateCommandEncoder(GraphicsDevice.WebGpuContext.Device, &copyDescriptor);
                    try
                    {
                        foreach (var pool in timestampPools) pool.EncodeReadback(this, copyEncoder);
                        readback = Api.wgpuCommandEncoderFinish(copyEncoder, &descriptor);
                        GraphicsDevice.WebGpuContext.ThrowIfFailed();
                    }
                    finally { if (copyEncoder.Handle != 0) Api.wgpuCommandEncoderRelease(copyEncoder); }
                }
                var completions = new List<Action<bool>>();
                foreach (var pool in timestampPools) completions.Add(pool.SealRecording(this));
                timestampPools.Clear();
                return new CompiledCommandList { Lease = new WebGpuCommandBufferLease(GraphicsDevice, buffer, readback, completions.ToArray()) };
            }
            catch
            {
                foreach (var pool in timestampPools) pool.CancelRecording(this);
                timestampPools.Clear();
                if (buffer.Handle != 0) Api.wgpuCommandBufferRelease(buffer);
                if (readback.Handle != 0) Api.wgpuCommandBufferRelease(readback);
                throw;
            }
        }
        public partial void Flush()
        {
            GraphicsDevice.ExecuteCommandList(Close());
        }
        private partial void ClearStateImpl()
        {
            activePipeline = null;
            boundSets = Array.Empty<DescriptorSet>();
            vertexBuffers.Clear(); indexBuffer = default; stencilReference = 0;
        }
        private void ResetTargetsImpl()
        {
        }
        private partial void SetRenderTargetsImpl(Texture depthStencilView, ReadOnlySpan<Texture> renderTargetViews)
        {
        }
        private unsafe partial void SetScissorRectangleImpl(ref readonly Rectangle scissorRectangle)
        {
            // Applied when a render pass begins.
        }
        private unsafe partial void SetScissorRectanglesImpl(ReadOnlySpan<Rectangle> scissorRectangles)
        {
            if (scissorRectangles.Length > 1) throw new NotSupportedException("WebGPU supports one scissor rectangle.");
        }
        public void WriteTimestamp(QueryPool queryPool, int index)
        {
            EnsureRecording();
            ArgumentNullException.ThrowIfNull(queryPool);
            if (!ReferenceEquals(queryPool.GraphicsDevice, GraphicsDevice)) throw new InvalidOperationException("The query pool belongs to another device.");
            queryPool.RecordTimestamp(this, encoder, index);
            timestampPools.Add(queryPool);
        }
        public void SetStreamTargets(params Buffer[] buffers)
        {
            WebGpuUnsupported.Throw();
        }
        public void UnsetReadWriteBuffers()
        {
            WebGpuUnsupported.Throw();
        }
        public void UnsetRenderTargets()
        {
            ResetTargets();
        }

        public void SetStencilReference(int stencilReference)
        {
            this.stencilReference = unchecked((uint)stencilReference);
        }

        public void SetPipelineState(PipelineState pipelineState)
        {
            EnsureResource(pipelineState);
            activePipeline = pipelineState;
        }

        public void SetVertexBuffer(int index, Buffer buffer, int offset, int stride)
        {
            EnsureResource(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(stride);
            if (offset >= buffer.SizeInBytes) throw new ArgumentOutOfRangeException(nameof(offset));
            vertexBuffers[index] = (buffer, offset, stride);
        }

        public void SetIndexBuffer(Buffer buffer, int offset, bool is32bits)
        {
            EnsureResource(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= buffer.SizeInBytes || offset % (is32bits ? 4 : 2) != 0) throw new ArgumentOutOfRangeException(nameof(offset));
            indexBuffer = (buffer, offset, is32bits);
        }

        public void ResourceBarrierTransition(GraphicsResource resource, BarrierLayout newLayout, uint subresource = uint.MaxValue)
        {
            // WebGPU derives transitions from command usage.
        }

        public void ResourceBarrierTransition(GraphicsResource resource, GraphicsResourceState newState)
        {
            // WebGPU derives transitions from command usage.
        }

        public void SetDescriptorSets(int index, DescriptorSet[] descriptorSets)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentNullException.ThrowIfNull(descriptorSets);
            if (boundSets.Length < checked(index + descriptorSets.Length)) Array.Resize(ref boundSets, index + descriptorSets.Length);
            descriptorSets.CopyTo(boundSets, index);
        }
        public void Dispatch(int threadCountX, int threadCountY, int threadCountZ)
        {
            var maximum = GraphicsDevice.WebGpuContext.Limits.maxComputeWorkgroupsPerDimension;
            if (threadCountX < 0 || threadCountY < 0 || threadCountZ < 0 || threadCountX > maximum || threadCountY > maximum || threadCountZ > maximum)
                throw new ArgumentOutOfRangeException(nameof(threadCountX), "WebGPU dispatch dimensions exceed device limits.");
            var pass = BeginCompute();
            try { Api.wgpuComputePassEncoderDispatchWorkgroups(pass, (uint)threadCountX, (uint)threadCountY, (uint)threadCountZ); }
            finally { EndCompute(pass); }
        }
        public void Dispatch(Buffer indirectBuffer, int offsetInBytes)
        {
            ValidateIndirectBuffer(indirectBuffer, offsetInBytes, 12);
            var pass = BeginCompute();
            try { Api.wgpuComputePassEncoderDispatchWorkgroupsIndirect(pass, indirectBuffer.NativeBuffer, (ulong)offsetInBytes); }
            finally { EndCompute(pass); }
        }

        public void UnorderedAccessBarrier(GraphicsResource resource)
        {
            // WebGPU orders resource accesses between passes.
        }

        public void UnorderedAccessBarrier()
        {
            // WebGPU orders resource accesses between passes.
        }
        public void Draw(int vertexCount, int startVertexLocation = 0)
        {
            DrawInstanced(vertexCount, 1, startVertexLocation);
        }
        public void DrawAuto()
        {
            WebGpuUnsupported.Throw();
        }
        public void DrawIndexed(int indexCount, int startIndexLocation = 0, int baseVertexLocation = 0)
        {
            DrawIndexedInstanced(indexCount, 1, startIndexLocation, baseVertexLocation);
        }
        public void DrawIndexedInstanced(int indexCountPerInstance, int instanceCount, int startIndexLocation = 0, int baseVertexLocation = 0, int startInstanceLocation = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(indexCountPerInstance);
            ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
            ArgumentOutOfRangeException.ThrowIfNegative(startIndexLocation);
            ArgumentOutOfRangeException.ThrowIfNegative(startInstanceLocation);
            if (indexBuffer.Buffer == null) throw new InvalidOperationException("An index buffer must be bound.");
            var pass = BeginDraw();
            try
            {
                Api.wgpuRenderPassEncoderSetIndexBuffer(pass, indexBuffer.Buffer.NativeBuffer, indexBuffer.Is32 ? WGPUIndexFormat.Uint32 : WGPUIndexFormat.Uint16, (ulong)indexBuffer.Offset, (ulong)(indexBuffer.Buffer.SizeInBytes - indexBuffer.Offset));
                Api.wgpuRenderPassEncoderDrawIndexed(pass, (uint)indexCountPerInstance, (uint)instanceCount, (uint)startIndexLocation, baseVertexLocation, (uint)startInstanceLocation);
            }
            finally { EndDraw(pass); }
        }
        public void DrawIndexedInstanced(Buffer argumentsBuffer, int alignedByteOffsetForArgs = 0)
        {
            ValidateIndirectBuffer(argumentsBuffer, alignedByteOffsetForArgs, 20);
            if (indexBuffer.Buffer == null) throw new InvalidOperationException("An index buffer must be bound.");
            var pass = BeginDraw();
            try
            {
                Api.wgpuRenderPassEncoderSetIndexBuffer(pass, indexBuffer.Buffer.NativeBuffer, indexBuffer.Is32 ? WGPUIndexFormat.Uint32 : WGPUIndexFormat.Uint16, (ulong)indexBuffer.Offset, (ulong)(indexBuffer.Buffer.SizeInBytes - indexBuffer.Offset));
                Api.wgpuRenderPassEncoderDrawIndexedIndirect(pass, argumentsBuffer.NativeBuffer, (ulong)alignedByteOffsetForArgs);
            }
            finally { EndDraw(pass); }
        }
        public void DrawInstanced(int vertexCountPerInstance, int instanceCount, int startVertexLocation = 0, int startInstanceLocation = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(vertexCountPerInstance);
            ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
            ArgumentOutOfRangeException.ThrowIfNegative(startVertexLocation);
            ArgumentOutOfRangeException.ThrowIfNegative(startInstanceLocation);
            var pass = BeginDraw();
            try { Api.wgpuRenderPassEncoderDraw(pass, (uint)vertexCountPerInstance, (uint)instanceCount, (uint)startVertexLocation, (uint)startInstanceLocation); }
            finally { EndDraw(pass); }
        }
        public void DrawInstanced(Buffer argumentsBuffer, int alignedByteOffsetForArgs = 0)
        {
            ValidateIndirectBuffer(argumentsBuffer, alignedByteOffsetForArgs, 16);
            var pass = BeginDraw();
            try { Api.wgpuRenderPassEncoderDrawIndirect(pass, argumentsBuffer.NativeBuffer, (ulong)alignedByteOffsetForArgs); }
            finally { EndDraw(pass); }
        }
        public void BeginProfile(Color4 profileColor, string name)
        {
            EnsureRecording();
            ArgumentNullException.ThrowIfNull(name);
            GraphicsDevice.PushDebugScope(name);
            profileScopes.Add(name);
            PushNativeProfile(name);
        }
        private void PushNativeProfile(string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            fixed (byte* text = bytes) Api.wgpuCommandEncoderPushDebugGroup(encoder, new WGPUStringView { data = text, length = (nuint)bytes.Length });
        }
        public void EndProfile()
        {
            EnsureRecording();
            if (profileScopes.Count == 0) throw new InvalidOperationException("There is no active WebGPU profile scope.");
            Api.wgpuCommandEncoderPopDebugGroup(encoder);
            profileScopes.RemoveAt(profileScopes.Count - 1);
            GraphicsDevice.PopDebugScope();
        }
        public void Clear(Texture depthStencilBuffer, DepthStencilClearOptions options, float depth = 1, byte stencil = 0)
        {
            EnsureResource(depthStencilBuffer);
            if (!depthStencilBuffer.IsDepthStencil) throw new ArgumentException("A depth-stencil texture is required.", nameof(depthStencilBuffer));
            if (depthStencilBuffer.IsDepthStencilReadOnly) throw new ArgumentException("A read-only depth-stencil view cannot be cleared.", nameof(depthStencilBuffer));
            EnsureRecording();
            var attachment = WGPURenderPassDepthStencilAttachment.CreateDefault();
            attachment.view = depthStencilBuffer.NativeView;
            attachment.depthLoadOp = (options & DepthStencilClearOptions.DepthBuffer) != 0 ? WGPULoadOp.Clear : WGPULoadOp.Load;
            attachment.depthStoreOp = WGPUStoreOp.Store;
            attachment.depthClearValue = depth;
            if (depthStencilBuffer.HasStencil)
            {
                attachment.stencilLoadOp = (options & DepthStencilClearOptions.Stencil) != 0 ? WGPULoadOp.Clear : WGPULoadOp.Load;
                attachment.stencilStoreOp = WGPUStoreOp.Store;
                attachment.stencilClearValue = stencil;
            }
            var descriptor = WGPURenderPassDescriptor.CreateDefault();
            descriptor.depthStencilAttachment = &attachment;
            var pass = Api.wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
            Api.wgpuRenderPassEncoderEnd(pass);
            Api.wgpuRenderPassEncoderRelease(pass);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }
        public void Clear(Texture renderTarget, Color4 color)
        {
            EnsureResource(renderTarget);
            if (!renderTarget.IsRenderTarget) throw new ArgumentException("A render-target texture is required.", nameof(renderTarget));
            EnsureRecording();
            var attachment = WGPURenderPassColorAttachment.CreateDefault();
            attachment.view = renderTarget.NativeView;
            attachment.loadOp = WGPULoadOp.Clear;
            attachment.storeOp = WGPUStoreOp.Store;
            attachment.clearValue = new WGPUColor { r = color.R, g = color.G, b = color.B, a = color.A };
            var descriptor = WGPURenderPassDescriptor.CreateDefault();
            descriptor.colorAttachmentCount = 1;
            descriptor.colorAttachments = &attachment;
            // A Stride 3D mip view covers its depth slices; WebGPU attaches
            // exactly one slice per color attachment/pass.
            var depthCount = renderTarget.Dimension == TextureDimension.Texture3D ? renderTarget.ViewDepth : 1;
            for (var slice = 0; slice < depthCount; slice++)
            {
                if (renderTarget.Dimension == TextureDimension.Texture3D) attachment.depthSlice = (uint)slice;
                var pass = Api.wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
                Api.wgpuRenderPassEncoderEnd(pass);
                Api.wgpuRenderPassEncoderRelease(pass);
            }
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }
        public void ClearReadWrite(Buffer buffer, Vector4 value)
        {
            ClearBuffer(buffer, default, value);
        }
        public void ClearReadWrite(Buffer buffer, Int4 value)
        {
            ClearBuffer(buffer, new UInt4((uint)value.X, (uint)value.Y, (uint)value.Z, (uint)value.W), null);
        }
        public void ClearReadWrite(Buffer buffer, UInt4 value)
        {
            ClearBuffer(buffer, value, null);
        }
        private void ClearBuffer(Buffer buffer, UInt4 value, Vector4? floats)
        {
            EnsureRecording(); EnsureResource(buffer);
            var pattern = WebGpuBufferClear.GetPattern(buffer, value, floats, out var period);
            if (buffer.SizeInBytes == 0) return;
            if (pattern == default)
                Api.wgpuCommandEncoderClearBuffer(encoder, buffer.NativeBuffer, 0, (ulong)((buffer.SizeInBytes + 3L) & ~3L));
            else GraphicsDevice.BufferClear.Encode(encoder, buffer, pattern, period);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }
        public void ClearReadWrite(Texture texture, Vector4 value)
        {
            ClearTexture(texture, default, value);
        }
        public void ClearReadWrite(Texture texture, Int4 value)
        {
            ClearTexture(texture, new UInt4((uint)value.X, (uint)value.Y, (uint)value.Z, (uint)value.W), null);
        }
        public void ClearReadWrite(Texture texture, UInt4 value)
        {
            ClearTexture(texture, value, null);
        }

        public void Copy(GraphicsResource source, GraphicsResource destination)
        {
            EnsureResource(source); EnsureResource(destination); EnsureRecording();
            if (source is Buffer sourceBuffer && destination is Buffer destinationBuffer)
            {
                if (sourceBuffer.SizeInBytes != destinationBuffer.SizeInBytes) throw new ArgumentException("Buffer copy sizes must match.");
                GraphicsDevice.WebGpuContext.CancelPendingRead(destinationBuffer.NativeBuffer);
                Api.wgpuCommandEncoderCopyBufferToBuffer(encoder, sourceBuffer.NativeBuffer, 0, destinationBuffer.NativeBuffer, 0, (ulong)((sourceBuffer.SizeInBytes + 3L) & ~3L));
            }
            else if (source is Texture src && destination is Texture dst)
            {
                if (src.Width != dst.Width || src.Height != dst.Height || src.Depth != dst.Depth || src.ArraySize != dst.ArraySize || src.MipLevelCount != dst.MipLevelCount || src.Format != dst.Format)
                    throw new ArgumentException("Texture copy descriptions must match.");
                if (src.Usage == GraphicsResourceUsage.Staging)
                {
                    if (ReferenceEquals(src, dst) || src.IsDepthTexture || dst.IsMultiSampled)
                        throw new NotSupportedException("This WebGPU staging source copy is not supported.");
                    // MapRead staging buffers cannot also have CopySrc usage.
                    // Submit prior writes, map the completed contents, and upload
                    // through the queue before any subsequent recorded commands.
                    Flush(); Reset();
                    var mapped = GraphicsDevice.WebGpuContext.MapBufferRead(src.NativeStagingBuffer, src.NativeStagingSize);
                    try
                    {
                        if (dst.Usage == GraphicsResourceUsage.Staging)
                        {
                            GraphicsDevice.WebGpuContext.CancelPendingRead(dst.NativeStagingBuffer);
                            Api.wgpuQueueWriteBuffer(GraphicsDevice.WebGpuContext.Queue, dst.NativeStagingBuffer, 0, (void*)mapped, (nuint)src.NativeStagingSize);
                            GraphicsDevice.WebGpuContext.ThrowIfFailed();
                            return;
                        }
                        for (var i = 0; i < src.ArraySize * src.MipLevelCount; i++)
                        {
                            var layout = src.SubresourceLayout(i);
                            dst.Upload(i, new DataBox(mapped + checked((nint)layout.Offset), checked((int)layout.RowPitch), checked((int)(layout.RowPitch * layout.Rows))));
                        }
                    }
                    finally { Api.wgpuBufferUnmap(src.NativeStagingBuffer); }
                    return;
                }
                if (src.IsMultiSampled || dst.IsMultiSampled
                    || (src.IsDepthTexture && src.Format != PixelFormat.D32_Float && dst.Usage == GraphicsResourceUsage.Staging))
                    throw new NotSupportedException("This WebGPU texture copy is not supported.");
                if (src.NativeTexture == dst.NativeTexture) throw new ArgumentException("A full texture copy cannot alias its source.");
                for (var i = 0; i < src.ArraySize * src.MipLevelCount; i++)
                {
                    var location = src.CopyLocation(i);
                    var extent = src.CopyExtent(i);
                    if (dst.Usage == GraphicsResourceUsage.Staging)
                    {
                        GraphicsDevice.WebGpuContext.CancelPendingRead(dst.NativeStagingBuffer);
                        var layout = dst.SubresourceLayout(i);
                        if (src.IsDepthTexture) location.aspect = WGPUTextureAspect.DepthOnly;
                        var target = WGPUTexelCopyBufferInfo.CreateDefault();
                        target.buffer = dst.NativeStagingBuffer;
                        target.layout.offset = layout.Offset;
                        target.layout.bytesPerRow = layout.RowPitch;
                        target.layout.rowsPerImage = layout.Rows;
                        Api.wgpuCommandEncoderCopyTextureToBuffer(encoder, &location, &target, &extent);
                    }
                    else
                    {
                        var target = dst.CopyLocation(i);
                        CopyTextureWithStencil(src, location, target, extent);
                    }
                }
            }
            else throw new NotSupportedException("WebGPU copies require two buffers or two textures.");
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }

        public void CopyMultisample(Texture sourceMultisampleTexture, int sourceSubResource, Texture destTexture, int destSubResource, PixelFormat format = PixelFormat.None)
        {
            EnsureResource(sourceMultisampleTexture); EnsureResource(destTexture); EnsureRecording();
            if (!sourceMultisampleTexture.IsMultiSampled || destTexture.IsMultiSampled || !sourceMultisampleTexture.IsRenderTarget || destTexture.Usage == GraphicsResourceUsage.Staging ||
                sourceMultisampleTexture.IsDepthStencil || destTexture.IsDepthStencil || sourceMultisampleTexture.Format != destTexture.Format ||
                (format != PixelFormat.None && format != sourceMultisampleTexture.Format) ||
                !WebGpuFormatFeatures.CanResolve(WebGpuConvert.TextureFormat(sourceMultisampleTexture.Format)))
                throw new NotSupportedException("WebGPU resolve requires matching multisampled/single-sample color render targets with a resolvable format.");
            var sourceLocation = sourceMultisampleTexture.CopyLocation(sourceSubResource);
            var destinationLocation = destTexture.CopyLocation(destSubResource);
            var sourceExtent = sourceMultisampleTexture.CopyExtent(sourceSubResource);
            var destinationExtent = destTexture.CopyExtent(destSubResource);
            if (sourceExtent.width != destinationExtent.width || sourceExtent.height != destinationExtent.height || sourceExtent.depthOrArrayLayers != 1 || destinationExtent.depthOrArrayLayers != 1)
                throw new ArgumentException("Resolve subresources must have matching two-dimensional extents.");
            var view = WGPUTextureViewDescriptor.CreateDefault();
            view.format = WebGpuConvert.TextureFormat(sourceMultisampleTexture.Format);
            view.dimension = WGPUTextureViewDimension._2D;
            view.baseMipLevel = sourceLocation.mipLevel; view.mipLevelCount = 1;
            view.baseArrayLayer = sourceLocation.origin.z; view.arrayLayerCount = 1;
            var sourceView = Api.wgpuTextureCreateView(sourceLocation.texture, &view);
            WGPUTextureView destinationView = default;
            try
            {
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                view.baseMipLevel = destinationLocation.mipLevel; view.baseArrayLayer = destinationLocation.origin.z;
                destinationView = Api.wgpuTextureCreateView(destinationLocation.texture, &view);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                var color = WGPURenderPassColorAttachment.CreateDefault();
                color.view = sourceView; color.resolveTarget = destinationView;
                color.loadOp = WGPULoadOp.Load; color.storeOp = WGPUStoreOp.Store;
                var descriptor = WGPURenderPassDescriptor.CreateDefault();
                descriptor.colorAttachments = &color; descriptor.colorAttachmentCount = 1;
                var pass = Api.wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
                Api.wgpuRenderPassEncoderEnd(pass); Api.wgpuRenderPassEncoderRelease(pass);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
            }
            finally
            {
                if (sourceView.Handle != 0) Api.wgpuTextureViewRelease(sourceView);
                if (destinationView.Handle != 0) Api.wgpuTextureViewRelease(destinationView);
            }
        }

        public void CopyRegion(GraphicsResource source, int sourceSubresource, ResourceRegion? sourecRegion, GraphicsResource destination, int destinationSubResource, int dstX = 0, int dstY = 0, int dstZ = 0)
        {
            EnsureResource(source); EnsureResource(destination); EnsureRecording();
            ArgumentOutOfRangeException.ThrowIfNegative(dstX);
            ArgumentOutOfRangeException.ThrowIfNegative(dstY);
            ArgumentOutOfRangeException.ThrowIfNegative(dstZ);
            if (source is Buffer srcBuffer && destination is Buffer dstBuffer)
            {
                if (sourceSubresource != 0 || destinationSubResource != 0) throw new ArgumentOutOfRangeException(nameof(sourceSubresource));
                if (srcBuffer.Usage == GraphicsResourceUsage.Staging || srcBuffer.NativeBuffer.Handle == dstBuffer.NativeBuffer.Handle)
                    throw new NotSupportedException("WebGPU region copies require a GPU source and a distinct destination buffer.");
                var region = sourecRegion ?? new ResourceRegion(0, 0, 0, srcBuffer.SizeInBytes, 1, 1);
                if (region.Top != 0 || region.Bottom != 1 || region.Front != 0 || region.Back != 1 || dstY != 0 || dstZ != 0 ||
                    region.Left < 0 || region.Right < region.Left || region.Right > srcBuffer.SizeInBytes ||
                    (long)dstX + region.Right - region.Left > dstBuffer.SizeInBytes)
                    throw new ArgumentOutOfRangeException(nameof(sourecRegion));
                var length = region.Right - region.Left;
                if (region.Left % 4 != 0 || dstX % 4 != 0 || length % 4 != 0)
                    throw new ArgumentException("WebGPU buffer copy offsets and length must be aligned to four bytes.");
                if (length == 0) return;
                GraphicsDevice.WebGpuContext.CancelPendingRead(dstBuffer.NativeBuffer);
                Api.wgpuCommandEncoderCopyBufferToBuffer(encoder, srcBuffer.NativeBuffer, (ulong)region.Left, dstBuffer.NativeBuffer, (ulong)dstX, (ulong)length);
            }
            else if (source is Texture src && destination is Texture dst)
            {
                if (src.Format != dst.Format || src.Usage == GraphicsResourceUsage.Staging || src.IsMultiSampled || dst.IsMultiSampled
                    || (src.IsDepthTexture && dst.Usage == GraphicsResourceUsage.Staging))
                    throw new NotSupportedException("WebGPU region copies require matching single-sample formats and a GPU source; depth readback is not implemented.");
                var location = src.CopyLocation(sourceSubresource);
                var destinationLocation = dst.CopyLocation(destinationSubResource);
                var sourceExtent = src.CopyExtent(sourceSubresource);
                var destinationExtent = dst.CopyExtent(destinationSubResource);
                var region = sourecRegion ?? new ResourceRegion(0, 0, 0, (int)sourceExtent.width, (int)sourceExtent.height, (int)sourceExtent.depthOrArrayLayers);
                if (region.Left < 0 || region.Top < 0 || region.Front < 0 || region.Right < region.Left || region.Bottom < region.Top || region.Back < region.Front ||
                    region.Right > sourceExtent.width || region.Bottom > sourceExtent.height || region.Back > sourceExtent.depthOrArrayLayers ||
                    (long)dstX + region.Right - region.Left > destinationExtent.width || (long)dstY + region.Bottom - region.Top > destinationExtent.height ||
                    (long)dstZ + region.Back - region.Front > destinationExtent.depthOrArrayLayers)
                    throw new ArgumentOutOfRangeException(nameof(sourecRegion));
                var extent = new WGPUExtent3D { width = (uint)(region.Right - region.Left), height = (uint)(region.Bottom - region.Top), depthOrArrayLayers = (uint)(region.Back - region.Front) };
                if (extent.width == 0 || extent.height == 0 || extent.depthOrArrayLayers == 0) return;
                if (src.IsDepthTexture && (region.Left != 0 || region.Top != 0 || dstX != 0 || dstY != 0
                    || extent.width != sourceExtent.width || extent.height != sourceExtent.height
                    || extent.width != destinationExtent.width || extent.height != destinationExtent.height))
                    throw new NotSupportedException("WebGPU depth/stencil copies must cover the full source and destination subresource dimensions.");
                location.origin.x = (uint)region.Left; location.origin.y = (uint)region.Top; location.origin.z += (uint)region.Front;
                destinationLocation.origin.x = (uint)dstX; destinationLocation.origin.y = (uint)dstY; destinationLocation.origin.z += (uint)dstZ;
                if (dst.Usage == GraphicsResourceUsage.Staging)
                {
                    var layout = dst.SubresourceLayout(destinationSubResource);
                    var target = WGPUTexelCopyBufferInfo.CreateDefault();
                    GraphicsDevice.WebGpuContext.CancelPendingRead(dst.NativeStagingBuffer);
                    target.buffer = dst.NativeStagingBuffer;
                    target.layout.bytesPerRow = layout.RowPitch; target.layout.rowsPerImage = layout.Rows;
                    target.layout.offset = checked(layout.Offset + ((ulong)dstZ * layout.Rows + (ulong)dstY) * layout.RowPitch + (ulong)dstX * (ulong)dst.Format.SizeInBytes);
                    Api.wgpuCommandEncoderCopyTextureToBuffer(encoder, &location, &target, &extent);
                }
                else
                {
                    if (location.texture.Handle == destinationLocation.texture.Handle && location.mipLevel == destinationLocation.mipLevel &&
                        location.origin.z < destinationLocation.origin.z + extent.depthOrArrayLayers && destinationLocation.origin.z < location.origin.z + extent.depthOrArrayLayers)
                        throw new ArgumentException("WebGPU texture copies cannot alias the same mip and array/depth subresources.");
                    CopyTextureWithStencil(src, location, destinationLocation, extent);
                }
            }
            else throw new NotSupportedException("WebGPU region copies require two buffers or two textures.");
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }

        public void CopyCount(Buffer sourceBuffer, Buffer destBuffer, int offsetInBytes)
        {
            WebGpuUnsupported.Throw();
        }
        internal void UpdateSubResource(GraphicsResource resource, int subResourceIndex, ReadOnlySpan<byte> sourceData)
        {
            fixed (byte* pointer = sourceData)
            {
                if (resource is Texture texture)
                {
                    var extent = texture.CopyExtent(subResourceIndex);
                    Image.ComputePitch(texture.Format, (int)extent.width, (int)extent.height, out var row, out var slice, out _, out _);
                    if (sourceData.Length < checked(slice * (int)extent.depthOrArrayLayers)) throw new ArgumentException("The texture source data is too short.", nameof(sourceData));
                    UpdateSubResource(resource, subResourceIndex, new DataBox((nint)pointer, row, slice));
                }
                else UpdateSubResource(resource, subResourceIndex, new DataBox((nint)pointer, sourceData.Length, 0));
            }
        }
        internal void UpdateSubResource(GraphicsResource resource, int subResourceIndex, DataBox sourceData)
        {
            EnsureResource(resource); EnsureRecording();
            if (resource is Buffer buffer)
            {
                if (subResourceIndex != 0) throw new ArgumentOutOfRangeException(nameof(subResourceIndex));
                if (sourceData.RowPitch != 0 && sourceData.RowPitch < buffer.SizeInBytes) throw new ArgumentException("The source buffer data is too short.", nameof(sourceData));
                UploadBuffer(buffer, sourceData.DataPointer, 0, buffer.SizeInBytes);
            }
            else if (resource is Texture texture)
            {
                var extent = texture.CopyExtent(subResourceIndex);
                UploadTexture(texture, subResourceIndex, sourceData, new ResourceRegion(0, 0, 0, (int)extent.width, (int)extent.height, (int)extent.depthOrArrayLayers));
            }
            else throw new NotSupportedException("Unknown WebGPU resource type.");
        }

        internal void UpdateSubResource(GraphicsResource resource, int subResourceIndex, ReadOnlySpan<byte> sourceData, ResourceRegion region)
        {
            if (region.Right <= region.Left || region.Bottom <= region.Top || region.Back <= region.Front) return;
            fixed (byte* pointer = sourceData)
            {
                if (resource is Texture texture)
                {
                    Image.ComputePitch(texture.Format, region.Width, region.Height, out var row, out var slice, out _, out _);
                    if (sourceData.Length < checked(slice * region.Depth)) throw new ArgumentException("The texture source data is too short.", nameof(sourceData));
                    UpdateSubResource(resource, subResourceIndex, new DataBox((nint)pointer, row, slice), region);
                }
                else UpdateSubResource(resource, subResourceIndex, new DataBox((nint)pointer, sourceData.Length, 0), region);
            }
        }

        internal unsafe partial void UpdateSubResource(GraphicsResource resource, int subResourceIndex, DataBox sourceData, ResourceRegion region)
        {
            EnsureResource(resource); EnsureRecording();
            if (region.Right <= region.Left || region.Bottom <= region.Top || region.Back <= region.Front) return;
            if (resource is Buffer buffer && subResourceIndex == 0)
            {
                if (sourceData.RowPitch != 0 && sourceData.RowPitch < (long)region.Right - region.Left) throw new ArgumentException("The source buffer data is too short.", nameof(sourceData));
                UploadBuffer(buffer, sourceData.DataPointer, region.Left, region.Right - region.Left);
            }
            else if (resource is Texture texture) UploadTexture(texture, subResourceIndex, sourceData, region);
            else throw new NotSupportedException("Unknown WebGPU resource type or invalid buffer subresource.");
        }

        public unsafe partial MappedResource MapSubResource(GraphicsResource resource, int subResourceIndex, MapMode mapMode, bool doNotWait = false, int offsetInBytes = 0, int lengthInBytes = 0)
        {
            EnsureResource(resource);
            if (mappedBuffers.ContainsKey(resource) || writeMaps.ContainsKey(resource) || textureWriteMaps.ContainsKey(resource)) throw new InvalidOperationException("The resource is already mapped by this command list.");
            if ((mapMode == MapMode.WriteDiscard || mapMode == MapMode.WriteNoOverwrite) && resource is Buffer writable && writable.Usage == GraphicsResourceUsage.Dynamic && !doNotWait)
            {
                EnsureRecording();
                if (subResourceIndex != 0) throw new ArgumentOutOfRangeException(nameof(subResourceIndex));
                ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);
                if (lengthInBytes == 0) lengthInBytes = writable.SizeInBytes - offsetInBytes;
                if (lengthInBytes <= 0 || (long)offsetInBytes + lengthInBytes > writable.SizeInBytes || offsetInBytes % 4 != 0 || lengthInBytes % 4 != 0)
                    throw new ArgumentOutOfRangeException(nameof(lengthInBytes), "WebGPU dynamic buffer map ranges must be bounded and aligned to four bytes.");
                // Encode only the written range at Unmap. Earlier draws retain
                // their data through command ordering, even if a later map reuses
                // the same region; no GPU memory is exposed to the CPU.
                var memory = (nint)NativeMemory.AllocZeroed((nuint)lengthInBytes);
                writeMaps.Add(resource, (memory, offsetInBytes, lengthInBytes));
                return new MappedResource(resource, 0, new DataBox(memory, lengthInBytes, 0), offsetInBytes, lengthInBytes);
            }
            if (mapMode == MapMode.WriteDiscard && !doNotWait && resource is Texture writableTexture && writableTexture.Usage == GraphicsResourceUsage.Dynamic)
                return MapDynamicTexture(writableTexture, subResourceIndex, offsetInBytes, lengthInBytes);
            if (mapMode != MapMode.Read) throw new NotSupportedException("WebGPU supports staging read maps and dynamic buffer/texture upload maps.");
            WGPUBuffer buffer;
            ulong size, offset = 0;
            int rowPitch = 0, slicePitch = 0;
            if (resource is Texture texture && texture.Usage == GraphicsResourceUsage.Staging)
            {
                if (offsetInBytes != 0 || lengthInBytes != 0) throw new ArgumentException("Texture map byte ranges are not supported.");
                var layout = texture.SubresourceLayout(subResourceIndex);
                buffer = texture.NativeStagingBuffer; size = texture.NativeStagingSize; offset = layout.Offset;
                rowPitch = checked((int)layout.RowPitch); slicePitch = checked(rowPitch * (int)layout.Rows);
            }
            else if (resource is Buffer source && source.Usage == GraphicsResourceUsage.Staging)
            {
                if (subResourceIndex != 0) throw new ArgumentOutOfRangeException(nameof(subResourceIndex));
                ArgumentOutOfRangeException.ThrowIfNegative(offsetInBytes);
                if (lengthInBytes == 0) lengthInBytes = source.SizeInBytes - offsetInBytes;
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthInBytes);
                if ((long)offsetInBytes + lengthInBytes > source.SizeInBytes) throw new ArgumentOutOfRangeException(nameof(lengthInBytes));
                buffer = source.NativeBuffer; size = (ulong)((source.SizeInBytes + 3L) & ~3L); offset = (ulong)offsetInBytes;
            }
            else throw new NotSupportedException("WebGPU read maps require a staging resource.");
            // Submit this recording before requesting the asynchronous map. The
            // callback establishes completion of the texture/buffer copy.
            if (encoder.Handle != 0) Flush();
            Reset();
            nint pointer;
            if (doNotWait)
            {
                if (!GraphicsDevice.WebGpuContext.TryMapBufferRead(buffer, size, out pointer)) return default;
            }
            else pointer = GraphicsDevice.WebGpuContext.MapBufferRead(buffer, size);
            Api.wgpuBufferAddRef(buffer);
            mappedBuffers.Add(resource, buffer);
            return new MappedResource(resource, subResourceIndex, new DataBox(pointer + checked((nint)offset), rowPitch, slicePitch), offsetInBytes, lengthInBytes);
        }

        public unsafe partial void UnmapSubResource(MappedResource mappedResource)
        {
            if (writeMaps.Remove(mappedResource.Resource, out var write))
            {
                try { EnsureResource(mappedResource.Resource); EnsureRecording(); UploadBuffer((Buffer)mappedResource.Resource, write.Pointer, write.Offset, write.Length); }
                finally { NativeMemory.Free((void*)write.Pointer); }
                return;
            }
            if (textureWriteMaps.Remove(mappedResource.Resource, out var textureWrite))
            {
                UnmapDynamicTexture((Texture)mappedResource.Resource, textureWrite);
                return;
            }
            if (!mappedBuffers.Remove(mappedResource.Resource, out var buffer)) throw new InvalidOperationException("The resource is not mapped by this command list.");
            Api.wgpuBufferUnmap(buffer);
            Api.wgpuBufferRelease(buffer);
        }

        private void EnsureRecording()
        {
            if (encoder.Handle == 0) throw new InvalidOperationException("Call Reset before recording WebGPU commands.");
        }

        private void ValidateIndirectBuffer(Buffer buffer, int offset, int size)
        {
            EnsureResource(buffer);
            if ((buffer.Flags & BufferFlags.ArgumentBuffer) == 0) throw new ArgumentException("An indirect argument buffer is required.", nameof(buffer));
            if (offset < 0 || offset % 4 != 0 || (long)offset + size > buffer.SizeInBytes) throw new ArgumentOutOfRangeException(nameof(offset));
        }

        private void UploadBuffer(Buffer destination, nint pointer, int offset, int length)
        {
            if (pointer == 0) throw new ArgumentException("The upload pointer is null.", nameof(pointer));
            if (offset < 0 || length <= 0 || (long)offset + length > destination.SizeInBytes || offset % 4 != 0 || length % 4 != 0)
                throw new ArgumentOutOfRangeException(nameof(length), "WebGPU buffer updates require a bounded range aligned to four bytes.");
            var descriptor = WGPUBufferDescriptor.CreateDefault();
            descriptor.size = (ulong)length;
            descriptor.usage = WGPUBufferUsage.CopySrc;
            descriptor.mappedAtCreation = 1;
            var upload = Api.wgpuDeviceCreateBuffer(GraphicsDevice.WebGpuContext.Device, &descriptor);
            try
            {
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                var mapped = Api.wgpuBufferGetMappedRange(upload, 0, (nuint)length);
                if (mapped == null) throw new InvalidOperationException("WebGPU upload buffer could not be mapped.");
                new ReadOnlySpan<byte>((void*)pointer, length).CopyTo(new Span<byte>(mapped, length));
                Api.wgpuBufferUnmap(upload);
                Api.wgpuCommandEncoderCopyBufferToBuffer(encoder, upload, 0, destination.NativeBuffer, (ulong)offset, (ulong)length);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
            }
            finally { if (upload.Handle != 0) Api.wgpuBufferRelease(upload); }
        }

        private void UploadTexture(Texture destination, int subresource, DataBox source, ResourceRegion region)
        {
            if (destination.Usage == GraphicsResourceUsage.Staging || destination.IsDepthStencil || destination.IsMultiSampled)
                throw new NotSupportedException("WebGPU texture updates require a single-sample color texture.");
            var location = destination.CopyLocation(subresource);
            var full = destination.CopyExtent(subresource);
            if (region.Left < 0 || region.Top < 0 || region.Front < 0 || region.Right > full.width || region.Bottom > full.height || region.Back > full.depthOrArrayLayers)
                throw new ArgumentOutOfRangeException(nameof(region));
            var blockWidth = destination.Format.BlockWidth;
            var blockHeight = destination.Format.BlockHeight;
            if (region.Left % blockWidth != 0 || region.Top % blockHeight != 0 || region.Width % blockWidth != 0 || region.Height % blockHeight != 0)
                throw new ArgumentException("WebGPU texture update regions must cover complete compression blocks.", nameof(region));
            Image.ComputePitch(destination.Format, region.Width, region.Height, out var rowBytes, out var packedSlice, out _, out _);
            var rows = packedSlice / rowBytes;
            var sourceSlice = source.SlicePitch == 0 ? checked(source.RowPitch * rows) : source.SlicePitch;
            if (source.DataPointer == 0 || source.RowPitch < rowBytes || sourceSlice < checked(source.RowPitch * rows))
                throw new ArgumentException("Texture source pointer or pitch is invalid.", nameof(source));
            var rowPitch = checked((rowBytes + 255) & ~255);
            var slicePitch = checked(rowPitch * rows);
            var size = checked(slicePitch * region.Depth);
            var descriptor = WGPUBufferDescriptor.CreateDefault();
            descriptor.size = (ulong)size; descriptor.usage = WGPUBufferUsage.CopySrc; descriptor.mappedAtCreation = 1;
            var upload = Api.wgpuDeviceCreateBuffer(GraphicsDevice.WebGpuContext.Device, &descriptor);
            try
            {
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                var mapped = (byte*)Api.wgpuBufferGetMappedRange(upload, 0, (nuint)size);
                if (mapped == null) throw new InvalidOperationException("WebGPU texture upload buffer could not be mapped.");
                new Span<byte>(mapped, size).Clear();
                for (var z = 0; z < region.Depth; z++)
                for (var y = 0; y < rows; y++)
                    new ReadOnlySpan<byte>((byte*)source.DataPointer + checked(z * sourceSlice + y * source.RowPitch), rowBytes)
                        .CopyTo(new Span<byte>(mapped + z * slicePitch + y * rowPitch, rowBytes));
                Api.wgpuBufferUnmap(upload);
                var copy = WGPUTexelCopyBufferInfo.CreateDefault(); copy.buffer = upload;
                copy.layout.bytesPerRow = (uint)rowPitch; copy.layout.rowsPerImage = (uint)rows;
                location.origin.x += (uint)region.Left; location.origin.y += (uint)region.Top; location.origin.z += (uint)region.Front;
                var extent = new WGPUExtent3D { width = (uint)region.Width, height = (uint)region.Height, depthOrArrayLayers = (uint)region.Depth };
                Api.wgpuCommandEncoderCopyBufferToTexture(encoder, &copy, &location, &extent);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
            }
            finally { if (upload.Handle != 0) Api.wgpuBufferRelease(upload); }
        }

        private DescriptorSetEntry ReadBinding(PipelineState.WebGpuBinding mapping)
        {
            if (mapping.ImmutableSampler != null) return new DescriptorSetEntry { Value = mapping.ImmutableSampler };
            if (mapping.SourceSet >= boundSets.Length || boundSets[mapping.SourceSet].HeapObjects == null) throw new InvalidOperationException("A shader descriptor set has not been bound.");
            var set = boundSets[mapping.SourceSet];
            var entry = set.HeapObjects[set.DescriptorStartOffset + mapping.SourceSlot];
            if (entry.Value == null && mapping.IsSampler) entry.Value = GraphicsDevice.SamplerStates.LinearClamp;
            return entry;
        }

        private PipelineState SelectPipelineForResources()
        {
            EnsureResource(activePipeline);
            var depthBindings = new HashSet<uint>();
            var storageFormats = new Dictionary<uint, PixelFormat>();
            foreach (var mapping in activePipeline.Bindings)
                if (ReadBinding(mapping).Value is Texture texture)
                {
                    if (texture.IsDepthTexture) depthBindings.Add(mapping.Binding);
                    if ((texture.ViewFlags & TextureFlags.UnorderedAccess) != 0) storageFormats[mapping.Binding] = texture.ViewFormat;
                }
            var readOnlyDepth = !activePipeline.IsCompute && depthStencilBuffer != null
                && (depthStencilBuffer.IsDepthStencilReadOnly || DepthStencilAccess == DepthStencilAccess.Read);
            return activePipeline.WithDepthBindings(depthBindings, readOnlyDepth, storageFormats);
        }

        private WGPUBindGroup CreateBindings(PipelineState pipeline)
        {
            var mappings = pipeline.Bindings;
            if (mappings.Count == 0) return default;
            var entries = stackalloc WGPUBindGroupEntry[mappings.Count];
            for (var i = 0; i < mappings.Count; i++)
            {
                var mapping = mappings[i];
                var source = ReadBinding(mapping);
                entries[i] = WGPUBindGroupEntry.CreateDefault();
                entries[i].binding = mapping.Binding;
                if (source.Value == null && mapping.NullableFloat2D)
                {
                    entries[i].textureView = GraphicsDevice.WebGpuContext.NullReadTexture2D();
                    continue;
                }
                if (source.Value == null && mapping.ReadOnlyStorage)
                {
                    // Stride leaves unused clustered-light buffers unbound.
                    // A native storage binding is mandatory; its zero-filled
                    // contents preserve null-resource reads without aliasing a
                    // writable fallback. Native bounds checks cover larger indices.
                    entries[i].buffer = GraphicsDevice.WebGpuContext.NullReadBuffer(mapping.StructureStride);
                    entries[i].size = Math.Max(16u, (mapping.StructureStride + 3) & ~3u);
                    continue;
                }
                switch (source.Value)
                {
                    case Buffer buffer:
                        EnsureResource(buffer);
                        if (buffer.NativeBuffer.Handle == 0)
                            throw new InvalidOperationException($"WebGPU binding {mapping.Binding} ({mapping.Name}) has no native buffer: flags={buffer.Flags}, usage={buffer.Usage}.");
                        if ((buffer.Flags & BufferFlags.StructuredBuffer) != 0 && mapping.StructureStride != 0 && buffer.Description.StructureByteStride != mapping.StructureStride)
                            throw new NotSupportedException($"WebGPU binding {mapping.Binding} requires a structured buffer stride of {mapping.StructureStride} bytes; the buffer uses {buffer.Description.StructureByteStride}. Pad CPU elements to the shader layout.");
                        if ((buffer.Flags & BufferFlags.StructuredBuffer) == 0 && mapping.StructureStride != 0 && buffer.ViewFormat != PixelFormat.None && buffer.ViewFormat.SizeInBytes != mapping.StructureStride)
                            throw new NotSupportedException($"WebGPU binding {mapping.Binding} requires storage elements of {mapping.StructureStride} bytes; the typed buffer view uses {buffer.ViewFormat.SizeInBytes}. This view format requires conversion before WebGPU storage binding.");
                        entries[i].buffer = buffer.NativeBuffer;
                        entries[i].offset = (ulong)Math.Max(0, source.Offset);
                        entries[i].size = (ulong)(source.Size > 0 ? source.Size : buffer.SizeInBytes);
                        break;
                    case Texture texture:
                        EnsureResource(texture);
                        entries[i].textureView = texture.IsDepthTexture ? texture.NativeDepthShaderView : texture.NativeView;
                        if (entries[i].textureView.Handle == 0)
                            throw new InvalidOperationException($"WebGPU binding {mapping.Binding} ({mapping.Name}) has no shader texture view: format={texture.ViewFormat}, flags={texture.ViewFlags}, usage={texture.Usage}.");
                        break;
                    case SamplerState sampler:
                        EnsureResource(sampler);
                        entries[i].sampler = sampler.NativeSampler;
                        if (entries[i].sampler.Handle == 0)
                            throw new InvalidOperationException($"WebGPU binding {mapping.Binding} ({mapping.Name}) has no native sampler.");
                        break;
                    default: throw new InvalidOperationException($"WebGPU binding {mapping.Binding} ({mapping.Name}, descriptor set {mapping.SourceSet}, slot {mapping.SourceSlot}) is missing or has an unsupported resource type ({source.Value?.GetType().FullName ?? "null"}).");
                }
            }
            var cached = FindBindings(pipeline.NativeBindGroupLayout, new ReadOnlySpan<WGPUBindGroupEntry>(entries, mappings.Count));
            if (cached.Handle != 0) return cached;
            var descriptor = WGPUBindGroupDescriptor.CreateDefault();
            descriptor.layout = pipeline.NativeBindGroupLayout;
            descriptor.entryCount = (nuint)mappings.Count;
            descriptor.entries = entries;
            var group = Api.wgpuDeviceCreateBindGroup(GraphicsDevice.WebGpuContext.Device, &descriptor);
            try
            {
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                CacheBindings(pipeline.NativeBindGroupLayout, new ReadOnlySpan<WGPUBindGroupEntry>(entries, mappings.Count), group);
                return group;
            }
            catch { if (group.Handle != 0) Api.wgpuBindGroupRelease(group); throw; }
        }

        private WGPURenderPassEncoder BeginDraw()
        {
            EnsureRecording();
            if (activePipeline == null || activePipeline.NativePipeline.Handle == 0) throw new InvalidOperationException("A WebGPU render pipeline must be bound before drawing.");
            if (renderTargetCount == 0 && depthStencilBuffer == null) throw new InvalidOperationException("A render target or depth buffer must be bound.");
            if (ViewportCount > 1) throw new NotSupportedException("WebGPU supports one viewport.");
            var attachments = stackalloc WGPURenderPassColorAttachment[renderTargetCount];
            for (var i = 0; i < renderTargetCount; i++)
            {
                attachments[i] = WGPURenderPassColorAttachment.CreateDefault();
                if (renderTargets[i] == null) continue;
                EnsureResource(renderTargets[i]);
                attachments[i].view = renderTargets[i].NativeView;
                attachments[i].loadOp = WGPULoadOp.Load; attachments[i].storeOp = WGPUStoreOp.Store;
            }
            var descriptor = WGPURenderPassDescriptor.CreateDefault();
            descriptor.colorAttachmentCount = (nuint)renderTargetCount; descriptor.colorAttachments = attachments;
            var depthAttachment = WGPURenderPassDepthStencilAttachment.CreateDefault();
            if (depthStencilBuffer != null)
            {
                EnsureResource(depthStencilBuffer);
                depthAttachment.view = depthStencilBuffer.NativeView;
                if (depthStencilBuffer.IsDepthStencilReadOnly || DepthStencilAccess == DepthStencilAccess.Read)
                {
                    depthAttachment.depthReadOnly = 1;
                    if (depthStencilBuffer.HasStencil) depthAttachment.stencilReadOnly = 1;
                }
                else
                {
                    depthAttachment.depthLoadOp = WGPULoadOp.Load; depthAttachment.depthStoreOp = WGPUStoreOp.Store;
                    if (depthStencilBuffer.HasStencil) { depthAttachment.stencilLoadOp = WGPULoadOp.Load; depthAttachment.stencilStoreOp = WGPUStoreOp.Store; }
                }
                descriptor.depthStencilAttachment = &depthAttachment;
            }
            var pipeline = SelectPipelineForResources();
            var group = CreateBindings(pipeline);
            WGPURenderPassEncoder pass = default;
            try
            {
                pass = Api.wgpuCommandEncoderBeginRenderPass(encoder, &descriptor);
                Api.wgpuRenderPassEncoderSetPipeline(pass, pipeline.NativePipeline);
                if (group.Handle != 0) Api.wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
                for (var i = 0; i < activePipeline.VertexStrides.Length; i++)
                {
                    if (!vertexBuffers.TryGetValue(i, out var binding)) throw new InvalidOperationException($"Vertex buffer {i} is not bound.");
                    EnsureResource(binding.Buffer);
                    if (binding.Stride != activePipeline.VertexStrides[i]) throw new NotSupportedException("The bound vertex stride must match the pipeline input layout.");
                    Api.wgpuRenderPassEncoderSetVertexBuffer(pass, (uint)i, binding.Buffer.NativeBuffer, (ulong)binding.Offset, (ulong)(binding.Buffer.SizeInBytes - binding.Offset));
                }
                var target = renderTargetCount > 0 ? renderTargets[0] : depthStencilBuffer;
                var viewport = ViewportCount > 0 ? Viewport : new Viewport(0, 0, target.ViewWidth, target.ViewHeight);
                Api.wgpuRenderPassEncoderSetViewport(pass, viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth);
                if (activePipeline.ScissorEnabled && ScissorCount > 0)
                {
                    var left = Math.Clamp(Scissor.Left, 0, target.ViewWidth);
                    var top = Math.Clamp(Scissor.Top, 0, target.ViewHeight);
                    var right = Math.Clamp(Scissor.Right, left, target.ViewWidth);
                    var bottom = Math.Clamp(Scissor.Bottom, top, target.ViewHeight);
                    Api.wgpuRenderPassEncoderSetScissorRect(pass, (uint)left, (uint)top, (uint)(right - left), (uint)(bottom - top));
                }
                Api.wgpuRenderPassEncoderSetStencilReference(pass, stencilReference);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                return pass;
            }
            catch { if (pass.Handle != 0) { Api.wgpuRenderPassEncoderEnd(pass); Api.wgpuRenderPassEncoderRelease(pass); } throw; }
            finally { if (group.Handle != 0) Api.wgpuBindGroupRelease(group); }
        }

        private WGPUComputePassEncoder BeginCompute()
        {
            EnsureRecording();
            if (activePipeline == null || !activePipeline.IsCompute) throw new InvalidOperationException("A WebGPU compute pipeline must be bound before dispatch.");
            var pipeline = SelectPipelineForResources();
            var group = CreateBindings(pipeline);
            WGPUComputePassEncoder pass = default;
            try
            {
                var descriptor = WGPUComputePassDescriptor.CreateDefault();
                pass = Api.wgpuCommandEncoderBeginComputePass(encoder, &descriptor);
                Api.wgpuComputePassEncoderSetPipeline(pass, pipeline.NativeComputePipeline);
                if (group.Handle != 0) Api.wgpuComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
                return pass;
            }
            catch { if (pass.Handle != 0) { Api.wgpuComputePassEncoderEnd(pass); Api.wgpuComputePassEncoderRelease(pass); } throw; }
            finally { if (group.Handle != 0) Api.wgpuBindGroupRelease(group); }
        }

        private void EndCompute(WGPUComputePassEncoder pass)
        {
            Api.wgpuComputePassEncoderEnd(pass); Api.wgpuComputePassEncoderRelease(pass);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }

        private void EndDraw(WGPURenderPassEncoder pass)
        {
            Api.wgpuRenderPassEncoderEnd(pass); Api.wgpuRenderPassEncoderRelease(pass);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }

        private void EnsureResource(GraphicsResourceBase resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (!ReferenceEquals(resource.GraphicsDevice, GraphicsDevice)) throw new ArgumentException("The resource belongs to another device.", nameof(resource));
            if (resource.LifetimeState == GraphicsResourceLifetimeState.Destroyed) throw new ObjectDisposedException(resource.Name);
        }

        protected internal override void OnDestroyed(bool immediately = false)
        {
            foreach (var buffer in mappedBuffers.Values) { Api.wgpuBufferUnmap(buffer); Api.wgpuBufferRelease(buffer); }
            mappedBuffers.Clear();
            foreach (var write in writeMaps.Values) NativeMemory.Free((void*)write.Pointer);
            writeMaps.Clear();
            foreach (var write in textureWriteMaps.Values)
            {
                Api.wgpuBufferUnmap(write.Upload); Api.wgpuBufferRelease(write.Upload);
            }
            textureWriteMaps.Clear();
            foreach (var pool in timestampPools) pool.CancelRecording(this);
            timestampPools.Clear();
            ClearBindingCache();
            if (encoder.Handle != 0) Api.wgpuCommandEncoderRelease(encoder);
            encoder = default;
            base.OnDestroyed(immediately);
        }
    }
}

#endif
