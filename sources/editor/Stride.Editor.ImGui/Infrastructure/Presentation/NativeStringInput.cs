using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static unsafe class NativeStringInput
{
    private sealed class Buffer : IDisposable
    {
        internal byte[] Bytes;
        private GCHandle pin;
        private readonly ImGuiInputTextCallback? editCallback;
        internal readonly ImGuiInputTextCallback Callback;
        internal byte* Pointer => (byte*)pin.AddrOfPinnedObject();
        internal Buffer(string text, ImGuiInputTextCallback? editCallback = null)
        {
            this.editCallback = editCallback;
            var length = Encoding.UTF8.GetByteCount(text);
            Bytes = ArrayPool<byte>.Shared.Rent(checked(length + 1));
            Encoding.UTF8.GetBytes(text.AsSpan(), Bytes.AsSpan()); Bytes[length] = 0;
            pin = GCHandle.Alloc(Bytes, GCHandleType.Pinned); Callback = Resize;
        }
        private int Resize(ImGuiInputTextCallbackData* data)
        {
            if (data->EventFlag != ImGuiInputTextFlags.CallbackResize) return editCallback != null ? editCallback(data) : 0;
            var required = Math.Max(data->BufSize, checked(data->BufTextLen + 1));
            if (required > Bytes.Length)
            {
                var replacement = ArrayPool<byte>.Shared.Rent(required);
                Bytes.AsSpan().CopyTo(replacement);
                var replacementPin = GCHandle.Alloc(replacement, GCHandleType.Pinned);
                pin.Free(); ArrayPool<byte>.Shared.Return(Bytes);
                Bytes = replacement; pin = replacementPin;
            }
            data->Buf = Pointer; data->BufSize = Bytes.Length;
            return 0;
        }
        internal string Text()
        {
            var length = Array.IndexOf(Bytes, (byte)0);
            return Encoding.UTF8.GetString(Bytes.AsSpan(0, length < 0 ? Bytes.Length : length));
        }
        public void Dispose() { if (pin.IsAllocated) pin.Free(); ArrayPool<byte>.Shared.Return(Bytes); }
    }

    internal static bool Draw(string id, ref string text, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, bool selectAllOnFocus = true)
        => DrawWithHint(id, "", ref text, flags, selectAllOnFocus);
    internal static bool DrawMultiline(string id, ref string text, System.Numerics.Vector2 size,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        var original = text; var embeddedNull = original.IndexOf('\0');
        using var buffer = new Buffer(embeddedNull < 0 ? original : original[..embeddedNull]);
        var readOnly = (flags & ImGuiInputTextFlags.ReadOnly) != 0;
        if (readOnly) Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled, false);
        try
        {
            var submitted = Gui.InputTextMultiline(id, buffer.Pointer, (nuint)buffer.Bytes.Length, size,
                flags | ImGuiInputTextFlags.CallbackResize, buffer.Callback);
            if (Gui.IsItemEdited() && !readOnly)
                text = buffer.Text() + (embeddedNull < 0 ? "" : original[embeddedNull..]);
            GC.KeepAlive(buffer.Callback);
            return !readOnly && submitted;
        }
        finally { if (readOnly) Gui.PopItemFlag(); }
    }
    internal static bool DrawWithHint(string id, string hint, ref string text, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, bool selectAllOnFocus = true,
        ImGuiInputTextCallback? editCallback = null)
    {
        var original = text; var embeddedNull = original.IndexOf('\0');
        using var buffer = new Buffer(embeddedNull < 0 ? original : original[..embeddedNull], editCallback);
        var readOnly = (flags & ImGuiInputTextFlags.ReadOnly) != 0;
        if (readOnly) Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled, false);
        try
        {
            var submitted = Gui.InputTextWithHint(id, hint, buffer.Pointer, (nuint)buffer.Bytes.Length,
                flags | ImGuiInputTextFlags.CallbackResize | (selectAllOnFocus ? ImGuiInputTextFlags.AutoSelectAll : ImGuiInputTextFlags.None), buffer.Callback);
            if (Gui.IsItemEdited() && !readOnly)
                text = buffer.Text() + (embeddedNull < 0 ? "" : original[embeddedNull..]);
            GC.KeepAlive(buffer.Callback);
            return !readOnly && submitted;
        }
        finally { if (readOnly) Gui.PopItemFlag(); }
    }
}
