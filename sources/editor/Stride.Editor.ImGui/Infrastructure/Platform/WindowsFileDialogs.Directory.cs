using System.Runtime.InteropServices;

namespace Stride.Editor.ImGui;

internal static partial class WindowsFileDialogs
{
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId, out nint instance);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid interfaceId, out nint item);

    /// <summary>Native IFileDialog folder selection. COM pointers never leave this adapter.</summary>
    public static unsafe string? OpenDirectory(nint owner, string? title, string? initialDirectory)
    {
        // ABI/flags from Windows SDK ShObjIdl_core.h: IFileDialog and IShellItem.
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2)); // COINIT_APARTMENTTHREADED
        nint dialog = 0, initial = 0, result = 0, name = 0;
        try
        {
            var classId = new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
            var interfaceId = new Guid("42F85136-DB7E-439C-85F1-E4075D135FC8");
            var shellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, 1, ref interfaceId, out dialog)); // CLSCTX_INPROC_SERVER
            var methods = *(nint**)dialog;
            uint options = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)methods[10])(dialog, &options));
            options |= 0x20 | 0x40 | 0x800 | 0x8; // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST | NOCHANGEDIR
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)methods[9])(dialog, options));
            if (!string.IsNullOrEmpty(title))
                fixed (char* text = title) Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)methods[17])(dialog, text));
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetFullPath(initialDirectory), 0, ref shellItemId, out initial));
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)methods[12])(dialog, initial));
            }
            int shown;
            using (var protection = WindowsBackgroundDialogScope.Begin(owner))
            {
                shown = ((delegate* unmanaged[Stdcall]<nint, nint, int>)methods[3])(dialog, owner);
                protection?.Verify();
            }
            if (shown == unchecked((int)0x800704C7)) return null; // HRESULT_FROM_WIN32(ERROR_CANCELLED)
            Marshal.ThrowExceptionForHR(shown);
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)methods[20])(dialog, &result));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)result)[5])(result, 0x80058000, &name)); // SIGDN_FILESYSPATH
            return Path.GetFullPath(Marshal.PtrToStringUni(name) ?? throw new InvalidOperationException("The selected folder has no filesystem path."));
        }
        finally
        {
            if (name != 0) Marshal.FreeCoTaskMem(name);
            ReleaseShellObject(result); ReleaseShellObject(initial); ReleaseShellObject(dialog);
            CoUninitialize();
        }
    }
    private static unsafe void ReleaseShellObject(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance);
    }
}
