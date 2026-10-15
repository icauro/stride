// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;

namespace Stride.Core.Assets.Tests;

[DataContract, ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<TestContent>), Profile = "Content")]
public class TestContent;

[DataContract("TestAssetClonerContent")]
public class TestAssetClonerContent
{
    public TestContent? Content;
}

[DataContract("TestAssetClonerObject")]
public class TestAssetClonerObject
{
    public string? Name { get; set; }

    public TestAssetClonerObject? SubObject { get; set; }

    public TestObjectReference? ObjectWithAttachedReference { get; set; }
}

[DataContract("TestObjectWithCollection")]
public class TestObjectWithCollection
{
    public string? Name { get; set; }

    public List<string> Items { get; set; } = [];
}

[DataContract]
[ContentSerializer(typeof(DataContentSerializer<TestObjectReference>))]
[ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<TestObjectReference>), Profile = "Asset")]
public class TestObjectReference;

public class TestAssetCloner
{
    [Fact]
    public void TestUnloadableContentProxyClonePreservesUnresolvedData()
    {
        var events = new List<ParsingEvent> { new Scalar("unresolved-font-data") };
        var proxy = (TestContent)UnloadableObjectInstantiator.CreateUnloadableObject(typeof(TestContent), "UnknownFont", "MissingAssembly", "Unknown font content", events);
        Assert.IsType<ReferenceSerializer<TestContent>>(AssetCloner.ClonerSelector.GetSerializer(typeof(TestContent)));
        Assert.Null(AttachedReferenceManager.GetAttachedReference(proxy));
        var clone = AssetCloner.Clone(new TestAssetClonerContent { Content = proxy });
        Assert.NotSame(proxy, clone.Content);
        var unresolved = Assert.IsAssignableFrom<IUnloadable>(clone.Content);
        Assert.Equal("UnknownFont", unresolved.TypeName);
        Assert.Equal("MissingAssembly", unresolved.AssemblyName);
        Assert.Equal("Unknown font content", unresolved.Error);
        Assert.Equal(events, unresolved.ParsingEvents);
        Assert.Null(AttachedReferenceManager.GetAttachedReference(clone.Content));

        var actualReference = AttachedReferenceManager.GetOrCreateAttachedReference(proxy);
        actualReference.Id = AssetId.New(); actualReference.Url = "Fonts/Existing"; actualReference.IsProxy = true;
        clone = AssetCloner.Clone(new TestAssetClonerContent { Content = proxy });
        var clonedReference = AttachedReferenceManager.GetAttachedReference(clone.Content);
        Assert.NotSame(actualReference, clonedReference);
        Assert.Equal(actualReference.Id, clonedReference!.Id);
        Assert.Equal(actualReference.Url, clonedReference.Url);
        Assert.True(clonedReference.IsProxy);
        Assert.Equal(events, Assert.IsAssignableFrom<IUnloadable>(clone.Content).ParsingEvents);
    }

    [Fact]
    public void TestUnloadableOrdinaryContractCloneRetainsKnownBaseMembers()
    {
        var proxy = (TestAssetClonerObject)UnloadableObjectInstantiator.CreateUnloadableObject(typeof(TestAssetClonerObject), "UnknownObject", "MissingAssembly", "Missing type", []);
        proxy.Name = "Known member";
        proxy.SubObject = new TestAssetClonerObject { Name = "Nested member" };
        var clone = AssetCloner.Clone(proxy);
        Assert.NotSame(proxy, clone);
        Assert.Equal(proxy.Name, clone.Name);
        Assert.NotSame(proxy.SubObject, clone.SubObject);
        Assert.Equal(proxy.SubObject.Name, clone.SubObject!.Name);
        Assert.Equal("UnknownObject", Assert.IsAssignableFrom<IUnloadable>(clone).TypeName);
    }

    [Fact]
    public void TestAssetClonerContent()
    {
        var obj1 = new TestAssetClonerContent { Content = new TestContent() };
        var obj2 = AssetCloner.Clone(obj1, AssetClonerFlags.KeepReferences);
        Assert.Equal(obj1.Content, obj2.Content);
    }

    [Fact]
    public void TestHash()
    {
        var obj1 = new TestAssetClonerObject
        {
            Name = "Test1",
            SubObject = new TestAssetClonerObject() { Name = "Test2" },
            ObjectWithAttachedReference = new TestObjectReference()
        };

        // Create a fake reference to make sure that the attached reference will not be serialized
        var attachedReference = AttachedReferenceManager.GetOrCreateAttachedReference(obj1.ObjectWithAttachedReference);
        attachedReference.Url = "just_for_test";
        attachedReference.Id = AssetId.New();

        var obj2 = AssetCloner.Clone(obj1);

        var hash1 = AssetHash.Compute(obj1);
        var hash2 = AssetHash.Compute(obj2);
        Assert.Equal(hash1, hash2);

        obj1.Name = "Yes";
        var hash11 = AssetHash.Compute(obj1);
        Assert.NotEqual(hash11, hash2);
        obj1.Name = "Test1";

        var hash12 = AssetHash.Compute(obj1);
        Assert.Equal(hash12, hash2);

        obj2 = AssetCloner.Clone(obj1);

        var hash1WithOverrides = AssetHash.Compute(obj1);
        var hash2WithOverrides = AssetHash.Compute(obj2);
        Assert.Equal(hash1WithOverrides, hash2WithOverrides);
    }

    [Fact]
    public void TestCloneCollectionIds()
    {
        var obj = new TestObjectWithCollection { Name = "Test", Items = { "aaa", "bbb" } };
        var ids = CollectionItemIdHelper.GetCollectionItemIds(obj.Items);
        ids.Add(0, new ItemId());
        ids.Add(1, new ItemId());
        ids.MarkAsDeleted(new ItemId());
        var clone = AssetCloner.Clone(obj);
        var idsExist = CollectionItemIdHelper.TryGetCollectionItemIds(clone.Items, out var cloneIds);
        Assert.True(idsExist);
        Assert.Equal(ids.KeyCount, cloneIds.KeyCount);
        Assert.Equal(ids.DeletedCount, cloneIds.DeletedCount);
        Assert.Equal(ids[0], cloneIds[0]);
        Assert.Equal(ids[1], cloneIds[1]);
        Assert.Equal(ids.DeletedItems.Single(), cloneIds.DeletedItems.Single());

        clone = AssetCloner.Clone(obj, AssetClonerFlags.RemoveItemIds);
        idsExist = CollectionItemIdHelper.TryGetCollectionItemIds(clone.Items, out _);
        Assert.False(idsExist);
    }

    [Fact]
    public void TestDiscardCollectionIds()
    {
        var obj = new TestObjectWithCollection { Name = "Test", Items = { "aaa", "bbb" } };
        var ids = CollectionItemIdHelper.GetCollectionItemIds(obj.Items);
        ids.Add(0, new ItemId());
        ids.Add(1, new ItemId());
        ids.MarkAsDeleted(new ItemId());
        var clone = AssetCloner.Clone(obj);
        var idsExist = CollectionItemIdHelper.TryGetCollectionItemIds(clone.Items, out var cloneIds);
        Assert.True(idsExist);
        Assert.Equal(ids.KeyCount, cloneIds.KeyCount);
        Assert.Equal(ids.DeletedCount, cloneIds.DeletedCount);
        Assert.Equal(ids[0], cloneIds[0]);
        Assert.Equal(ids[1], cloneIds[1]);
        Assert.Equal(ids.DeletedItems.Single(), cloneIds.DeletedItems.Single());

        clone = AssetCloner.Clone(obj, AssetClonerFlags.RemoveItemIds);
        idsExist = CollectionItemIdHelper.TryGetCollectionItemIds(clone.Items, out _);
        Assert.False(idsExist);
    }
}
