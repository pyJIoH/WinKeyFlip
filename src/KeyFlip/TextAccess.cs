using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using KeyFlip.Core;

namespace KeyFlip
{
    /// The text KeyFlip is about to replace, as UI Automation ranges. UI Automation has
    /// no character offsets, so every range is checked by the text it returns.
    internal sealed class TextTarget
    {
        public AutomationElement Element;
        public TextPattern Pattern;
        public IntPtr Foreground;
        /// The selection (or caret) when the action started.
        public TextPatternRange OriginalSelection;
        /// The selection, or the word before the caret.
        public TextPatternRange Range;
        public string Text;
        public bool WasSelected;
        /// The spaces between the word and the caret.
        public string Trailing = "";
    }

    internal enum TargetFailureKind { NoWord, Secure, Unavailable }

    internal sealed class TextTargetFailure : Exception
    {
        public readonly TargetFailureKind Kind;
        public readonly string Reason;
        /// A terminal: its text must not be replaced through the clipboard either.
        public bool Terminal;
        /// False when the focused control is known not to be an editor (read-only text,
        /// a list, a button), so copying and pasting there would achieve nothing.
        public bool AllowsCopy = true;
        /// The editor reported a caret with nothing selected.
        public bool SelectionEmpty;

        public TextTargetFailure(TargetFailureKind kind, string reason) : base(Describe(kind, reason))
        {
            Kind = kind;
            Reason = reason;
        }

        private static string Describe(TargetFailureKind kind, string reason)
        {
            switch (kind)
            {
                case TargetFailureKind.NoWord: return "No word before the cursor. Select text or type a word first.";
                case TargetFailureKind.Secure: return "Password fields are excluded.";
                default: return "This editor did not expose editable text (" + reason + ").";
            }
        }
    }

    internal enum CaretProbe
    {
        /// No usable editor; UI Automation cannot confirm or contradict the typed word.
        Unavailable,
        Secure,
        /// The caret follows exactly the typed word and its spaces.
        Matches,
        /// As `Matches`, with an application-made selection after the caret, such as
        /// inline completion in an address bar. Typing always collapses a user selection.
        CompletionSelected,
        /// The editor's text does not end with the typed word at a word boundary.
        Contradicts
    }

    internal sealed class TextAccess
    {
        private enum Usability { Usable, Unusable, ReadOnly, NotText, Secure, Terminal }

        /// Controls that never hold editable text. Panes, windows and custom controls are
        /// absent on purpose: editors that draw their own text appear as those.
        private static readonly ControlType[] NonTextControls =
        {
            ControlType.Button, ControlType.CheckBox, ControlType.RadioButton, ControlType.SplitButton,
            ControlType.List, ControlType.ListItem, ControlType.Tree, ControlType.TreeItem,
            ControlType.Menu, ControlType.MenuBar, ControlType.MenuItem, ControlType.Tab, ControlType.TabItem,
            ControlType.ToolBar, ControlType.Slider, ControlType.ScrollBar, ControlType.Hyperlink,
            ControlType.Image, ControlType.ProgressBar, ControlType.Header, ControlType.HeaderItem,
            ControlType.TitleBar, ControlType.StatusBar, ControlType.Thumb, ControlType.Separator,
            ControlType.Calendar
        };

        /// Enough to hold the longest word, its spaces, and a character before it.
        private const int WordContext = TextRules.MaximumWordLength + TextRules.MaximumTrailingSpaces + 16;

        private readonly object preparingLock = new object();
        private readonly Dictionary<uint, DateTime> preparing = new Dictionary<uint, DateTime>();

        /// Loads UI Automation ahead of the first action.
        public void WarmUp()
        {
            try
            {
                AutomationElement root = AutomationElement.RootElement;
                if (root != null) GC.KeepAlive(root.Current.ProcessId);
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused != null) GC.KeepAlive(focused.Current.ControlType);
            }
            catch (Exception)
            {
            }
        }

        // ---- Finding the editor ----

        private static Usability Inspect(AutomationElement element, out TextPattern pattern)
        {
            pattern = null;
            if (element == null) return Usability.Unusable;
            try
            {
                AutomationElement.AutomationElementInformation current = element.Current;
                if (current.IsPassword) return Usability.Secure;
                // The input proxy of xterm.js, the terminal of VS Code and other Electron tools.
                if ((current.ClassName ?? "").IndexOf("xterm-helper-textarea", StringComparison.Ordinal) >= 0)
                {
                    return Usability.Terminal;
                }
                if (!current.IsEnabled) return Usability.Unusable;
                object textPattern;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out textPattern))
                {
                    return Array.IndexOf(NonTextControls, current.ControlType) >= 0 ? Usability.NotText : Usability.Unusable;
                }
                object valuePattern;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out valuePattern)
                    && ((ValuePattern)valuePattern).Current.IsReadOnly)
                {
                    return Usability.ReadOnly;
                }
                TextPattern text = (TextPattern)textPattern;
                TextPatternRange[] selection = text.GetSelection();
                if (selection == null || selection.Length == 0) return Usability.Unusable;
                object readOnly = selection[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                if (readOnly is bool && (bool)readOnly) return Usability.ReadOnly;
                pattern = text;
                return Usability.Usable;
            }
            catch (Exception)
            {
                return Usability.Unusable;
            }
        }

        /// For checks: how a control would be treated ("Usable", "Secure", "ReadOnly", ...).
        internal string Classify(AutomationElement element)
        {
            TextPattern pattern;
            return Inspect(element, out pattern).ToString();
        }

        /// The focused, editable, unprotected text control. Two independent sources are
        /// tried: an application shell reported by one must not hide the editor.
        private AutomationElement FocusedEditor(out TextPattern pattern, out FocusInfo focus)
        {
            pattern = null;
            focus = Native.GetFocusInfo();
            if (focus.Foreground == IntPtr.Zero) throw new TextTargetFailure(TargetFailureKind.Unavailable, "no focused window");
            if (Native.IsHungAppWindow(focus.Foreground))
            {
                throw new TextTargetFailure(TargetFailureKind.Unavailable, "the application is not responding");
            }
            if (AppCategories.IsTerminal(focus)) throw TerminalFailure();
            List<AutomationElement> candidates = new List<AutomationElement>();
            try
            {
                candidates.Add(AutomationElement.FocusedElement);
            }
            catch (Exception)
            {
            }
            if (focus.Focus != IntPtr.Zero)
            {
                try
                {
                    candidates.Add(AutomationElement.FromHandle(focus.Focus));
                }
                catch (Exception)
                {
                }
            }
            string description = null;
            // Decided by the first candidate, the focused element itself.
            bool allowsCopy = true;
            foreach (AutomationElement candidate in candidates)
            {
                Usability usability = Inspect(candidate, out pattern);
                switch (usability)
                {
                    case Usability.Usable:
                        NoteContact(focus, true);
                        return candidate;
                    case Usability.Secure:
                        throw new TextTargetFailure(TargetFailureKind.Secure, "password field");
                    case Usability.Terminal:
                        throw TerminalFailure();
                }
                if (description != null) continue;
                allowsCopy = usability == Usability.Unusable;
                description = Describe(candidate) + (usability == Usability.ReadOnly ? ": read-only text"
                    : usability == Usability.NotText ? ": not a text field" : ": text selection unavailable");
            }
            NoteContact(focus, false);
            TextTargetFailure failure = new TextTargetFailure(TargetFailureKind.Unavailable, description ?? "no focused editor");
            failure.AllowsCopy = allowsCopy;
            throw failure;
        }

        private static TextTargetFailure TerminalFailure()
        {
            TextTargetFailure failure = new TextTargetFailure(TargetFailureKind.Unavailable, "terminal");
            failure.Terminal = true;
            return failure;
        }

        private static string Describe(AutomationElement element)
        {
            try
            {
                string name = element.Current.ControlType.ProgrammaticName ?? "";
                return name.StartsWith("ControlType.", StringComparison.Ordinal) ? name.Substring(12) : name;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// Browsers and Electron applications build their accessibility tree after the
        /// first query, so the first answer from such a process may be an empty shell.
        private void NoteContact(FocusInfo focus, bool usable)
        {
            lock (preparingLock)
            {
                if (usable)
                {
                    preparing[focus.ProcessId] = DateTime.MinValue;
                }
                else if (!preparing.ContainsKey(focus.ProcessId) && AppCategories.StartsAccessibilityOnDemand(focus))
                {
                    if (preparing.Count > 256) preparing.Clear();
                    preparing[focus.ProcessId] = DateTime.UtcNow;
                }
            }
        }

        public bool IsPreparingFocusedApplication
        {
            get
            {
                FocusInfo focus = Native.GetFocusInfo();
                lock (preparingLock)
                {
                    DateTime started;
                    return preparing.TryGetValue(focus.ProcessId, out started)
                        && (DateTime.UtcNow - started).TotalSeconds < 1.5;
                }
            }
        }

        // ---- Reading ----

        private static bool IsDegenerate(TextPatternRange range)
        {
            return range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0;
        }

        private static TextPatternRange CollapsedToStart(TextPatternRange range)
        {
            TextPatternRange start = range.Clone();
            start.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            return start;
        }

        /// Up to `units` characters before `point`. A unit can be longer than one
        /// UTF-16 unit, so the result can be longer than requested.
        private static string TextBefore(TextPatternRange point, int units, out bool reachedStart)
        {
            TextPatternRange range = CollapsedToStart(point);
            int moved = range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -units);
            string text = range.GetText(-1) ?? "";
            // Classic edit controls report the requested count even when the document
            // start stopped them, so the length of the text is checked as well.
            reachedStart = -moved < units || text.Length < units;
            return text;
        }

        public TextTarget Target()
        {
            TextPattern pattern;
            FocusInfo focus;
            AutomationElement element = FocusedEditor(out pattern, out focus);
            try
            {
                return TargetFor(element, pattern, focus.Foreground);
            }
            catch (TextTargetFailure)
            {
                throw;
            }
            catch (AppFailure)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new TextTargetFailure(TargetFailureKind.Unavailable, Describe(element) + ": " + error.GetType().Name);
            }
        }

        /// The selection of `element`, or the word before its caret.
        internal TextTarget TargetFor(AutomationElement element, TextPattern pattern, IntPtr foreground)
        {
            string role = Describe(element);
            TextPatternRange[] selection = pattern.GetSelection();
            if (selection == null || selection.Length == 0)
            {
                throw new TextTargetFailure(TargetFailureKind.Unavailable, role + ": text selection unavailable");
            }
            if (selection.Length > 1)
            {
                throw new TextTargetFailure(TargetFailureKind.Unavailable, role + ": several selections");
            }
            TextTarget target = new TextTarget();
            target.Element = element;
            target.Pattern = pattern;
            target.Foreground = foreground;
            target.OriginalSelection = selection[0].Clone();
            if (!IsDegenerate(selection[0]))
            {
                string selected = selection[0].GetText(TextRules.MaximumSelectionLength + 1) ?? "";
                if (selected.Length > TextRules.MaximumSelectionLength) throw new AppFailure("The selection is too long to convert.");
                if (selected.Length == 0)
                {
                    throw new TextTargetFailure(TargetFailureKind.Unavailable, role + ": target text unreadable");
                }
                target.Range = selection[0].Clone();
                target.Text = selected;
                target.WasSelected = true;
                return target;
            }
            bool reachedStart;
            string preceding = TextBefore(selection[0], WordContext, out reachedStart);
            if (preceding.Length == 0) throw new TextTargetFailure(TargetFailureKind.NoWord, "empty");
            int offset = reachedStart ? 0 : 1;
            TextRange? found = WordAtCaret.Range(preceding, offset);
            if (!found.HasValue) throw new TextTargetFailure(TargetFailureKind.NoWord, "no word");
            int location = found.Value.Location - offset;
            string word = preceding.Substring(location, found.Value.Length);
            string trailing = preceding.Substring(location + found.Value.Length);
            // Build a range that covers exactly the word, and prove it by its text.
            string tail = word + trailing;
            TextPatternRange range = selection[0].Clone();
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -tail.Length);
            string covered = range.GetText(-1) ?? "";
            for (int guard = tail.Length; covered.Length > tail.Length && guard > 0; guard--)
            {
                if (range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 1) == 0) break;
                covered = range.GetText(-1) ?? "";
            }
            if (covered != tail) throw CaretFailure(role + ": text before cursor unavailable");
            if (trailing.Length > 0)
            {
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -trailing.Length);
                if ((range.GetText(-1) ?? "") != word) throw CaretFailure(role + ": target text unreadable");
            }
            target.Range = range;
            target.Text = word;
            target.Trailing = trailing;
            return target;
        }

        private static TextTargetFailure CaretFailure(string reason)
        {
            TextTargetFailure failure = new TextTargetFailure(TargetFailureKind.Unavailable, reason);
            failure.SelectionEmpty = true;
            return failure;
        }

        /// Checks the tracked word against the editor, when the editor exposes its text.
        public CaretProbe ProbeCaret(TypedWordBuffer typed)
        {
            TextPattern pattern;
            FocusInfo focus;
            try
            {
                FocusedEditor(out pattern, out focus);
            }
            catch (TextTargetFailure failure)
            {
                return failure.Kind == TargetFailureKind.Secure ? CaretProbe.Secure : CaretProbe.Unavailable;
            }
            try
            {
                return ProbeCaretFor(pattern, typed);
            }
            catch (Exception)
            {
                return CaretProbe.Unavailable;
            }
        }

        internal CaretProbe ProbeCaretFor(TextPattern pattern, TypedWordBuffer typed)
        {
            TextPatternRange[] selection = pattern.GetSelection();
            if (selection == null || selection.Length != 1) return CaretProbe.Unavailable;
            int needed = typed.Text.Length + typed.TrailingSpaces + 1;
            bool reachedStart;
            string preceding = TextBefore(selection[0], needed, out reachedStart);
            if (!typed.Matches(preceding, reachedStart)) return CaretProbe.Contradicts;
            return IsDegenerate(selection[0]) ? CaretProbe.Matches : CaretProbe.CompletionSelected;
        }

        // ---- Checking and changing ----

        public bool StillFocused(TextTarget target)
        {
            if (Native.GetForegroundWindow() != target.Foreground) return false;
            try
            {
                TextPattern pattern;
                FocusInfo focus;
                AutomationElement element = FocusedEditor(out pattern, out focus);
                return Automation.Compare(element, target.Element);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SelectionIs(TextTarget target, TextPatternRange expected)
        {
            try
            {
                TextPatternRange[] selection = target.Pattern.GetSelection();
                return selection != null && selection.Length == 1
                    && selection[0].CompareEndpoints(TextPatternRangeEndpoint.Start, expected, TextPatternRangeEndpoint.Start) == 0
                    && selection[0].CompareEndpoints(TextPatternRangeEndpoint.End, expected, TextPatternRangeEndpoint.End) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TextIs(TextPatternRange range, string expected)
        {
            try
            {
                return (range.GetText(expected.Length + 1) ?? "") == expected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// Asks the editor to select `range`. Editors acknowledge the request before
        /// some of them publish the new selection; callers wait for confirmation.
        public bool Select(TextPatternRange range)
        {
            try
            {
                range.Select();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\x00A0', ' ');
        }

        /// Whether the text before the caret now ends with `replacement`.
        public bool Replaced(TextTarget target, string replacement)
        {
            try
            {
                TextPatternRange[] selection = target.Pattern.GetSelection();
                if (selection == null || selection.Length != 1) return false;
                string expected = Normalize(replacement);
                if (!IsDegenerate(selection[0]))
                {
                    // A few editors leave pasted text selected.
                    return Normalize(selection[0].GetText(expected.Length * 2 + 2) ?? "") == expected;
                }
                string tail = expected.Length > 512 ? expected.Substring(expected.Length - 512) : expected;
                int lineBreaks = 0;
                foreach (char character in tail)
                {
                    if (character == '\n') lineBreaks++;
                }
                bool reachedStart;
                string before = Normalize(TextBefore(selection[0], tail.Length + lineBreaks + 2, out reachedStart));
                return before.EndsWith(tail, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// Moves the caret forward over the spaces that followed the replaced word.
        /// `before` is what the text before the caret ends with once it is there.
        public bool MoveCaretForward(TextTarget target, int count, string before)
        {
            try
            {
                TextPatternRange[] selection = target.Pattern.GetSelection();
                if (selection == null || selection.Length != 1 || !IsDegenerate(selection[0])) return false;
                TextPatternRange caret = selection[0].Clone();
                if (caret.Move(TextUnit.Character, count) != count) return false;
                caret.Select();
                string expected = Normalize(before);
                if (expected.Length > 64) expected = expected.Substring(expected.Length - 64);
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    if (SelectionIs(target, caret)) return true;
                    // Browsers report the end of a text as a position that does not
                    // compare equal to the one requested; go by the text instead.
                    if (CaretFollows(target, expected)) return true;
                    Thread.Sleep(25);
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool CaretFollows(TextTarget target, string expected)
        {
            TextPatternRange[] selection = target.Pattern.GetSelection();
            if (selection == null || selection.Length != 1 || !IsDegenerate(selection[0])) return false;
            bool reachedStart;
            string text = Normalize(TextBefore(selection[0], expected.Length + 2, out reachedStart));
            return text.EndsWith(expected, StringComparison.Ordinal);
        }
    }
}
