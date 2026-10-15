// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#pragma warning disable SA1405 // Debug.Assert must provide message text

using System.Diagnostics;
using Stride.Core.Extensions;

namespace Stride.Core.Streaming;

/// <summary>
/// Streamable resources content management service.
/// </summary>
public class ContentStreamingService : IDisposable
{
    private readonly Dictionary<string, ContentStorage> containers = [];

    /// <summary>
    /// The unused data chunks lifetime.
    /// </summary>
    public TimeSpan UnusedDataChunksLifetime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets the storage container.
    /// </summary>
    /// <param name="storageHeader">The storage header.</param>
    /// <returns>A service-owned container. Consumers retaining it must acquire their own reference.</returns>
    public ContentStorage GetStorage(ref ContentStorageHeader storageHeader)
    {
        ContentStorage? result;
        lock (containers)
        {
            if (!containers.TryGetValue(storageHeader.DataUrl, out result))
            {
                result = new ContentStorage(this);
                containers.Add(storageHeader.DataUrl, result);
                result.Init(ref storageHeader);
            }
            else if (result.PackageTime != storageHeader.PackageTime || result.GetHashCode() != storageHeader.HashCode)
            {
                result.Init(ref storageHeader);
            }
        }

        Debug.Assert(result != null && result.Url == storageHeader.DataUrl);
        return result;
    }

    /// <summary>
    /// Updates this service.
    /// </summary>
    public void Update()
    {
        lock (containers)
        {
            foreach (var e in containers)
                e.Value.ReleaseUnusedChunks();
        }
    }

    /// <summary>
    /// Performs resources disposing.
    /// </summary>
    public void Dispose()
    {
        lock (containers)
        {
            var owned = containers.Values.ToArray();
            containers.Clear();
            foreach (var storage in owned) storage.Dispose();
        }
    }

    internal void UnregisterStorage(ContentStorage storage)
    {
        lock (containers)
        {
            containers.Remove(storage.Url);
        }
    }
}
