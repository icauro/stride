using System.Reflection;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal unsafe object InspectAggregateFlagsNumericDraft(AggregateFlagsParityFixture fixture)
    {
        var field = AggregateFlagsNumericField(fixture);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var table = typeof(PropertyStringEditor).GetField("drafts", flags)!.GetValue(PropertyGrid.Strings)!;
        object?[] lookup = [field.Node, null];
        object? draft = null; uint? control = null;
        if ((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, lookup)!
            && lookup[1] is System.Collections.IDictionary drafts && drafts.Count == 1)
        { control = drafts.Keys.Cast<uint>().Single(); draft = drafts[control]!; }
        object? Read(string name) => draft?.GetType().GetField(name, flags)?.GetValue(draft);
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        return new { ownerEntity = field.Component.Entity.Id, component = field.Component.Id, control,
            nativeActiveId = context.IsNull ? 0u : context.ActiveId,
            draftActive = Read("Active"), draftEdited = Read("Edited"), draftError = Read("LastValidationError"),
            pending = PropertyGrid.Strings.HasPending,
            enterDown = Hexa.NET.ImGui.ImGui.IsKeyDown(Hexa.NET.ImGui.ImGuiKey.Enter),
            escapeDown = Hexa.NET.ImGui.ImGui.IsKeyDown(Hexa.NET.ImGui.ImGuiKey.Escape),
            ownerSelected = fixture.Owner.IsSelected(field.Component.Entity.Id) };
    }

    internal object VerifyDiagnosticOwnershipParity(ComponentField actualInvalidField)
    {
        if (!args.Contains("--ui-test")) throw new InvalidOperationException("Diagnostic contracts require a private UI test.");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var table = typeof(PropertyStringEditor).GetField("drafts", flags)!.GetValue(PropertyGrid.Strings)!;
        object?[] lookup = [actualInvalidField.Node, null];
        if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, lookup)!
            || lookup[1] is not System.Collections.IDictionary drafts || drafts.Count != 1)
            throw new InvalidOperationException("An actual failed canonical numeric widget draft is required.");
        var draft = drafts.Values.Cast<object>().Single();
        var first = draft.GetType().GetField("LastValidationException", flags)!.GetValue(draft) as Exception
            ?? throw new InvalidOperationException("The actual widget has no failed validation exception.");
        Exception second;
        try { NativeNumericText.Parse("not-a-number", actualInvalidField.Type, actualInvalidField.EffectiveNumericRange);
            throw new InvalidOperationException("The native numeric parser accepted the invalid contract input."); }
        catch (Exception error) { second = error; }
        if (ReferenceEquals(first, second) || first.Message != second.Message)
            throw new InvalidOperationException("The contract requires distinct native parser receipts with identical messages.");
        var previousStatus = statusMessage; var previousStatusOwner = statusDiagnosticOwner;
        var previousAsset = assetEditError; var previousAssetOwner = assetEditDiagnosticOwner;
        static void Require(bool condition) { if (!condition) throw new InvalidOperationException("A cancelled draft erased another diagnostic receipt."); }
        try
        {
            SetPropertyDraftDiagnostic(first); statusMessage = first.Message;
            ClearCancelledPropertyDraft(first); Require(statusMessage == first.Message && statusDiagnosticOwner == null);
            SetPropertyDraftDiagnostic(second); ClearCancelledPropertyDraft(first);
            Require(statusMessage == second.Message && ReferenceEquals(statusDiagnosticOwner, second));
            ClearCancelledPropertyDraft(second); Require(statusMessage == "" && statusDiagnosticOwner == null);
            SetPropertyDraftDiagnostic(first, procedural: true); assetEditError = first.Message;
            ClearCancelledPropertyDraft(first, procedural: true);
            Require(assetEditError == first.Message && assetEditDiagnosticOwner == null);
            SetPropertyDraftDiagnostic(second, procedural: true); ClearCancelledPropertyDraft(first, procedural: true);
            Require(assetEditError == second.Message && ReferenceEquals(assetEditDiagnosticOwner, second));
            ClearCancelledPropertyDraft(second, procedural: true);
            Require(assetEditError == "" && assetEditDiagnosticOwner == null);
            return new { ActualInvalidWidget = true, SameTextUnownedWritePreserved = true,
                DistinctNativeParserReceiptPreserved = true, ProceduralReceiptContract = true,
                Scope = "One actual invalid numeric widget; second receipt is a real parser failure, not a second widget." };
        }
        finally
        {
            statusMessage = previousStatus; statusDiagnosticOwner = previousStatusOwner;
            assetEditError = previousAsset; assetEditDiagnosticOwner = previousAssetOwner;
        }
    }
}
