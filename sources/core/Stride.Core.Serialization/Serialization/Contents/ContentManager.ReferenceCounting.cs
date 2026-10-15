// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Stride.Core.Serialization.Contents;

partial class ContentManager
{
    // Used internally for Garbage Collection
    // Allocate once and reuse collection for every GC
    private Stack<Reference> stack = new();
    private uint nextCollectIndex;

    /// <summary>
    /// Increments reference count of an <see cref="Reference"/>.
    /// </summary>
    /// <param name="reference">The reference.</param>
    /// <param name="publicReference">True if public reference.</param>
    internal void IncrementReference(Reference reference, bool publicReference)
    {
        if (reference.ReleasePending || reference.ObjectReleased)
            throw new InvalidOperationException($"Content [{reference.Url}] is pending final release.");
        if (publicReference)
        {
            reference.PublicReferenceCount++;
        }
        else
        {
            reference.PrivateReferenceCount++;
        }
    }

    /// <summary>
    /// Decrements reference count of an <see cref="Reference"/>.
    /// </summary>
    /// <param name="reference">The reference.</param>
    /// <param name="publicReference">True if public erefere</param>
    internal void DecrementReference(Reference reference, bool publicReference)
    {
        if (reference.ReleasePending)
        {
            if (reference.ReleaseInProgress || reference.ReleasePublicReference != publicReference)
                throw new InvalidOperationException("A pending final content release must resume through its original reference category.");
            CompleteFinalRelease(reference); return;
        }
        int referenceCount;
        if (publicReference)
        {
            if (reference.PublicReferenceCount <= 0)
                throw new InvalidOperationException("Cannot release an object that doesn't have active public references. Load/Unload pairs must match.");

            referenceCount = --reference.PublicReferenceCount + reference.PrivateReferenceCount;
        }
        else
        {
            if (reference.PrivateReferenceCount <= 0)
                throw new InvalidOperationException("Cannot release an object that doesn't have active private references. This is either due to non-matching Load/Unload pairs or an engine internal error.");
             
            referenceCount = --reference.PrivateReferenceCount + reference.PublicReferenceCount;
        }

        if (referenceCount == 0)
        {
            // Retain the actual final reference until object and dependency
            // disposal finish. A failed phase remains retryable via Unload.
            if (publicReference) reference.PublicReferenceCount++;
            else reference.PrivateReferenceCount++;
            reference.ReleasePending = true; reference.ReleasePublicReference = publicReference;
            CompleteFinalRelease(reference);
        }
        else if (publicReference && reference.PublicReferenceCount == 0)
        {
            // If there is no more public reference but object is still alive, let's kick a cycle GC
            CollectUnreferencedCycles();
        }
    }

    /// <summary>
    /// Releases an asset.
    /// </summary>
    /// <param name="reference">The reference.</param>
    private void ReleaseAsset(Reference reference)
    {
        ReleaseAssetObject(reference);
        RemoveReleasedAsset(reference);
    }

    private void CompleteFinalRelease(Reference reference)
    {
        reference.ReleaseInProgress = true;
        try
        {
            ReleaseAssetObject(reference);
            foreach (var childReference in reference.References.ToArray())
            {
                DecrementReference(childReference, false);
                reference.References.Remove(childReference);
            }
            RemoveReleasedAsset(reference);
            if (reference.ReleasePublicReference) reference.PublicReferenceCount--;
            else reference.PrivateReferenceCount--;
            reference.ReleasePending = false;
        }
        finally { reference.ReleaseInProgress = false; }
    }

    private static void ReleaseAssetObject(Reference reference)
    {
        if (reference.ObjectReleased) return;
        if (reference.Object is IReferencable referencable)
        {
            referencable.Release();
        }
        else
        {
            if (reference.Object is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        reference.ObjectReleased = true;
    }

    private void RemoveReleasedAsset(Reference reference)
    {
        // Do the object-key lookup before changing URL/linked-list metadata;
        // failures preserve the pending owner and its actual reference category.
        LoadedAssetReferences.Remove(reference.Object);
        // Remove Reference from loaded assets.
        var oldPrev = reference.Prev;
        var oldNext = reference.Next;
        if (oldPrev != null)
            oldPrev.Next = oldNext;
        if (oldNext != null)
            oldNext.Prev = oldPrev;

        if (oldPrev == null)
        {
            if (oldNext == null)
                LoadedAssetUrls.Remove(reference.Url);
            else
                LoadedAssetUrls[reference.Url] = oldNext;
        }
        reference.Object = null;
    }

    internal void CollectUnreferencedCycles()
    {
        // A failed release leaves its real registry entry for retry. Rebuild
        // reachability from those entries rather than replay a partial worklist.
        stack.Clear();
        // Push everything on the stack
        var currentCollectIndex = nextCollectIndex++;
        foreach (var asset in LoadedAssetUrls)
        {
            var currentAsset = asset.Value;
            do
            {
                if (currentAsset.PublicReferenceCount > 0 || currentAsset.ReleasePending)
                    stack.Push(currentAsset);
                currentAsset = currentAsset.Next;
            }
            while (currentAsset != null);
        }

        // Until stack is empty, collect references and push them on the stack
        while (stack.Count > 0)
        {
            var v = stack.Pop();

            // We use CollectIndex to know if object has already been processed during current collection
            var collectIndex = v.CollectIndex;
            if (collectIndex != currentCollectIndex)
            {
                v.CollectIndex = currentCollectIndex;
                foreach (var reference in v.References)
                {
                    if (reference.CollectIndex != currentCollectIndex)
                        stack.Push(reference);
                }
            }
        }

        // Collect objects that are not referenceable.
        // Reuse stack
        // TODO: Use collections where you can iterate and remove at the same time?
        foreach (var asset in LoadedAssetUrls)
        {
            var currentAsset = asset.Value;
            do
            {
                if (currentAsset.CollectIndex != currentCollectIndex)
                {
                    stack.Push(currentAsset);
                }
                currentAsset = currentAsset.Next;
            }
            while (currentAsset != null);
        }

        // Release those objects
        // Note: order of release might be unexpected (i.e. if A ref B, B might be released before A)
        // We don't really have a choice if there is cycle anyway, but still user could have reference himself to prevent or enforce this order if it's really important.
        foreach (var assetReference in stack)
        {
            ReleaseAsset(assetReference);
        }
    }
}
