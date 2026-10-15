using Stride.Core;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static string CompositorReferenceName(AssetMetadataDocument document, object? value)
        => value is Stride.Rendering.Compositing.SceneCameraSlotId { IsEmpty: false } slot
            ? document.GetCompositorCameraSlotChoices().FirstOrDefault(choice => choice.Value.Id == slot.Id)?.Label ?? slot.Id.ToString()
            : CompositorReferenceName(value);
    private static string CompositorReferenceIdentity(object value) => value switch
    {
        Stride.Rendering.Compositing.SceneCameraSlotId slot => slot.Id.ToString(),
        IIdentifiable identifiable => identifiable.Id.ToString(),
        _ => value.GetType().AssemblyQualifiedName ?? value.GetType().FullName ?? "",
    };
}
