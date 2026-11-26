namespace KeyFlip.Core
{
    /// The kinds of program whose shortcuts differ from the general rule.
    public enum AppKind
    {
        Other, Browser,
        /// A folder window.
        FileManager,
        Terminal, CodeEditor, JetBrains, Office,
        /// The desktop and the taskbar: a file manager with nothing to close.
        Desktop
    }

    /// What a shortcut does when it is not a key press.
    public enum ShortcutCommand { None, NextWindow, PreviousWindow, Minimize, Lock }

    /// One key press with the modifiers that are held for it.
    public struct Chord
    {
        public readonly Modifiers Modifiers;
        public readonly int Key;

        public Chord(Modifiers modifiers, int key)
        {
            Modifiers = modifiers;
            Key = key;
        }
    }

    /// What Windows receives in place of a Mac shortcut.
    public sealed class Shortcut
    {
        /// The key presses, in order; empty for a command.
        public readonly Chord[] Chords;
        public readonly ShortcutCommand Command;
        /// Alt+Tab: until Alt is released, every key belongs to the task switcher.
        public readonly bool Switcher;

        public Shortcut(Chord[] chords, bool switcher)
        {
            Chords = chords;
            Switcher = switcher;
        }

        public Shortcut(ShortcutCommand command)
        {
            Chords = new Chord[0];
            Command = command;
        }
    }

    /// What the shortcut table needs to know about the program in front.
    public interface IShortcutContext
    {
        /// False while shortcuts are left as they are: the program in front is excluded.
        bool Active { get; }
        AppKind App { get; }
        /// The keyboard focus is in a text box, not in a list of files. Asked for file
        /// managers only.
        bool EditingText { get; }
    }

    /// The Mac shortcuts of a keyboard shared with a Mac, as Windows key presses. On
    /// such a keyboard Alt is where Command is and the Windows key is where Option is,
    /// so in `held` Alt stands for Command and Win stands for Option.
    public static class MacShortcuts
    {
        private enum KeyClass
        {
            /// Not a key that shortcuts are made of: left exactly as it is.
            Other,
            /// Letters, digits, punctuation, editing and navigation keys.
            Plain,
            /// Keys whose shortcuts with Alt and Win stay Windows shortcuts.
            System
        }

        private const Modifiers None = Modifiers.None;
        private const Modifiers Control = Modifiers.Control;
        private const Modifiers Shift = Modifiers.Shift;
        private const Modifiers Alt = Modifiers.Alt;
        private const Modifiers Win = Modifiers.Win;

        private static KeyClass Classify(int key)
        {
            if ((key >= '0' && key <= '9') || (key >= 'A' && key <= 'Z')) return KeyClass.Plain;
            // Punctuation: ; = , - . / ` and [ \ ] ' and the extra key of ISO keyboards.
            if ((key >= 0xBA && key <= 0xC0) || (key >= 0xDB && key <= 0xDE) || key == 0xE2) return KeyClass.Plain;
            if (key >= VirtualKeys.PageUp && key <= VirtualKeys.DownArrow) return KeyClass.Plain;
            switch (key)
            {
                case VirtualKeys.Backspace:
                case VirtualKeys.Enter:
                case VirtualKeys.Space:
                case VirtualKeys.Insert:
                case VirtualKeys.Delete:
                    return KeyClass.Plain;
                case VirtualKeys.Tab:
                case VirtualKeys.Escape:
                case VirtualKeys.PrintScreen:
                case VirtualKeys.Menu:
                    return KeyClass.System;
            }
            // Function keys, and the numeric keypad with its Alt codes.
            if (key >= VirtualKeys.F1 && key <= VirtualKeys.F24) return KeyClass.System;
            if (key >= VirtualKeys.Numpad0 && key <= VirtualKeys.Divide) return KeyClass.System;
            return KeyClass.Other;
        }

        private static Shortcut Press(Modifiers modifiers, int key)
        {
            return new Shortcut(new[] { new Chord(modifiers, key) }, false);
        }

        private static Shortcut Press(Modifiers first, int firstKey, Modifiers second, int secondKey)
        {
            return new Shortcut(new[] { new Chord(first, firstKey), new Chord(second, secondKey) }, false);
        }

        private static Shortcut Do(ShortcutCommand command)
        {
            return new Shortcut(command);
        }

        /// The key is taken, and nothing happens.
        private static Shortcut Nothing()
        {
            return new Shortcut(new Chord[0], false);
        }

        /// The key with the modifiers that are held, as Windows itself would take them.
        public static Shortcut Unchanged(Modifiers held, int key)
        {
            return Press(held, key);
        }

        /// Returns null for a key press that is not a shortcut of this table: it is
        /// passed on untouched.
        public static Shortcut Find(int key, Modifiers held, IShortcutContext context)
        {
            bool command = (held & Alt) != 0;
            bool option = (held & Win) != 0;
            if (!command && !option) return null;
            KeyClass kind = Classify(key);
            if (kind == KeyClass.Other) return null;
            bool control = (held & Control) != 0;
            Modifiers shift = held & Shift;
            AppKind app = context.App;
            Shortcut found;
            if (command && control) found = CommandControl(key, option);
            else if (command && option) found = CommandOption(key, shift, app);
            else if (command) found = CommandAlone(key, shift, app, context);
            else if (control) found = ControlOption(key, shift);
            else found = OptionAlone(key, shift, app);
            if (found != null) return found;
            // The general rule: Command is Ctrl, and Command+Option is Ctrl+Alt.
            if (!command || control || kind == KeyClass.System) return Unchanged(held, key);
            return Press(Control | shift | (option ? Alt : None), key);
        }

        private static bool IsLetter(int key)
        {
            return key >= 'A' && key <= 'Z';
        }

        /// The previous or the next tab.
        private static Shortcut SwitchTab(AppKind app, bool previous)
        {
            if (app == AppKind.JetBrains) return Press(Alt, previous ? VirtualKeys.LeftArrow : VirtualKeys.RightArrow);
            if (app == AppKind.CodeEditor) return Press(Control, previous ? VirtualKeys.PageUp : VirtualKeys.PageDown);
            return Press(previous ? Control | Shift : Control, VirtualKeys.Tab);
        }

        private static Shortcut CommandAlone(int key, Modifiers shift, AppKind app, IShortcutContext context)
        {
            bool shifted = shift != None;
            // The files of a folder window or of the desktop, not the text of a name.
            bool files = (app == AppKind.FileManager || app == AppKind.Desktop) && !context.EditingText;
            switch (key)
            {
                case VirtualKeys.Tab:
                    return new Shortcut(new[] { new Chord(Alt | shift, key) }, true);
                case VirtualKeys.Space:
                    if (!shifted) return Press(Win, 'S'); // Search.
                    break;
                case 'Q':
                    if (shifted) break;
                    // On the desktop, Alt+F4 offers to shut the computer down.
                    return app == AppKind.Desktop ? Nothing() : Press(Alt, VirtualKeys.F4);
                case VirtualKeys.Backquote:
                    return Do(shifted ? ShortcutCommand.PreviousWindow : ShortcutCommand.NextWindow);
                case 'M':
                case 'H':
                    if (!shifted) return Do(ShortcutCommand.Minimize);
                    break;
                case '3':
                    if (shifted) return Press(Win, VirtualKeys.PrintScreen);
                    break;
                case '4':
                case '5':
                    if (shifted) return Press(Win | Shift, 'S');
                    break;
                case 'Z':
                    // Redo is Ctrl+Y nearly everywhere; JetBrains uses Ctrl+Shift+Z, and Ctrl+Y deletes a line there.
                    if (shifted && app != AppKind.JetBrains) return Press(Control, 'Y');
                    break;
                case 'G':
                    // Find next and previous. In Office, F3 and Shift+F3 are other commands.
                    if (app == AppKind.Office) return Press(shifted ? Control | Shift : Shift, VirtualKeys.F4);
                    return Press(shift, VirtualKeys.F3);
                case VirtualKeys.LeftBracket:
                case VirtualKeys.RightBracket:
                {
                    bool back = key == VirtualKeys.LeftBracket;
                    int arrow = back ? VirtualKeys.LeftArrow : VirtualKeys.RightArrow;
                    if (shifted) return SwitchTab(app, back);
                    if (app == AppKind.Browser || app == AppKind.FileManager) return Press(Alt, arrow);
                    if (app == AppKind.JetBrains) return Press(Control | Alt, arrow);
                    break;
                }
                case VirtualKeys.LeftArrow:
                    return Press(shift, VirtualKeys.Home);
                case VirtualKeys.RightArrow:
                    return Press(shift, VirtualKeys.End);
                case VirtualKeys.UpArrow:
                case VirtualKeys.DownArrow:
                {
                    bool up = key == VirtualKeys.UpArrow;
                    int edge = up ? VirtualKeys.Home : VirtualKeys.End;
                    if (files && !shifted)
                    {
                        // The folder above, and "open".
                        return up ? Press(Alt, VirtualKeys.UpArrow) : Press(None, VirtualKeys.Enter);
                    }
                    // In a shell, Ctrl+Home deletes to the start of the line; with Shift it scrolls.
                    if (app == AppKind.Terminal) return Press(Control | Shift, edge);
                    return Press(Control | shift, edge);
                }
                case VirtualKeys.Backspace:
                    if (files) return Press(None, VirtualKeys.Delete);
                    if (app == AppKind.JetBrains) return Press(Control, 'Y');
                    if (app == AppKind.Terminal) return Press(Control, VirtualKeys.Home);
                    return Press(Shift, VirtualKeys.Home, None, VirtualKeys.Backspace);
                case VirtualKeys.Delete:
                    if (app == AppKind.Terminal) return Press(Control, VirtualKeys.End);
                    return Press(Shift, VirtualKeys.End, None, VirtualKeys.Delete);
            }
            switch (app)
            {
                case AppKind.FileManager:
                case AppKind.Desktop:
                    if (shifted || !files) break;
                    if (key == 'I') return Press(Alt, VirtualKeys.Enter); // Properties.
                    if (key == 'O') return Press(None, VirtualKeys.Enter);
                    break;
                case AppKind.Browser:
                    if (!shifted && key == 'Y') return Press(Control, 'H'); // History.
                    break;
                case AppKind.JetBrains:
                    if (key == 'O') return Press(Control | shift, 'N'); // Go to class, go to file.
                    if (shifted) break;
                    if (key == 'L') return Press(Control, 'G'); // Go to line.
                    if (key == VirtualKeys.Comma) return Press(Control | Alt, 'S'); // Settings.
                    if (key >= '0' && key <= '9') return Press(Alt, key); // Tool windows.
                    break;
                case AppKind.Terminal:
                    // Ctrl+C would interrupt the running program; with Shift it only copies.
                    if (IsLetter(key)) return Press(Control | Shift, key);
                    if (!shifted && key >= '1' && key <= '9') return Press(Control | Alt, key); // Tabs.
                    break;
            }
            return null;
        }

        private static Shortcut CommandOption(int key, Modifiers shift, AppKind app)
        {
            switch (key)
            {
                case VirtualKeys.Escape:
                    return Press(Control | Shift, VirtualKeys.Escape); // Task Manager, for "Force Quit".
                case VirtualKeys.Space:
                    return Unchanged(Alt | Win | shift, key);
                case VirtualKeys.LeftArrow:
                case VirtualKeys.RightArrow:
                    if (app != AppKind.JetBrains) return SwitchTab(app, key == VirtualKeys.LeftArrow);
                    break;
                case 'I':
                case 'J':
                case 'C':
                    if (app == AppKind.Browser) return Press(Control | Shift, key); // Developer tools.
                    break;
                case 'U':
                    if (app == AppKind.Browser) return Press(Control, key); // Page source.
                    break;
            }
            return null;
        }

        private static Shortcut CommandControl(int key, bool option)
        {
            if (option) return null;
            switch (key)
            {
                case 'F':
                    return Press(None, VirtualKeys.F11); // Full screen.
                case 'Q':
                    return Do(ShortcutCommand.Lock);
                case VirtualKeys.Space:
                    return Press(Win, VirtualKeys.Period); // The emoji panel.
            }
            return null;
        }

        private static bool IsArrow(int key)
        {
            return key >= VirtualKeys.LeftArrow && key <= VirtualKeys.DownArrow;
        }

        /// Win+arrows move the caret by words, so snapping a window takes Ctrl as well.
        private static Shortcut ControlOption(int key, Modifiers shift)
        {
            return IsArrow(key) ? Press(Win | shift, key) : null;
        }

        private static Shortcut OptionAlone(int key, Modifiers shift, AppKind app)
        {
            bool shifted = shift != None;
            switch (key)
            {
                case VirtualKeys.LeftArrow:
                case VirtualKeys.RightArrow:
                    return Press(Control | shift, key);
                case VirtualKeys.UpArrow:
                case VirtualKeys.DownArrow:
                    // Code editors move the line; JetBrains grows and shrinks the selection.
                    if (app == AppKind.CodeEditor) return Press(Alt | shift, key);
                    if (app == AppKind.JetBrains)
                    {
                        if (shifted) return Press(Alt | Shift, key);
                        return Press(key == VirtualKeys.UpArrow ? Control : Control | Shift, 'W');
                    }
                    return Press(Control | shift, key);
                case VirtualKeys.Backspace:
                case VirtualKeys.Delete:
                    return Press(Control, key);
                case VirtualKeys.Enter:
                    if (app == AppKind.JetBrains) return Press(Alt | shift, key); // Show the quick fixes.
                    break;
            }
            return null;
        }
    }
}
