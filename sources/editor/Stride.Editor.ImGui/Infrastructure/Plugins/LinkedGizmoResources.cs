// Linked toolkit-neutral gizmo sources use their native companion resource helpers.
// Supply only that resource boundary; loading the companion plugin itself would
// bring its WPF session and presentation dependencies into this host.
namespace Stride.Editor.ImGui
{
    internal static class LinkedGizmoResources
    {
        internal static byte[] Load(string fileName)
        {
            using var stream = typeof(LinkedGizmoResources).Assembly.GetManifestResourceStream("Stride.Editor.ImGui.Gizmos." + fileName)
                ?? throw new InvalidOperationException("Embedded gizmo resource not found: " + fileName);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}

namespace Stride.Audio.Editor
{
    internal static class AudioEditorPlugin
    {
        internal static byte[] LoadResource(string fileName) => Stride.Editor.ImGui.LinkedGizmoResources.Load(fileName);
    }
}

namespace Stride.Particles.Editor
{
    internal static class ParticlesEditorPlugin
    {
        internal static byte[] LoadResource(string fileName) => Stride.Editor.ImGui.LinkedGizmoResources.Load(fileName);
    }
}
