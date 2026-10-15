// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;
using Stride.Core.Extensions;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Core.Storage;
using Stride.Core.Yaml.Serialization;

namespace Stride.Core.Assets.Tracking;

public static class SourceHashesHelper
{
    public const string MemberName = "~SourceHashes";

    private static readonly ShadowObjectPropertyKey AbsoluteSourceHashesKey = new(new object(), true);
    private static readonly ShadowObjectPropertyKey RelativeSourceHashesKey = new(new object(), true);
    private static readonly object LockObj = new();

    public static void UpdateHashes(Asset asset, IReadOnlyDictionary<UFile, ObjectId> newHashes)
    {
        lock (LockObj)
        {
            var hashes = GetOrCreate(asset, AbsoluteSourceHashesKey);
            ReplaceContents(hashes, newHashes, null, null, true);
        }
    }

    public static Dictionary<UFile, ObjectId> GetAllHashes(Asset asset)
    {
        lock (LockObj)
        {
            var hashes = TryGet(asset, AbsoluteSourceHashesKey);
            return hashes == null ? [] : new Dictionary<UFile, ObjectId>(hashes);
        }
    }

    /// <summary>Converts import hash paths and their serialized keys for an asset transfer.</summary>
    public static void ConvertPaths(Asset asset, UDirectory assetFolder, UPathType pathType)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assetFolder);
        lock (LockObj)
        {
            UpdateUPaths(asset, assetFolder, pathType);
            // A copied asset has no package save pass to normalize the serialized
            // dictionary. Absolute transfer keys preserve the original source identity.
            if (pathType == UPathType.Absolute && TryGet(asset, AbsoluteSourceHashesKey) is { } hashes)
                CopyContents(hashes, GetOrCreate(asset, RelativeSourceHashesKey), static path => path);
        }
    }

    /// <summary>Preserves imported source identities when an authored asset changes folders.</summary>
    public static void RebaseRelativePaths(Asset asset, UDirectory previousFolder, UDirectory nextFolder)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(previousFolder);
        ArgumentNullException.ThrowIfNull(nextFolder);
        lock (LockObj)
        {
            UpdateUPaths(asset, previousFolder, UPathType.Absolute);
            UpdateUPaths(asset, nextFolder, UPathType.Relative);
        }
    }

    private static Dictionary<UFile, ObjectId>? TryGet(Asset asset, ShadowObjectPropertyKey key)
    {
        var shadow = ShadowObject.GetOrCreate(asset);
        if (shadow.TryGetValue(key, out var obj))
        {
            return (Dictionary<UFile, ObjectId>)obj;
        }
        return null;
    }

    private static Dictionary<UFile, ObjectId> GetOrCreate(Asset asset, ShadowObjectPropertyKey key)
    {
        var shadow = ShadowObject.GetOrCreate(asset);
        if (shadow.TryGetValue(key, out var obj))
        {
            return (Dictionary<UFile, ObjectId>)obj;
        }
        var hashes = new Dictionary<UFile, ObjectId>();
        shadow[key] = hashes;
        return hashes;
    }

    private static void SetDictionary(Asset asset, ShadowObjectPropertyKey key, Dictionary<UFile, ObjectId> dictionary)
    {
        var shadow = ShadowObject.GetOrCreate(asset);
        shadow[key] = dictionary;
    }

    internal static void AddSourceHashesMember(ObjectDescriptor objectDescriptor, List<IMemberDescriptor> memberDescriptors)
    {
        var type = objectDescriptor.Type;
        if (!typeof(Asset).IsAssignableFrom(type))
            return;

        memberDescriptors.Add(SourceHashesDynamicMember.Default);
    }

    internal static void UpdateUPaths(Asset asset, UDirectory assetFolder, UPathType convertUPathTo)
    {
        switch (convertUPathTo)
        {
            case UPathType.Absolute:
                ConvertUPaths(asset, RelativeSourceHashesKey, AbsoluteSourceHashesKey, x => UPath.Combine(assetFolder, x));
                break;
            case UPathType.Relative:
                ConvertUPaths(asset, AbsoluteSourceHashesKey, RelativeSourceHashesKey, x => x.MakeRelative(assetFolder));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(convertUPathTo), convertUPathTo, null);
        }
    }

    private static void ConvertUPaths(Asset asset, ShadowObjectPropertyKey from, ShadowObjectPropertyKey to, Func<UFile, UFile> converter)
    {
        var fromHashes = TryGet(asset, from);
        if (fromHashes != null)
        {
            CopyContents(fromHashes, GetOrCreate(asset, to), converter);
        }
    }

    private static void CopyContents(Dictionary<UFile, ObjectId> source, Dictionary<UFile, ObjectId> target, Func<UFile, UFile> converter)
    {
        var values = source.ToDictionary(pair => converter(pair.Key), pair => pair.Value);
        Dictionary<UFile, ItemId>? mappedIds = null; ItemId[]? deleted = null;
        if (CollectionItemIdHelper.TryGetCollectionItemIds(source, out var ids))
        {
            mappedIds = ids.Where(pair => pair.Key is UFile path && source.ContainsKey(path)).ToDictionary(pair => converter((UFile)pair.Key), pair => pair.Value);
            deleted = ids.DeletedItems.ToArray();
        }
        ReplaceContents(target, values, mappedIds, deleted, true);
    }

    private static void ReplaceContents(Dictionary<UFile, ObjectId> target, IReadOnlyDictionary<UFile, ObjectId> values,
        IReadOnlyDictionary<UFile, ItemId>? sourceIds, IEnumerable<ItemId>? deleted, bool retainTargetIds)
    {
        var snapshot = values.ToArray(); // Alias-safe, including source == target.
        var hadIds = CollectionItemIdHelper.TryGetCollectionItemIds(target, out var currentIds);
        Dictionary<UFile, ItemId> retained = hadIds && retainTargetIds
            ? currentIds!.Where(pair => pair.Key is UFile).ToDictionary(pair => (UFile)pair.Key, pair => pair.Value) : [];
        ItemId[] tombstones = hadIds && retainTargetIds ? currentIds!.DeletedItems.ToArray() : [];
        target.Clear();
        foreach (var pair in snapshot) target.Add(pair.Key, pair.Value);
        if (!hadIds && sourceIds == null && deleted == null) return;
        var ids = CollectionItemIdHelper.GetCollectionItemIds(target); ids.Clear();
        foreach (var id in tombstones.Concat(deleted ?? [])) ids.MarkAsDeleted(id);
        foreach (var pair in snapshot)
        {
            if (sourceIds != null && sourceIds.TryGetValue(pair.Key, out var sourceId)) ids.Set(pair.Key, sourceId);
            else if (retained.TryGetValue(pair.Key, out var targetId)) ids.Set(pair.Key, targetId);
        }
        var activeIds = ids.Select(pair => pair.Value).ToHashSet();
        foreach (var pair in retained)
            if (!target.ContainsKey(pair.Key) && !activeIds.Contains(pair.Value)) ids.MarkAsDeleted(pair.Value);
        ids.Validate(false);
    }

    private static Dictionary<UFile, ObjectId>? CloneDictionary(Dictionary<UFile, ObjectId>? source, bool copyItemIds)
    {
        if (source == null) return null;
        var copy = new Dictionary<UFile, ObjectId>(source);
        if (copyItemIds && CollectionItemIdHelper.TryGetCollectionItemIds(source, out var ids))
            ids.CloneInto(CollectionItemIdHelper.GetCollectionItemIds(copy), null);
        return copy;
    }

    internal static void CopyForClone(Asset source, Asset target, bool copyItemIds)
    {
        lock (LockObj)
        {
            foreach (var key in new[] { AbsoluteSourceHashesKey, RelativeSourceHashesKey })
                if (CloneDictionary(TryGet(source, key), copyItemIds) is { } hashes) SetDictionary(target, key, hashes);
        }
    }

    public sealed class Snapshot
    {
        internal readonly Dictionary<UFile, ObjectId>? Absolute, Relative;
        internal Snapshot(Dictionary<UFile, ObjectId>? absolute, Dictionary<UFile, ObjectId>? relative)
        { Absolute = CloneDictionary(absolute, true); Relative = CloneDictionary(relative, true); }
    }
    public static Snapshot CaptureSnapshot(Asset asset)
    {
        lock (LockObj) return new(TryGet(asset, AbsoluteSourceHashesKey), TryGet(asset, RelativeSourceHashesKey));
    }
    public static void RestoreSnapshot(Asset asset, Snapshot snapshot)
    {
        lock (LockObj)
        {
            Restore(AbsoluteSourceHashesKey, snapshot.Absolute);
            Restore(RelativeSourceHashesKey, snapshot.Relative);
            void Restore(ShadowObjectPropertyKey key, Dictionary<UFile, ObjectId>? source)
            {
                var target = GetOrCreate(asset, key);
                var ids = source != null && CollectionItemIdHelper.TryGetCollectionItemIds(source, out var sourceItemIds) ? sourceItemIds : null;
                ReplaceContents(target, source ?? [], ids?.Where(pair => pair.Key is UFile).ToDictionary(pair => (UFile)pair.Key, pair => pair.Value),
                    ids?.DeletedItems, false);
            }
        }
    }

    internal class SourceHashesDynamicMember : DynamicMemberDescriptorBase
    {
        public const int DefaultOrder = int.MaxValue;

        public static readonly SourceHashesDynamicMember Default = new()
        {
            ShouldSerialize = static (x, parentTypeMemberDesc) =>
            {
                return x is Asset asset && (TryGet(asset, RelativeSourceHashesKey)?.Count > 0
                    || TryGet(asset, AbsoluteSourceHashesKey)?.Count > 0);
            }
        };

        static SourceHashesDynamicMember()
        {
            // Safety check, we need to have the asset id deserialized before the source hashes
            var idOrder = typeof(Asset).GetProperty(nameof(Asset.Id))!.GetCustomAttribute<DataMemberAttribute>()!.Order;
            if (idOrder >= DefaultOrder)
                throw new InvalidOperationException("The order of the Asset.Id property must be lower than the order of the SourceHashes property.");
        }

        public SourceHashesDynamicMember() : base(MemberName, typeof(Dictionary<UFile, ObjectId>), typeof(Asset))
        {
            Order = DefaultOrder;
        }

        public override bool HasSet => true;

        public override object? Get(object thisObject)
        {
            var asset = (Asset)thisObject;
            // Id can be empty when the asset is contained in a base.
            if (asset.Id == AssetId.Empty)
                return null;

            lock (LockObj)
            {
                var value = TryGet(asset, RelativeSourceHashesKey);
                if (value == null || value.Count == 0)
                    return null;

                return value;
            }
        }

        public override void Set(object thisObject, object value)
        {
            if (value == null)
                return;

            var sourceHashes = (Dictionary<UFile, ObjectId>)value;
            var asset = (Asset)thisObject;
            // Id can be empty when the asset is contained in a base.
            if (asset.Id == AssetId.Empty)
                return;

            lock (LockObj)
            {
                SetDictionary(asset, RelativeSourceHashesKey, sourceHashes);
            }
        }
    }
}
