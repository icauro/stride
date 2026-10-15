using Stride.Core;
using System.Numerics;

namespace Stride.Editor.ImGui;

internal interface IProjectCreationPresentation
{
    Vector2 MeasureCaption(string text);
    bool Checkbox(string label, ref bool value, PlatformType? platform = null);
    bool LiteralCheckbox(string identity, string text, ref bool value, PlatformType? platform = null);
    bool BeginChoice(string identity, string preview);
    bool ChoiceOption(string identity, string text, bool selected);
    void Heading(string text);
    void ParameterHeading(string text);
    bool ParameterButton(string label, Vector2 size);
    void TemplateRow(ProjectTemplateChoice choice, Vector2 minimum, Vector2 maximum);
    void ValidationMessage(string text);
    bool ValidationAccept();
}
