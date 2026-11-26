using System;
using System.Collections.Generic;
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
                    new Scenario("Empty field: Alt only switches the layout", EmptyField)
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

        private static void Send(params Native.INPUT[] inputs)
        {
            if (!HasFocus()) throw new FocusLost();
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
