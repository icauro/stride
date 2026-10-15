using Stride.Animations;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class ModelAssetPreview
{
    internal PlayingAnimation AttachPreviewAnimation(AnimationClip clip)
    {
        var component = new AnimationComponent();
        component.Animations.Add("preview", clip);
        modelEntity.Add(component);
        component.Play("preview");
        var playing = component.PlayingAnimations.Last();
        playing.RepeatMode = AnimationRepeatMode.LoopInfinite;
        return playing;
    }
}
