using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    /// A scripted check in another application's window: real hooks, real key presses.
    /// Keys are pressed only while the target window is in the foreground.
    ///
    /// One step per line; lines starting with # are comments:
    ///   type:TEXT            press the keys that type TEXT with the English layout
    ///   key:CHORD            tab, end, ctrl+end, ctrl+a, shift+left, alt+c, win+left, alt+`, ...
    ///   tap:alt|ralt|ctrl|rctrl|win
    ///   expect:TEXT          the focused element's text is TEXT (\n, \s for a space, \\)
    ///   contains:TEXT        the focused element's text contains TEXT
    ///   element:ID:TEXT      the element with this automation id has TEXT in its name
    ///   file:PATH:TEXT       the file's text, without line ends at its end, is TEXT;
    ///                        PATH may use %VARIABLES%
    ///   show-file:PATH       print the file's text
    ///   layout:en|ru         select a layout
    ///   tap-key:alt|win      the key whose tap converts the layout (alt at the start)
    ///   caps-lock:on|off     whether Caps Lock alone switches the layout (off at the start)
    ///   mac-shortcuts:on|off whether Alt is Command and Win is Option (off at the start)
    ///   front:TITLE          the window in front has TITLE in its title; the next steps
    ///                        are for that window
    ///   minimized:TITLE      the window with TITLE in its title is minimized
    ///   gone                 the window the keys were going to has closed
    ///   expect-layout:en|ru
    ///   show                 describe the focused element
    ///   close                ask the target window to close
    ///   wait:MILLISECONDS
    ///
    /// With --installed the check starts no engine of its own: the keys go to the copy
    /// of KeyFlip that is running, with its settings, and the steps that change
    /// settings are skipped.
    internal static class ExternalCheck
    {
        private sealed class FocusLost : Exception
        {
            public FocusLost() : base("The target window lost the keyboard focus; nothing more was typed.")
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        private const int Restore = 9;

        private static Engine engine;
        private static bool installed;
        private static KeyboardLayout english;
        private static KeyboardLayout russian;
        private static IntPtr target;
        private static uint targetProcess;
        private static readonly Dictionary<char, int> Keys = new Dictionary<char, int>();
        private static readonly Dictionary<string, int> Named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const int Extended = 0x10000;

        public static int Run(string titleFragment, string scriptPath, bool trace, bool useInstalled)
        {
            installed = useInstalled;
            string[] steps = File.ReadAllLines(scriptPath, Encoding.UTF8);
            Layouts layouts = new Layouts(false);
            english = layouts.Find(layouts.EnglishId);
            russian = layouts.Find(layouts.RussianId);
            if (english == null || russian == null)
            {
                Console.WriteLine("SKIP add an English and a Russian keyboard layout first.");
                return 3;
            }
            foreach (int scanCode in KeyMap.MainBlockScanCodes)
            {
                foreach (bool shifted in new[] { true, false })
                {
                    string character = english.Characters(scanCode, shifted);
                    if (character != null) Keys[character[0]] = scanCode | (shifted ? 0x100 : 0);
                }
            }
            // Virtual key, scan code << 8, and whether the key is an extended one.
            Named["tab"] = 0x09 | 0x0F << 8;
            Named["enter"] = 0x0D | 0x1C << 8;
            Named["esc"] = 0x1B | 0x01 << 8;
            Named["backspace"] = 0x08 | 0x0E << 8;
            Named["space"] = 0x20 | 0x39 << 8;
            Named["delete"] = 0x2E | 0x53 << 8 | Extended;
            Named["home"] = 0x24 | 0x47 << 8 | Extended;
            Named["end"] = 0x23 | 0x4F << 8 | Extended;
            Named["left"] = 0x25 | 0x4B << 8 | Extended;
            Named["up"] = 0x26 | 0x48 << 8 | Extended;
            Named["right"] = 0x27 | 0x4D << 8 | Extended;
            Named["down"] = 0x28 | 0x50 << 8 | Extended;
            Named["f6"] = 0x75 | 0x40 << 8;
            Named["capslock"] = 0x14 | 0x3A << 8;
            Named["ctrl"] = VirtualKeys.LeftControl | 0x1D << 8;
            Named["shift"] = VirtualKeys.LeftShift | 0x2A << 8;
            Named["alt"] = VirtualKeys.LeftAlt | 0x38 << 8;
            Named["win"] = VirtualKeys.LeftWin | 0x5B << 8 | Extended;

            target = FindWindow(titleFragment);
            if (target == IntPtr.Zero)
            {
                Console.WriteLine("SKIP no window with \"" + titleFragment + "\" in its title.");
                return 3;
            }
            Native.GetWindowThreadProcessId(target, out targetProcess);
            Console.WriteLine("     target: \"" + Native.WindowTitle(target) + "\" (" + Native.ProcessName(targetProcess) + ")");

            WinClipboard clipboard = new WinClipboard();
            string clipboardBefore = clipboard.GetText();
            uint initialLayout = 0;
            engine = new Engine(layouts);
            engine.Silent = true;
            if (trace) engine.Trace = delegate(string message) { Console.WriteLine("       " + message); };
            try
            {
                if (installed)
                {
                    if (Process.GetProcessesByName("KeyFlip").Length == 0)
                    {
                        Console.WriteLine("SKIP KeyFlip is not running.");
                        return 3;
                    }
                    Console.WriteLine("     using the running KeyFlip");
                }
                else
                {
                    engine.Start();
                    if (!engine.Running)
                    {
                        Console.WriteLine("FAIL the keyboard listener did not start.");
                        return 1;
                    }
                }
                if (!TakeFocus())
                {
                    Console.WriteLine("SKIP Windows did not let the target window come forward. Nothing was typed.");
                    return 3;
                }
                engine.InvalidateContext();
                Thread.Sleep(300);
                initialLayout = CurrentLayout();
                foreach (string line in steps)
                {
                    string step = line.TrimEnd('\r', '\n');
                    if (step.Trim().Length == 0 || step.StartsWith("#", StringComparison.Ordinal)) continue;
                    try
                    {
                        Perform(step);
                    }
                    catch (FocusLost lost)
                    {
                        Console.WriteLine("SKIP " + step + ": " + lost.Message);
                        uint owner;
                        IntPtr foreground = Native.GetForegroundWindow();
                        Native.GetWindowThreadProcessId(foreground, out owner);
                        Console.WriteLine("     in front: \"" + Native.WindowTitle(foreground) + "\" (" + Native.ProcessName(owner) + ")");
                        Console.WriteLine("     the target window is " + (!Native.IsWindowVisible(target) ? "gone or hidden"
                            : Native.IsIconic(target) ? "minimized" : "still open, behind"));
                        Expect.Failures++;
                        break;
                    }
                    catch (Exception error)
                    {
                        Expect.Failures++;
                        Console.WriteLine("FAIL " + step + ": " + error.GetType().Name + ": " + error.Message);
                    }
                }
            }
            finally
            {
                try
                {
                    Idle(8000);
                    KeyboardLayout initial = layouts.Find(initialLayout);
                    if (initial != null && HasFocus() && CurrentLayout() != initialLayout) layouts.Select(initial, false);
                }
                catch (Exception)
                {
                }
                engine.Shutdown();
            }
            if (clipboard.GetText() != clipboardBefore)
            {
                Expect.Failures++;
                Console.WriteLine("FAIL the clipboard text is not what it was before the check");
            }
            Console.WriteLine(Expect.Failures == 0 ? "Scripted check passed." : Expect.Failures + " failed steps.");
            return Expect.Failures == 0 ? 0 : 1;
        }

        // ---- Steps ----

        private static void Perform(string step)
        {
            int colon = step.IndexOf(':');
            string command = (colon < 0 ? step : step.Substring(0, colon)).Trim().ToLowerInvariant();
            string argument = colon < 0 ? "" : step.Substring(colon + 1);
            switch (command)
            {
                case "type":
                    Type(Unescape(argument));
                    Console.WriteLine("     type " + argument);
                    break;
                case "key":
                    Chord(argument.Trim());
                    Console.WriteLine("     key " + argument.Trim());
                    Thread.Sleep(120);
                    Idle(8000);
                    break;
                case "caps-lock":
                    if (SkippedSetting(step)) break;
                    engine.CapsLockSwitchesLayout = argument.Trim().ToLowerInvariant() == "on";
                    Console.WriteLine("     caps-lock " + argument.Trim());
                    break;
                case "tap-key":
                    if (SkippedSetting(step)) break;
                    engine.LayoutTapKey = argument.Trim().ToLowerInvariant() == "win" ? LayoutTapKey.Win : LayoutTapKey.Alt;
                    Console.WriteLine("     tap-key " + engine.LayoutKeyName);
                    break;
                case "mac-shortcuts":
                    if (SkippedSetting(step)) break;
                    engine.MacShortcuts = argument.Trim().ToLowerInvariant() == "on";
                    Thread.Sleep(100);
                    Console.WriteLine("     mac-shortcuts " + argument.Trim());
                    break;
                case "front":
                    ExpectFront(argument.Trim(), step);
                    break;
                case "minimized":
                    {
                        IntPtr window = FindWindow(argument.Trim());
                        bool matched = window != IntPtr.Zero && WaitFor(delegate { return Native.IsIconic(window); }, 2500);
                        Report(matched, step, window == IntPtr.Zero ? "no such window" : "the window is not minimized");
                        break;
                    }
                case "gone":
                    {
                        // The window the keys were going to, which a shortcut closed.
                        IntPtr window = target;
                        bool matched = WaitFor(delegate { return !Native.IsWindowVisible(window); }, 2500);
                        Report(matched, step, "the window is still open");
                        break;
                    }
                case "tap":
                    Tap(argument.Trim().ToLowerInvariant());
                    break;
                case "expect":
                    ExpectText(Unescape(argument), false, step);
                    break;
                case "contains":
                    ExpectText(Unescape(argument), true, step);
                    break;
                case "element":
                    ExpectElement(argument, step);
                    break;
                case "file":
                    ExpectFile(argument, step);
                    break;
                case "show-file":
                    Idle(8000);
                    Thread.Sleep(900);
                    Console.WriteLine("     file: " + Visible(File.ReadAllText(Environment.ExpandEnvironmentVariables(argument.Trim()), Encoding.UTF8)));
                    break;
                case "layout":
                    Idle(8000);
                    if (!HasFocus()) throw new FocusLost();
                    engine.Layouts.Select(LayoutNamed(argument), false);
                    Console.WriteLine("     layout " + argument.Trim());
                    break;
                case "expect-layout":
                    {
                        KeyboardLayout wanted = LayoutNamed(argument);
                        bool matched = WaitFor(delegate { return CurrentLayout() == wanted.Id; }, 2000);
                        Report(matched, step, "the layout is " + LayoutNames.Describe(CurrentLayout()));
                        break;
                    }
                case "show":
                    Show();
                    break;
                case "close":
                    Idle(8000);
                    Native.PostMessage(target, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
                    Console.WriteLine("     close");
                    break;
                case "wait":
                    Thread.Sleep(int.Parse(argument.Trim()));
                    break;
                default:
                    throw new InvalidOperationException("Unknown step.");
            }
        }

        private static bool SkippedSetting(string step)
        {
            if (installed) Console.WriteLine("     skipped: " + step + " (the running KeyFlip has its own settings)");
            return installed;
        }

        /// A shortcut brought another window forward: the steps that follow press
        /// their keys there.
        private static void ExpectFront(string titleFragment, string step)
        {
            IntPtr foreground = IntPtr.Zero;
            bool matched = WaitFor(delegate
            {
                foreground = Native.GetForegroundWindow();
                return Native.WindowTitle(foreground).IndexOf(titleFragment, StringComparison.OrdinalIgnoreCase) >= 0;
            }, 2500);
            uint process = 0;
            if (matched)
            {
                Native.GetWindowThreadProcessId(foreground, out process);
                // Never a window of another program: the check types only where it was sent.
                matched = process == targetProcess;
            }
            if (matched) target = foreground;
            Report(matched, step, "in front: " + Visible(Native.WindowTitle(foreground)));
        }

        private static KeyboardLayout LayoutNamed(string name)
        {
            return name.Trim().ToLowerInvariant() == "ru" ? russian : english;
        }

        private static string Unescape(string text)
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\' || i + 1 == text.Length)
                {
                    result.Append(text[i]);
                    continue;
                }
                i++;
                if (text[i] == 'n') result.Append('\n');
                else if (text[i] == 's') result.Append(' ');
                else result.Append(text[i]);
            }
            return result.ToString();
        }

        private static string Visible(string text)
        {
            if (text == null) return "(none)";
            return "\"" + text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private static void Report(bool passed, string step, string actual)
        {
            if (!passed) Expect.Failures++;
            Console.WriteLine((passed ? "PASS " : "FAIL ") + step + (passed ? "" : "  -- " + actual
                + " (status: " + engine.Status + ")"));
        }

        /// Waits until the engine of the check has finished what it was doing. The
        /// running copy of KeyFlip cannot be asked, and an engine that was never
        /// started would never answer: there the expectations that follow wait.
        private static bool Idle(int milliseconds)
        {
            if (!installed) return engine.WaitUntilIdle(milliseconds);
            Thread.Sleep(250);
            return true;
        }

        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            for (int waited = 0; waited <= milliseconds; waited += 50)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return false;
        }

        /// What the focused element holds, as an assistive tool would read it.
        private static string FocusedText()
        {
            try
            {
                AutomationElement element = AutomationElement.FocusedElement;
                if (element == null) return null;
                object pattern;
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    return ((TextPattern)pattern).DocumentRange.GetText(-1).Replace("\r", "");
                }
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    return ((ValuePattern)pattern).Current.Value.Replace("\r", "");
                }
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void ExpectText(string expected, bool contains, string step)
        {
            Idle(8000);
            string actual = null;
            bool matched = WaitFor(delegate
            {
                actual = FocusedText();
                if (actual == null) return false;
                return contains ? actual.Contains(expected) : actual == expected;
            }, 2500);
            Report(matched, step, "got " + Visible(actual));
        }

        private static void ExpectElement(string argument, string step)
        {
            int colon = argument.IndexOf(':');
            string id = argument.Substring(0, colon);
            string expected = Unescape(argument.Substring(colon + 1));
            string actual = null;
            bool matched = WaitFor(delegate
            {
                try
                {
                    AutomationElement element = AutomationElement.FromHandle(target).FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    if (element == null) return false;
                    actual = element.Current.Name;
                    if (string.IsNullOrEmpty(actual))
                    {
                        // A container: its text is in its children.
                        StringBuilder text = new StringBuilder();
                        foreach (AutomationElement child in element.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                        {
                            text.Append(child.Current.Name);
                        }
                        actual = text.ToString();
                    }
                    return actual.Contains(expected);
                }
                catch (Exception)
                {
                    return false;
                }
            }, 2500);
            Report(matched, step, "got " + Visible(actual));
        }

        private static void ExpectFile(string argument, string step)
        {
            int colon = argument.LastIndexOf(':');
            string path = Environment.ExpandEnvironmentVariables(argument.Substring(0, colon));
            string expected = Unescape(argument.Substring(colon + 1));
            string actual = null;
            bool matched = WaitFor(delegate
            {
                try
                {
                    actual = File.ReadAllText(path, Encoding.UTF8).Replace("\r", "").TrimEnd('\n');
                    return actual == expected;
                }
                catch (IOException)
                {
                    return false;
                }
            }, 2500);
            Report(matched, step, "got " + Visible(actual));
        }

        private static void Show()
        {
            try
            {
                AutomationElement element = AutomationElement.FocusedElement;
                AutomationElement.AutomationElementInformation info = element.Current;
                StringBuilder patterns = new StringBuilder();
                foreach (AutomationPattern pattern in element.GetSupportedPatterns())
                {
                    patterns.Append(patterns.Length == 0 ? "" : ", ").Append(pattern.ProgrammaticName.Replace("PatternIdentifiers.Pattern", ""));
                }
                Console.WriteLine("     focused: " + info.ControlType.ProgrammaticName.Replace("ControlType.", "")
                    + " class=" + Visible(info.ClassName) + " id=" + Visible(info.AutomationId)
                    + " framework=" + Visible(info.FrameworkId) + " password=" + info.IsPassword
                    + " patterns=[" + patterns + "]");
                Console.WriteLine("     text: " + Visible(info.IsPassword ? "(password)" : FocusedText())
                    + "  layout: " + LayoutNames.Describe(CurrentLayout()));
            }
            catch (Exception error)
            {
                Console.WriteLine("     focused element unavailable: " + error.Message);
            }
        }

        // ---- Focus and input ----

        private static IntPtr FindWindow(string titleFragment)
        {
            IntPtr found = IntPtr.Zero;
            uint own = (uint)Process.GetCurrentProcess().Id;
            for (int waited = 0; waited < 20000 && found == IntPtr.Zero; waited += 250)
            {
                Native.EnumWindows(delegate(IntPtr window, IntPtr parameter)
                {
                    uint process;
                    Native.GetWindowThreadProcessId(window, out process);
                    if (process == own || !Native.IsWindowVisible(window)) return true;
                    if (Native.WindowTitle(window).IndexOf(titleFragment, StringComparison.OrdinalIgnoreCase) < 0) return true;
                    found = window;
                    return false;
                }, IntPtr.Zero);
                if (found == IntPtr.Zero) Thread.Sleep(250);
            }
            return found;
        }

        private static bool HasFocus()
        {
            // Strictly this window: another window of the same process may be a
            // different document, or the terminal the check was started from.
            return Native.GetForegroundWindow() == target;
        }

        private static uint CurrentLayout()
        {
            return Layouts.IdOf(Native.GetKeyboardLayout(Native.GetFocusInfo().ThreadId));
        }

        private static bool TakeFocus()
        {
            if (Native.IsIconic(target)) Native.ShowWindow(target, Restore);
            for (int attempt = 0; attempt < 3 && Native.GetForegroundWindow() != target; attempt++)
            {
                IntPtr foreground = Native.GetForegroundWindow();
                uint ignored;
                uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out ignored);
                uint thisThread = Native.GetCurrentThreadId();
                bool attached = attempt > 0 && foregroundThread != 0 && AttachThreadInput(thisThread, foregroundThread, true);
                BringWindowToTop(target);
                Native.SetForegroundWindow(target);
                if (attached) AttachThreadInput(thisThread, foregroundThread, false);
                for (int waited = 0; waited < 600 && Native.GetForegroundWindow() != target; waited += 20) Thread.Sleep(20);
            }
            return Native.GetForegroundWindow() == target;
        }

        /// An untagged event, so the engine treats it as real input.
        private static Native.INPUT Key(int virtualKey, int scanCode, bool up, bool extended)
        {
            Native.INPUT input = new Native.INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.u.ki.wVk = (ushort)virtualKey;
            input.u.ki.wScan = (ushort)scanCode;
            input.u.ki.dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0);
            return input;
        }

        private static void Send(params Native.INPUT[] inputs)
        {
            if (!HasFocus()) throw new FocusLost();
            if (Native.SendInput((uint)inputs.Length, inputs, KeySender.InputSize) != inputs.Length)
            {
                throw new InvalidOperationException("Windows refused the check's key presses.");
            }
        }

        private static void Type(string text)
        {
            foreach (char character in text)
            {
                int key;
                if (!Keys.TryGetValue(character, out key)) throw new InvalidOperationException("No key for '" + character + "'.");
                int scanCode = key & 0xFF;
                bool shifted = (key & 0x100) != 0;
                int virtualKey = (int)Native.MapVirtualKeyEx((uint)scanCode, Native.MAPVK_VSC_TO_VK, english.Handle);
                if (shifted) Send(Key(VirtualKeys.LeftShift, 0x2A, false, false));
                Send(Key(virtualKey, scanCode, false, false));
                Send(Key(virtualKey, scanCode, true, false));
                if (shifted) Send(Key(VirtualKeys.LeftShift, 0x2A, true, false));
                Thread.Sleep(25);
            }
        }

        private static int KeyNamed(string name)
        {
            int key;
            if (Named.TryGetValue(name, out key)) return key;
            if (name.Length == 1 && Keys.TryGetValue(char.ToLowerInvariant(name[0]), out key))
            {
                int scanCode = key & 0xFF;
                return (int)Native.MapVirtualKeyEx((uint)scanCode, Native.MAPVK_VSC_TO_VK, english.Handle) | scanCode << 8;
            }
            throw new InvalidOperationException("No key named '" + name + "'.");
        }

        private static void Chord(string chord)
        {
            string[] names = chord.Split('+');
            List<int> keys = new List<int>();
            foreach (string name in names) keys.Add(KeyNamed(name.Trim()));
            List<int> held = new List<int>();
            try
            {
                foreach (int key in keys)
                {
                    Send(Key(key & 0xFF, key >> 8 & 0xFF, false, (key & Extended) != 0));
                    held.Add(key);
                    Thread.Sleep(15);
                }
            }
            finally
            {
                // What went down goes up, wherever the focus is by now: a shortcut may
                // have brought another window forward.
                held.Reverse();
                foreach (int key in held)
                {
                    Native.INPUT[] release = { Key(key & 0xFF, key >> 8 & 0xFF, true, (key & Extended) != 0) };
                    Native.SendInput(1, release, KeySender.InputSize);
                    Thread.Sleep(15);
                }
            }
        }

        private static void Tap(string which)
        {
            int virtualKey = VirtualKeys.LeftAlt;
            int scanCode = 0x38;
            bool extended = false;
            if (which == "ralt")
            {
                virtualKey = VirtualKeys.RightAlt;
                extended = true;
            }
            else if (which == "ctrl")
            {
                virtualKey = VirtualKeys.LeftControl;
                scanCode = 0x1D;
            }
            else if (which == "rctrl")
            {
                virtualKey = VirtualKeys.RightControl;
                scanCode = 0x1D;
                extended = true;
            }
            else if (which == "win")
            {
                virtualKey = VirtualKeys.LeftWin;
                scanCode = 0x5B;
                extended = true;
            }
            else if (which != "alt")
            {
                throw new InvalidOperationException("No tap named '" + which + "'.");
            }
            Thread.Sleep(80);
            Send(Key(virtualKey, scanCode, false, extended));
            Thread.Sleep(30);
            Send(Key(virtualKey, scanCode, true, extended));
            Stopwatch watch = Stopwatch.StartNew();
            Thread.Sleep(60);
            bool idle = Idle(10000);
            Console.WriteLine("     tap " + which + (installed ? "" : " -> " + engine.Status + "  [" + watch.ElapsedMilliseconds + " ms"
                + (idle ? "" : ", still busy") + "]"));
        }
    }
}
