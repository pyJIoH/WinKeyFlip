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
        /// Side-agnostic modifier state after this event.
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
        private const int FakeLeftControlScanCode = 0x21D;

        private static readonly int[] ModifierKeys =
        {
            VirtualKeys.LeftShift, VirtualKeys.RightShift, VirtualKeys.LeftControl, VirtualKeys.RightControl,
            VirtualKeys.LeftAlt, VirtualKeys.RightAlt, VirtualKeys.LeftWin, VirtualKeys.RightWin
        };

        public Action<InputEvent> OnEvent;
        public Action<IntPtr> OnForegroundChanged;

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
        private volatile bool running;
        private volatile bool capsLockSwitchesLayout;
        // Only the input thread touches this.
        private readonly CapsLockGesture capsLock = new CapsLockGesture();
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
                        if (!InstallInputHooks()) break;
                        continue;
                    }
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessage(ref message);
                }
            }
            running = false;
            RemoveInputHooks();
            if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
            foregroundHook = IntPtr.Zero;
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

        private static Modifiers ModifierOf(int key)
        {
            switch (key)
            {
                case VirtualKeys.LeftShift:
                case VirtualKeys.RightShift:
                    return Modifiers.Shift;
                case VirtualKeys.LeftControl:
                case VirtualKeys.RightControl:
                    return Modifiers.Control;
                case VirtualKeys.LeftAlt:
                case VirtualKeys.RightAlt:
                    return Modifiers.Alt;
                case VirtualKeys.LeftWin:
                case VirtualKeys.RightWin:
                    return Modifiers.Win;
                default:
                    return Modifiers.None;
            }
        }

        /// Reads the modifier state from the system on every event. Inside a low-level
        /// hook the asynchronous key state does not yet include the current event, so
        /// that key is applied by hand. Nothing is accumulated: a key-up that Windows
        /// never delivered (secure desktop, lock screen) cannot leave a modifier stuck.
        private static Modifiers ModifiersAfter(int changedKey, bool down)
        {
            Modifiers modifiers = Modifiers.None;
            foreach (int key in ModifierKeys)
            {
                bool held = key == changedKey ? down : Native.IsKeyDown(key);
                if (held) modifiers |= ModifierOf(key);
            }
            return modifiers;
        }

        private static bool MouseButtonHeld()
        {
            return Native.IsKeyDown(Native.VK_LBUTTON) || Native.IsKeyDown(Native.VK_RBUTTON)
                || Native.IsKeyDown(Native.VK_MBUTTON);
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
                        // Caps Lock, taken to switch the layout: no other program sees it.
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
            InputEvent input = new InputEvent();
            input.Key = key;
            input.ScanCode = scanCode;
            input.Time = Now();
            if (key == Native.VK_CAPITAL)
            {
                // With a modifier held, a new press stays the regular Caps Lock.
                int tick = Environment.TickCount;
                CapsLockStep step = down
                    ? capsLock.Down(tick, capsLockSwitchesLayout && ModifiersAfter(0, false) == Modifiers.None)
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
            if (ModifierOf(key) != Modifiers.None)
            {
                // Only real transitions count. This drops auto-repeat, and also the
                // release of a left Ctrl that was never pressed, which Windows sends
                // whenever the keyboard layout changes.
                if (down == Native.IsKeyDown(key)) return false;
                input.Kind = InputKind.ModifiersChanged;
                input.Modifiers = ModifiersAfter(key, down);
                input.MouseButtonHeld = MouseButtonHeld();
                // The left Ctrl that Windows adds to AltGr is not a key the user pressed:
                // it changes the modifier state, but it must never start or finish a tap.
                if (scanCode == FakeLeftControlScanCode) input.Key = 0;
            }
            else
            {
                if (!down) return false; // Releases of ordinary keys carry no information.
                bool lockKey = key == Native.VK_CAPITAL || key == Native.VK_NUMLOCK || key == Native.VK_SCROLL;
                input.Kind = lockKey ? InputKind.LockKey : InputKind.KeyDown;
                input.Modifiers = ModifiersAfter(0, false);
                input.CapsLock = (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;
            }
            handler(input);
            return false;
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
            // MSLLHOOKSTRUCT starts with the screen position.
            input.X = Marshal.ReadInt32(lParam, 0);
            input.Y = Marshal.ReadInt32(lParam, 4);
            input.Modifiers = ModifiersAfter(0, false);
            input.Time = Now();
            handler(input);
        }

        private void ForegroundProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
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
