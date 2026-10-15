namespace Stride.Editor.ImGui;

internal sealed partial class AssetCreationPanel
{
    internal Action<string>? RecordControl { get; set; }
    internal static string TemplateChoiceControl(Choice choice) => "template/choice/" + choice.Template?.Id + "/" + choice.Factory?.GetType().FullName + "/" + choice.AssetType.FullName;
    internal object CaptureTemplateDraft() => new { filter, name, sourceNamespace, requestOpen, referencePickerPending,
        selectedName = selected?.Name, selectedType = selected?.AssetType.FullName,
        selectedFactory = selected?.Factory?.GetType().FullName, templateId = selected?.Template?.Id,
        referenceId = reference?.ToString(), pending = HasRequest };
    internal Choice[] InspectTemplateChoices() => choices.ToArray();
    internal Choice? InspectSelectedTemplate() => selected;
    internal Stride.Core.Assets.AssetId? InspectTemplateReference() => reference;
    internal string InspectTemplateName() => name;
}
