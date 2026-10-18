// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;
namespace Stride.Graphics;

public unsafe partial class QueryPool
{
    private static long nextMapToken;
    private static readonly ConcurrentDictionary<nint, MapCompletion> pendingMaps = new();
    private readonly object queryGate = new();
    private WGPUQuerySet nativeQueries;
    private WGPUBuffer resolveBuffer, readbackBuffer;
    private bool[] written;
    private CommandList recording;
    private readonly List<int> recordingIndices = new();
    private int pendingSubmissions, generation;
    private bool cancelled;
    private long[] results;
    private nint mapToken;
    private MapCompletion mapping;
    private sealed class MapCompletion { public int Status; }

    private unsafe partial void Recreate()
    {
        if (!GraphicsDevice.WebGpuContext.HasEncoderTimestamps)
            throw new NotSupportedException("WebGPU timestamps require TimestampQuery and native TimestampQueryInsideEncoders.");
        if (QueryType != QueryType.Timestamp) throw new NotSupportedException("Only timestamp queries are supported.");
        if (QueryCount <= 0 || QueryCount > 8192) throw new ArgumentOutOfRangeException(nameof(QueryCount));
        written = new bool[QueryCount];
        var queries = WGPUQuerySetDescriptor.CreateDefault();
        queries.type = WGPUQueryType.Timestamp; queries.count = (uint)QueryCount;
        try
        {
            nativeQueries = Api.wgpuDeviceCreateQuerySet(GraphicsDevice.WebGpuContext.Device, &queries);
            var buffer = WGPUBufferDescriptor.CreateDefault();
            buffer.size = (ulong)QueryCount * 256; buffer.usage = WGPUBufferUsage.QueryResolve | WGPUBufferUsage.CopySrc;
            resolveBuffer = Api.wgpuDeviceCreateBuffer(GraphicsDevice.WebGpuContext.Device, &buffer);
            buffer.size = (ulong)QueryCount * 8; buffer.usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst;
            readbackBuffer = Api.wgpuDeviceCreateBuffer(GraphicsDevice.WebGpuContext.Device, &buffer);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
        }
        catch { ReleaseNative(); throw; }
    }

    internal void RecordTimestamp(CommandList owner, WGPUCommandEncoder encoder, int index)
    {
        lock (queryGate)
        {
            ObjectDisposedException.ThrowIf(nativeQueries.Handle == 0, this);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, QueryCount);
            if (mapping != null) throw new InvalidOperationException("Timestamp results are still being mapped.");
            if (results != null || (cancelled && pendingSubmissions == 0 && recording == null))
            { Array.Clear(written); results = null; cancelled = false; generation++; }
            if (cancelled) throw new InvalidOperationException("A timestamp recording was cancelled; outstanding submissions must retire before reuse.");
            if (recording != null && !ReferenceEquals(recording, owner))
                throw new InvalidOperationException("The query pool is already being recorded by another command list.");
            if (written[index]) throw new InvalidOperationException("This timestamp index has already been written in the current result generation.");
            Api.wgpuCommandEncoderWriteTimestamp(encoder, nativeQueries, (uint)index);
            // Keep aligned results until a separate command buffer copies them.
            // Same-encoder resolve/copy returns stale data on affected Vulkan
            // drivers: https://github.com/gfx-rs/wgpu/issues/6406.
            Api.wgpuCommandEncoderResolveQuerySet(encoder, nativeQueries, (uint)index, 1, resolveBuffer, (ulong)index * 256);
            GraphicsDevice.WebGpuContext.ThrowIfFailed();
            written[index] = true; recording = owner; recordingIndices.Add(index);
        }
    }

    internal void EncodeReadback(CommandList owner, WGPUCommandEncoder encoder)
    {
        lock (queryGate)
        {
            if (!ReferenceEquals(recording, owner) || nativeQueries.Handle == 0) return;
            foreach (var index in recordingIndices)
                Api.wgpuCommandEncoderCopyBufferToBuffer(encoder, resolveBuffer, (ulong)index * 256, readbackBuffer, (ulong)index * 8, 8);
        }
    }

    internal Action<bool> SealRecording(CommandList owner)
    {
        lock (queryGate)
        {
            if (!ReferenceEquals(recording, owner)) return _ => { };
            recording = null; recordingIndices.Clear(); pendingSubmissions++;
            var currentGeneration = generation;
            return submitted =>
            {
                lock (queryGate)
                {
                    if (generation != currentGeneration) return;
                    pendingSubmissions--; cancelled |= !submitted;
                }
            };
        }
    }

    internal void CancelRecording(CommandList owner)
    {
        lock (queryGate)
            if (ReferenceEquals(recording, owner)) { recording = null; recordingIndices.Clear(); cancelled = true; }
    }

    public bool TryGetData(long[] dataArray)
    {
        ArgumentNullException.ThrowIfNull(dataArray);
        if (dataArray.Length != QueryCount) throw new ArgumentException("The result array must match QueryCount.", nameof(dataArray));
        lock (queryGate)
        {
            ObjectDisposedException.ThrowIf(nativeQueries.Handle == 0, this);
            if (cancelled) throw new InvalidOperationException("A command list containing timestamp queries was not submitted.");
            if (results != null) { results.CopyTo(dataArray, 0); return true; }
            if (recording != null || pendingSubmissions != 0 || Array.IndexOf(written, false) >= 0) return false;
            if (mapping == null)
            {
                mapping = new MapCompletion();
                mapToken = checked((nint)Interlocked.Increment(ref nextMapToken));
                pendingMaps[mapToken] = mapping;
                var callback = WGPUBufferMapCallbackInfo.CreateDefault();
                callback.mode = WGPUCallbackMode.AllowProcessEvents;
                callback.callback = &OnQueryMap; callback.userdata1 = (void*)mapToken;
                Api.wgpuBufferMapAsync(readbackBuffer, WGPUMapMode.Read, 0, (nuint)QueryCount * 8, callback);
            }
            GraphicsDevice.WebGpuContext.ProcessEvents();
            var status = Volatile.Read(ref mapping.Status);
            if (status == 0) return false;
            try
            {
                if ((WGPUMapAsyncStatus)status != WGPUMapAsyncStatus.Success)
                    throw new InvalidOperationException($"WebGPU timestamp readback failed ({(WGPUMapAsyncStatus)status}).");
                var pointer = Api.wgpuBufferGetConstMappedRange(readbackBuffer, 0, (nuint)QueryCount * 8);
                if (pointer == null) throw new InvalidOperationException("Timestamp mapping returned a null range.");
                results = new ReadOnlySpan<long>(pointer, QueryCount).ToArray();
                results.CopyTo(dataArray, 0);
                return true;
            }
            finally { CancelMap(); }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnQueryMap(WGPUMapAsyncStatus status, WGPUStringView message, void* user1, void* user2)
    {
        if (pendingMaps.TryRemove((nint)user1, out var completion)) Volatile.Write(ref completion.Status, (int)status);
    }

    private void CancelMap()
    {
        if (mapping == null) return;
        pendingMaps.TryRemove(mapToken, out _);
        Api.wgpuBufferUnmap(readbackBuffer);
        mapping = null; mapToken = 0;
    }

    private void ReleaseNative()
    {
        CancelMap();
        if (readbackBuffer.Handle != 0) Api.wgpuBufferRelease(readbackBuffer);
        if (resolveBuffer.Handle != 0) Api.wgpuBufferRelease(resolveBuffer);
        if (nativeQueries.Handle != 0) Api.wgpuQuerySetRelease(nativeQueries);
        readbackBuffer = default; resolveBuffer = default; nativeQueries = default;
    }

    protected internal override void OnDestroyed(bool immediately = false)
    {
        lock (queryGate) ReleaseNative();
        base.OnDestroyed(immediately);
    }
}
#endif
