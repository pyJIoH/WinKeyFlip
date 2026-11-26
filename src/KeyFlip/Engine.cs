using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using KeyFlip.Core;

namespace KeyFlip
{
    internal sealed class WindowInfo
    {
        public IntPtr Handle;
        public string Title;

        public override string ToString()
        {
            return Title;
        }
    }

    /// Coordinates input, the replacement strategies and the status line.
    ///
    /// Threads: the input thread calls `Handle` for every event and must never block;
    /// the action thread runs one queued action at a time and may block freely; the
    /// user interface thread calls the public methods.
    internal sealed class Engine
    {
        /// The status line while nothing has happened yet.
        public string ReadyMessage
        {
            get { return "Ready — tap " + LayoutKeyName + " or left Ctrl alone."; }
        }

        private const string ListenerFailedMessage = "Could not start the keyboard listener. Quit and reopen KeyFlip.";

        public readonly Layouts Layouts;

        /// Raised on any thread when the status, the pause state or the layout name changes.
        public event Action StateChanged;

        /// Checks set this so that expected failures do not play the system sound.
        internal bool Silent { get; set; }

        /// Checks set this to follow decisions. It receives key codes, never characters.
        internal Action<string> Trace { get; set; }

        private void Note(string message)
        {
            Action<string> trace = Trace;
            if (trace != null) trace(message);
        }

        private readonly TextAccess access = new TextAccess();
        private readonly InputMonitor monitor = new InputMonitor();
        private readonly TextReplacement replacement;
        private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        private readonly uint ownProcess = Native.GetCurrentProcessId();
        private readonly double doubleClickSeconds = Native.GetDoubleClickTime() / 1000.0;
        private Thread worker;

        // Guarded by `gate`: written by the input thread on every event, read by the action thread.
        private readonly object gate = new object();
        private readonly TapDetector detector = new TapDetector();
        /// The word being typed, in memory only, for editors that hide their text.
        private readonly TypedWordBuffer typed = new TypedWordBuffer();
        private IntPtr typedWindow;
        /// The user has just dragged, double-clicked or extended a selection with Shift.
        private bool selectionGesture;
        /// The modifier that is down was pressed while a replacement was under way.
        private bool pressedWhileBusy;
        private bool hasMouseDown;
        private int mouseDownX;
        private int mouseDownY;
        private double mouseDownTime;

        /// Incremented for every real input event and every context change.
        private long activity;
        private volatile bool enabled = true;
        private volatile LayoutTapKey layoutTapKey = LayoutTapKey.Alt;
        private volatile bool running;
        private volatile string status = "Starting…";
        private volatile string shortcutStatus = "No gesture detected yet.";
        private volatile string currentLayoutName = "";
        private volatile string lastExternalTitle = "";
        private IntPtr lastExternalWindow;
        /// The layout that a Caps Lock press switched away from. Action thread only.
        private uint layoutBeforeCapsLock;
        private int lastReinstallTick;

        public Engine() : this(new Layouts())
        {
            monitor.CapsLockSwitchesLayout = Settings.CapsLockSwitchesLayout;
            monitor.SetProgramsWithoutMacShortcuts(Settings.ProgramsWithoutMacShortcuts);
            monitor.MacShortcuts = Settings.MacShortcuts;
            layoutTapKey = Settings.LayoutTapKey;
            detector.LayoutKey = layoutTapKey;
        }

        public Engine(Layouts layouts)
        {
            Layouts = layouts;
            replacement = new TextReplacement(access, new WinClipboard(), delegate { return Activity; });
        }

        /// The key whose lone tap converts the layout: Alt, or the Windows key.
        public LayoutTapKey LayoutTapKey
        {
            get { return layoutTapKey; }
            set
            {
                if (value == layoutTapKey) return;
                bool ready = status == ReadyMessage;
                layoutTapKey = value;
                lock (gate)
                {
                    detector.LayoutKey = value;
                    detector.Reset();
                }
                if (ready) status = ReadyMessage;
                RaiseStateChanged();
            }
        }

        /// What the interface calls that key.
        public string LayoutKeyName
        {
            get { return layoutTapKey == LayoutTapKey.Win ? "Win" : "Alt"; }
        }

        public void SaveLayoutTapKey(LayoutTapKey value)
        {
            Settings.LayoutTapKey = value;
            LayoutTapKey = value;
        }

        /// Caps Lock, pressed alone, only switches the keyboard layout.
        public bool CapsLockSwitchesLayout
        {
            get { return monitor.CapsLockSwitchesLayout; }
            set { monitor.CapsLockSwitchesLayout = value; }
        }

        public void SaveCapsLockSwitchesLayout(bool value)
        {
            Settings.CapsLockSwitchesLayout = value;
            CapsLockSwitchesLayout = value;
        }

        /// Mac shortcuts: Alt is Command and the Windows key is Option.
        public bool MacShortcuts
        {
            get { return monitor.MacShortcuts; }
            set
            {
                if (value == monitor.MacShortcuts) return;
                monitor.MacShortcuts = value;
                RaiseStateChanged();
            }
        }

        public void SaveMacShortcuts(bool value)
        {
            Settings.MacShortcuts = value;
            MacShortcuts = value;
        }

        /// Programs that keep their own shortcuts, as the user wrote them.
        public void SaveProgramsWithoutMacShortcuts(string names)
        {
            Settings.ProgramsWithoutMacShortcuts = names;
            UseProgramsWithoutMacShortcuts(names);
        }

        /// Without storing the choice.
        internal void UseProgramsWithoutMacShortcuts(string names)
        {
            monitor.SetProgramsWithoutMacShortcuts(names);
        }

        /// The session was locked or switched, or the computer slept: keys that were
        /// held then were released where the keyboard hook could not see it.
        public void InputInterrupted()
        {
            monitor.ResetShortcuts();
            InvalidateContext();
        }

        public bool Enabled { get { return enabled; } }
        public bool Running { get { return running; } }
        public string Status { get { return status; } }
        public string ShortcutStatus { get { return shortcutStatus; } }
        public string CurrentLayoutName { get { return currentLayoutName; } }
        public string LastExternalTitle { get { return lastExternalTitle; } }

        private long Activity { get { return Interlocked.Read(ref activity); } }

        // ---- Lifecycle ----

        public void Start()
        {
            monitor.OnEvent = Handle;
            monitor.OnForegroundChanged = ForegroundChanged;
            monitor.OnCommand = RunCommand;
            worker = new Thread(Work);
            worker.IsBackground = true;
            worker.Name = "KeyFlip actions";
            worker.Start();
            RememberExternal(Native.GetForegroundWindow());
            currentLayoutName = Layouts.CurrentName;
            running = monitor.Start();
            status = running ? ReadyMessage : ListenerFailedMessage;
            Enqueue(access.WarmUp);
            RaiseStateChanged();
        }

        public void Shutdown()
        {
            enabled = false;
            monitor.Stop();
            running = false;
            work.CompleteAdding();
            // Let an action that is under way finish before taking the clipboard back.
            if (worker != null) worker.Join(1500);
            replacement.Shutdown();
        }

        public void ToggleEnabled()
        {
            enabled = !enabled;
            InvalidateContext();
            if (enabled)
            {
                running = monitor.Start();
                status = running ? ReadyMessage : ListenerFailedMessage;
            }
            else
            {
                monitor.Stop();
                running = false;
                status = "Paused.";
            }
            RaiseStateChanged();
        }

        public void RefreshLayouts()
        {
            Layouts.Reload();
            InvalidateContext();
            RaiseStateChanged();
        }

        public void SaveLayouts(uint english, uint russian)
        {
            if (english != 0) Layouts.EnglishId = english;
            if (russian != 0) Layouts.RussianId = russian;
            InvalidateContext();
        }

        /// Called once a second on the user interface thread.
        public void Tick()
        {
            string name = Layouts.CurrentName;
            bool changed = name != currentLayoutName;
            currentLayoutName = name;
            if (enabled)
            {
                if (!monitor.Running)
                {
                    bool restarted = monitor.Start();
                    if (restarted != running)
                    {
                        running = restarted;
                        status = restarted ? ReadyMessage : ListenerFailedMessage;
                        changed = true;
                    }
                }
                else
                {
                    ReinstallHooksIfDropped();
                }
            }
            if (changed) RaiseStateChanged();
        }

        /// Windows removes a low-level hook without notice when its callback was too
        /// slow once. If the system saw input that the hooks did not, replace them.
        private void ReinstallHooksIfDropped()
        {
            Native.LASTINPUTINFO info = new Native.LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
            if (!Native.GetLastInputInfo(ref info)) return;
            int now = Environment.TickCount;
            int unseen = unchecked((int)info.dwTime - monitor.LastEventTick);
            if (unseen <= 2000 || unchecked(now - lastReinstallTick) <= 5000) return;
            lastReinstallTick = now;
            monitor.Reinstall();
            InvalidateContext();
        }

        public void InvalidateContext()
        {
            Interlocked.Increment(ref activity);
            lock (gate)
            {
                detector.Reset();
                typed.Reset();
                selectionGesture = false;
            }
        }

        private void RaiseStateChanged()
        {
            Action handler = StateChanged;
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception)
            {
                // A listener that is shutting down must not break input handling.
            }
        }

        private void SetStatus(string message)
        {
            status = message;
            RaiseStateChanged();
        }

        private void Fail(string message)
        {
            status = message;
            try
            {
                if (!Silent) SystemSounds.Beep.Play();
            }
            catch (Exception)
            {
            }
            RaiseStateChanged();
        }

        // ---- The action thread ----

        private void Enqueue(Action action)
        {
            try
            {
                work.Add(action);
            }
            catch (InvalidOperationException)
            {
                // Shutting down.
            }
        }

        private void Work()
        {
            foreach (Action action in work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (AppFailure failure)
                {
                    Fail(failure.Message);
                }
                catch (Exception error)
                {
                    Fail("Unexpected error: " + error.Message);
                }
            }
        }

        /// Blocks until everything queued so far has finished. For checks.
        internal bool WaitUntilIdle(int timeoutMilliseconds)
        {
            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                ManualResetEventSlim signal = done;
                Enqueue(delegate
                {
                    try
                    {
                        signal.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
                return done.Wait(timeoutMilliseconds);
            }
        }

        // ---- Input (the input thread) ----

        private void Handle(InputEvent input)
        {
            if (!enabled) return;
            if (input.Kind == InputKind.LayoutSwitch || input.Kind == InputKind.CapsLockHeld)
            {
                // No application sees this key, so it is not input to an editor: the
                // tracked word and an action that is under way stay as they are.
                bool held = input.Kind == InputKind.CapsLockHeld;
                if (Trace != null) Note("input " + input.Kind);
                shortcutStatus = held ? "Caps Lock held." : "Caps Lock pressed.";
                if (held) Enqueue(ToggleCapsLock);
                else Enqueue(SwitchLayoutOnly);
                RaiseStateChanged();
                return;
            }
            long version = Interlocked.Increment(ref activity);
            if (Trace != null)
            {
                Note("input " + input.Kind + " key=0x" + input.Key.ToString("X2") + " scan=0x" + input.ScanCode.ToString("X2")
                    + " modifiers=" + input.Modifiers + (replacement.Busy ? " (busy)" : ""));
            }
            KeyAction? detected = null;
            bool overlapped = false;
            lock (gate)
            {
                bool busy = replacement.Busy;
                if (busy)
                {
                    // Real input is mixing with synthetic input: nothing is predictable,
                    // so no action starts from it. A tap is still followed to its
                    // release, only to keep that release from opening a menu.
                    typed.Reset();
                    if (input.Kind != InputKind.ModifiersChanged)
                    {
                        detector.Cancel();
                        return;
                    }
                }
                switch (input.Kind)
                {
                    case InputKind.ModifiersChanged:
                        detected = detector.ModifiersChanged(input.Key, input.Modifiers, input.Time);
                        if (input.MouseButtonHeld)
                        {
                            // A modifier during a drag belongs to the drag.
                            detector.Cancel();
                            detected = null;
                        }
                        if (input.Modifiers != Modifiers.None)
                        {
                            pressedWhileBusy = busy;
                        }
                        else
                        {
                            overlapped = busy || pressedWhileBusy;
                            pressedWhileBusy = false;
                        }
                        break;
                    case InputKind.KeyDown:
                        RecordTyping(input);
                        NoteSelectionKey(input);
                        detector.Cancel();
                        break;
                    case InputKind.MouseDown:
                        typed.Reset(); // A click can move the caret or the focus.
                        NoteMouseDown(input);
                        detector.Cancel();
                        break;
                    case InputKind.MouseUp:
                        NoteMouseUp(input);
                        detector.Cancel();
                        break;
                    default:
                        // Lock keys and scrolling do not move the caret.
                        detector.Cancel();
                        break;
                }
            }
            if (!detected.HasValue) return;
            KeyAction action = detected.Value;
            if (action == KeyAction.Layout)
            {
                // Still inside the hook, so this is handled before the release is. It
                // keeps the release of Alt from activating the window's menu bar, and
                // the release of the Windows key from opening the Start menu.
                try
                {
                    KeySender.Send(KeySender.MenuMask());
                }
                catch (AppFailure)
                {
                }
            }
            if (overlapped) return;
            shortcutStatus = action == KeyAction.Layout ? LayoutKeyName + " tap detected." : "Left Ctrl tap detected.";
            Enqueue(delegate
            {
                // Let the application receive the modifier release before its selection
                // is queried or keys are sent to it.
                Thread.Sleep(40);
                if (!enabled || Activity != version) return;
                Perform(action, version);
            });
            RaiseStateChanged();
        }

        /// A Mac shortcut that acts on a window. Called inside the keyboard hook, so the
        /// work itself is left to the action thread.
        private void RunCommand(ShortcutCommand command)
        {
            if (!enabled) return;
            if (Trace != null) Note("command " + command);
            Enqueue(delegate { WindowCommands.Run(command); });
        }

        private void ForegroundChanged(IntPtr window)
        {
            if (Trace != null) Note("foreground changed to " + Native.ClassName(window));
            InvalidateContext();
            RememberExternal(window);
        }

        private void RememberExternal(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            uint process;
            Native.GetWindowThreadProcessId(window, out process);
            if (process == ownProcess || !IsApplicationWindow(window)) return;
            lock (gate) lastExternalWindow = window;
            lastExternalTitle = Describe(window, process);
        }

        internal static bool IsPasswordControl(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            // Classic edit controls and their subclasses; the style bit means something
            // else for other window classes.
            return Native.ClassName(window).IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0
                && (Native.GetWindowLong(window, Native.GWL_STYLE) & Native.ES_PASSWORD) != 0;
        }

        private static bool IsNavigationKey(int key)
        {
            // Page Up, Page Down, End, Home and the arrows.
            return key >= Native.VK_PRIOR && key <= Native.VK_DOWN;
        }

        /// Volume, media and Print Screen keys do not touch the text.
        private static bool IsInertKey(int key)
        {
            return key == 0x2C || (key >= 0xAD && key <= 0xB3);
        }

        /// Tracks what a key press inserted. Anything that may move the caret, select text
        /// or run a command makes the editor's contents unpredictable, so it clears the word.
        private void RecordTyping(InputEvent input)
        {
            if (IsInertKey(input.Key)) return;
            FocusInfo focus = Native.GetFocusInfo();
            if (focus.Target != typedWindow)
            {
                typed.Reset();
                typedWindow = focus.Target;
            }
            if ((input.Modifiers & (Modifiers.Control | Modifiers.Alt | Modifiers.Win)) != 0
                || IsPasswordControl(focus.Focus))
            {
                typed.Reset();
                return;
            }
            if (input.Key == Native.VK_BACK)
            {
                typed.DeleteBackward();
                return;
            }
            if (input.Key == Native.VK_PACKET)
            {
                // Unicode input injected by another tool: the scan code is the character.
                typed.Insert(((char)input.ScanCode).ToString());
                return;
            }
            string text = Layouts.TypedCharacter(focus.ThreadId, input.Key, input.ScanCode,
                (input.Modifiers & Modifiers.Shift) != 0, input.CapsLock);
            if (text == null) typed.Reset();
            else typed.Insert(text);
        }

        private void NoteSelectionKey(InputEvent input)
        {
            if (IsInertKey(input.Key)) return;
            Modifiers commands = input.Modifiers & (Modifiers.Control | Modifiers.Alt | Modifiers.Win);
            if (IsNavigationKey(input.Key))
            {
                // Shift with a navigation key extends the selection; without, it collapses it.
                selectionGesture = (input.Modifiers & Modifiers.Shift) != 0;
            }
            else if (commands == Modifiers.Control && input.Key == Native.VK_A)
            {
                selectionGesture = true;
            }
            else if (commands == Modifiers.None)
            {
                selectionGesture = false; // Typing replaces the selection.
            }
        }

        private void NoteMouseDown(InputEvent input)
        {
            if (!input.LeftButton) return;
            bool repeated = hasMouseDown && input.Time - mouseDownTime <= doubleClickSeconds
                && Math.Abs(input.X - mouseDownX) <= 4 && Math.Abs(input.Y - mouseDownY) <= 4;
            selectionGesture = repeated || (input.Modifiers & Modifiers.Shift) != 0;
            hasMouseDown = true;
            mouseDownX = input.X;
            mouseDownY = input.Y;
            mouseDownTime = input.Time;
        }

        private void NoteMouseUp(InputEvent input)
        {
            if (input.LeftButton && hasMouseDown
                && (Math.Abs(input.X - mouseDownX) > 4 || Math.Abs(input.Y - mouseDownY) > 4))
            {
                selectionGesture = true;
            }
        }

        // ---- Actions (the action thread) ----

        private TypedWordBuffer UsableTypedWord(FocusInfo focus)
        {
            lock (gate)
            {
                return typed.IsConvertible && typedWindow == focus.Target ? typed.Clone() : null;
            }
        }

        private void ForgetTypedWord()
        {
            lock (gate)
            {
                typed.Reset();
                selectionGesture = false;
            }
        }

        private string Transform(string text, KeyAction action, out KeyboardLayout destination)
        {
            destination = null;
            if (action == KeyAction.InvertCase) return Transforms.InvertCase(text);
            Conversion conversion = Layouts.ConversionFor(text);
            destination = conversion.Target;
            return conversion.Text;
        }

        private static string Done(KeyAction action)
        {
            return action == KeyAction.Layout ? "Layout converted." : "Letter case inverted.";
        }

        /// Strategies, in order:
        /// 1. A word typed just now: Backspace and retype it. Works in any editor that
        ///    accepts typing; used unless UI Automation shows different text before the caret.
        /// 2. UI Automation: read the selection or the word before the caret, then paste.
        /// 3. A selection the editor does not expose: copy, convert and paste it.
        /// 4. Otherwise, only switch the keyboard layout.
        private void Perform(KeyAction action, long version)
        {
            for (int retriesRemaining = 15; ; retriesRemaining--)
            {
                if (!enabled || Activity != version) return;
                FocusInfo focus = Native.GetFocusInfo();
                if (focus.Foreground == IntPtr.Zero) return;
                if (IsPasswordControl(focus.Focus))
                {
                    SecureField(action);
                    return;
                }
                if (AppCategories.ForwardsKeys(focus))
                {
                    ForgetTypedWord();
                    if (action == KeyAction.Layout)
                    {
                        Layouts.Toggle();
                        SetStatus("Keyboard layout switched; text in a remote session is not changed.");
                    }
                    else
                    {
                        SetStatus("Text in a remote session or virtual machine is not changed.");
                    }
                    return;
                }
                TypedWordBuffer word = UsableTypedWord(focus);
                if (word != null)
                {
                    switch (access.ProbeCaret(word))
                    {
                        case CaretProbe.Unavailable:
                            // A browser that is only now starting its accessibility support
                            // cannot say yet whether this is a password field. Ask again
                            // before trusting the word.
                            if (retriesRemaining > 0 && access.IsPreparingFocusedApplication)
                            {
                                Thread.Sleep(100);
                                if (Native.GetForegroundWindow() != focus.Foreground) return;
                                continue;
                            }
                            RetypeTypedWord(action, word, 0, version);
                            return;
                        case CaretProbe.Matches:
                            RetypeTypedWord(action, word, 0, version);
                            return;
                        case CaretProbe.CompletionSelected:
                            RetypeTypedWord(action, word, 1, version);
                            return;
                        case CaretProbe.Secure:
                            SecureField(action);
                            return;
                    }
                }
                try
                {
                    TextTarget target = access.Target();
                    KeyboardLayout destination;
                    string output = Transform(target.Text, action, out destination);
                    if (Activity != version) return;
                    if (output == target.Text)
                    {
                        if (destination != null) Layouts.Select(destination, false);
                        SetStatus("No letters needed changing.");
                        return;
                    }
                    bool positioned;
                    try
                    {
                        positioned = replacement.Replace(target, output);
                    }
                    catch (SelectionRejected)
                    {
                        if (word == null || Activity != version) throw;
                        RetypeTypedWord(action, word, 0, version);
                        return;
                    }
                    ForgetTypedWord();
                    if (destination != null) Layouts.Select(destination, true);
                    SetStatus(positioned ? Done(action) : Done(action) + " The cursor may be before the spaces that follow.");
                    return;
                }
                catch (TextTargetFailure failure)
                {
                    if (failure.Kind == TargetFailureKind.Secure)
                    {
                        SecureField(action);
                        return;
                    }
                    // UI Automation shows no text at all: trust what was typed.
                    if (word != null)
                    {
                        RetypeTypedWord(action, word, 0, version);
                        return;
                    }
                    if (retriesRemaining > 0 && access.IsPreparingFocusedApplication)
                    {
                        SetStatus("Waiting for the editor's accessibility support…");
                        Thread.Sleep(200);
                        if (Native.GetForegroundWindow() != focus.Foreground) return;
                        continue;
                    }
                    ReplaceCopiedSelection(action, failure, focus, version);
                    return;
                }
            }
        }

        private void SecureField(KeyAction action)
        {
            ForgetTypedWord();
            if (action == KeyAction.Layout)
            {
                Layouts.Toggle();
                SetStatus("Keyboard layout switched. Password fields are excluded.");
            }
            else
            {
                SetStatus("Password fields are excluded.");
            }
        }

        private void RetypeTypedWord(KeyAction action, TypedWordBuffer word, int extraDeletions, long version)
        {
            string text = word.Text;
            KeyboardLayout destination;
            string output = Transform(text, action, out destination);
            if (Activity != version) return;
            // Ask for the new layout first; the request and the typed characters reach
            // the application in this order.
            Layouts.PendingSwitch pending = destination == null ? null : Layouts.Request(destination);
            string layoutError = null;
            if (output != text)
            {
                if (!KeySender.WaitForModifiersReleased(250)) throw new AppFailure(TextReplacement.ModifiersHeldMessage);
                if (Activity != version) return;
                try
                {
                    replacement.Retype(word.CharacterCount + extraDeletions, output + word.TrailingText);
                }
                catch (AppFailure)
                {
                    ForgetTypedWord();
                    throw;
                }
                lock (gate)
                {
                    // Allow tapping again to undo, unless other input arrived meanwhile.
                    if (Activity == version) typed.ReplaceWord(output);
                    else typed.Reset();
                    selectionGesture = false;
                }
            }
            if (pending != null)
            {
                try
                {
                    Layouts.Confirm(pending, output != text);
                }
                catch (AppFailure failure)
                {
                    layoutError = failure.Message;
                }
            }
            if (layoutError != null) Fail(layoutError);
            else SetStatus(output == text ? "No letters needed changing." : Done(action));
        }

        private void ReplaceCopiedSelection(KeyAction action, TextTargetFailure failure, FocusInfo focus, long version)
        {
            bool gesture;
            lock (gate) gesture = selectionGesture;
            // A caret with no word before it needs no copy, unless the user has just
            // selected something: then the editor is exposing a stand-in for its text.
            bool worthTrying = gesture
                || (failure.Kind == TargetFailureKind.Unavailable && failure.AllowsCopy && !failure.SelectionEmpty);
            if (!worthTrying || failure.Terminal || !AppCategories.AllowsClipboardFallback(focus))
            {
                SwitchLayoutWithoutText(action, failure);
                return;
            }
            if (Activity != version) return;
            KeyboardLayout destination = null;
            bool nothingCopied = false;
            replacement.ReplaceSelectionUsingClipboard(
                delegate(string text)
                {
                    KeyboardLayout layout;
                    string output = Transform(text, action, out layout);
                    destination = layout;
                    return output;
                },
                delegate(ClipboardOutcome outcome)
                {
                    if (outcome == ClipboardOutcome.NothingCopied)
                    {
                        nothingCopied = true;
                        return;
                    }
                    ForgetTypedWord();
                    try
                    {
                        if (destination != null) Layouts.Select(destination, outcome == ClipboardOutcome.Replaced);
                        SetStatus(outcome == ClipboardOutcome.Unchanged ? "No letters needed changing." : Done(action));
                    }
                    catch (AppFailure layoutFailure)
                    {
                        Fail(layoutFailure.Message);
                    }
                });
            if (nothingCopied) SwitchLayoutWithoutText(action, failure);
        }

        /// A press of Caps Lock: the layout switches at once; no text is read or changed.
        private void SwitchLayoutOnly()
        {
            layoutBeforeCapsLock = 0;
            if (!enabled) return;
            uint before = Layouts.CurrentId;
            Layouts.Toggle();
            layoutBeforeCapsLock = before;
            SetStatus("Keyboard layout switched.");
        }

        /// The press turned out to be a long one, which means the regular Caps Lock:
        /// its state is toggled, and the layout goes back to what it was.
        private void ToggleCapsLock()
        {
            uint before = layoutBeforeCapsLock;
            layoutBeforeCapsLock = 0;
            if (!enabled) return;
            KeySender.Send(KeySender.CapsLock());
            if (before != 0 && Layouts.CurrentId != before)
            {
                KeyboardLayout layout = Layouts.Find(before);
                if (layout != null) Layouts.Select(layout, false);
            }
            SetStatus("Caps Lock toggled; the keyboard layout is as it was.");
        }

        private void SwitchLayoutWithoutText(KeyAction action, TextTargetFailure failure)
        {
            bool noWord = failure.Kind == TargetFailureKind.NoWord;
            if (action == KeyAction.Layout)
            {
                Layouts.Toggle();
                SetStatus(noWord ? "Keyboard layout switched."
                    : "Keyboard layout switched; text was not changed (" + failure.Reason + ").");
            }
            else if (noWord)
            {
                SetStatus("Select text or put the cursor after a word.");
            }
            else
            {
                Fail("Cannot read this editor's selection (" + failure.Reason + ").");
            }
        }

        // ---- The "Test conversion" controls ----

        /// Runs an action in another window through the same pipeline as a tap.
        public void ApplyInEditor(KeyAction action, IntPtr window)
        {
            if (!enabled || !running)
            {
                SetStatus("Resume KeyFlip first.");
                return;
            }
            IntPtr target = window;
            if (target == IntPtr.Zero)
            {
                lock (gate) target = lastExternalWindow;
            }
            if (target == IntPtr.Zero || !Native.IsWindow(target))
            {
                SetStatus("Open an editor and select some text first.");
                return;
            }
            if (Native.IsIconic(target)) Native.ShowWindow(target, 9); // SW_RESTORE
            Native.SetForegroundWindow(target);
            Enqueue(delegate
            {
                for (int waited = 0; Native.GetForegroundWindow() != target; waited += 25)
                {
                    if (waited >= 500)
                    {
                        SetStatus("Could not bring the selected window to the front.");
                        return;
                    }
                    Thread.Sleep(25);
                }
                Thread.Sleep(250);
                if (Native.GetForegroundWindow() != target) return;
                Perform(action, Activity);
            });
        }

        internal static bool IsApplicationWindow(IntPtr window)
        {
            if (!Native.IsWindowVisible(window) || Native.GetWindow(window, Native.GW_OWNER) != IntPtr.Zero) return false;
            if ((Native.GetWindowLong(window, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return false;
            int cloaked;
            if (Native.DwmGetWindowAttribute(window, Native.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return false;
            }
            return Native.WindowTitle(window).Length > 0;
        }

        private static string Describe(IntPtr window, uint process)
        {
            string title = Native.WindowTitle(window);
            if (title.Length > 60) title = title.Substring(0, 59) + "…";
            string name = Native.ProcessName(process);
            return name.Length == 0 ? title : title + " (" + name + ")";
        }

        /// Windows the user could pick as a test target.
        public List<WindowInfo> TestWindows()
        {
            List<WindowInfo> windows = new List<WindowInfo>();
            Native.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint process;
                Native.GetWindowThreadProcessId(window, out process);
                if (process != ownProcess && IsApplicationWindow(window))
                {
                    WindowInfo info = new WindowInfo();
                    info.Handle = window;
                    info.Title = Describe(window, process);
                    windows.Add(info);
                }
                return true;
            }, IntPtr.Zero);
            windows.Sort(delegate(WindowInfo a, WindowInfo b)
            {
                return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            });
            return windows;
        }

        /// For checks: the tracked word right now.
        internal string TypedWordForChecks()
        {
            lock (gate) return typed.ToString();
        }
    }
}
