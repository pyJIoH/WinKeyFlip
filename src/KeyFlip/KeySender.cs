using System;
using System.Runtime.InteropServices;
using System.Threading;
using KeyFlip.Core;

namespace KeyFlip
{
    /// Synthetic keyboard input, tagged so InputMonitor ignores KeyFlip's own events.
    internal static class KeySender
    {
        /// "KFLP". It must fit in 32 bits for 32-bit processes.
        public const uint TagValue = 0x4B464C50;
        public static readonly UIntPtr Tag = new UIntPtr(TagValue);
        public static readonly int InputSize = Marshal.SizeOf(typeof(Native.INPUT));

        /// An unassigned virtual key. Pressing any key while Alt is held stops Windows
        /// from activating the menu bar when Alt is released.
        public const ushort MenuMaskKey = 0xE8;

        public const ushort ScanBackspace = 0x0E;
        public const ushort ScanLeftControl = 0x1D;
        public const ushort ScanLeftShift = 0x2A;
        public const ushort ScanInsert = 0x52;

        public const string BlockedMessage =
            "Windows blocked keyboard input into this window. It may be running as administrator.";

        private static Native.INPUT Key(ushort virtualKey, ushort scanCode, uint flags)
        {
            Native.INPUT input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.u.ki.wVk = virtualKey;
            input.u.ki.wScan = scanCode;
            input.u.ki.dwFlags = flags;
            input.u.ki.dwExtraInfo = Tag;
            return input;
        }

        /// One Backspace press per character to remove.
        public static Native.INPUT[] Backspaces(int count)
        {
            count = Math.Max(0, count);
            Native.INPUT[] inputs = new Native.INPUT[count * 2];
            for (int i = 0; i < count; i++)
            {
                inputs[i * 2] = Key(Native.VK_BACK, ScanBackspace, 0);
                inputs[i * 2 + 1] = Key(Native.VK_BACK, ScanBackspace, Native.KEYEVENTF_KEYUP);
            }
            return inputs;
        }

        /// One key press per UTF-16 unit. Each event carries its character, so the result
        /// does not depend on the layout the receiving application is using.
        public static Native.INPUT[] Typing(string text)
        {
            text = text ?? "";
            Native.INPUT[] inputs = new Native.INPUT[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                inputs[i * 2] = Key(0, text[i], Native.KEYEVENTF_UNICODE);
                inputs[i * 2 + 1] = Key(0, text[i], Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP);
            }
            return inputs;
        }

        /// Ctrl+Insert. Unlike Ctrl+C it can never interrupt a console program.
        public static Native.INPUT[] Copy()
        {
            return Chord(VirtualKeys.LeftControl, ScanLeftControl);
        }

        /// Shift+Insert.
        public static Native.INPUT[] Paste()
        {
            return Chord(VirtualKeys.LeftShift, ScanLeftShift);
        }

        private static Native.INPUT[] Chord(int modifier, ushort modifierScanCode)
        {
            // Insert must be marked extended; otherwise it is the keypad 0 key.
            return new[]
            {
                Key((ushort)modifier, modifierScanCode, 0),
                Key(Native.VK_INSERT, ScanInsert, Native.KEYEVENTF_EXTENDEDKEY),
                Key(Native.VK_INSERT, ScanInsert, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP),
                Key((ushort)modifier, modifierScanCode, Native.KEYEVENTF_KEYUP)
            };
        }

        public static Native.INPUT[] MenuMask()
        {
            return new[] { Key(MenuMaskKey, 0, 0), Key(MenuMaskKey, 0, Native.KEYEVENTF_KEYUP) };
        }

        /// A press of the regular Caps Lock. It carries the tag, so KeyFlip's own hook
        /// lets it through to the system.
        public static Native.INPUT[] CapsLock()
        {
            return new[]
            {
                Key((ushort)Native.VK_CAPITAL, 0x3A, 0),
                Key((ushort)Native.VK_CAPITAL, 0x3A, Native.KEYEVENTF_KEYUP)
            };
        }

        /// Sends all events as one uninterrupted sequence.
        public static void Send(Native.INPUT[] inputs)
        {
            if (inputs.Length == 0) return;
            if (Native.SendInput((uint)inputs.Length, inputs, InputSize) != inputs.Length)
            {
                throw new AppFailure(BlockedMessage);
            }
        }

        /// Sends one press and release at a time with a short pause: some applications
        /// drop keys that arrive in a single burst.
        public static void SendPaced(Native.INPUT[] inputs)
        {
            if (inputs.Length == 0) return;
            Native.INPUT[] pair = new Native.INPUT[2];
            Native.timeBeginPeriod(1);
            try
            {
                for (int i = 0; i + 1 < inputs.Length; i += 2)
                {
                    if (i > 0) Thread.Sleep(1);
                    pair[0] = inputs[i];
                    pair[1] = inputs[i + 1];
                    if (Native.SendInput(2, pair, InputSize) != 2) throw new AppFailure(BlockedMessage);
                }
            }
            finally
            {
                Native.timeEndPeriod(1);
            }
        }

        public static bool AnyModifierDown()
        {
            return Native.IsKeyDown(Native.VK_SHIFT) || Native.IsKeyDown(Native.VK_CONTROL)
                || Native.IsKeyDown(Native.VK_MENU) || Native.IsKeyDown(VirtualKeys.LeftWin)
                || Native.IsKeyDown(VirtualKeys.RightWin);
        }

        /// A held modifier would turn synthetic keys into different shortcuts.
        public static bool WaitForModifiersReleased(int timeoutMilliseconds)
        {
            for (int waited = 0; ; waited += 10)
            {
                if (!AnyModifierDown()) return true;
                if (waited >= timeoutMilliseconds) return false;
                Thread.Sleep(10);
            }
        }
    }
}
