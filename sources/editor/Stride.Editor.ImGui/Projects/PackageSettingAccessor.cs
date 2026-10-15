using Stride.Core.Assets;
using Stride.Core.Settings;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal abstract class PackageSettingAccessor
{
    public abstract SettingsKey Key { get; }
    public abstract object? Get(Package package);
    public abstract void Set(PackageSettingsDocument document, object? value);
    public abstract void EditValue(PackageSettingsDocument document, Action<IObjectNode> edit);
    public abstract object? Snapshot(Package package);
    public abstract void EditValueReplacingRoot(PackageSettingsDocument document, Func<IObjectNode, object?> edit);
    public static PackageSettingAccessor Create(SettingsKey key) => (PackageSettingAccessor)Activator.CreateInstance(
        typeof(TypedAccessor<>).MakeGenericType(key.Type), key)!;
    private sealed class TypedAccessor<T> : PackageSettingAccessor
    {
        private readonly SettingsKey<T> key;
        public TypedAccessor(SettingsKey<T> key) => this.key = key;
        public override SettingsKey Key => key;
        public override object? Get(Package package) => package.UserSettings.GetValue(key);
        public override void Set(PackageSettingsDocument document, object? value) => document.Set(key, (T)value!);
        public override void EditValue(PackageSettingsDocument document, Action<IObjectNode> edit) => document.EditValue(key, edit);
        public override void EditValueReplacingRoot(PackageSettingsDocument document, Func<IObjectNode, object?> edit) => document.EditValueReplacingRoot(key, edit);
        public override object? Snapshot(Package package) => AssetCloner.Clone(package.UserSettings.GetValue(key));
    }
}
