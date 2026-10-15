using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Default native metrics at viewport transitions, without replacing active style overrides.</summary>
internal sealed unsafe class EditorViewportStylePolicy
{
    private ImGuiStyle baseline, applied;
    private bool[] scalableWords = [];
    private bool initialized;
    private float appliedScale = float.NaN;

    internal void SetBaseline(ImGuiStyle style)
    {
        baseline = style;
        var original = style;
        var probe = style;
        probe.ScaleAllSizes(2);
        var count = sizeof(ImGuiStyle) / sizeof(uint);
        if (scalableWords.Length != count) scalableWords = new bool[count];
        var source = (uint*)&original;
        var doubled = (uint*)&probe;
        // Use this installed native implementation's own scaling policy, not a
        // guessed list of offsets. Fonts, colors, ratios and timing do not change.
        for (var word = 0; word < count; word++) scalableWords[word] = source[word] != doubled[word];
        initialized = false; appliedScale = float.NaN;
    }

    internal void Apply(float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (scalableWords.Length == 0 || initialized && appliedScale == scale) return;
        var next = baseline;
        next.ScaleAllSizes(scale); // Scale a local fresh baseline, never the live style.
        var previous = applied;
        var previousWords = (uint*)&previous;
        var nextWords = (uint*)&next;
        var live = (uint*)Gui.GetStyle().Handle;
        Span<bool> overridden = stackalloc bool[scalableWords.Length];
        overridden.Clear();
        Span<bool> seen = stackalloc bool[(int)ImGuiStyleVar.Count];
        seen.Clear();
        var stack = Gui.GetCurrentContext().StyleVarStack;
        for (var index = 0; index < stack.Size; index++)
        {
            var modification = stack[index];
            var variable = (int)modification.VarIdx;
            if ((uint)variable >= (uint)seen.Length) throw new InvalidOperationException("A native style override has an invalid index.");
            var info = ImGuiP.GetStyleVarInfo(modification.VarIdx);
            if (info.DataType != ImGuiDataType.Float || info.Count is < 1 or > 2 || info.Offset % sizeof(uint) != 0)
                throw new InvalidOperationException("A native style override has an unsupported layout.");
            var word = checked((int)info.Offset / sizeof(uint));
            var components = checked((int)info.Count);
            if (word < 0 || word + components > overridden.Length)
                throw new InvalidOperationException("A native style override is outside the actual style.");
            for (var component = 0; component < components; component++) overridden[word + component] = true;
            if (seen[variable]) continue;
            seen[variable] = true;
            if (!initialized) continue;
            // Only the first backup restores the default. Later backups belong
            // to nested explicit overrides and must retain their original values.
            var backup = (uint*)&modification.Union;
            var defaultBackup = true;
            for (var component = 0; component < components; component++)
                defaultBackup &= backup[component] == previousWords[word + component];
            if (!defaultBackup) continue; // Preserve direct provider-owned customization.
            for (var component = 0; component < components; component++)
                if (scalableWords[word + component]) backup[component] = nextWords[word + component];
            stack[index] = modification;
        }
        for (var word = 0; word < scalableWords.Length; word++)
            if (scalableWords[word] && !overridden[word]) live[word] = nextWords[word];
        applied = next; appliedScale = scale; initialized = true;
    }
}
