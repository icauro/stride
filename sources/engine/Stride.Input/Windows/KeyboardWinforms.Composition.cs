// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#if (STRIDE_UI_WINFORMS || STRIDE_UI_WPF)
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Stride.Input
{
    internal partial class KeyboardWinforms
    {
        private Stride.Core.Mathematics.Rectangle nativeTextRectangle;
        private bool nativeTextRectangleSet, showNativeTextComposition;
        public void SetTextInputRectangle(Stride.Core.Mathematics.Rectangle rectangle, bool showNativeComposition = false)
        {
            nativeTextRectangle = rectangle; nativeTextRectangleSet = true; showNativeTextComposition = showNativeComposition;
            PositionNativeComposition();
        }
        private void PositionNativeComposition()
        {
            if (!nativeTextRectangleSet || !textInputEnabled || richTextBox.IsDisposed || uiControl.IsDisposed) return;
            var context = ImmGetContext(richTextBox.Handle); if (context == IntPtr.Zero) return;
            try
            {
                // Keep the native input control hidden. IMM receives its actual client-coordinate caret.
                var point = richTextBox.PointToClient(uiControl.PointToScreen(new Point(nativeTextRectangle.X, nativeTextRectangle.Y)));
                var position = new NativeImePoint { X = point.X, Y = point.Y };
                var composition = new NativeCompositionForm { Style = 0x20, Position = position }; // CFS_FORCE_POSITION
                var candidate = new NativeCandidateForm { Style = 0x80, Position = position, // CFS_EXCLUDE
                    Area = new NativeImeRect { Left = point.X, Top = point.Y, Right = point.X + Math.Max(1, nativeTextRectangle.Width), Bottom = point.Y + Math.Max(1, nativeTextRectangle.Height) } };
                ImmSetCompositionWindow(context, ref composition); ImmSetCandidateWindow(context, ref candidate);
            }
            finally { ImmReleaseContext(richTextBox.Handle, context); }
        }
        private void CancelNativeComposition()
        {
            if (!richTextBox.IsHandleCreated || richTextBox.IsDisposed) return;
            var context = ImmGetContext(richTextBox.Handle); if (context == IntPtr.Zero) return;
            try { ImmNotifyIME(context, 0x15, 4, 0); } // NI_COMPOSITIONSTR / CPS_CANCEL
            finally { ImmReleaseContext(richTextBox.Handle, context); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeImePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeImeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeCompositionForm { internal uint Style; internal NativeImePoint Position; internal NativeImeRect Area; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeCandidateForm { internal uint Index, Style; internal NativeImePoint Position; internal NativeImeRect Area; }
        [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
        [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
        [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmSetCompositionWindow(IntPtr context, ref NativeCompositionForm form);
        [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmSetCandidateWindow(IntPtr context, ref NativeCandidateForm form);
        [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ImmNotifyIME(IntPtr context, uint action, uint index, uint value);
    }
}
#endif
