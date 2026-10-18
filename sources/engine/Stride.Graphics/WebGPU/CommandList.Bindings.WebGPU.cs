// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Collections.Generic;
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

namespace Stride.Graphics;

public unsafe partial class CommandList
{
    private const int BindingCacheLimit = 512;
    private readonly Dictionary<int, List<CachedBindings>> bindingCache = new();
    private int bindingCacheCount;
    private sealed record CachedBindings(WGPUBindGroupLayout Layout, WGPUBindGroupEntry[] Entries, WGPUBindGroup Group);

    private static int BindingHash(WGPUBindGroupLayout layout, ReadOnlySpan<WGPUBindGroupEntry> entries)
    {
        var hash = new HashCode();
        hash.Add(layout.Handle);
        foreach (ref readonly var entry in entries)
        {
            hash.Add(entry.binding); hash.Add(entry.buffer.Handle);
            hash.Add(entry.offset); hash.Add(entry.size);
            hash.Add(entry.textureView.Handle); hash.Add(entry.sampler.Handle);
        }
        return hash.ToHashCode();
    }

    private WGPUBindGroup FindBindings(WGPUBindGroupLayout layout, ReadOnlySpan<WGPUBindGroupEntry> entries)
    {
        if (!bindingCache.TryGetValue(BindingHash(layout, entries), out var bucket)) return default;
        foreach (var candidate in bucket)
        {
            if (candidate.Layout != layout || candidate.Entries.Length != entries.Length) continue;
            var equal = true;
            for (var i = 0; i < entries.Length; i++)
            {
                ref readonly var a = ref entries[i];
                ref readonly var b = ref candidate.Entries[i];
                if (a.binding != b.binding || a.buffer != b.buffer || a.offset != b.offset || a.size != b.size
                    || a.textureView != b.textureView || a.sampler != b.sampler)
                { equal = false; break; }
            }
            if (!equal) continue;
            // Each pass owns a temporary reference, whether created or reused.
            Api.wgpuBindGroupAddRef(candidate.Group);
            return candidate.Group;
        }
        return default;
    }

    private void CacheBindings(WGPUBindGroupLayout layout, ReadOnlySpan<WGPUBindGroupEntry> entries, WGPUBindGroup group)
    {
        // Bound retained resources even when a single recording has many unique
        // descriptor combinations. Recorded passes retain their own references.
        if (bindingCacheCount == BindingCacheLimit) ClearBindingCache();
        var hash = BindingHash(layout, entries);
        if (!bindingCache.TryGetValue(hash, out var bucket)) bindingCache.Add(hash, bucket = new());
        bucket.Add(new CachedBindings(layout, entries.ToArray(), group));
        Api.wgpuBindGroupAddRef(group);
        bindingCacheCount++;
    }

    private void ClearBindingCache()
    {
        foreach (var bucket in bindingCache.Values)
            foreach (var entry in bucket) Api.wgpuBindGroupRelease(entry.Group);
        bindingCache.Clear();
        bindingCacheCount = 0;
    }
}
#endif
