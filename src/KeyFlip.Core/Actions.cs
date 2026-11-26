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

    /// Windows virtual-key codes: the modifier keys, and the keys that shortcuts name.
    /// Letters and digits are their own character codes.
    public static class VirtualKeys
    {
        public const int Backspace = 0x08;
        public const int Tab = 0x09;
        public const int Enter = 0x0D;
        public const int Escape = 0x1B;
        public const int Space = 0x20;
        public const int PageUp = 0x21;
        public const int PageDown = 0x22;
        public const int End = 0x23;
        public const int Home = 0x24;
        public const int LeftArrow = 0x25;
        public const int UpArrow = 0x26;
        public const int RightArrow = 0x27;
        public const int DownArrow = 0x28;
        public const int PrintScreen = 0x2C;
        public const int Insert = 0x2D;
        public const int Delete = 0x2E;
        public const int Menu = 0x5D;
        public const int Numpad0 = 0x60;
        public const int Divide = 0x6F;
        public const int F1 = 0x70;
        public const int F3 = 0x72;
        public const int F4 = 0x73;
        public const int F11 = 0x7A;
        public const int F24 = 0x87;
        public const int Comma = 0xBC;
        public const int Period = 0xBE;
        public const int Backquote = 0xC0;
        public const int LeftBracket = 0xDB;
        public const int RightBracket = 0xDD;

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
