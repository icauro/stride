using Hexa.NET.ImGui;
using Stride.Engine;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using SVector3 = Stride.Core.Mathematics.Vector3;
using SQuaternion = Stride.Core.Mathematics.Quaternion;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ProjectWorkspace? workspace;
    private CompiledProjectContent? projectContent;
    private EntityHierarchyDocument? document;
    private EngineViewport? engineViewport;
    private EditorIcons? icons;
    private string statusMessageValue = "Open a project package to begin";
    private Exception? statusDiagnosticOwner;
    private string statusMessage
    {
        get => statusMessageValue;
        set { statusMessageValue = value; statusDiagnosticOwner = null; }
    }
    internal SceneDocument? Document => document as SceneDocument;
    internal string StatusMessage => statusMessage;
}
