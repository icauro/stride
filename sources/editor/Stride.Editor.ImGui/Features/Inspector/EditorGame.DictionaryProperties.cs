using System.ComponentModel;
using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{

    private static string DictionaryKeyText(object key) => TypeDescriptor.GetConverter(key.GetType()).ConvertToString(key)
        ?? Convert.ToString(key, CultureInfo.InvariantCulture) ?? "";

    private void DrawDictionaryProperty(ComponentField field)
    {
        using var referencePhase = new ContentReferenceCommitPhase(command => pendingCollectionAction = command,
            command => Execute(() => { ValidatePendingPropertyStrings(); command(); }));
        Gui.PushID(field.Name);
        var open = Gui.TreeNodeEx("##Dictionary", ImGuiTreeNodeFlags.None);
        Gui.SameLine(); Gui.TextUnformatted(field.Label);
        if (open)
        {
            var owner = document!;
            foreach (var item in owner.GetCollectionItems(field))
            {
                if (!owner.IsHierarchyItemVisible(item)) continue;
                Gui.PushID(item.Id.ToString());
                if (owner.HierarchyItemLabel(item) is { } itemLabel) Gui.TextUnformatted(itemLabel);
                DrawRetainedDictionaryKey(owner, item);
                Gui.BeginDisabled(!item.IsContentReference && item.Type != typeof(string) && !UsesCollectionNumericDraft(item) && !IsPackageColorType(item.Type)
                    && (!owner.CanEditCollection(field) || !owner.CanEditHierarchyItem(item.Collection, item.Index))); DrawCollectionItemValue(item); Gui.EndDisabled();
                if (Gui.BeginPopupContextItem("Item property actions"))
                {
                    try { DrawCollectionItemClipboardCommands(owner, item); }
                    finally { Gui.EndPopup(); }
                }
                Gui.BeginDisabled(!owner.CanEditHierarchyItem(item.Collection, item.Index));
                if (owner.CanRemoveCollection(field) && Gui.SmallButton(EditorLocalization.Widget("Remove", "Button"))) pendingCollectionAction = () => owner.RemoveCollectionItem(item);
                Gui.EndDisabled();
                Gui.PopID();
            }
            if (owner.GetRootHierarchyCollection(field) is { } context)
                DrawHierarchyDictionaryDefaultAdd([owner], [context]);
            Gui.TreePop();
        }
        Gui.PopID();
    }
}
