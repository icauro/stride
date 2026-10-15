// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Core.Streaming;
using Xunit;

namespace Stride.Core.Tests;

public class TestContentStreaming
{
    private static ContentStorageHeader Header()
    {
        var time = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var hash = unchecked((((int)time.Ticks * 397) ^ 1) * 397) ^ 16;
        return new ContentStorageHeader { DataUrl = "/test/texture_Data", PackageTime = time, HashCode = hash,
            Chunks = [new ContentStorageHeader.ChunkEntry { Location = 0, Size = 16 }] };
    }

    [Fact]
    public void RepeatedTextureLoadsPreserveSharedChunks()
    {
        using var service = new ContentStreamingService();
        var header = Header();
        var first = service.GetStorage(ref header);
        var chunk = first.GetChunk(0);
        var second = service.GetStorage(ref header);
        Assert.Same(first, second);
        Assert.Same(chunk, second.GetChunk(0));
    }

    [Fact]
    public void ServiceReleasesItsOwnershipWithoutInvalidatingLiveConsumers()
    {
        var service = new ContentStreamingService();
        var header = Header();
        var storage = service.GetStorage(ref header);
        var reference = (IReferencable)storage;
        reference.AddReference();
        service.Dispose();
        Assert.False(storage.IsDisposed);
        Assert.Equal(1, reference.ReferenceCount);
        Assert.Equal(16, storage.GetChunk(0).Size);
        reference.Release();
        Assert.True(storage.IsDisposed);
        service.Dispose();
    }

    [Fact]
    public void ServiceDisposesUnusedStorage()
    {
        var service = new ContentStreamingService();
        var header = Header();
        var storage = service.GetStorage(ref header);
        service.Dispose();
        Assert.True(storage.IsDisposed);
    }
}
