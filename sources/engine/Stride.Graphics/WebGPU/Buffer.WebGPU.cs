// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_WEBGPU

using System;
using System.Runtime.InteropServices;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics
{
    public partial class Buffer
    {
        internal WGPUBuffer NativeBuffer;
        protected partial Buffer InitializeFromImpl(ref readonly BufferDescription description, BufferFlags viewFlags, PixelFormat viewFormat, IntPtr dataPointer)
        {
            bufferDescription = description;
            ViewFlags = viewFlags;
            ViewFormat = viewFormat;
            Recreate(dataPointer);

            return this;
        }
        public unsafe void Recreate(IntPtr dataPointer)
        {
            if (NativeBuffer.Handle != 0) OnDestroyed();
            var descriptor = WGPUBufferDescriptor.CreateDefault();
            descriptor.size = checked((ulong)((SizeInBytes + 3L) & ~3L));
            descriptor.usage = WGPUBufferUsage.CopySrc | WGPUBufferUsage.CopyDst;
            if (bufferDescription.Usage == GraphicsResourceUsage.Staging)
                descriptor.usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst;
            else
            {
                var flags = bufferDescription.BufferFlags;
                if ((flags & BufferFlags.VertexBuffer) != 0) descriptor.usage |= WGPUBufferUsage.Vertex;
                if ((flags & BufferFlags.IndexBuffer) != 0) descriptor.usage |= WGPUBufferUsage.Index;
                if ((flags & BufferFlags.ConstantBuffer) != 0) descriptor.usage |= WGPUBufferUsage.Uniform;
                if ((flags & (BufferFlags.ShaderResource | BufferFlags.UnorderedAccess)) != 0) descriptor.usage |= WGPUBufferUsage.Storage;
                if ((flags & BufferFlags.ArgumentBuffer) != 0) descriptor.usage |= WGPUBufferUsage.Indirect;
                if ((flags & (BufferFlags.StreamOutput | BufferFlags.RenderTarget)) != 0) throw new NotSupportedException("WebGPU does not support this buffer usage.");
            }
            NativeBuffer = Api.wgpuDeviceCreateBuffer(GraphicsDevice.WebGpuContext.Device, &descriptor);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            if (dataPointer != IntPtr.Zero)
            {
                if (bufferDescription.Usage == GraphicsResourceUsage.Staging) throw new NotSupportedException("Initial data for WebGPU staging buffers is not supported.");
                var bytes = new byte[checked((int)descriptor.size)];
                Marshal.Copy(dataPointer, bytes, 0, SizeInBytes);
                fixed (byte* data = bytes) Api.wgpuQueueWriteBuffer(GraphicsDevice.WebGpuContext.Queue, NativeBuffer, 0, data, (nuint)bytes.Length);
                GraphicsDevice.WebGpuContext.ThrowIfFailed();
            }
        }
        protected internal override void OnDestroyed(bool immediately = false)
        {
            if (NativeBuffer.Handle != 0) GraphicsDevice.WebGpuContext?.CancelPendingRead(NativeBuffer);
            if (NativeBuffer.Handle != 0) Api.wgpuBufferRelease(NativeBuffer);
            NativeBuffer = default;
            base.OnDestroyed(immediately);
        }
    }
}

#endif
