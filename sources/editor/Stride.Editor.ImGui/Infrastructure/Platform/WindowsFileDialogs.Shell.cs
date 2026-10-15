using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal static partial class WindowsFileDialogs
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ShellFilter { public nint Name, Pattern; }

    // Installed Windows SDK ShObjIdl_core.h supplies these IFileDialog,
    // IFileOpenDialog, IShellItemArray and IShellItem ABI slots.
    private static unsafe string[] ShowFileDialog(nint owner, string? title, string filter, string? directory,
        string? initialPath, bool multiple, bool save, string? extension)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        nint dialog = 0, folder = 0, results = 0;
        var filters = new List<ShellFilter>();
        try
        {
            var classId = new Guid(save ? "C0B4E2F3-BA21-4773-8DBA-335EC946EB8B" : "DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
            var interfaceId = new Guid(save ? "84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB" : "D57C7288-D4AD-4768-BE02-9D969532D960");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, 1, ref interfaceId, out dialog));
            var methods = *(nint**)dialog;
            uint options = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)methods[10])(dialog, &options));
            options |= 0x40 | 0x800 | 0x8; // FORCEFILESYSTEM | PATHMUSTEXIST | NOCHANGEDIR
            options |= save ? 0x2u : 0x1000u; // OVERWRITEPROMPT or FILEMUSTEXIST
            if (multiple) options |= 0x200; // ALLOWMULTISELECT
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)methods[9])(dialog, options));
            var parts = filter.Split('\0');
            for (var index = 0; index + 1 < parts.Length && parts[index].Length != 0; index += 2)
            {
                // Original FileOpenModalDialog skips empty extension lists.
                if (parts[index + 1].Length == 0) continue;
                nint name = Marshal.StringToCoTaskMemUni(parts[index]), pattern = 0;
                try { pattern = Marshal.StringToCoTaskMemUni(parts[index + 1]); filters.Add(new() { Name = name, Pattern = pattern }); }
                catch { Marshal.FreeCoTaskMem(name); if (pattern != 0) Marshal.FreeCoTaskMem(pattern); throw; }
            }
            if (filters.Count != 0)
            {
                var specs = filters.ToArray();
                fixed (ShellFilter* values = specs)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, ShellFilter*, int>)methods[4])(dialog, (uint)specs.Length, values));
            }
            if (!string.IsNullOrEmpty(title)) SetDialogText(dialog, 17, title);
            if (!string.IsNullOrEmpty(extension)) SetDialogText(dialog, 22, extension.TrimStart('.'));
            if (!string.IsNullOrWhiteSpace(initialPath))
            {
                if (Path.IsPathFullyQualified(initialPath))
                { directory = Path.GetDirectoryName(initialPath); initialPath = Path.GetFileName(initialPath); }
                SetDialogText(dialog, 15, initialPath);
            }
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                var shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetFullPath(directory), 0, ref shellItemId, out folder));
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)methods[12])(dialog, folder));
            }
            int shown;
            using (var protection = WindowsBackgroundDialogScope.Begin(owner))
            {
                shown = ((delegate* unmanaged[Stdcall]<nint, nint, int>)methods[3])(dialog, owner);
                protection?.Verify();
            }
            if (shown == unchecked((int)0x800704C7)) return [];
            Marshal.ThrowExceptionForHR(shown);
            if (!multiple)
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)methods[20])(dialog, &results));
                return [ReadShellFilePath(results)];
            }
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)methods[27])(dialog, &results));
            var array = *(nint**)results;
            uint count = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)array[7])(results, &count));
            var paths = new string[checked((int)count)];
            for (uint index = 0; index < count; index++)
            {
                nint item = 0;
                try
                {
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)array[8])(results, index, &item));
                    paths[checked((int)index)] = ReadShellFilePath(item);
                }
                finally { ReleaseShellObject(item); }
            }
            return paths;
        }
        finally
        {
            ReleaseShellObject(results); ReleaseShellObject(folder); ReleaseShellObject(dialog);
            foreach (var specification in filters) { Marshal.FreeCoTaskMem(specification.Name); Marshal.FreeCoTaskMem(specification.Pattern); }
            CoUninitialize();
        }
    }
    private static unsafe void SetDialogText(nint dialog, int method, string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Dialog text contains a null character.", nameof(value));
        fixed (char* text = value)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)dialog)[method])(dialog, text));
    }
    private static unsafe string ReadShellFilePath(nint item)
    {
        nint name = 0;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)item)[5])(item, 0x80058000, &name));
            return Path.GetFullPath(Marshal.PtrToStringUni(name) ?? throw new InvalidOperationException("The selected file has no filesystem path."));
        }
        finally { if (name != 0) Marshal.FreeCoTaskMem(name); }
    }
}
