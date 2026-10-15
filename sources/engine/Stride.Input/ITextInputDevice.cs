// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Input
{
    /// <summary>
    /// A device such as a keyboard that supports text input. This can be a windows keyboard with IME support or a touch keyboard on a smartphone device
    /// </summary>
    public interface ITextInputDevice : IInputDevice
    {
        /// <summary>
        /// Allows input to be entered, the input device will then send text events through the input manager
        /// </summary>
        void EnabledTextInput();
        
        /// <summary>
        /// Disallows text input to be entered, will close any IME active and stop sending text events
        /// </summary>
        void DisableTextInput();

        /// <summary>Positions native text composition in window client coordinates.</summary>
        /// <param name="rectangle">The active text caret area.</param>
        /// <param name="showNativeComposition">Show native composition UI instead of drawing preedit text in the application.</param>
        void SetTextInputRectangle(Stride.Core.Mathematics.Rectangle rectangle, bool showNativeComposition = false) { }
    }
}
