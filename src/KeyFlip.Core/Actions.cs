using System;

namespace KeyFlip.Core
{
    public enum KeyAction { Layout, InvertCase }

    /// The key whose lone tap converts the layout: either Alt key, or either Windows
    /// key (where Option is on a keyboard that is shared with a Mac).
    public enum LayoutTapKey { Alt, Win }

    public enum Alphabet { English, Russian, Mixed, Neither }

    /// Side-agnostic modifier state, as the tap detector sees it.
    [Flags]
    public enum Modifiers
    {
        None = 0,
        Win = 1,
        Alt = 2,
        Shift = 4,
        Control = 8
    }

    /// Windows virtual-key codes of the modifier keys.
    public static class VirtualKeys
    {
        public const int LeftWin = 0x5B;
        public const int RightWin = 0x5C;
        public const int LeftShift = 0xA0;
        public const int RightShift = 0xA1;
        public const int LeftControl = 0xA2;
        public const int RightControl = 0xA3;
        public const int LeftAlt = 0xA4;
        public const int RightAlt = 0xA5;
    }

    /// A failure whose message is meant for the status line.
    public class AppFailure : Exception
    {
        public AppFailure(string message) : base(message) { }
    }
}
