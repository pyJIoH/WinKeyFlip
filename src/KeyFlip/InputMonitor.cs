using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using KeyFlip.Core;

namespace KeyFlip
{
    internal enum InputKind
    {
        /// A modifier key went down or up (auto-repeat is filtered out).
        ModifiersChanged,
        /// Any other key went down.
        KeyDown,
        /// Caps Lock, Num Lock or Scroll Lock went down.
        LockKey,
        /// Caps Lock was pressed alone while it switches the layout. The press is kept
        /// from the rest of the system, so the Caps Lock state does not change.
        LayoutSwitch,
        /// That press has lasted long enough to be meant as the regular Caps Lock.
        CapsLockHeld,
        MouseDown,
        MouseUp,
        Scroll
    }

    internal struct InputEvent
    {
        public InputKind Kind;
        /// Virtual-key code.
        public int Key;
        public int ScanCode;
        /// Side-agnostic modifier state after this event: the keys that are physically
        /// held, or, for a key press that a Mac shortcut passed on with other
        /// modifiers, the modifiers the application receives.
        public Modifiers Modifiers;
        public bool CapsLock;
        public bool MouseButtonHeld;
        public bool LeftButton;
        public int X;
        public int Y;
        /// Seconds on a monotonic clock.
        public double Time;
    }

    /// Low-level keyboard and mouse hooks plus a foreground-change hook, on a dedicated
    /// thread with its own message loop. Callbacks must return quickly: Windows silently
    /// removes a low-level hook whose callback is slow, and every key waits for it.
    internal sealed class InputMonitor
    {
        private const uint ReinstallMessage = Native.WM_APP + 1;
        private const uint ResetShortcutsMessage = Native.WM_APP + 2;
        private const int FakeLeftControlScanCode = KeyRemapper.FakeControlScanCode;

        public Action<InputEvent> OnEvent;
        public Action<IntPtr> OnForegroundChanged;
        /// A Mac shortcut that is not a key press. Called inside the keyboard hook.
        public Action<ShortcutCommand> OnCommand;

        // The delegates must stay referenced for as long as the hooks exist.
        private readonly Native.HookProc keyboardProc;
        private readonly Native.HookProc mouseProc;
        private readonly Native.WinEventProc foregroundProc;
        private readonly object gate = new object();
        private Thread thread;
        private uint threadId;
        private IntPtr keyboardHook;
        private IntPtr mouseHook;
        private IntPtr foregroundHook;
        private IntPtr desktopHook;
        private volatile bool running;
        private volatile bool capsLockSwitchesLayout;
        private volatile bool macShortcuts;
        // Only the input thread touches these.
        private readonly CapsLockGesture capsLock = new CapsLockGesture();
        private readonly KeyRemapper remapper = new KeyRemapper();
        private readonly ShortcutContext context = new ShortcutContext();
        /// Releases of lent keys that Windows refused, to send as soon as it will take them.
        private Native.INPUT[] unsent;
        private int lastEventTick;

        public InputMonitor()
        {
            keyboardProc = KeyboardProc;
            mouseProc = MouseProc;
            foregroundProc = ForegroundProc;
        }

        public bool Running { get { return running; } }

        /// While set, a Caps Lock press with no modifier held is reported as
        /// `LayoutSwitch` and no other program sees it.
        public bool CapsLockSwitchesLayout
        {
            get { return capsLockSwitchesLayout; }
            set { capsLockSwitchesLayout = value; }
        }

        /// While set, Alt is Command and the Windows key is Option: see `MacShortcuts`.
        public bool MacShortcuts
        {
            get { return macShortcuts; }
            set
            {
                if (value == macShortcuts) return;
                macShortcuts = value;
                // Whatever a shortcut that is under way holds down must not stay down.
                ResetShortcuts();
            }
        }

        /// Programs in which the Mac shortcuts are left alone, as the user wrote them.
        public void SetProgramsWithoutMacShortcuts(string names)
        {
            context.SetExcluded(names);
        }

        /// Forgets the modifier keys that the Mac shortcuts believe are held: their
        /// releases were missed, or are about to be. May be called on any thread.
        public void ResetShortcuts()
        {
            lock (gate)
            {
                if (running) Native.PostThreadMessage(threadId, ResetShortcutsMessage, IntPtr.Zero, IntPtr.Zero);
            }
        }

        /// `Environment.TickCount` of the last event either hook saw.
        public int LastEventTick { get { return Volatile.Read(ref lastEventTick); } }

        public static double Now()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        public bool Start()
        {
            lock (gate)
            {
                if (running) return true;
                using (ManualResetEventSlim ready = new ManualResetEventSlim(false))
                {
                    thread = new Thread(delegate() { Run(ready); });
                    thread.IsBackground = true;
                    thread.Name = "KeyFlip input";
                    thread.Start();
                    ready.Wait();
                }
                if (!running)
                {
                    thread.Join(1000);
                    thread = null;
                }
                return running;
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (thread == null) return;
                running = false;
                Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread.Join(2000);
                thread = null;
            }
        }

        /// Replaces the input hooks, in case Windows dropped them.
        public void Reinstall()
        {
            lock (gate)
            {
                if (running) Native.PostThreadMessage(threadId, ReinstallMessage, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private void Run(ManualResetEventSlim ready)
        {
            threadId = Native.GetCurrentThreadId();
            Native.MSG message;
            bool installed = InstallInputHooks();
            if (installed)
            {
                foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
                desktopHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_DESKTOPSWITCH, Native.EVENT_SYSTEM_DESKTOPSWITCH,
                    IntPtr.Zero, foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            }
            running = installed;
            ready.Set();
            if (installed)
            {
                while (Native.GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.hwnd == IntPtr.Zero && message.message == ReinstallMessage)
                    {
                        RemoveInputHooks();
                        // Keys were pressed and released while the hooks were gone.
                        ForgetHeldKeys();
                        if (!InstallInputHooks()) break;
                        continue;
                    }
                    if (message.hwnd == IntPtr.Zero && message.message == ResetShortcutsMessage)
                    {
                        ForgetHeldKeys();
                        continue;
                    }
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessage(ref message);
                }
            }
            running = false;
            ForgetHeldKeys();
            RemoveInputHooks();
            if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
            if (desktopHook != IntPtr.Zero) Native.UnhookWinEvent(desktopHook);
            foregroundHook = IntPtr.Zero;
            desktopHook = IntPtr.Zero;
        }

        private bool InstallInputHooks()
        {
            IntPtr module = Native.GetModuleHandle(null);
            Volatile.Write(ref lastEventTick, Environment.TickCount);
            keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
            if (keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero) return true;
            RemoveInputHooks();
            return false;
        }

        private void RemoveInputHooks()
        {
            if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            keyboardHook = IntPtr.Zero;
            mouseHook = IntPtr.Zero;
        }

        /// Reads the modifier keys from the system on every event, one bit per key as
        /// in `KeyRemapper`. Inside a low-level hook the asynchronous key state does not
        /// yet include the current event. Nothing is accumulated: a key-up that Windows
        /// never delivered (secure desktop, lock screen) cannot leave a modifier stuck.
        private static int SystemKeys()
        {
            int keys = 0;
            for (int i = 0; i < KeyRemapper.ModifierKeys.Length; i++)
            {
                if (Native.IsKeyDown(KeyRemapper.ModifierKeys[i])) keys |= 1 << i;
            }
            return keys;
        }

        /// The modifier keys that are physically held. The Mac shortcuts hide some of
        /// them from the system and lend it others.
        private int HeldKeys()
        {
            int system = SystemKeys();
            return macShortcuts ? remapper.HeldGiven(system) : system;
        }

        private static bool MouseButtonHeld()
        {
            return Native.IsKeyDown(Native.VK_LBUTTON) || Native.IsKeyDown(Native.VK_RBUTTON)
                || Native.IsKeyDown(Native.VK_MBUTTON);
        }

        /// Sends what the Mac shortcuts asked for. From inside a hook, Windows handles
        /// these events before the one that the hook is looking at.
        private void SendShortcutKeys()
        {
            SendShortcutKeys(false);
        }

        private void SendShortcutKeys(bool releases)
        {
            if (unsent != null)
            {
                Native.INPUT[] earlier = unsent;
                unsent = null;
                if (Native.SendInput((uint)earlier.Length, earlier, KeySender.InputSize) != earlier.Length) unsent = earlier;
            }
            if (remapper.Output.Count == 0) return;
            Native.INPUT[] inputs = KeySender.Strokes(remapper.Output);
            remapper.Output.Clear();
            // Windows refuses while a window that runs as administrator is in front.
            // What the shortcuts believe is corrected from the system's state on the
            // next event; only the release of a lent key has to be remembered, or
            // that key would stay down.
            if (Native.SendInput((uint)inputs.Length, inputs, KeySender.InputSize) == inputs.Length || !releases) return;
            if (unsent != null)
            {
                Native.INPUT[] all = new Native.INPUT[unsent.Length + inputs.Length];
                unsent.CopyTo(all, 0);
                inputs.CopyTo(all, unsent.Length);
                inputs = all;
            }
            unsent = inputs;
        }

        private void ForgetHeldKeys()
        {
            remapper.Reset(SystemKeys());
            SendShortcutKeys(true);
        }

        private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == Native.HC_ACTION)
            {
                Volatile.Write(ref lastEventTick, Environment.TickCount);
                try
                {
                    // KBDLLHOOKSTRUCT: vkCode, scanCode, flags, time, dwExtraInfo.
                    uint extra = unchecked((uint)Marshal.ReadIntPtr(lParam, 16).ToInt64());
                    if (extra != KeySender.TagValue
                        && HandleKey(wParam.ToInt32(), Marshal.ReadInt32(lParam, 0), Marshal.ReadInt32(lParam, 4)))
                    {
                        // Caps Lock taken to switch the layout, or a key that a Mac
                        // shortcut replaces: no other program sees it.
                        return (IntPtr)1;
                    }
                }
                catch (Exception error)
                {
                    Debug.WriteLine("KeyFlip keyboard hook: " + error);
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        /// Returns true when the event must not be passed on.
        private bool HandleKey(int message, int key, int scanCode)
        {
            Action<InputEvent> handler = OnEvent;
            if (handler == null) return false;
            bool down = message == Native.WM_KEYDOWN || message == Native.WM_SYSKEYDOWN;
            bool remap = macShortcuts;
            int system = SystemKeys();
            InputEvent input = new InputEvent();
            input.Key = key;
            input.ScanCode = scanCode;
            input.Time = Now();
            if (key == Native.VK_CAPITAL)
            {
                // With a modifier held, a new press stays the regular Caps Lock.
                int tick = Environment.TickCount;
                int held = remap ? remapper.HeldGiven(system) : system;
                CapsLockStep step = down
                    ? capsLock.Down(tick, capsLockSwitchesLayout && held == 0)
                    : capsLock.Up(tick);
                if (step != CapsLockStep.Pass)
                {
                    if (step != CapsLockStep.Swallow)
                    {
                        input.Kind = step == CapsLockStep.Hold ? InputKind.CapsLockHeld : InputKind.LayoutSwitch;
                        handler(input);
                    }
                    return true;
                }
            }
            int bit = KeyRemapper.BitOf(key);
            bool swallow = false;
            bool transition;
            int after;
            Modifiers received = Modifiers.None;
            bool passedWithOtherModifiers = false;
            if (remap)
            {
                swallow = remapper.Key(key, scanCode, down, system, Environment.TickCount, context);
                transition = remapper.Transition;
                after = remapper.Held;
                passedWithOtherModifiers = !swallow && remapper.EffectiveKey == key;
                received = remapper.EffectiveModifiers;
                ShortcutCommand command = remapper.Command;
                SendShortcutKeys();
                if (command != ShortcutCommand.None)
                {
                    Action<ShortcutCommand> run = OnCommand;
                    if (run != null) run(command);
                }
            }
            else
            {
                // Only real transitions count. This drops auto-repeat, and also the
                // release of a left Ctrl that was never pressed, which Windows sends
                // whenever the keyboard layout changes.
                transition = bit != 0 && down != ((system & bit) != 0);
                after = bit == 0 ? system : down ? system | bit : system & ~bit;
            }
            if (bit != 0)
            {
                if (!transition) return swallow;
                input.Kind = InputKind.ModifiersChanged;
                input.Modifiers = KeyRemapper.ModifiersOf(after);
                input.MouseButtonHeld = MouseButtonHeld();
                // The left Ctrl that Windows adds to AltGr is not a key the user pressed:
                // it changes the modifier state, but it must never start or finish a tap.
                if (scanCode == FakeLeftControlScanCode) input.Key = 0;
            }
            else
            {
                if (!down) return swallow; // Releases of ordinary keys carry no information.
                bool lockKey = key == Native.VK_CAPITAL || key == Native.VK_NUMLOCK || key == Native.VK_SCROLL;
                input.Kind = lockKey ? InputKind.LockKey : InputKind.KeyDown;
                // Alt+A that went on as Ctrl+A is Ctrl+A to the editor. A key that a
                // shortcut replaced is reported as it was pressed, with Alt or Win held:
                // a command, whatever it turned into.
                input.Modifiers = passedWithOtherModifiers ? received : KeyRemapper.ModifiersOf(after);
                input.CapsLock = (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;
            }
            handler(input);
            return swallow;
        }

        private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == Native.HC_ACTION)
            {
                Volatile.Write(ref lastEventTick, Environment.TickCount);
                int message = wParam.ToInt32();
                // Movement is by far the most frequent event: leave as early as possible.
                if (message != Native.WM_MOUSEMOVE)
                {
                    try
                    {
                        HandleMouse(message, lParam);
                    }
                    catch (Exception error)
                    {
                        Debug.WriteLine("KeyFlip mouse hook: " + error);
                    }
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private void HandleMouse(int message, IntPtr lParam)
        {
            Action<InputEvent> handler = OnEvent;
            if (handler == null) return;
            InputEvent input = new InputEvent();
            switch (message)
            {
                case Native.WM_LBUTTONDOWN:
                    input.Kind = InputKind.MouseDown;
                    input.LeftButton = true;
                    break;
                case Native.WM_RBUTTONDOWN:
                case Native.WM_MBUTTONDOWN:
                case Native.WM_XBUTTONDOWN:
                    input.Kind = InputKind.MouseDown;
                    break;
                case Native.WM_LBUTTONUP:
                    input.Kind = InputKind.MouseUp;
                    input.LeftButton = true;
                    break;
                case Native.WM_MOUSEWHEEL:
                case Native.WM_MOUSEHWHEEL:
                    input.Kind = InputKind.Scroll;
                    break;
                default:
                    return;
            }
            if (input.Kind == InputKind.MouseDown && macShortcuts)
            {
                // Command+click is Ctrl+click: Ctrl goes down before the click arrives.
                remapper.MouseDown(SystemKeys(), context);
                SendShortcutKeys();
            }
            // MSLLHOOKSTRUCT starts with the screen position.
            input.X = Marshal.ReadInt32(lParam, 0);
            input.Y = Marshal.ReadInt32(lParam, 4);
            input.Modifiers = KeyRemapper.ModifiersOf(HeldKeys());
            input.Time = Now();
            handler(input);
        }

        private void ForegroundProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (eventType == Native.EVENT_SYSTEM_DESKTOPSWITCH)
                {
                    // The lock screen or a permission prompt took the keyboard.
                    ForgetHeldKeys();
                    return;
                }
                if (macShortcuts)
                {
                    if (context.HidesInput(hwnd)) ForgetHeldKeys();
                    else if (unsent != null) SendShortcutKeys();
                }
                Action<IntPtr> handler = OnForegroundChanged;
                if (handler != null) handler(hwnd);
            }
            catch (Exception error)
            {
                Debug.WriteLine("KeyFlip foreground hook: " + error);
            }
        }
    }
}
