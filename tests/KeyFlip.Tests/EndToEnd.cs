using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    /// The whole pipeline in the test's own window: real hooks, real key presses, real
    /// replacements. It takes the keyboard focus for a few seconds, presses keys only
    /// while its window is in the foreground, and switches the layout an even number of times.
    internal static class EndToEnd
    {
        private sealed class FocusLost : Exception
        {
            public FocusLost() : base("The test window lost the keyboard focus; nothing more was typed.")
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        private static EditorHost host;
        /// A second window of the test, while a scenario has one open.
        private static IntPtr second;
        private static Engine engine;
        private static KeyboardLayout english;
        private static KeyboardLayout russian;
        private static readonly Dictionary<char, int> Keys = new Dictionary<char, int>();

        /// `filter` limits the run to scenarios whose name contains it; `trace` prints
        /// the engine's view of the input.
        public static int Run(string filter, bool trace)
        {
            Layouts layouts = new Layouts(false);
            english = layouts.Find(layouts.EnglishId);
            russian = layouts.Find(layouts.RussianId);
            if (english == null || russian == null)
            {
                Console.WriteLine("SKIP add an English and a Russian keyboard layout first.");
                return 3;
            }
            // Which key and Shift state types each character with the English layout.
            foreach (int scanCode in KeyMap.MainBlockScanCodes)
            {
                foreach (bool shifted in new[] { true, false })
                {
                    string character = english.Characters(scanCode, shifted);
                    if (character != null) Keys[character[0]] = scanCode | (shifted ? 0x100 : 0);
                }
            }

            WinClipboard clipboard = new WinClipboard();
            string clipboardBefore = clipboard.GetText();
            uint initialLayout = 0;
            engine = new Engine(layouts);
            engine.Silent = true;
            if (trace) engine.Trace = delegate(string message) { Console.WriteLine("     " + message); };
            host = new EditorHost(false, "KeyFlip end-to-end check — please do not type");
            try
            {
                engine.Start();
                if (!engine.Running)
                {
                    Console.WriteLine("FAIL the keyboard listener did not start.");
                    return 1;
                }
                if (!TakeFocus())
                {
                    Console.WriteLine("SKIP Windows did not let the test window take the keyboard focus. Nothing was typed.");
                    return 3;
                }
                initialLayout = Layouts.IdOf(Native.GetKeyboardLayout(host.ThreadId));
                Scenario[] scenarios =
                {
                    new Scenario("Typed word: Alt converts, switches the layout, and converts back", TypedWordLayout),
                    new Scenario("Typed word: left Ctrl inverts case and back", TypedWordCase),
                    new Scenario("Typed word with a trailing space", TypedWordTrailingSpace),
                    new Scenario("Typed word with Caps Lock on", TypedWordCapsLock),
                    new Scenario("Caps Lock alone only switches the layout", CapsLockSwitchesLayout),
                    new Scenario("A lone Alt tap does not open the menu bar", MenuStaysClosed),
                    new Scenario("Win as the conversion key: a lone tap converts and does not open Start", WinKeyConverts),
                    new Scenario("A tap during a replacement opens no menu", OverlappingTapOpensNoMenu),
                    new Scenario("Existing word: selected, pasted and verified", ExistingWord),
                    new Scenario("Selection inside a sentence", SelectionInSentence),
                    new Scenario("Existing word with trailing spaces keeps the caret", ExistingWordTrailingSpaces),
                    new Scenario("Rich edit control", RichEdit),
                    new Scenario("Shortcuts and other modifiers never trigger", ShortcutsDoNotTrigger),
                    new Scenario("Password field is left alone", PasswordField),
                    new Scenario("Editor without accessibility: a typed word is retyped", CanvasTypedWord),
                    new Scenario("Editor without accessibility: a selection is copied, converted and pasted", CanvasSelection),
                    new Scenario("Editor without accessibility: nothing selected, nothing changed", CanvasNothingSelected),
                    new Scenario("Editor without accessibility: a copied whole line is not pasted back", CanvasWholeLine),
                    new Scenario("Empty field: Alt only switches the layout", EmptyField),
                    new Scenario("Mac shortcuts: moving, selecting and deleting text", MacTextNavigation),
                    new Scenario("Mac shortcuts: what the application receives", MacKeysReceived),
                    new Scenario("Mac shortcuts: taps still convert, and no menu opens", MacTaps),
                    new Scenario("Mac shortcuts: the next window of the program, and minimizing", MacWindows),
                    new Scenario("Mac shortcuts: Alt+click is Ctrl+click", MacClick),
                    new Scenario("Mac shortcuts: an excluded program keeps its own", MacExcludedProgram)
                };
                foreach (Scenario scenario in scenarios)
                {
                    if (filter != null && scenario.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int before = Expect.Failures;
                    try
                    {
                        scenario.Run();
                    }
                    catch (FocusLost lost)
                    {
                        Console.WriteLine("SKIP " + scenario.Name + ": " + lost.Message);
                        Expect.Failures++;
                        break;
                    }
                    catch (Exception error)
                    {
                        Expect.Failures++;
                        Console.WriteLine("FAIL " + scenario.Name + ": " + error.Message);
                    }
                    Console.WriteLine((before == Expect.Failures ? "PASS " : "FAIL ") + scenario.Name
                        + "  [" + string.Join(", ", TapMilliseconds) + " ms]");
                    TapMilliseconds.Clear();
                }
            }
            finally
            {
                try
                {
                    // Put the layout back, whatever happened.
                    KeyboardLayout initial = layouts.Find(initialLayout);
                    if (initial != null && Layouts.IdOf(Native.GetKeyboardLayout(host.ThreadId)) != initialLayout
                        && Native.GetForegroundWindow() == host.Handle)
                    {
                        layouts.Select(initial, false);
                    }
                }
                catch (Exception)
                {
                }
                engine.WaitUntilIdle(3000);
                engine.Shutdown();
                host.Dispose();
            }
            Expect.True(clipboard.GetText() == clipboardBefore, "the clipboard text is not what it was before the check");
            Console.WriteLine(Expect.Failures == 0 ? "End-to-end check passed." : Expect.Failures + " failed assertions.");
            return Expect.Failures == 0 ? 0 : 1;
        }

        // ---- Focus and input ----

        private static bool HasFocus()
        {
            return Native.GetForegroundWindow() == host.Handle;
        }

        private static bool TakeFocus()
        {
            for (int attempt = 0; attempt < 3 && !HasFocus(); attempt++)
            {
                IntPtr foreground = Native.GetForegroundWindow();
                uint ignored;
                uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out ignored);
                uint thisThread = Native.GetCurrentThreadId();
                // Sharing an input queue with the foreground thread is the documented way
                // for another window to be allowed to come forward.
                bool attached = attempt > 0 && foregroundThread != 0 && AttachThreadInput(thisThread, foregroundThread, true);
                BringWindowToTop(host.Handle);
                Native.SetForegroundWindow(host.Handle);
                if (attached) AttachThreadInput(thisThread, foregroundThread, false);
                for (int waited = 0; waited < 400 && !HasFocus(); waited += 20) Thread.Sleep(20);
            }
            if (!HasFocus()) return false;
            Focus(host.Plain);
            return true;
        }

        private static void Focus(Control control)
        {
            host.Do(delegate { control.Focus(); });
            // The focus change is not input; make sure no stale word is tracked.
            engine.InvalidateContext();
            Thread.Sleep(50);
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

        /// Keys are pressed only while one of the test's own windows is in front.
        private static bool InFront()
        {
            return HasFocus() || (second != IntPtr.Zero && Native.GetForegroundWindow() == second);
        }

        private static void Send(params Native.INPUT[] inputs)
        {
            if (!InFront()) throw new FocusLost();
            if (Native.SendInput((uint)inputs.Length, inputs, KeySender.InputSize) != inputs.Length)
            {
                throw new InvalidOperationException("Windows refused the test's key presses.");
            }
        }

        private static void Press(int virtualKey, int scanCode, bool extended, int holdMilliseconds)
        {
            Send(Key(virtualKey, scanCode, false, extended));
            Thread.Sleep(holdMilliseconds);
            Send(Key(virtualKey, scanCode, true, extended));
        }

        /// Types text given in English-layout characters; the active layout decides what appears.
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
                Thread.Sleep(15);
            }
        }

        private static readonly List<long> TapMilliseconds = new List<long>();

        /// A tap, then waits until the engine has finished what it started.
        private static void Tap(int virtualKey, int scanCode, bool extended)
        {
            Thread.Sleep(60);
            Press(virtualKey, scanCode, extended, 30);
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            Thread.Sleep(30);
            Expect.True(engine.WaitUntilIdle(8000), "the engine did not finish in time");
            TapMilliseconds.Add(watch.ElapsedMilliseconds);
        }

        private static void TapAlt()
        {
            Tap(VirtualKeys.LeftAlt, 0x38, false);
        }

        private static void TapWin()
        {
            Tap(VirtualKeys.LeftWin, 0x5B, true);
        }

        /// The Start menu takes the foreground when it opens.
        private static void ExpectStartClosed(string what)
        {
            Thread.Sleep(500);
            Expect.True(HasFocus(), what + ": the test window lost the foreground (the Start menu opened?)");
        }

        /// Closes a Start menu that a failed check left open, so that the next scenarios can run.
        private static void RecoverFromStart()
        {
            if (HasFocus()) return;
            Thread.Sleep(300);
            if (Native.ClassName(Native.GetForegroundWindow()) == "Windows.UI.Core.CoreWindow")
            {
                Native.INPUT[] escape = { Key(0x1B, 0x01, false, false), Key(0x1B, 0x01, true, false) };
                Native.SendInput((uint)escape.Length, escape, KeySender.InputSize);
                Thread.Sleep(400);
            }
            TakeFocus();
        }

        private static void TapLeftControl()
        {
            Tap(VirtualKeys.LeftControl, 0x1D, false);
        }

        private static bool WaitFor(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        private static void ExpectText(TextBoxBase box, string expected, string what)
        {
            bool matched = WaitFor(delegate { return host.TextOf(box) == expected; });
            Expect.True(matched, what + ": expected \"" + expected + "\", got \"" + host.TextOf(box)
                + "\" (status: " + engine.Status + ")");
        }

        private static void ExpectLayout(KeyboardLayout layout, string what)
        {
            bool matched = WaitFor(delegate { return Layouts.IdOf(Native.GetKeyboardLayout(host.ThreadId)) == layout.Id; });
            Expect.True(matched, what + ": the layout is not " + layout.Name + " (status: " + engine.Status + ")");
        }

        private static void ExpectCaret(TextBoxBase box, int position, string what)
        {
            bool matched = WaitFor(delegate
            {
                return host.SelectionStartOf(box) == position && host.SelectionLengthOf(box) == 0;
            });
            Expect.True(matched, what + ": expected the caret at " + position + ", got "
                + host.SelectionStartOf(box) + "+" + host.SelectionLengthOf(box));
        }

        /// Starts a scenario in `box`: focused, with the English layout, holding `text`.
        private static void Begin(TextBoxBase box, string text, int selectionStart, int selectionLength)
        {
            if (!HasFocus()) throw new FocusLost();
            engine.WaitUntilIdle(8000);
            if (Layouts.IdOf(Native.GetKeyboardLayout(host.ThreadId)) != english.Id) engine.Layouts.Select(english, false);
            host.Set(box, text, selectionStart, selectionLength);
            Focus(box);
        }

        private static void BeginCanvas(string text, int selectionStart, int selectionLength, bool copiesLine)
        {
            if (!HasFocus()) throw new FocusLost();
            engine.WaitUntilIdle(8000);
            if (Layouts.IdOf(Native.GetKeyboardLayout(host.ThreadId)) != english.Id) engine.Layouts.Select(english, false);
            host.Do(delegate
            {
                host.Canvas.Value = text;
                host.Canvas.SelectionStart = selectionStart;
                host.Canvas.SelectionLength = selectionLength;
                host.Canvas.CopiesLineWhenNothingSelected = copiesLine;
                host.Canvas.Invalidate();
            });
            Focus(host.Canvas);
        }

        private static void ExpectCanvas(string expected, string what)
        {
            bool matched = WaitFor(delegate { return host.Get(delegate { return host.Canvas.Value; }) == expected; });
            Expect.True(matched, what + ": expected \"" + expected + "\", got \""
                + host.Get(delegate { return host.Canvas.Value; }) + "\" (status: " + engine.Status + ")");
        }

        private static void ExpectClipboard(string expected, string what)
        {
            WinClipboard clipboard = new WinClipboard();
            bool matched = WaitFor(delegate { return clipboard.GetText() == expected; });
            Expect.True(matched, what + ": the clipboard was not restored");
        }

        // ---- Mac shortcuts ----

        private const int Alt = VirtualKeys.LeftAlt;
        private const int Win = VirtualKeys.LeftWin;
        private const int Shift = VirtualKeys.LeftShift;
        private const int Ctrl = VirtualKeys.LeftControl;
        private const int F6 = VirtualKeys.F1 + 5;

        /// The scan code of a key, with 0x100 for the keys that are sent as extended.
        private static int ScanCodeOf(int key)
        {
            if (key == VirtualKeys.LeftWin) return 0x15B;
            bool extended = (key >= VirtualKeys.PageUp && key <= VirtualKeys.DownArrow)
                || key == VirtualKeys.Insert || key == VirtualKeys.Delete;
            return (int)Native.MapVirtualKey((uint)key, Native.MAPVK_VK_TO_VSC) | (extended ? 0x100 : 0);
        }

        private static void Hold(int key)
        {
            int scanCode = ScanCodeOf(key);
            Send(Key(key, scanCode & 0xFF, false, (scanCode & 0x100) != 0));
            Thread.Sleep(20);
        }

        private static void Release(int key)
        {
            int scanCode = ScanCodeOf(key);
            Send(Key(key, scanCode & 0xFF, true, (scanCode & 0x100) != 0));
            Thread.Sleep(20);
        }

        /// Holds all keys but the last, presses the last, and lets go in reverse order.
        private static void Chord(params int[] keys)
        {
            Thread.Sleep(40);
            foreach (int key in keys) Hold(key);
            for (int i = keys.Length - 1; i >= 0; i--) Release(keys[i]);
            Thread.Sleep(40);
            Expect.True(engine.WaitUntilIdle(8000), "the engine did not finish in time");
        }

        private static bool ModifiersUp()
        {
            foreach (int key in KeyRemapper.ModifierKeys)
            {
                if (Native.IsKeyDown(key)) return false;
            }
            return true;
        }

        /// Runs a scenario with the Mac shortcuts on, and checks that it leaves no
        /// modifier key down for the system.
        private static void WithMacShortcuts(Action scenario)
        {
            engine.MacShortcuts = true;
            Thread.Sleep(80);
            try
            {
                scenario();
                Expect.True(WaitFor(ModifiersUp), "a modifier key was left down");
            }
            finally
            {
                // Whatever went wrong, the keys this check holds go up.
                if (!ModifiersUp())
                {
                    List<Native.INPUT> releases = new List<Native.INPUT>();
                    foreach (int key in new[] { Alt, Win, Shift, Ctrl })
                    {
                        int scanCode = ScanCodeOf(key);
                        releases.Add(Key(key, scanCode & 0xFF, true, (scanCode & 0x100) != 0));
                    }
                    Native.SendInput((uint)releases.Count, releases.ToArray(), KeySender.InputSize);
                }
                engine.MacShortcuts = false;
                Thread.Sleep(80);
            }
        }

        private static void ExpectSelection(TextBoxBase box, int start, int length, string what)
        {
            bool matched = WaitFor(delegate
            {
                return host.SelectionStartOf(box) == start && host.SelectionLengthOf(box) == length;
            });
            Expect.True(matched, what + ": expected the selection " + start + "+" + length + ", got "
                + host.SelectionStartOf(box) + "+" + host.SelectionLengthOf(box));
        }

        /// What the canvas editor received since the last check: key presses and clicks
        /// with their modifiers.
        private static void ExpectReceived(string expected, string what)
        {
            string received = "";
            Func<bool> read = delegate
            {
                string more = host.Canvas.TakeReceived();
                if (more.Length > 0) received = received.Length == 0 ? more : received + " " + more;
                return received == expected;
            };
            if (WaitFor(read))
            {
                // Nothing may follow.
                Thread.Sleep(80);
                read();
            }
            Expect.True(received == expected, what + ": the editor should have received \"" + expected
                + "\", and received \"" + received + "\"");
        }

        private static void MacTextNavigation()
        {
            WithMacShortcuts(delegate
            {
                Begin(host.Plain, "one two three", 13, 0);
                Chord(Win, VirtualKeys.LeftArrow);
                ExpectSelection(host.Plain, 8, 0, "Win+Left");
                Chord(Win, Shift, VirtualKeys.LeftArrow);
                ExpectSelection(host.Plain, 4, 4, "Win+Shift+Left");
                Chord(Alt, VirtualKeys.LeftArrow);
                ExpectSelection(host.Plain, 0, 0, "Alt+Left");
                Chord(Win, VirtualKeys.RightArrow);
                ExpectSelection(host.Plain, 4, 0, "Win+Right");
                Chord(Alt, VirtualKeys.RightArrow);
                ExpectSelection(host.Plain, 13, 0, "Alt+Right");
                Chord(Alt, Shift, VirtualKeys.LeftArrow);
                ExpectSelection(host.Plain, 0, 13, "Alt+Shift+Left");
                // The same with Shift pressed first, and two keys during one hold.
                Begin(host.Plain, "one two three", 13, 0);
                Hold(Shift);
                Hold(Win);
                Hold(VirtualKeys.LeftArrow);
                Release(VirtualKeys.LeftArrow);
                Hold(VirtualKeys.LeftArrow);
                Release(VirtualKeys.LeftArrow);
                Release(Win);
                Release(Shift);
                ExpectSelection(host.Plain, 4, 9, "Shift+Win+Left twice");
                ExpectStartClosed("Win+arrows");

                Begin(host.Plain, "one two three", 7, 0);
                Chord(Alt, VirtualKeys.Backspace);
                ExpectText(host.Plain, " three", "Alt+Backspace");
                Chord(Alt, VirtualKeys.Delete);
                ExpectText(host.Plain, "", "Alt+Delete");
                // With a menu open or a modifier stuck, these letters would not arrive.
                Type("ok");
                ExpectText(host.Plain, "ok", "typing after the shortcuts");

                Begin(host.Multiline, "one\r\ntwo\r\nthree", 6, 0);
                Chord(Alt, VirtualKeys.UpArrow);
                ExpectSelection(host.Multiline, 0, 0, "Alt+Up");
                Chord(Alt, VirtualKeys.DownArrow);
                ExpectSelection(host.Multiline, 15, 0, "Alt+Down");
                Chord(Alt, Shift, VirtualKeys.UpArrow);
                ExpectSelection(host.Multiline, 0, 15, "Alt+Shift+Up");
                Chord(Alt, VirtualKeys.DownArrow);
                Chord(Alt, VirtualKeys.Backspace);
                ExpectText(host.Multiline, "one\r\ntwo\r\n", "Alt+Backspace deletes to the start of the line only");
                ExpectLayout(english, "the shortcuts must not switch the layout");
            });
        }

        /// Whether Alt+Shift changes the input language on this computer. The keys are
        /// tagged as KeyFlip's own, so that KeyFlip leaves them alone.
        private static bool AltShiftChangesLanguage()
        {
            List<KeyStroke> keys = new List<KeyStroke>();
            keys.Add(new KeyStroke(VirtualKeys.LeftAlt, false));
            keys.Add(new KeyStroke(VirtualKeys.LeftShift, false));
            keys.Add(new KeyStroke(VirtualKeys.LeftShift, true));
            // Keeps the release of Alt from opening the menu bar.
            keys.Add(new KeyStroke(KeyRemapper.MaskKey, false));
            keys.Add(new KeyStroke(KeyRemapper.MaskKey, true));
            keys.Add(new KeyStroke(VirtualKeys.LeftAlt, true));
            Send(KeySender.Strokes(keys));
            bool changed = false;
            for (int waited = 0; waited < 600 && !changed; waited += 20)
            {
                Thread.Sleep(20);
                changed = Layouts.IdOf(Native.GetKeyboardLayout(host.ThreadId)) != english.Id;
            }
            if (changed) engine.Layouts.Select(english, false);
            ExpectLayout(english, "back to English");
            return changed;
        }

        private static void MacKeysReceived()
        {
            WithMacShortcuts(delegate
            {
                BeginCanvas("", 0, 0, false);
                host.Canvas.TakeReceived();
                Chord(Alt, 'C');
                ExpectReceived("Ctrl+C", "Alt+C");
                Chord(Shift, Alt, 'T');
                ExpectReceived("Ctrl+Shift+T", "Shift+Alt+T");
                Chord(Ctrl, 'C');
                ExpectReceived("Ctrl+C", "Ctrl+C");
                Chord(Alt, Shift, 'Z');
                ExpectReceived("Ctrl+Y", "Alt+Shift+Z");
                Chord(Alt, 'G');
                ExpectReceived("F3", "Alt+G");
                Chord(Alt, Shift, 'G');
                ExpectReceived("Shift+F3", "Alt+Shift+G");
                Chord(Win, VirtualKeys.Backspace);
                ExpectReceived("Ctrl+Back", "Win+Backspace");
                Chord(Win, VirtualKeys.Delete);
                ExpectReceived("Ctrl+Delete", "Win+Delete");
                Chord(Alt, Win, VirtualKeys.RightArrow);
                ExpectReceived("Ctrl+Tab", "Alt+Win+Right");
                Chord(Alt, Shift, VirtualKeys.LeftBracket);
                ExpectReceived("Ctrl+Shift+Tab", "Alt+Shift+[");
                Chord(Alt, F6);
                ExpectReceived("Alt+F6", "Alt+F6");
                // Several keys during one hold, a held key that repeats, and Alt let go first.
                Hold(Alt);
                Hold('C');
                Release('C');
                Hold('V');
                Hold('V');
                Release(Alt);
                Release('V');
                ExpectReceived("Ctrl+C Ctrl+V Ctrl+V", "Alt held for several keys");
                ExpectStartClosed("Win shortcuts");

                // Shift goes up while Alt is still down for the system, with no key in
                // between: to Windows that is the shortcut that changes the input
                // language, unless KeyFlip prevents it.
                bool changes = AltShiftChangesLanguage();
                host.Canvas.TakeReceived();
                Hold(Alt);
                Hold(F6);
                Release(F6);
                Hold(Shift);
                Hold('Z');
                Release('Z');
                Release(Shift);
                Release(Alt);
                ExpectReceived("Alt+F6 Ctrl+Y", "Alt+F6, then Shift+Z");
                Thread.Sleep(400);
                ExpectLayout(english, "a shortcut changed the input language");
                if (!changes) Console.WriteLine("     Alt+Shift does not change the input language here: that check proved nothing.");
            });
        }

        private static void MacTaps()
        {
            WithMacShortcuts(delegate
            {
                Begin(host.Plain, "", 0, 0);
                Type("ghbdtn");
                ExpectText(host.Plain, "ghbdtn", "typing");
                TapAlt();
                ExpectText(host.Plain, "привет", "Alt");
                ExpectLayout(russian, "Alt");
                // With the menu bar active, "f" would open the File menu instead of typing.
                Type("f");
                ExpectText(host.Plain, "привета", "typing after Alt");
                TapAlt();
                ExpectText(host.Plain, "ghbdtnf", "Alt back");
                ExpectLayout(english, "Alt back");
                TapLeftControl();
                ExpectText(host.Plain, "GHBDTNF", "Ctrl");
                // A shortcut is not a tap.
                string status = engine.Status;
                Chord(Alt, VirtualKeys.RightArrow);
                Chord(Win, VirtualKeys.LeftArrow);
                Thread.Sleep(150);
                ExpectText(host.Plain, "GHBDTNF", "after shortcuts");
                Expect.Equal(engine.Status, status);
                ExpectLayout(english, "after shortcuts");

                engine.LayoutTapKey = LayoutTapKey.Win;
                try
                {
                    Begin(host.Plain, "", 0, 0);
                    Type("ghbdtn");
                    TapWin();
                    ExpectText(host.Plain, "привет", "Win");
                    ExpectLayout(russian, "Win");
                    ExpectStartClosed("Win");
                    TapWin();
                    ExpectText(host.Plain, "ghbdtn", "Win back");
                    ExpectLayout(english, "Win back");
                    Chord(Win, VirtualKeys.LeftArrow);
                    ExpectSelection(host.Plain, 0, 0, "Win+Left with Win as the conversion key");
                    ExpectText(host.Plain, "ghbdtn", "Win+Left is not a tap");
                    ExpectStartClosed("Win+Left");
                }
                finally
                {
                    engine.LayoutTapKey = LayoutTapKey.Alt;
                    RecoverFromStart();
                }
            });
        }

        private static bool InForeground(IntPtr window)
        {
            return WaitFor(delegate { return Native.GetForegroundWindow() == window; });
        }

        private static void MacWindows()
        {
            WithMacShortcuts(delegate
            {
                Begin(host.Plain, "", 0, 0);
                second = host.OpenSecond("KeyFlip end-to-end check — second window");
                try
                {
                    Thread.Sleep(200);
                    Expect.True(HasFocus(), "the second window must open behind the first");
                    Chord(Alt, VirtualKeys.Backquote);
                    Expect.True(InForeground(second), "Alt+` did not bring the other window forward");
                    Chord(Alt, VirtualKeys.Backquote);
                    Expect.True(InForeground(host.Handle), "a second Alt+` did not go back to the first window");
                    Chord(Alt, Shift, VirtualKeys.Backquote);
                    Expect.True(InForeground(second), "Alt+Shift+` did not bring the other window forward");
                    Chord(Alt, 'M');
                    Expect.True(WaitFor(delegate { return Native.IsIconic(second); }), "Alt+M did not minimize the window");
                }
                finally
                {
                    host.CloseSecond();
                    second = IntPtr.Zero;
                    Thread.Sleep(100);
                    if (!HasFocus()) TakeFocus();
                }
                // The first window has no minimize button: Alt+M leaves it alone.
                Chord(Alt, 'M');
                Thread.Sleep(200);
                Expect.True(!Native.IsIconic(host.Handle) && HasFocus(), "Alt+M minimized a window that cannot be minimized");
            });
        }

        private static void Click()
        {
            Native.INPUT down = new Native.INPUT();
            down.type = 0; // INPUT_MOUSE
            down.u.mi.dwFlags = 0x0002; // MOUSEEVENTF_LEFTDOWN
            Native.INPUT up = down;
            up.u.mi.dwFlags = 0x0004; // MOUSEEVENTF_LEFTUP
            Send(down);
            Thread.Sleep(30);
            Send(up);
            Thread.Sleep(30);
        }

        private static void MacClick()
        {
            Point pointer = Cursor.Position;
            try
            {
                WithMacShortcuts(delegate
                {
                    BeginCanvas("", 0, 0, false);
                    host.Canvas.TakeReceived();
                    Cursor.Position = host.CenterOf(host.Canvas);
                    Thread.Sleep(80);
                    Click();
                    ExpectReceived("Click", "a click");
                    Hold(Alt);
                    Click();
                    Click();
                    Release(Alt);
                    ExpectReceived("Ctrl+Click Ctrl+Click", "Alt+click");
                    Hold(Shift);
                    Hold(Alt);
                    Click();
                    Release(Alt);
                    Release(Shift);
                    ExpectReceived("Ctrl+Shift+Click", "Shift+Alt+click");
                    Click();
                    ExpectReceived("Click", "a click afterwards");
                    Thread.Sleep(300);
                    ExpectLayout(english, "a click must not switch the layout");
                });
            }
            finally
            {
                Cursor.Position = pointer;
            }
        }

        private static void MacExcludedProgram()
        {
            WithMacShortcuts(delegate
            {
                BeginCanvas("", 0, 0, false);
                host.Canvas.TakeReceived();
                uint process;
                Native.GetWindowThreadProcessId(host.Handle, out process);
                engine.UseProgramsWithoutMacShortcuts("other, " + Native.ProcessName(process) + ".exe");
                try
                {
                    Chord(Alt, 'C');
                    ExpectReceived("Alt+C", "Alt+C in an excluded program");
                    Chord(Alt, Shift, 'Z');
                    ExpectReceived("Alt+Shift+Z", "Alt+Shift+Z in an excluded program");
                }
                finally
                {
                    engine.UseProgramsWithoutMacShortcuts("");
                }
                Chord(Alt, 'C');
                ExpectReceived("Ctrl+C", "Alt+C when the program is no longer excluded");
            });
        }

        // ---- Scenarios ----

        private static void CanvasTypedWord()
        {
            BeginCanvas("", 0, 0, false);
            Type("ghbdtn ");
            ExpectCanvas("ghbdtn ", "typing");
            TapAlt();
            ExpectCanvas("привет ", "Alt");
            ExpectLayout(russian, "Alt");
            Expect.Equal(engine.Status, "Layout converted.");
            TapAlt();
            ExpectCanvas("ghbdtn ", "Alt back");
            ExpectLayout(english, "Alt back");
            TapLeftControl();
            ExpectCanvas("GHBDTN ", "Ctrl");
        }

        private static void CanvasSelection()
        {
            string before = new WinClipboard().GetText();
            BeginCanvas("one ghbdtn two", 4, 6, false);
            TapLeftControl();
            ExpectCanvas("one GHBDTN two", "Ctrl");
            Expect.Equal(engine.Status, "Letter case inverted.");
            ExpectClipboard(before, "Ctrl");
            BeginCanvas("one ghbdtn two", 4, 6, false);
            TapAlt();
            ExpectCanvas("one привет two", "Alt");
            ExpectLayout(russian, "Alt");
            ExpectClipboard(before, "Alt");
            host.Do(delegate
            {
                host.Canvas.SelectionStart = 4;
                host.Canvas.SelectionLength = 6;
            });
            engine.InvalidateContext();
            TapAlt();
            ExpectCanvas("one ghbdtn two", "Alt back");
            ExpectLayout(english, "Alt back");
            ExpectClipboard(before, "Alt back");
        }

        private static void CanvasNothingSelected()
        {
            string before = new WinClipboard().GetText();
            BeginCanvas("one ghbdtn two", 14, 0, false);
            TapLeftControl();
            ExpectCanvas("one ghbdtn two", "Ctrl");
            Expect.True(engine.Status.StartsWith("Cannot read this editor's selection", StringComparison.Ordinal),
                "unexpected status: " + engine.Status);
            TapAlt();
            ExpectCanvas("one ghbdtn two", "Alt");
            ExpectLayout(russian, "Alt only switches the layout");
            Expect.True(engine.Status.StartsWith("Keyboard layout switched; text was not changed", StringComparison.Ordinal),
                "unexpected status: " + engine.Status);
            TapAlt();
            ExpectLayout(english, "Alt back");
            ExpectClipboard(before, "nothing selected");
        }

        private static void CanvasWholeLine()
        {
            string before = new WinClipboard().GetText();
            BeginCanvas("one ghbdtn two", 14, 0, true);
            TapLeftControl();
            ExpectCanvas("one ghbdtn two", "Ctrl");
            ExpectClipboard(before, "whole line");
            Expect.True(engine.Status.StartsWith("Cannot read this editor's selection", StringComparison.Ordinal),
                "unexpected status: " + engine.Status);
        }

        private static void TypedWordLayout()
        {
            Begin(host.Plain, "", 0, 0);
            Type("ghbdtn");
            ExpectText(host.Plain, "ghbdtn", "typing");
            TapAlt();
            ExpectText(host.Plain, "привет", "first Alt");
            ExpectLayout(russian, "first Alt");
            Expect.Equal(engine.Status, "Layout converted.");
            TapAlt();
            ExpectText(host.Plain, "ghbdtn", "second Alt");
            ExpectLayout(english, "second Alt");
            ExpectCaret(host.Plain, 6, "second Alt");
        }

        private static void TypedWordCase()
        {
            Begin(host.Plain, "", 0, 0);
            Type("Hello");
            ExpectText(host.Plain, "Hello", "typing");
            TapLeftControl();
            ExpectText(host.Plain, "hELLO", "first Ctrl");
            Expect.Equal(engine.Status, "Letter case inverted.");
            TapLeftControl();
            ExpectText(host.Plain, "Hello", "second Ctrl");
            ExpectLayout(english, "case inversion must not switch the layout");
        }

        private static void TypedWordTrailingSpace()
        {
            Begin(host.Plain, "", 0, 0);
            Type("first ghbdtn ");
            ExpectText(host.Plain, "first ghbdtn ", "typing");
            TapAlt();
            ExpectText(host.Plain, "first привет ", "Alt");
            ExpectCaret(host.Plain, 13, "Alt");
            ExpectLayout(russian, "Alt");
            // Keep typing in the new layout, then convert that word back.
            Type("vbh");
            ExpectText(host.Plain, "first привет мир", "typing in Russian");
            TapAlt();
            ExpectText(host.Plain, "first привет vbh", "Alt on the Russian word");
            ExpectLayout(english, "Alt on the Russian word");
        }

        private static bool CapsLockOn()
        {
            // Asked on the window's own thread, whose key state follows its input.
            return host.Get(delegate { return Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock); });
        }

        private static void TypedWordCapsLock()
        {
            Begin(host.Plain, "", 0, 0);
            bool initial = CapsLockOn();
            try
            {
                if (!initial) Press(Native.VK_CAPITAL, 0x3A, false, 30);
                Expect.True(WaitFor(CapsLockOn), "Caps Lock did not turn on");
                Type("Ghbdtn");
                ExpectText(host.Plain, "gHBDTN", "typing with Caps Lock");
                TapAlt();
                ExpectText(host.Plain, "пРИВЕТ", "Alt");
                ExpectLayout(russian, "Alt");
                TapAlt();
                ExpectText(host.Plain, "gHBDTN", "second Alt");
                ExpectLayout(english, "second Alt");
            }
            finally
            {
                if (HasFocus() && CapsLockOn() != initial) Press(Native.VK_CAPITAL, 0x3A, false, 30);
                WaitFor(delegate { return CapsLockOn() == initial; });
            }
        }

        private static void PressCapsLock(bool withShift)
        {
            Thread.Sleep(60);
            if (withShift) Send(Key(VirtualKeys.LeftShift, 0x2A, false, false));
            Press(Native.VK_CAPITAL, 0x3A, false, 30);
            if (withShift) Send(Key(VirtualKeys.LeftShift, 0x2A, true, false));
            Thread.Sleep(30);
            Expect.True(engine.WaitUntilIdle(8000), "the engine did not finish in time");
        }

        private static void HoldCapsLock()
        {
            Thread.Sleep(60);
            Press(Native.VK_CAPITAL, 0x3A, false, 650);
            Thread.Sleep(30);
            Expect.True(engine.WaitUntilIdle(8000), "the engine did not finish in time");
        }

        private static void CapsLockSwitchesLayout()
        {
            Begin(host.Plain, "", 0, 0);
            bool initial = CapsLockOn();
            engine.CapsLockSwitchesLayout = true;
            try
            {
                Type("ghbdtn");
                PressCapsLock(false);
                ExpectLayout(russian, "Caps Lock");
                ExpectText(host.Plain, "ghbdtn", "Caps Lock must not change text");
                Expect.Equal(engine.Status, "Keyboard layout switched.");
                Expect.True(CapsLockOn() == initial, "Caps Lock alone changed the Caps Lock state");
                // The word typed before the switch is still tracked.
                TapAlt();
                ExpectText(host.Plain, "привет", "Alt after Caps Lock");
                ExpectLayout(russian, "Alt after Caps Lock");
                Type(" vbh");
                ExpectText(host.Plain, "привет мир", "typing in Russian, in lower case");
                PressCapsLock(false);
                ExpectLayout(english, "second Caps Lock");
                ExpectText(host.Plain, "привет мир", "second Caps Lock must not change text");
                // With Shift it is the ordinary Caps Lock.
                PressCapsLock(true);
                Expect.True(WaitFor(delegate { return CapsLockOn() != initial; }), "Shift+Caps Lock did not toggle Caps Lock");
                ExpectLayout(english, "Shift+Caps Lock must not switch the layout");
                PressCapsLock(true);
                Expect.True(WaitFor(delegate { return CapsLockOn() == initial; }), "Shift+Caps Lock did not toggle Caps Lock back");
                // A long press is the regular Caps Lock too, and leaves the layout as it was.
                HoldCapsLock();
                Expect.True(WaitFor(delegate { return CapsLockOn() != initial; }), "a long press did not toggle Caps Lock");
                ExpectLayout(english, "a long press must leave the layout as it was");
                ExpectText(host.Plain, "привет мир", "a long press must not change text");
                Expect.Equal(engine.Status, "Caps Lock toggled; the keyboard layout is as it was.");
                // With auto-repeat, that happens while the key is still down, and once.
                Thread.Sleep(60);
                Send(Key(Native.VK_CAPITAL, 0x3A, false, false));
                Thread.Sleep(300);
                Send(Key(Native.VK_CAPITAL, 0x3A, false, false));
                Thread.Sleep(300);
                Send(Key(Native.VK_CAPITAL, 0x3A, false, false));
                Expect.True(WaitFor(delegate { return CapsLockOn() == initial; }), "a held Caps Lock did not toggle Caps Lock before its release");
                Send(Key(Native.VK_CAPITAL, 0x3A, false, false));
                Send(Key(Native.VK_CAPITAL, 0x3A, true, false));
                Thread.Sleep(30);
                Expect.True(engine.WaitUntilIdle(8000), "the engine did not finish in time");
                Thread.Sleep(200);
                Expect.True(CapsLockOn() == initial, "the release of a held Caps Lock toggled it again");
                ExpectLayout(english, "a held Caps Lock must leave the layout as it was");
            }
            finally
            {
                engine.CapsLockSwitchesLayout = false;
                if (HasFocus() && CapsLockOn() != initial) Press(Native.VK_CAPITAL, 0x3A, false, 30);
                WaitFor(delegate { return CapsLockOn() == initial; });
            }
        }

        private static void WinKeyConverts()
        {
            Begin(host.Plain, "", 0, 0);
            engine.LayoutTapKey = LayoutTapKey.Win;
            try
            {
                Type("ghbdtn");
                TapWin();
                ExpectText(host.Plain, "привет", "Win");
                ExpectLayout(russian, "Win");
                ExpectStartClosed("Win");
                // With the Start menu open, this would go to its search box.
                Type("f");
                ExpectText(host.Plain, "привета", "typing after Win");
                TapWin();
                ExpectText(host.Plain, "ghbdtnf", "Win back");
                ExpectLayout(english, "Win back");
                ExpectStartClosed("Win back");
                Expect.Equal(engine.ShortcutStatus, "Win tap detected.");
            }
            finally
            {
                engine.LayoutTapKey = LayoutTapKey.Alt;
                RecoverFromStart();
            }
        }

        /// A second tap that arrives while the first is still being handled starts
        /// nothing, and its release must not open the menu bar or the Start menu.
        private static void OverlappingTapOpensNoMenu()
        {
            foreach (bool win in new[] { false, true })
            {
                string what = win ? "Win" : "Alt";
                int key = win ? VirtualKeys.LeftWin : VirtualKeys.LeftAlt;
                int scanCode = win ? 0x5B : 0x38;
                BeginCanvas("one ghbdtn two", 14, 0, false);
                int overlapping = 0;
                Action<string> trace = engine.Trace;
                engine.LayoutTapKey = win ? LayoutTapKey.Win : LayoutTapKey.Alt;
                engine.Trace = delegate(string message)
                {
                    if (message.EndsWith("(busy)", StringComparison.Ordinal)) Interlocked.Increment(ref overlapping);
                    if (trace != null) trace(message);
                };
                try
                {
                    // Nothing is selected, so the first tap waits for a copy that does
                    // not come; the second tap arrives in the middle of that wait.
                    Thread.Sleep(60);
                    Press(key, scanCode, win, 30);
                    Thread.Sleep(450);
                    Press(key, scanCode, win, 30);
                    Thread.Sleep(30);
                    Expect.True(engine.WaitUntilIdle(8000), "the engine did not finish in time");
                    Expect.True(overlapping > 0, what + ": the second tap did not arrive during the first action");
                    ExpectStartClosed(what);
                    ExpectCanvas("one ghbdtn two", what);
                    // With a menu open, this letter would not reach the editor.
                    Type("z");
                    bool typed = WaitFor(delegate { return host.Get(delegate { return host.Canvas.Value; }).Length == 15; });
                    Expect.True(typed, what + ": a letter typed after the taps did not reach the editor, which holds \""
                        + host.Get(delegate { return host.Canvas.Value; }) + "\"");
                }
                finally
                {
                    engine.Trace = trace;
                    engine.LayoutTapKey = LayoutTapKey.Alt;
                    RecoverFromStart();
                }
            }
        }

        private static void MenuStaysClosed()
        {
            Begin(host.Plain, "", 0, 0);
            Type("ghbdtn");
            ExpectText(host.Plain, "ghbdtn", "typing");
            TapAlt();
            ExpectText(host.Plain, "привет", "Alt");
            // With the menu bar active, "f" would open the File menu instead of typing.
            Type("f");
            ExpectText(host.Plain, "привета", "typing after Alt");
            TapAlt();
            ExpectText(host.Plain, "ghbdtnf", "Alt back");
            ExpectLayout(english, "Alt back");
        }

        private static void ExistingWord()
        {
            Begin(host.Plain, "first ghbdtn", 12, 0);
            TapAlt();
            ExpectText(host.Plain, "first привет", "Alt");
            ExpectCaret(host.Plain, 12, "Alt");
            ExpectLayout(russian, "Alt");
            Expect.Equal(engine.Status, "Layout converted.");
            TapAlt();
            ExpectText(host.Plain, "first ghbdtn", "Alt back");
            ExpectLayout(english, "Alt back");
        }

        private static void SelectionInSentence()
        {
            Begin(host.Plain, "one ghbdtn two", 4, 6);
            TapLeftControl();
            ExpectText(host.Plain, "one GHBDTN two", "Ctrl");
            ExpectCaret(host.Plain, 10, "the caret belongs after the replaced selection");
            Begin(host.Plain, "one ghbdtn two", 4, 6);
            TapAlt();
            ExpectText(host.Plain, "one привет two", "Alt");
            ExpectLayout(russian, "Alt");
            host.Do(delegate { host.Plain.Select(4, 6); });
            engine.InvalidateContext();
            TapAlt();
            ExpectText(host.Plain, "one ghbdtn two", "Alt back");
            ExpectLayout(english, "Alt back");
        }

        private static void ExistingWordTrailingSpaces()
        {
            Begin(host.Plain, "first ghbdtn  ", 14, 0);
            TapLeftControl();
            ExpectText(host.Plain, "first GHBDTN  ", "Ctrl");
            ExpectCaret(host.Plain, 14, "Ctrl");
        }

        private static void RichEdit()
        {
            Begin(host.Rich, "", 0, 0);
            Type("ghbdtn");
            ExpectText(host.Rich, "ghbdtn", "typing");
            TapAlt();
            ExpectText(host.Rich, "привет", "Alt");
            ExpectLayout(russian, "Alt");
            TapAlt();
            ExpectText(host.Rich, "ghbdtn", "Alt back");
            ExpectLayout(english, "Alt back");
            Begin(host.Rich, "line one\nfirst ghbdtn", 21, 0);
            TapLeftControl();
            ExpectText(host.Rich, "line one\nfirst GHBDTN", "Ctrl on an existing word");
            ExpectCaret(host.Rich, 21, "Ctrl on an existing word");
        }

        private static void ShortcutsDoNotTrigger()
        {
            Begin(host.Plain, "", 0, 0);
            Type("ghbdtn");
            ExpectText(host.Plain, "ghbdtn", "typing");
            string status = engine.Status;
            // Ctrl+C: a shortcut, not a tap.
            Send(Key(VirtualKeys.LeftControl, 0x1D, false, false));
            Thread.Sleep(30);
            Send(Key(0x43, 0x2E, false, false));
            Send(Key(0x43, 0x2E, true, false));
            Thread.Sleep(30);
            Send(Key(VirtualKeys.LeftControl, 0x1D, true, false));
            // Right Ctrl and Shift alone.
            Press(VirtualKeys.RightControl, 0x1D, true, 30);
            Press(VirtualKeys.LeftShift, 0x2A, false, 30);
            // Left Ctrl held for longer than a tap.
            Press(VirtualKeys.LeftControl, 0x1D, false, 1150);
            Thread.Sleep(150);
            engine.WaitUntilIdle(8000);
            ExpectText(host.Plain, "ghbdtn", "after shortcuts");
            Expect.Equal(engine.Status, status);
            ExpectLayout(english, "after shortcuts");
        }

        private static void PasswordField()
        {
            Begin(host.Password, "", 0, 0);
            Type("ghbdtn");
            ExpectText(host.Password, "ghbdtn", "typing");
            Expect.Equal(engine.TypedWordForChecks(), new TypedWordBuffer().ToString());
            TapLeftControl();
            ExpectText(host.Password, "ghbdtn", "Ctrl");
            Expect.Equal(engine.Status, "Password fields are excluded.");
            TapAlt();
            ExpectText(host.Password, "ghbdtn", "Alt");
            ExpectLayout(russian, "Alt only switches the layout");
            TapAlt();
            ExpectText(host.Password, "ghbdtn", "Alt back");
            ExpectLayout(english, "Alt back");
        }

        private static void EmptyField()
        {
            Begin(host.Plain, "", 0, 0);
            TapAlt();
            ExpectText(host.Plain, "", "Alt");
            ExpectLayout(russian, "Alt");
            Expect.Equal(engine.Status, "Keyboard layout switched.");
            TapAlt();
            ExpectLayout(english, "Alt back");
            TapLeftControl();
            Expect.Equal(engine.Status, "Select text or put the cursor after a word.");
        }
    }
}
