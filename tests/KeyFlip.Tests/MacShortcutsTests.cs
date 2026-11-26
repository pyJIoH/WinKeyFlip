using System.Collections.Generic;
using KeyFlip.Core;

namespace KeyFlip.Tests
{
    internal static class MacShortcutsTests
    {
        private const int Alt = VirtualKeys.LeftAlt;
        private const int Win = VirtualKeys.LeftWin;
        private const int Ctrl = VirtualKeys.LeftControl;
        private const int Shift = VirtualKeys.LeftShift;
        private const int Left = VirtualKeys.LeftArrow;
        private const int Right = VirtualKeys.RightArrow;
        private const int Up = VirtualKeys.UpArrow;
        private const int Down = VirtualKeys.DownArrow;

        /// A keyboard, the remapper and the system behind it. The system keeps the state
        /// of the modifier keys and notes what an application would receive: every key
        /// press with the modifiers that are down at that moment. It also counts what
        /// synthetic modifier events must never cause: a menu opened by a lone Alt, the
        /// Start menu opened by a lone Windows key, and the input language changed by
        /// two modifiers pressed together.
        private sealed class Machine : IShortcutContext
        {
            public readonly KeyRemapper Remapper = new KeyRemapper();
            public readonly List<ShortcutCommand> Commands = new List<ShortcutCommand>();
            public bool Enabled = true;
            public AppKind Kind = AppKind.Other;
            public bool Caret;
            public int Os;
            public int Tick = 100000;
            public int Menus;
            public int Starts;
            public int LanguageSwitches;
            private readonly List<string> seen = new List<string>();
            private bool altAlone;
            private bool winAlone;
            private bool pairAlone;

            public bool Active { get { return Enabled; } }
            public AppKind App { get { return Kind; } }
            public bool EditingText { get { return Caret; } }

            private void Apply(int key, bool up)
            {
                int bit = KeyRemapper.BitOf(key);
                if (bit == 0)
                {
                    if (up) return;
                    altAlone = false;
                    winAlone = false;
                    pairAlone = false;
                    if (key != KeyRemapper.MaskKey) seen.Add(Describe(Os) + Name(key));
                    return;
                }
                if (!up)
                {
                    if ((Os & bit) != 0) return;
                    Os |= bit;
                    if ((bit & KeyRemapper.AltBits) != 0) altAlone = true;
                    if ((bit & KeyRemapper.WinBits) != 0) winAlone = true;
                    if ((Os & KeyRemapper.ShiftBits) != 0 && (Os & (KeyRemapper.ControlBits | KeyRemapper.AltBits)) != 0)
                    {
                        pairAlone = true;
                    }
                    return;
                }
                if ((Os & bit) == 0) return;
                Os &= ~bit;
                if ((bit & KeyRemapper.AltBits) != 0 && altAlone) Menus++;
                if ((bit & KeyRemapper.WinBits) != 0 && winAlone) Starts++;
                if ((bit & KeyRemapper.WinBits) == 0 && pairAlone) LanguageSwitches++;
                if ((bit & KeyRemapper.AltBits) != 0) altAlone = false;
                if ((bit & KeyRemapper.WinBits) != 0) winAlone = false;
                pairAlone = false;
            }

            private void ApplyOutput()
            {
                foreach (KeyStroke stroke in Remapper.Output) Apply(stroke.Key, stroke.Up);
            }

            private bool Event(int key, int scanCode, bool down)
            {
                Tick += 20;
                bool swallowed = Remapper.Key(key, scanCode, down, Os, Tick, this);
                ApplyOutput();
                if (!swallowed) Apply(key, !down);
                if (Remapper.Command != ShortcutCommand.None) Commands.Add(Remapper.Command);
                return swallowed;
            }

            /// Returns true when the system never saw the event.
            public bool Down(int key)
            {
                return Event(key, 0, true);
            }

            public bool Down(int key, int scanCode)
            {
                return Event(key, scanCode, true);
            }

            public bool Up(int key)
            {
                return Event(key, 0, false);
            }

            public bool Up(int key, int scanCode)
            {
                return Event(key, scanCode, false);
            }

            public void Press(int key)
            {
                Down(key);
                Up(key);
            }

            public void Click()
            {
                Tick += 20;
                Remapper.MouseDown(Os, this);
                ApplyOutput();
                seen.Add(Describe(Os) + "Click");
            }

            public void Reset()
            {
                Remapper.Reset(Os);
                ApplyOutput();
            }

            /// Holds the modifiers, presses the key, lets everything go, and returns
            /// what the application received.
            public string Chord(params int[] keys)
            {
                for (int i = 0; i < keys.Length - 1; i++) Down(keys[i]);
                Press(keys[keys.Length - 1]);
                for (int i = keys.Length - 2; i >= 0; i--) Up(keys[i]);
                return Take();
            }

            public string Take()
            {
                string text = string.Join(" ", seen);
                seen.Clear();
                return text;
            }

            private static string Describe(int keys)
            {
                string text = "";
                if ((keys & KeyRemapper.ControlBits) != 0) text += "Ctrl+";
                if ((keys & KeyRemapper.AltBits) != 0) text += "Alt+";
                if ((keys & KeyRemapper.ShiftBits) != 0) text += "Shift+";
                if ((keys & KeyRemapper.WinBits) != 0) text += "Win+";
                return text;
            }

            private static string Name(int key)
            {
                if ((key >= '0' && key <= '9') || (key >= 'A' && key <= 'Z')) return ((char)key).ToString();
                switch (key)
                {
                    case VirtualKeys.Backspace: return "Backspace";
                    case VirtualKeys.Tab: return "Tab";
                    case VirtualKeys.Enter: return "Enter";
                    case VirtualKeys.Escape: return "Esc";
                    case VirtualKeys.Space: return "Space";
                    case VirtualKeys.PageUp: return "PageUp";
                    case VirtualKeys.PageDown: return "PageDown";
                    case VirtualKeys.End: return "End";
                    case VirtualKeys.Home: return "Home";
                    case VirtualKeys.LeftArrow: return "Left";
                    case VirtualKeys.UpArrow: return "Up";
                    case VirtualKeys.RightArrow: return "Right";
                    case VirtualKeys.DownArrow: return "Down";
                    case VirtualKeys.PrintScreen: return "PrintScreen";
                    case VirtualKeys.Delete: return "Delete";
                    case VirtualKeys.F3: return "F3";
                    case VirtualKeys.F4: return "F4";
                    case VirtualKeys.F11: return "F11";
                    case VirtualKeys.Comma: return ",";
                    case VirtualKeys.Period: return ".";
                    case VirtualKeys.Backquote: return "`";
                    case VirtualKeys.LeftBracket: return "[";
                    case VirtualKeys.RightBracket: return "]";
                    default: return "0x" + key.ToString("X2");
                }
            }
        }

        public static void Add(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Mac shortcuts: Alt is Command, so Alt+C is Ctrl+C", GeneralRule));
            scenarios.Add(new Scenario("Mac shortcuts: a lone Alt, auto-repeat and what the tap detector is told", LoneModifiers));
            scenarios.Add(new Scenario("Mac shortcuts: moving and deleting by word, line and document", Navigation));
            scenarios.Add(new Scenario("Mac shortcuts: Alt+Tab, function keys and Win shortcuts stay as they are", SystemShortcuts));
            scenarios.Add(new Scenario("Mac shortcuts: quit, search, redo, find, tabs, screenshots, snapping", Exceptions));
            scenarios.Add(new Scenario("Mac shortcuts: commands for windows and the lock screen", Commands));
            scenarios.Add(new Scenario("Mac shortcuts: terminals, browsers, folders, code editors, Office", Programs));
            scenarios.Add(new Scenario("Mac shortcuts: Command+click is Ctrl+click", Clicks));
            scenarios.Add(new Scenario("Mac shortcuts: excluded programs, and changing program with Alt held", Excluded));
            scenarios.Add(new Scenario("Mac shortcuts: AltGr is not Command", AltGr));
            scenarios.Add(new Scenario("Mac shortcuts: a release that never arrived does not leave Command held", LostReleases));
            scenarios.Add(new Scenario("Mac shortcuts: programs are recognized by executable and window class", ProgramKinds));
        }

        private static void ExpectQuiet(Machine machine)
        {
            Expect.Equal(machine.Os, 0);
            Expect.Equal(machine.Remapper.Held, 0);
            Expect.Equal(machine.Menus, 0);
            Expect.Equal(machine.Starts, 0);
            Expect.Equal(machine.LanguageSwitches, 0);
        }

        private static void GeneralRule()
        {
            Machine machine = new Machine();
            Expect.Equal(machine.Chord(Alt, 'C'), "Ctrl+C");
            Expect.Equal(machine.Chord(Alt, Shift, 'T'), "Ctrl+Shift+T");
            Expect.Equal(machine.Chord(Shift, Alt, 'T'), "Ctrl+Shift+T");
            Expect.Equal(machine.Chord(VirtualKeys.RightAlt, '1'), "Ctrl+1");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Enter), "Ctrl+Enter");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Comma), "Ctrl+,");
            Expect.Equal(machine.Chord(Alt, Win, 'L'), "Ctrl+Alt+L");
            Expect.Equal(machine.Chord(Win, Alt, 'L'), "Ctrl+Alt+L");
            ExpectQuiet(machine);

            // Several keys during one hold.
            machine.Down(Alt);
            machine.Press('C');
            machine.Press('V');
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+C Ctrl+V");

            // The key itself goes on, its release too, and Ctrl is down only around it.
            Expect.True(machine.Down(Alt), "the system must not see Alt go down");
            Expect.Equal(machine.Os, 0);
            Expect.True(!machine.Down('C'), "the key itself must go on");
            Expect.Equal(machine.Os, KeyRemapper.LeftControlBit);
            Expect.True(!machine.Up('C'), "the release of the key must go on");
            Expect.Equal(machine.Os, 0);
            Expect.True(machine.Up(Alt), "the system must not see Alt go up");

            // Alt released before the key.
            machine.Down(Alt);
            machine.Down('C');
            machine.Up(Alt);
            Expect.Equal(machine.Os, 0);
            machine.Up('C');

            // Two keys overlap: Ctrl stays for as long as either is down.
            machine.Down(Alt);
            machine.Down('C');
            machine.Down('V');
            machine.Up('C');
            Expect.Equal(machine.Os, KeyRemapper.LeftControlBit);
            machine.Up('V');
            Expect.Equal(machine.Os, 0);
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+C Ctrl+C Ctrl+C Ctrl+V");

            // A key held down repeats.
            machine.Down(Alt);
            machine.Down('Z');
            machine.Down('Z');
            machine.Down('Z');
            machine.Up('Z');
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+Z Ctrl+Z Ctrl+Z");

            // The real Ctrl is still Ctrl, with or without Alt in between.
            Expect.Equal(machine.Chord(Ctrl, 'C'), "Ctrl+C");
            machine.Down(Alt);
            machine.Down('C');
            machine.Down(Ctrl);
            machine.Up('C');
            Expect.Equal(machine.Os, KeyRemapper.LeftControlBit);
            machine.Up(Ctrl);
            machine.Up(Alt);
            machine.Take();

            // What the editor is told about a key that went on: the modifiers it received.
            machine.Down(Alt);
            machine.Down('A');
            Expect.Equal(machine.Remapper.EffectiveKey, (int)'A');
            Expect.Equal(machine.Remapper.EffectiveModifiers, Modifiers.Control);
            machine.Up('A');
            machine.Down(Left);
            Expect.Equal(machine.Remapper.EffectiveKey, VirtualKeys.Home);
            machine.Up(Left);
            machine.Up(Alt);
            machine.Take();
            ExpectQuiet(machine);
        }

        private static void LoneModifiers()
        {
            Machine machine = new Machine();
            // A tap of Alt: nothing reaches the system, and the tap is still reported.
            Expect.True(machine.Down(Alt), "Alt down");
            Expect.True(machine.Remapper.Transition, "a press is a transition");
            Expect.Equal(machine.Remapper.Held, KeyRemapper.LeftAltBit);
            Expect.True(machine.Down(Alt), "auto-repeat of Alt");
            Expect.True(!machine.Remapper.Transition, "auto-repeat is not a transition");
            Expect.True(machine.Up(Alt), "Alt up");
            Expect.True(machine.Remapper.Transition, "a release is a transition");
            Expect.Equal(machine.Remapper.Held, 0);
            Expect.Equal(machine.Take(), "");
            ExpectQuiet(machine);

            // A release without a press belongs to the system and is no transition.
            Expect.True(!machine.Up(Ctrl), "a stray release");
            Expect.True(!machine.Remapper.Transition, "a stray release is not a transition");

            // The Windows key goes down for the system as always: a lone tap opens Start.
            Expect.True(!machine.Down(Win), "Win down");
            Expect.Equal(machine.Remapper.Held, KeyRemapper.LeftWinBit);
            Expect.True(!machine.Up(Win), "Win up");
            Expect.Equal(machine.Starts, 1);
            machine.Starts = 0;

            // Hidden for a word jump, it is still held, and its auto-repeat and release stay hidden.
            machine.Down(Win);
            machine.Press(Left);
            Expect.Equal(machine.Os, 0);
            Expect.Equal(machine.Remapper.Held, KeyRemapper.LeftWinBit);
            Expect.True(machine.Down(Win), "auto-repeat of a hidden Win");
            Expect.True(!machine.Remapper.Transition, "auto-repeat is not a transition");
            Expect.True(machine.Up(Win), "the release of a hidden Win");
            Expect.True(machine.Remapper.Transition, "a release is a transition");
            Expect.Equal(machine.Take(), "Ctrl+Left");
            ExpectQuiet(machine);

            // Ctrl+Shift alone still changes the input language where Windows is set up
            // that way. Alt+Shift no longer does: Alt is Command, and the system never
            // sees it go down.
            machine.Down(Ctrl);
            machine.Down(Shift);
            machine.Up(Shift);
            machine.Up(Ctrl);
            Expect.Equal(machine.LanguageSwitches, 1);
            machine.LanguageSwitches = 0;
            machine.Down(Alt);
            machine.Down(Shift);
            machine.Up(Shift);
            machine.Up(Alt);
            ExpectQuiet(machine);

            // Shift and Ctrl alone are never touched.
            Expect.True(!machine.Down(Shift), "Shift down");
            Expect.True(!machine.Up(Shift), "Shift up");
            Expect.Equal(machine.Chord(Shift, 'A'), "Shift+A");
            Expect.Equal(machine.Chord('A'), "A");
            ExpectQuiet(machine);
        }

        private static void Navigation()
        {
            Machine machine = new Machine();
            Expect.Equal(machine.Chord(Alt, Left), "Home");
            Expect.Equal(machine.Chord(Alt, Right), "End");
            Expect.Equal(machine.Chord(Alt, Shift, Right), "Shift+End");
            Expect.Equal(machine.Chord(Alt, Up), "Ctrl+Home");
            Expect.Equal(machine.Chord(Alt, Shift, Down), "Ctrl+Shift+End");
            Expect.Equal(machine.Chord(Win, Left), "Ctrl+Left");
            Expect.Equal(machine.Chord(Win, Shift, Right), "Ctrl+Shift+Right");
            Expect.Equal(machine.Chord(Win, Up), "Ctrl+Up");
            Expect.Equal(machine.Chord(Win, VirtualKeys.Backspace), "Ctrl+Backspace");
            Expect.Equal(machine.Chord(Win, VirtualKeys.Delete), "Ctrl+Delete");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Backspace), "Shift+Home Backspace");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Delete), "Shift+End Delete");
            ExpectQuiet(machine);

            // A replaced key never reaches the system, and neither does its release,
            // even when Alt goes up first.
            machine.Down(Alt);
            Expect.True(machine.Down(Left), "the arrow must be replaced");
            Expect.Equal(machine.Os, 0);
            machine.Up(Alt);
            Expect.True(machine.Up(Left), "the release of a replaced key must not go on");
            // After that the key is an ordinary key again.
            Expect.True(!machine.Down(Left), "an ordinary arrow");
            Expect.True(!machine.Up(Left), "an ordinary arrow's release");
            Expect.Equal(machine.Take(), "Home Left");

            // Shift stays down for the system around a selection, and comes back after
            // a shortcut that must not contain it.
            machine.Down(Shift);
            machine.Down(Alt);
            machine.Press(Left);
            Expect.Equal(machine.Os, KeyRemapper.LeftShiftBit);
            machine.Press(VirtualKeys.Backspace);
            Expect.Equal(machine.Os, KeyRemapper.LeftShiftBit);
            machine.Up(Alt);
            machine.Up(Shift);
            Expect.Equal(machine.Take(), "Shift+Home Shift+Home Backspace");

            // The key repeats while it is held.
            machine.Down(Win);
            machine.Down(Left);
            machine.Down(Left);
            machine.Up(Left);
            // After a word jump, the Windows key in the same hold is still the Windows key.
            machine.Press('E');
            machine.Press(Right);
            machine.Up(Win);
            Expect.Equal(machine.Take(), "Ctrl+Left Ctrl+Left Win+E Ctrl+Right");
            ExpectQuiet(machine);
        }

        private static void SystemShortcuts()
        {
            Machine machine = new Machine();
            // The task switcher needs a real Alt, held for as long as the key is.
            machine.Down(Alt);
            machine.Press(VirtualKeys.Tab);
            Expect.Equal(machine.Os, KeyRemapper.LeftAltBit);
            machine.Press(VirtualKeys.Tab);
            // Inside the switcher the arrows choose a window; they are not "start of line".
            machine.Press(Left);
            Expect.Equal(machine.Os, KeyRemapper.LeftAltBit);
            Expect.True(!machine.Up(Alt), "the release of Alt closes the switcher");
            Expect.Equal(machine.Take(), "Alt+Tab Alt+Tab Alt+Left");
            // The next hold is ordinary again.
            Expect.Equal(machine.Chord(Alt, Left), "Home");
            Expect.Equal(machine.Chord(Alt, Shift, VirtualKeys.Tab), "Alt+Shift+Tab");
            Expect.Equal(machine.Chord(VirtualKeys.RightAlt, VirtualKeys.Tab), "Alt+Tab");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.F4), "Alt+F4");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Escape), "Alt+Esc");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Numpad0 + 1), "Alt+0x61");
            Expect.Equal(machine.Chord(Ctrl, Alt, VirtualKeys.Delete), "Ctrl+Alt+Delete");
            Expect.Equal(machine.Chord(Ctrl, Alt, 'T'), "Ctrl+Alt+T");
            Expect.Equal(machine.Chord(Alt, Win, VirtualKeys.Space), "Alt+Win+Space");
            // Windows shortcuts.
            Expect.Equal(machine.Chord(Win, 'E'), "Win+E");
            Expect.Equal(machine.Chord(Win, 'L'), "Win+L");
            Expect.Equal(machine.Chord(Win, Shift, 'S'), "Shift+Win+S");
            Expect.Equal(machine.Chord(Win, VirtualKeys.Tab), "Win+Tab");
            Expect.Equal(machine.Chord(Ctrl, Win, 'D'), "Ctrl+Win+D");
            Expect.Equal(machine.Chord(Win, VirtualKeys.Enter), "Win+Enter");
            // Keys that are not shortcut keys go on untouched: volume up.
            machine.Down(Alt);
            Expect.True(!machine.Down(0xAF), "a media key");
            machine.Up(0xAF);
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "0xAF");
            ExpectQuiet(machine);
        }

        private static void Exceptions()
        {
            Machine machine = new Machine();
            Expect.Equal(machine.Chord(Alt, 'Q'), "Alt+F4");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Space), "Win+S");
            Expect.Equal(machine.Chord(Alt, Shift, 'Z'), "Ctrl+Y");
            Expect.Equal(machine.Chord(Alt, 'G'), "F3");
            Expect.Equal(machine.Chord(Alt, Shift, 'G'), "Shift+F3");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.LeftBracket), "Ctrl+[");
            Expect.Equal(machine.Chord(Alt, Shift, VirtualKeys.LeftBracket), "Ctrl+Shift+Tab");
            Expect.Equal(machine.Chord(Alt, Shift, VirtualKeys.RightBracket), "Ctrl+Tab");
            Expect.Equal(machine.Chord(Alt, Win, Left), "Ctrl+Shift+Tab");
            Expect.Equal(machine.Chord(Alt, Win, Right), "Ctrl+Tab");
            Expect.Equal(machine.Chord(Alt, Shift, '3'), "Win+PrintScreen");
            Expect.Equal(machine.Chord(Alt, Shift, '4'), "Shift+Win+S");
            Expect.Equal(machine.Chord(Alt, Shift, '5'), "Shift+Win+S");
            Expect.Equal(machine.Chord(Alt, Win, VirtualKeys.Escape), "Ctrl+Shift+Esc");
            Expect.Equal(machine.Chord(Ctrl, Alt, 'F'), "F11");
            Expect.Equal(machine.Chord(Ctrl, Alt, VirtualKeys.Space), "Win+.");
            // Snapping windows moves to Ctrl+Win+arrows, and to another monitor with Shift.
            Expect.Equal(machine.Chord(Ctrl, Win, Left), "Win+Left");
            Expect.Equal(machine.Chord(Ctrl, Win, Up), "Win+Up");
            Expect.Equal(machine.Chord(Ctrl, Win, Shift, Right), "Shift+Win+Right");
            ExpectQuiet(machine);

            // A real Ctrl that a shortcut must not contain goes up for it and comes back.
            machine.Down(Ctrl);
            machine.Down(Alt);
            machine.Press('F');
            Expect.Equal(machine.Os, KeyRemapper.LeftControlBit);
            machine.Up(Alt);
            machine.Up(Ctrl);
            Expect.Equal(machine.Take(), "F11");
            ExpectQuiet(machine);
        }

        private static void Commands()
        {
            Machine machine = new Machine();
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Backquote), "");
            Expect.Equal(machine.Chord(Alt, Shift, VirtualKeys.Backquote), "");
            Expect.Equal(machine.Chord(Alt, 'M'), "");
            Expect.Equal(machine.Chord(Alt, 'H'), "");
            Expect.Equal(string.Join(" ", machine.Commands), "NextWindow PreviousWindow Minimize Minimize");
            // With Shift those two letters are ordinary shortcuts.
            Expect.Equal(machine.Chord(Alt, Shift, 'M'), "Ctrl+Shift+M");
            ExpectQuiet(machine);

            // Locking: the keys are released on the lock screen, where no hook sees them.
            machine.Commands.Clear();
            machine.Down(Ctrl);
            machine.Down(Alt);
            Expect.True(machine.Down('Q'), "the key of a command must not go on");
            Expect.Equal(string.Join(" ", machine.Commands), "Lock");
            machine.Os = 0;
            machine.Press('A');
            Expect.Equal(machine.Take(), "A");
            ExpectQuiet(machine);
        }

        private static void Programs()
        {
            Machine machine = new Machine();
            machine.Kind = AppKind.Terminal;
            Expect.Equal(machine.Chord(Alt, 'C'), "Ctrl+Shift+C");
            Expect.Equal(machine.Chord(Alt, 'V'), "Ctrl+Shift+V");
            Expect.Equal(machine.Chord(Alt, 'T'), "Ctrl+Shift+T");
            Expect.Equal(machine.Chord(Ctrl, 'C'), "Ctrl+C");
            Expect.Equal(machine.Chord(Alt, '2'), "Ctrl+Alt+2");
            Expect.Equal(machine.Chord(Alt, Up), "Ctrl+Shift+Home");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Backspace), "Ctrl+Home");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Delete), "Ctrl+End");
            Expect.Equal(machine.Chord(Alt, Left), "Home");
            Expect.Equal(machine.Chord(Win, Left), "Ctrl+Left");
            Expect.Equal(machine.Chord(Alt, 'Q'), "Alt+F4");

            machine.Kind = AppKind.Browser;
            Expect.Equal(machine.Chord(Alt, VirtualKeys.LeftBracket), "Alt+Left");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.RightBracket), "Alt+Right");
            Expect.Equal(machine.Chord(Alt, Win, 'I'), "Ctrl+Shift+I");
            Expect.Equal(machine.Chord(Alt, Win, 'U'), "Ctrl+U");
            Expect.Equal(machine.Chord(Alt, 'Y'), "Ctrl+H");
            Expect.Equal(machine.Chord(Alt, 'L'), "Ctrl+L");
            Expect.Equal(machine.Chord(Alt, Shift, VirtualKeys.RightBracket), "Ctrl+Tab");

            machine.Kind = AppKind.FileManager;
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Backspace), "Delete");
            Expect.Equal(machine.Chord(Alt, Down), "Enter");
            Expect.Equal(machine.Chord(Alt, Up), "Alt+Up");
            Expect.Equal(machine.Chord(Alt, 'I'), "Alt+Enter");
            Expect.Equal(machine.Chord(Alt, 'O'), "Enter");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.LeftBracket), "Alt+Left");
            Expect.Equal(machine.Chord(Alt, Shift, 'N'), "Ctrl+Shift+N");
            // While a name is being edited, the same keys edit text.
            machine.Caret = true;
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Backspace), "Shift+Home Backspace");
            Expect.Equal(machine.Chord(Alt, Up), "Ctrl+Home");
            Expect.Equal(machine.Chord(Alt, 'I'), "Ctrl+I");
            machine.Caret = false;

            // The desktop is a file manager too, with nothing to quit: there, Alt+F4
            // would offer to shut the computer down.
            machine.Kind = AppKind.Desktop;
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Backspace), "Delete");
            Expect.Equal(machine.Chord(Alt, Down), "Enter");
            Expect.Equal(machine.Chord(Alt, 'I'), "Alt+Enter");
            machine.Down(Alt);
            Expect.True(machine.Down('Q'), "Alt+Q must do nothing on the desktop");
            Expect.True(machine.Up('Q'), "and its release must not go on");
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "");
            Expect.Equal(machine.Chord(Alt, 'C'), "Ctrl+C");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.LeftBracket), "Ctrl+[");

            machine.Kind = AppKind.CodeEditor;
            Expect.Equal(machine.Chord(Win, Up), "Alt+Up");
            Expect.Equal(machine.Chord(Win, Shift, Down), "Alt+Shift+Down");
            Expect.Equal(machine.Chord(Win, Left), "Ctrl+Left");
            Expect.Equal(machine.Chord(Alt, Shift, VirtualKeys.RightBracket), "Ctrl+PageDown");
            Expect.Equal(machine.Chord(Alt, Win, Left), "Ctrl+PageUp");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.RightBracket), "Ctrl+]");
            Expect.Equal(machine.Chord(Alt, Win, Up), "Ctrl+Alt+Up");
            Expect.Equal(machine.Chord(Alt, Shift, 'P'), "Ctrl+Shift+P");

            machine.Kind = AppKind.JetBrains;
            Expect.Equal(machine.Chord(Alt, Shift, 'Z'), "Ctrl+Shift+Z");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Backspace), "Ctrl+Y");
            Expect.Equal(machine.Chord(Win, VirtualKeys.Enter), "Alt+Enter");
            Expect.Equal(machine.Chord(Win, Up), "Ctrl+W");
            Expect.Equal(machine.Chord(Win, Down), "Ctrl+Shift+W");
            Expect.Equal(machine.Chord(Win, Shift, Up), "Alt+Shift+Up");
            Expect.Equal(machine.Chord(Alt, 'O'), "Ctrl+N");
            Expect.Equal(machine.Chord(Alt, Shift, 'O'), "Ctrl+Shift+N");
            Expect.Equal(machine.Chord(Alt, 'L'), "Ctrl+G");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.Comma), "Ctrl+Alt+S");
            Expect.Equal(machine.Chord(Alt, '1'), "Alt+1");
            Expect.Equal(machine.Chord(Alt, VirtualKeys.LeftBracket), "Ctrl+Alt+Left");
            Expect.Equal(machine.Chord(Alt, Shift, VirtualKeys.LeftBracket), "Alt+Left");
            Expect.Equal(machine.Chord(Alt, Win, 'L'), "Ctrl+Alt+L");
            Expect.Equal(machine.Chord(Alt, Win, Left), "Ctrl+Alt+Left");

            machine.Kind = AppKind.Office;
            Expect.Equal(machine.Chord(Alt, 'G'), "Shift+F4");
            Expect.Equal(machine.Chord(Alt, Shift, 'G'), "Ctrl+Shift+F4");
            Expect.Equal(machine.Chord(Alt, Shift, 'Z'), "Ctrl+Y");
            ExpectQuiet(machine);
        }

        private static void Clicks()
        {
            Machine machine = new Machine();
            machine.Click();
            machine.Down(Alt);
            machine.Click();
            machine.Click();
            Expect.Equal(machine.Os, KeyRemapper.LeftControlBit);
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Click Ctrl+Click Ctrl+Click");
            ExpectQuiet(machine);

            // Files are picked one by one with Command held, however long that takes.
            machine.Down(Alt);
            machine.Press('A');
            machine.Tick += 3 * KeyRemapper.IdleMilliseconds;
            machine.Click();
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+A Ctrl+Click");
            ExpectQuiet(machine);

            // Ctrl and Shift with nothing but a click between them must not look like
            // the shortcut that changes the input language.
            machine.Down(Shift);
            machine.Down(Alt);
            machine.Click();
            machine.Up(Alt);
            machine.Up(Shift);
            Expect.Equal(machine.Take(), "Ctrl+Shift+Click");
            ExpectQuiet(machine);

            // A key after the click, in the same hold.
            machine.Down(Alt);
            machine.Click();
            machine.Press(Left);
            machine.Press('C');
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+Click Home Ctrl+C");

            machine.Down(Alt);
            machine.Down(Win);
            machine.Click();
            machine.Up(Win);
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+Alt+Click");
            ExpectQuiet(machine);

            // Option+click and Ctrl+click are left alone.
            machine.Down(Ctrl);
            machine.Click();
            machine.Up(Ctrl);
            machine.Down(Win);
            machine.Click();
            machine.Press('E');
            machine.Up(Win);
            Expect.Equal(machine.Take(), "Ctrl+Click Win+Click Win+E");
            ExpectQuiet(machine);
        }

        private static void Excluded()
        {
            Machine machine = new Machine();
            machine.Enabled = false;
            Expect.True(!machine.Down(Alt), "Alt goes to an excluded program");
            Expect.True(!machine.Down('C'), "and so does the key");
            machine.Up('C');
            Expect.True(!machine.Up(Alt), "and the release of Alt");
            Expect.Equal(machine.Take(), "Alt+C");
            Expect.Equal(machine.Chord(Alt, Left), "Alt+Left");
            Expect.Equal(machine.Chord(Win, Left), "Win+Left");
            Expect.Equal(machine.Menus, 0);
            machine.Down(Alt);
            machine.Click();
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Alt+Click");
            machine.Menus = 0;
            // There, a lone Alt opens the menu as it always did.
            machine.Press(Alt);
            Expect.Equal(machine.Menus, 1);
            machine.Menus = 0;

            // Alt went down in an excluded program, and a normal one came to the front.
            machine.Down(Alt);
            machine.Enabled = true;
            machine.Press('C');
            Expect.Equal(machine.Os, 0);
            Expect.True(machine.Up(Alt), "Alt is hidden by now");
            Expect.Equal(machine.Take(), "Ctrl+C");
            ExpectQuiet(machine);

            // The other way round.
            machine.Down(Alt);
            machine.Press('C');
            machine.Enabled = false;
            machine.Press('C');
            Expect.Equal(machine.Os, KeyRemapper.LeftAltBit);
            Expect.True(!machine.Up(Alt), "the system has Alt as down and must see it go up");
            Expect.Equal(machine.Take(), "Ctrl+C Alt+C");
            ExpectQuiet(machine);
        }

        private static void AltGr()
        {
            Machine machine = new Machine();
            // Windows sends AltGr as a left Ctrl with its own scan code, then the right Alt.
            Expect.True(!machine.Down(Ctrl, KeyRemapper.FakeControlScanCode), "the Ctrl of AltGr");
            Expect.True(!machine.Down(VirtualKeys.RightAlt), "AltGr");
            Expect.True(!machine.Down('Q'), "the letter");
            machine.Up('Q');
            Expect.True(!machine.Down(VirtualKeys.RightAlt), "auto-repeat of AltGr");
            Expect.True(!machine.Up(VirtualKeys.RightAlt), "AltGr up");
            Expect.True(!machine.Up(Ctrl, KeyRemapper.FakeControlScanCode), "the Ctrl of AltGr up");
            Expect.Equal(machine.Take(), "Ctrl+Alt+Q");
            // Without that Ctrl, the right Alt is Command like the left one.
            Expect.Equal(machine.Chord(VirtualKeys.RightAlt, 'Q'), "Alt+F4");
            Expect.Equal(machine.Chord(VirtualKeys.RightAlt, 'C'), "Ctrl+C");
            ExpectQuiet(machine);
        }

        private static void LostReleases()
        {
            // Within one hold, a pause between keys changes nothing.
            Machine machine = new Machine();
            machine.Down(Alt);
            machine.Press('C');
            machine.Tick += 4000;
            machine.Press('V');
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+C Ctrl+V");

            // The release of Alt never arrived. After a long silence the next key is
            // typed, not taken as a shortcut.
            machine.Down(Alt);
            machine.Press('C');
            machine.Tick += KeyRemapper.IdleMilliseconds + 1000;
            machine.Press('A');
            Expect.Equal(machine.Take(), "Ctrl+C A");
            Expect.Equal(machine.Remapper.Held, 0);
            Expect.True(machine.Down(Alt), "a new press after the lost release");
            Expect.True(machine.Remapper.Transition, "it is a new press");
            machine.Up(Alt);
            ExpectQuiet(machine);

            // Until this keyboard has shown that it repeats modifier keys, a held Alt
            // with no events proves nothing.
            machine.Down(Alt);
            machine.Tick += 3000;
            machine.Press('C');
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+C");
            // Now it shows it.
            machine.Down(Alt);
            for (int i = 0; i < 100; i++) machine.Down(Alt);
            machine.Press('C');
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+C");
            // From then on, an Alt that was pressed last and has stopped repeating is not held.
            machine.Down(Alt);
            machine.Tick += 3000;
            machine.Press('A');
            Expect.Equal(machine.Take(), "A");
            // That says nothing about an Alt that another key was pressed after.
            machine.Down(Alt);
            machine.Press('C');
            machine.Tick += 3000;
            machine.Press('V');
            machine.Up(Alt);
            Expect.Equal(machine.Take(), "Ctrl+C Ctrl+V");
            ExpectQuiet(machine);

            // A window that runs as administrator came to the front: every lent key is
            // released, and what was held is forgotten.
            machine = new Machine();
            machine.Down(Alt);
            machine.Down('C');
            Expect.Equal(machine.Os, KeyRemapper.LeftControlBit);
            machine.Reset();
            Expect.Equal(machine.Os, 0);
            machine.Press('A');
            Expect.Equal(machine.Take(), "Ctrl+C A");
            ExpectQuiet(machine);

            // Win+L: the keys go up on the lock screen. The system knows, and it is believed.
            machine.Down(Win);
            machine.Down('L');
            machine.Os = 0;
            machine.Press('A');
            Expect.Equal(machine.Take(), "Win+L A");
            Expect.Equal(machine.Remapper.Held, 0);

            // The same after Ctrl+Alt+Delete, where Alt was given to the system for the key.
            machine.Down(Ctrl);
            machine.Down(Alt);
            machine.Down(VirtualKeys.Delete);
            Expect.Equal(machine.Os, KeyRemapper.LeftControlBit | KeyRemapper.LeftAltBit);
            machine.Os = 0;
            machine.Press('A');
            Expect.Equal(machine.Take(), "Ctrl+Alt+Delete A");
            Expect.Equal(machine.Remapper.Held, 0);

            // A lent Ctrl that something else released is not released twice.
            machine.Down(Alt);
            machine.Down('C');
            machine.Os = 0;
            machine.Up('C');
            Expect.Equal(machine.Remapper.Output.Count, 0);
            machine.Up(Alt);
            machine.Take();
            ExpectQuiet(machine);
        }

        private static void ProgramKinds()
        {
            Expect.Equal(AppCategories.KindOf("chrome", "Chrome_WidgetWin_1"), AppKind.Browser);
            Expect.Equal(AppCategories.KindOf("msedge", "Chrome_WidgetWin_1"), AppKind.Browser);
            Expect.Equal(AppCategories.KindOf("Code", "Chrome_WidgetWin_1"), AppKind.CodeEditor);
            Expect.Equal(AppCategories.KindOf("cursor", "Chrome_WidgetWin_1"), AppKind.CodeEditor);
            Expect.Equal(AppCategories.KindOf("pycharm64", "SunAwtFrame"), AppKind.JetBrains);
            Expect.Equal(AppCategories.KindOf("WINWORD", "OpusApp"), AppKind.Office);
            Expect.Equal(AppCategories.KindOf("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS"), AppKind.Terminal);
            Expect.Equal(AppCategories.KindOf("python", "ConsoleWindowClass"), AppKind.Terminal);
            Expect.Equal(AppCategories.KindOf("powershell", "ConsoleWindowClass"), AppKind.Terminal);
            Expect.Equal(AppCategories.KindOf("alacritty", "Window Class"), AppKind.Terminal);
            // A dialog that a script shows is not a terminal.
            Expect.Equal(AppCategories.KindOf("powershell", "WindowsForms10.Window.8.app.0.141b42a_r6_ad1"), AppKind.Other);
            Expect.Equal(AppCategories.KindOf("explorer", "CabinetWClass"), AppKind.FileManager);
            Expect.Equal(AppCategories.KindOf("explorer", "Progman"), AppKind.Desktop);
            Expect.Equal(AppCategories.KindOf("explorer", "Shell_TrayWnd"), AppKind.Desktop);
            Expect.True(AppCategories.ListsFiles("DirectUIHWND") && AppCategories.ListsFiles("SysTreeView32"), "lists of files");
            Expect.True(!AppCategories.ListsFiles("Edit") && !AppCategories.ListsFiles("") && !AppCategories.ListsFiles(null),
                "a text box is not a list of files");
            // The task switcher is the same program and is not a folder.
            Expect.Equal(AppCategories.KindOf("explorer", "XamlExplorerHostIslandWindow"), AppKind.Other);
            Expect.Equal(AppCategories.KindOf("notepad", "Notepad"), AppKind.Other);
            Expect.Equal(AppCategories.KindOf("", ""), AppKind.Other);
            Expect.Equal(AppCategories.KindOf(null, null), AppKind.Other);
            Expect.True(AppCategories.ForwardsKeys("mstsc"), "Remote Desktop keeps its own shortcuts");
            Expect.True(!AppCategories.ForwardsKeys("chrome"), "a browser does not");

            ShortcutContext context = new ShortcutContext();
            context.SetExcluded("Game.exe, other ;third\r\nfourth.EXE");
            Expect.True(context.Excludes("game") && context.Excludes("OTHER") && context.Excludes("third")
                && context.Excludes("fourth"), "names with or without .exe, in any case, with any separator");
            Expect.True(!context.Excludes("chrome") && !context.Excludes(""), "other programs are not excluded");
        }
    }
}
