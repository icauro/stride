using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed record AggregateFlagsParityFixture(EntityHierarchyDocument Owner, Entity First, Entity Second,
    EntityComponent FirstComponent, EntityComponent SecondComponent, long InitialHistory, Guid[] InitialSelection,
    Guid Primary, ulong Counter, ulong SecondCounter, Guid[] InitialTransactions, byte[] NativeSavedBaseline,
    HashSet<Guid> OwnedTransactions);

internal sealed partial class EditorGame
{
    internal AggregateFlagsParityFixture PrepareAggregateFlagsParityVerification()
    {
        if (!args.Contains("--ui-test") || document == null || sessionHistory == null || document.IsDirty
            || !Path.GetFullPath(document.Path).StartsWith(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Aggregate flags require a clean private canonical test scene.");
        var first = document.Asset.Hierarchy.Parts.Values.Select(part => part.Entity).Single(entity => entity.Name == "Sphere");
        var second = document.Asset.Hierarchy.Parts.Values.Select(part => part.Entity).Single(entity => entity.Name == "Imported cube");
        var script = first.Components.Single(component => component.GetType().Name == "InspectorTypes");
        // Normalize the disposable scene through the native save route before
        // taking the exact durable restoration boundary.
        document.Save();
        var baseline = File.ReadAllBytes(document.Path);
        var initialTransactions = document.History.Entries.Select(entry => entry.Id).ToArray();
        var initial = document.History.Position;
        var selection = document.SelectedIds.ToArray(); var primary = document.Selected?.Id ?? Guid.Empty;
        SelectCompositeHierarchyEntity(document, second, false);
        if (second.Components.Any(component => component.GetType() == script.GetType()))
            throw new InvalidOperationException("The private fixture already has a second InspectorTypes owner.");
        var added = document.AddComponent(second, script.GetType());
        var fixture = new AggregateFlagsParityFixture(document, first, second, script, added, initial, selection, primary,
            (ulong)script.GetType().GetProperty("Counter64")!.GetValue(script)!,
            (ulong)added.GetType().GetProperty("Counter64")!.GetValue(added)!, initialTransactions, baseline, []);
        try
        {
            var fields = AggregateFlagsFields(fixture);
            if (fields.Any(field => field.Node is not IAssetMemberNode { BaseNode: null }))
                throw new InvalidOperationException("This case requires two independently owned native flags nodes.");
            SelectCompositeHierarchyEntity(document, first, false);
            document.UpdateComponent(fields[0], Enum.ToObject(fields[0].Type, 1U)); document.EndComponentEdit();
            SelectCompositeHierarchyEntity(document, second, false);
            document.UpdateComponent(fields[1], Enum.ToObject(fields[1].Type, 0x80000000U)); document.EndComponentEdit();
            document.Save();
            foreach (var entry in document.History.Entries.Where(entry => !initialTransactions.Contains(entry.Id)))
                fixture.OwnedTransactions.Add(entry.Id);
            SelectCompositeHierarchyEntity(document, first, false);
            return fixture;
        }
        catch
        {
            // Preparation is synchronous on the editor actor; no UI command can
            // interleave with these native fixture operations.
            foreach (var entry in document.History.Entries.Where(entry => !initialTransactions.Contains(entry.Id)))
                fixture.OwnedTransactions.Add(entry.Id);
            RestoreAggregateFlagsParityVerification(fixture);
            throw;
        }
    }

    internal void CertifyAggregateFlagsParityTransaction(AggregateFlagsParityFixture fixture, long previousPosition, bool changed)
    {
        var entries = fixture.Owner.History.Entries;
        if (!entries.Take(fixture.InitialTransactions.Length).Select(entry => entry.Id).SequenceEqual(fixture.InitialTransactions))
            throw new InvalidOperationException("The original shared transaction prefix changed.");
        var added = entries.Where(entry => !fixture.InitialTransactions.Contains(entry.Id)
            && !fixture.OwnedTransactions.Contains(entry.Id)).ToArray();
        if (fixture.Owner.History.Position != previousPosition + (changed ? 1 : 0)
            || added.Length != (changed ? 1 : 0) || added.Any(entry => entry.Name != "Edit Mask"))
            throw new InvalidOperationException("The actual flags gesture did not exclusively own its expected transaction.");
        foreach (var entry in added) fixture.OwnedTransactions.Add(entry.Id);
    }

    internal void SelectAggregateFlagsParityVerification(AggregateFlagsParityFixture fixture)
    {
        SelectCompositeHierarchyEntity(fixture.Owner, fixture.First, false);
        SelectCompositeHierarchyEntity(fixture.Owner, fixture.Second, true);
    }

    internal ComponentField AggregateFlagsNumericField(AggregateFlagsParityFixture fixture)
        => fixture.Owner.GetComponentFields(fixture.FirstComponent).Single(field => field.Name == "Counter64");

    private ComponentField[] AggregateFlagsFields(AggregateFlagsParityFixture fixture)
        => new[] { fixture.FirstComponent, fixture.SecondComponent }.Select(component =>
            fixture.Owner.GetComponentFields(component).Single(field => field.Name == "Mask")).ToArray();

    internal string AggregateFlagsControl(AggregateFlagsParityFixture fixture)
    {
        var property = ReadAggregateProperties().Single(property => property.First.Name == "Mask"
            && property.Fields.Count == 2 && property.Fields.Any(field => field.Component.Id == fixture.FirstComponent.Id));
        return "aggregate/" + property.Key + "/flags";
    }

    internal bool VerifyAggregateFlagsParityValues(AggregateFlagsParityFixture fixture, uint first, uint second, bool persisted = false)
    {
        var fields = AggregateFlagsFields(fixture); var expected = new[] { first, second };
        for (var index = 0; index < fields.Length; index++)
        {
            if (Convert.ToUInt32(fields[index].Node.Retrieve()) != expected[index]
                || ReadRuntimeComponentValueVerification(fields[index]) is not { } runtime
                || Convert.ToUInt32(runtime) != expected[index]) return false;
        }
        if (!persisted) return true;
        var disk = AssetFileSerializer.Load<SceneAsset>(fixture.Owner.Path).Asset;
        var entities = new[] { fixture.First, fixture.Second };
        var components = new[] { fixture.FirstComponent, fixture.SecondComponent };
        for (var index = 0; index < entities.Length; index++)
        {
            var component = disk.Hierarchy.Parts[entities[index].Id].Entity.Components.Single(component => component.Id == components[index].Id);
            if (Convert.ToUInt32(component.GetType().GetProperty("Mask")!.GetValue(component)) != expected[index]) return false;
        }
        var firstScript = disk.Hierarchy.Parts[fixture.First.Id].Entity.Components.Single(component => component.Id == fixture.FirstComponent.Id);
        var secondScript = disk.Hierarchy.Parts[fixture.Second.Id].Entity.Components.Single(component => component.Id == fixture.SecondComponent.Id);
        return (ulong)firstScript.GetType().GetProperty("Counter64")!.GetValue(firstScript)! == fixture.Counter
            && (ulong)secondScript.GetType().GetProperty("Counter64")!.GetValue(secondScript)! == fixture.SecondCounter;
    }

    internal void RestoreAggregateFlagsParityVerification(AggregateFlagsParityFixture fixture)
    {
        if (!ReferenceEquals(document, fixture.Owner)) throw new InvalidOperationException("The canonical flags owner changed.");
        fixture.Owner.EndEdits();
        var entries = fixture.Owner.History.Entries;
        if (!entries.Take(fixture.InitialTransactions.Length).Select(entry => entry.Id).SequenceEqual(fixture.InitialTransactions)
            || entries.Skip(fixture.InitialTransactions.Length).Any(entry => !fixture.OwnedTransactions.Contains(entry.Id)))
            throw new InvalidOperationException("Unrelated transactions prohibit flags fixture rollback.");
        while (fixture.Owner.History.Position > fixture.InitialHistory) fixture.Owner.Undo();
        if (fixture.Owner.History.Position != fixture.InitialHistory) throw new InvalidOperationException("The flags fixture history boundary changed.");
        fixture.Owner.Save();
        if (!File.ReadAllBytes(fixture.Owner.Path).AsSpan().SequenceEqual(fixture.NativeSavedBaseline)
            || fixture.Second.Components.Any(component => component.Id == fixture.SecondComponent.Id)
            || (ulong)fixture.FirstComponent.GetType().GetProperty("Counter64")!.GetValue(fixture.FirstComponent)! != fixture.Counter)
            throw new InvalidOperationException("Native fixture rollback did not restore the exact saved scene and original component owners.");
        fixture.Owner.SelectRange(fixture.InitialSelection, fixture.Primary);
    }
}
