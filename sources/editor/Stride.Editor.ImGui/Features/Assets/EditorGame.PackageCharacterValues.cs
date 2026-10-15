using System.Globalization;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackageCharacterValue(Package package, PackageSettingAccessor accessor, Type declared, string label,
        bool editable, bool allowsNull, Action<object?> set, INodePresenter? presenter)
    {
        if (SessionAuthoringHistory is not { } history) return;
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path);
        var generation = state?.Generation;
        var revision = owner.AuthoringRevision;
        var session = workspace?.Session;
        var tree = packageSettingTrees[(package, accessor.Key)];
        object? Read() => presenter == null ? accessor.Get(package) : presenter.Value;
        var previous = Read();
        bool Current() => editable && PackageSettingsEditable(package) && !PackagePresenterReadOnly(presenter)
            && owner.CanSave && owner.AuthoringRevision == revision && state is { IsPublished: true, IsDisposed: false }
            && state.Generation == generation && ReferenceEquals(history.Registry.Find(owner.Path), state)
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true && tree.Version == (history.Extensions?.Version ?? -1)
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && SameSettingValue(accessor, accessor.Get(package), tree.Source) && Equals(Read(), previous);
        if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
        var available = Gui.GetContentRegionAvail().X;
        var width = Math.Min(UiScale(40), Math.Max(1, available - Gui.GetStyle().ItemSpacing.X - 1));
        Gui.BeginGroup();
        try
        {
            Gui.SetNextItemWidth(width);
            if (NativeCharacterInput.DrawGlyph(owner, previous is char character ? character : '\0', false, Current(), width, out var updated))
            {
                var feedback = Gui.GetID("##Character");
                Exception? glyphError = null;
                if (!Services.Commands.TryExecute(() =>
                {
                    try
                    {
                        ValidatePendingPropertyStrings();
                        var lease = AcquirePackagePropertyPins(package, accessor, presenter, Current);
                        try
                        {
                            if (!Current()) throw new InvalidOperationException("The registered character changed before assignment.");
                            object? next = updated == '\0' && Nullable.GetUnderlyingType(declared) != null ? null : updated;
                            if (next == null && !allowsNull) throw new ArgumentException("This registered package setting does not allow null.");
                            if (!Equals(Read(), next)) set(next);
                            CommitPackageSettingEdits(package, accessor, retainPresentation: true);
                        }
                        finally
                        {
                            try { lease.Dispose(); }
                            catch { packageCommandCleanup.Add(lease.Dispose); throw; }
                        }
                    }
                    catch (Exception error) { glyphError = error; throw; }
                }))
                {
                    if (glyphError != null) PropertyConversionFeedback.Fail(feedback, glyphError);
                    NativeCharacterInput.ResetGlyph(owner);
                }
                PropertyConversionFeedback.Draw(feedback);
            }
            if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Character", "ToolTIp"));
            if (!SameSettingValue(accessor, accessor.Get(package), tree.Source)) return;
            Gui.SameLine(); Gui.SetNextItemWidth(Math.Max(1, available - width - Gui.GetStyle().ItemSpacing.X));
            var unicodeId = Gui.GetID("##Unicode");
            DrawPackagePropertyString(package, accessor, "##Unicode", presenter, editable, Read,
                text =>
                {
                    var next = PropertyConversionFeedback.Convert(unicodeId, () => Convert.ToChar(Convert.ToUInt32(
                        NativeNumericText.Parse(text, typeof(double), retainedUnicodeRange)), CultureInfo.CurrentCulture));
                    if (!Equals(Read(), next)) set(next);
                }, format: value => value is char code ? ((int)code).ToString(CultureInfo.CurrentCulture) : "",
                step: text => NativeNumericInput.StepText(typeof(double), Read() is char code ? (object)(double)code : null, false,
                    retainedUnicodeRange, ref text) ? text : null);
            if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Unicode value", "ToolTip"));
            PropertyConversionFeedback.Draw(unicodeId);
        }
        finally { Gui.EndGroup(); }
    }
}
