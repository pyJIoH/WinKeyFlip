using System;
using System.Collections.Generic;
using System.Threading;
using KeyFlip.Core;

namespace KeyFlip
{
    /// The Mac shortcuts that are not key presses. They run on the action thread:
    /// they wait for other programs, which the keyboard hook must never do.
    internal static class WindowCommands
    {
        public static void Run(ShortcutCommand command)
        {
            switch (command)
            {
                case ShortcutCommand.Minimize:
                    Minimize();
                    break;
                case ShortcutCommand.NextWindow:
                    Cycle(false);
                    break;
                case ShortcutCommand.PreviousWindow:
                    Cycle(true);
                    break;
                case ShortcutCommand.Lock:
                    Native.LockWorkStation();
                    break;
            }
        }

        private static void Minimize()
        {
            IntPtr window = Root(Native.GetForegroundWindow());
            if (window == IntPtr.Zero) return;
            if ((Native.GetWindowLong(window, Native.GWL_STYLE) & Native.WS_MINIMIZEBOX) == 0) return;
            // Asked the way the title bar button asks, so that a program that goes to
            // the notification area instead still does.
            Native.PostMessage(window, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MINIMIZE, IntPtr.Zero);
        }

        /// A dialog stands for the window that owns it.
        private static IntPtr Root(IntPtr window)
        {
            if (window == IntPtr.Zero) return window;
            IntPtr root = Native.GetAncestor(window, Native.GA_ROOTOWNER);
            return root != IntPtr.Zero ? root : window;
        }

        /// The other open windows of the program in front, front to back: the same
        /// process and the same window class.
        internal static List<IntPtr> Siblings(IntPtr front)
        {
            List<IntPtr> windows = new List<IntPtr>();
            uint process;
            Native.GetWindowThreadProcessId(front, out process);
            string windowClass = Native.ClassName(front);
            Native.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint owner;
                Native.GetWindowThreadProcessId(window, out owner);
                if (owner == process && window != front && Engine.IsApplicationWindow(window)
                    && !Native.IsIconic(window) && Native.ClassName(window) == windowClass)
                {
                    windows.Add(window);
                }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        /// Command+` brings the window at the back to the front, so that pressing it
        /// again and again goes through all of them; with Shift it goes the other way.
        private static void Cycle(bool backwards)
        {
            IntPtr front = Root(Native.GetForegroundWindow());
            if (front == IntPtr.Zero) return;
            List<IntPtr> others = Siblings(front);
            if (others.Count == 0) return;
            IntPtr last = others[others.Count - 1];
            if (!backwards)
            {
                Activate(last);
                return;
            }
            // The window in front goes behind the others, and the one behind it comes forward.
            Native.SetWindowPos(front, last, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
            Activate(others[0]);
        }

        private static void Activate(IntPtr window)
        {
            if (Native.IsHungAppWindow(window)) return;
            // Windows lets a program bring a window forward when that program has just
            // sent input, and an empty mouse event counts.
            Native.SendInput(1, new Native.INPUT[1], KeySender.InputSize);
            Native.SetForegroundWindow(window);
            for (int waited = 0; waited < 100; waited += 10)
            {
                if (Native.GetForegroundWindow() == window) return;
                Thread.Sleep(10);
            }
            // Otherwise, sharing an input queue with the thread in front allows it.
            uint ignored;
            uint foreground = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out ignored);
            uint own = Native.GetCurrentThreadId();
            if (foreground == 0 || !Native.AttachThreadInput(own, foreground, true)) return;
            try
            {
                Native.BringWindowToTop(window);
                Native.SetForegroundWindow(window);
            }
            finally
            {
                Native.AttachThreadInput(own, foreground, false);
            }
        }
    }
}
