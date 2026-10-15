using Hexa.NET.ImGui;
using Stride.Core.IO;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static void SetSpriteSourceInputWidth()
        => Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - 16 * Gui.GetStyle().FontScaleDpi
            - Gui.GetStyle().FramePadding.X * 2 - Gui.GetStyle().ItemSpacing.X));
    private SpriteSourceEdit? SpriteSourceRequest(AssetMetadataDocument owner, SpriteSettingField[] fields)
        => spriteSourceEdits.TryGetValue(owner, out var edit) && fields.All(field => edit.Fields.Any(candidate => ReferenceEquals(candidate.Node, field.Node))) ? edit : null;
    private void CancelFailedSpriteSourceRequest(AssetMetadataDocument owner, SpriteSettingField[] fields)
    {
        if (SpriteSourceRequest(owner, fields)?.Failure != null) ReleaseSpriteSourceEdit(owner);
    }
    private void RegisterSpriteSourcePathDrop(AssetMetadataDocument document, SpriteSettingField[] members)
    {
        var expected = ReadSpriteSourceCertificate(document, members);
        bool Current() => !IsAuthoringPipelineUpdating && SpriteSourceFieldsCurrent(document, members)
            && members.All(document.CanEditSpriteSetting) && expected.SequenceEqual(ReadSpriteSourceCertificate(document, members));
        RegisterPropertyPathDropTarget(document, typeof(UFile), members[0].Node.Retrieve(), Current,
            () => members[0].Node.Retrieve(), path =>
            {
                CancelFailedSpriteSourceRequest(document, members);
                ValidatePendingPropertyStrings();
                if (!Current()) throw new InvalidOperationException("The sprite source changed before the file drop could be applied.");
                QueueSpriteSourceEdit(document, members, (UFile)path);
            });
    }
    private string? ChooseSpriteSourcePath(AssetMetadataDocument document, SpriteSettingField[] members, string current)
    {
        CancelFailedSpriteSourceRequest(document, members);
        ValidatePendingPropertyStrings();
        if (IsAuthoringPipelineUpdating || !SpriteSourceFieldsCurrent(document, members) || members.Any(member => !document.CanEditSpriteSetting(member)))
            throw new InvalidOperationException("This sprite source cannot be edited.");
        var expected = ReadSpriteSourceCertificate(document, members);
        using var lease = new RetainedPathLease(this, AcquireSpriteSourceCertificate(document, members));
        var origin = Path.GetDirectoryName(document.Path)!;
        var initial = string.IsNullOrEmpty(current) ? null : Path.GetFullPath(current, origin);
        var owner = GetCurrentDialogOwner();
        var chosen = WindowsFileDialogs.Open(owner, null, "All files\0*.*\0\0",
            initial == null ? origin : Path.GetDirectoryName(initial), initial);
        if (chosen != null)
        {
            if (!SpriteSourceFieldsCurrent(document, members) || !expected.SequenceEqual(ReadSpriteSourceCertificate(document, members)))
                throw new InvalidOperationException("The sprite source changed while browsing.");
            QueueSpriteSourceEdit(document, members, new UFile(chosen));
        }
        return chosen;
    }
    private bool DrawSpriteSourceBrowseButton()
    {
        Gui.SameLine();
        return icons.Button("sprite-source-browse", EditorLocalization.Text("Browse file", "ToolTip"), 16);
    }
    private void DrawRetainedSpriteSource(AssetMetadataDocument document, SpriteSettingField[] members)
    {
        var first = members[0]; var request = SpriteSourceRequest(document, members);
        var mixed = request == null ? members.Skip(1).Any(member => !Equals(member.Node.Retrieve(), first.Node.Retrieve()))
            : members.Skip(1).Any(member => !Equals(request.SourceFor(member), request.SourceFor(first)));
        bool Editable() => !IsAuthoringPipelineUpdating && SpriteSourceFieldsCurrent(document, members) && members.All(document.CanEditSpriteSetting);
        SetSpriteSourceInputWidth(); var feedback = Gui.GetID("##Value");
        try
        {
            PropertyGrid.Strings.Draw(first.Node, mixed, Editable, () => ReadSpriteSourceCertificate(document, members),
                text => QueueSpriteSourceEdit(document, members, PropertyConversionFeedback.Convert(feedback, () => new UFile(text))),
                () => AcquireSpriteSourceCertificate(document, members),
                format: value => SpriteSourceRequest(document, members) is { } pending ? pending.SourceFor(first)?.ToString() ?? "" : value?.ToString() ?? "");
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        var escaped = (Gui.IsItemActive() || Gui.IsItemDeactivated()) && Gui.IsKeyPressed(ImGuiKey.Escape);
        if (escaped && SpriteSourceRequest(document, members) != null) Execute(() => ReleaseSpriteSourceEdit(document));
        else if (request?.Failure != null && ReferenceEquals(SpriteSourceRequest(document, members), request)
            && Gui.IsItemActive() && Gui.IsKeyPressed(ImGuiKey.Enter) && !PropertyGrid.Strings.IsPending(first.Node, feedback))
            Execute(() => QueueSpriteSourceEdit(document, request.Fields, request.Sources, request.ResetToBase, request.OperationName, request.ProjectionTargets));
        if (request?.Failure is { } failure) PropertyConversionFeedback.Fail(feedback, failure);
        PropertyConversionFeedback.Draw(feedback);
        RegisterSpriteSourcePathDrop(document, members);
        if (members.Length == 1 && Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && Gui.IsMouseReleased(ImGuiMouseButton.Right))
            Gui.OpenPopup("##ResetToBase");
        if (members.Length == 1 && Gui.BeginPopup("##ResetToBase"))
        {
            try
            {
                if (Gui.MenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, Editable() && document.CanResetAssetSettingNode(first.Node)))
                    Execute(() =>
                    {
                        CancelFailedSpriteSourceRequest(document, members); ValidatePendingPropertyStrings();
                        if (((IAssetNode)first.Node).BaseNode is not IMemberNode inherited) throw new InvalidOperationException("The sprite source has no base value.");
                        QueueSpriteSourceEdit(document, members, new UFile?[] { inherited.Retrieve() as UFile }, true);
                    });
            }
            finally { Gui.EndPopup(); }
        }
        using var disabled = ImGuiScopes.Disabled(!Editable());
        if (DrawSpriteSourceBrowseButton()) Execute(() => ChooseSpriteSourcePath(document, members,
            SpriteSourceRequest(document, members) is { } pending ? pending.SourceFor(first)?.ToString() ?? "" : first.Node.Retrieve()?.ToString() ?? ""));
    }
    private void DrawSpriteCommonSource(AssetMetadataDocument document, SpriteCommonSetting setting)
        => DrawRetainedSpriteSource(document, setting.Fields.ToArray());
}
