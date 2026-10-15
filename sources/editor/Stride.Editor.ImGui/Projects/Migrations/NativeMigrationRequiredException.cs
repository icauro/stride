namespace Stride.Editor.ImGui;

internal sealed class NativeMigrationRequiredException : IOException
{
    internal const string CompilerMarker = "STRIDE_EDITOR_NATIVE_MIGRATION_REQUIRED";
    internal NativeMigrationRequiredException() : base("The project requires native package or asset migration.") { }
}
