using Stride.Core.Assets.Templates;

namespace Stride.Editor.ImGui;

/// <summary>Explicit native port of a registered generator's interactive preparation.</summary>
public interface IEditorSessionTemplatePreparation : IDisposable
{
    ITemplateGenerator<SessionTemplateGeneratorParameters> Generator { get; }
    void Draw();
}
