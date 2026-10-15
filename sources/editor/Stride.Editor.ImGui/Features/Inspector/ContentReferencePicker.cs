using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker(IContentReferencePickerHost host, string popupIdentity = "ContentReferenceAssetPicker")
{
    internal Action<string>? RecordControl { get; set; }
    private sealed class ReferenceDraft
    {
        internal string Text = "", Original = "";
        internal bool Editing, Edited, SourceNull, SourceMixed;
        internal Stride.Core.Assets.AssetId? SourceId;
        internal string? SourceLocation;
        internal Func<bool>? IsCurrent;
    }
    private readonly Dictionary<uint, ReferenceDraft> referenceDrafts = [];

    public void DrawInspector(Type type, object? value, bool allowsNull, Action<object?> commit, bool forceAssignment = false, bool canEdit = true,
        bool highlighted = false, bool mixed = false)
    {
        var reference = ContentAssetReference.Read(value);
        var entry = mixed || reference == null ? null : host.FindReferenceEntry(reference.Id);
        var available = Gui.GetContentRegionAvail().X;
        var size = 64 * Gui.GetStyle().FontScaleDpi;
        var buttonWidth = 16 * Gui.GetStyle().FontScaleDpi + Gui.GetStyle().FramePadding.X * 2;
        var buttonsWidth = (buttonWidth + Gui.GetStyle().ItemSpacing.X) * (allowsNull ? 3 : 2);
        var textWidth = Math.Max(Gui.CalcTextSize("Sphere Material").X, Gui.GetFrameHeight() * 3);
        var thumbnail = host.HasDynamicReferenceThumbnail(type)
            && available >= size + Gui.GetStyle().ItemSpacing.X + buttonsWidth + textWidth;
        Gui.BeginGroup();
        try
        {
            Gui.SetNextItemWidth(thumbnail ? available - size - Gui.GetStyle().ItemSpacing.X : -1);
            DrawValue(type, value, allowsNull, commit, mixed: mixed, forceAssignment: forceAssignment, canEdit: canEdit);
            if (thumbnail)
            {
                Gui.SameLine();
                DrawReferenceThumbnail(type, value, entry, size, allowsNull, canEdit, mixed, forceAssignment, highlighted, commit);
            }
        }
        finally { Gui.EndGroup(); }
    }

    public void DrawValue(Type type, object? value, bool allowsNull, Action<object?> commit, bool mixed = false, bool forceAssignment = false, bool canEdit = true)
    {
        Gui.BeginGroup();
        try
        {
            var label = Label(type, value, out var broken);
            if (mixed) { label = EditorLocalization.Text("(Different values)"); broken = false; }
            var identity = Gui.GetID("##ContentReference");
            if (!referenceDrafts.TryGetValue(identity, out var draft)) referenceDrafts.Add(identity, draft = new());
            if ((!draft.Editing || !draft.Edited && draft.IsCurrent == null) && draft.Original != label)
            { draft.Text = label; draft.Original = label; }
            var available = Gui.CalcItemWidth();
            var buttonWidth = 16 * Gui.GetStyle().FontScaleDpi + Gui.GetStyle().FramePadding.X * 2;
            Gui.SetNextItemWidth(Math.Max(1, available - (buttonWidth + Gui.GetStyle().ItemSpacing.X) * (allowsNull ? 3 : 2)));
            DrawReferenceText(type, value, allowsNull, canEdit, mixed, broken, draft, identity, commit);
            DrawDrop(type, commit, canEdit);
            if (Gui.BeginPopupContextItem("##ReferenceTextActions"))
            {
                try { DrawReferenceActions(type, value, allowsNull, canEdit, mixed, forceAssignment, commit); }
                finally { Gui.EndPopup(); }
            }
            Gui.SameLine();
            using (ImGuiScopes.Disabled(!canEdit))
                if (host.DrawPickupButton()) RequestModal(type, value, commit, mixed, forceAssignment);
            if (allowsNull)
            {
                Gui.SameLine();
                using (ImGuiScopes.Disabled(!canEdit || !mixed && value == null))
                    if (Gui.Button("x##ClearReference", new Vector2(buttonWidth, 0))) host.Assign(type, null, commit);
                if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Gui.SetTooltip(EditorLocalization.Text("Clear the reference", "ToolTip"));
            }
            Gui.SameLine();
            using (ImGuiScopes.Disabled(mixed || !host.CanFetch(value)))
                if (host.DrawFetchButton()) host.Fetch(value);
        }
        finally { Gui.EndGroup(); }
    }

    private string Label(Type type, object? value, out bool broken)
    {
        var diagnostic = InspectorReferenceDiagnostic.Read(value, host.FindReferenceAsset);
        broken = diagnostic.State == InspectorReferenceState.Missing;
        return diagnostic.State switch
        {
            InspectorReferenceState.None => EditorLocalization.Text("(No asset selected)"),
            InspectorReferenceState.Known => diagnostic.Asset!.Location.ToString(),
            _ => EditorLocalization.Text("(Broken reference)"),
        };
    }

    private unsafe void DrawDrop(Type type, Action<object?> commit, bool canEdit)
    {
        if (!canEdit || !Gui.BeginDragDropTarget()) return;
        try
        {
            var payload = Gui.AcceptDragDropPayload(host.AssetPayloadType);
            if (!payload.IsNull && payload.IsDelivery() && host.TryResolveDrop((nint)payload.Data, payload.DataSize, type, out var asset))
                host.Assign(type, asset, commit);
        }
        finally { Gui.EndDragDropTarget(); }
    }
}
