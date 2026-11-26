using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyFlip
{
    /// The focused control of the foreground window and the thread whose keyboard
    /// layout applies to it.
    internal struct FocusInfo
    {
        public IntPtr Foreground;
        public IntPtr Focus;
        public uint ThreadId;
        public uint ProcessId;

        /// The window that receives typing: the focused control, or the foreground window.
        public IntPtr Target { get { return Focus != IntPtr.Zero ? Focus : Foreground; } }
    }

    internal static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int HC_ACTION = 0;

        public const int WM_QUIT = 0x0012;
        public const int WM_INPUTLANGCHANGEREQUEST = 0x0050;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_MOUSEHWHEEL = 0x020E;
        public const int WM_APP = 0x8000;

        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_BACK = 0x08;
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_CAPITAL = 0x14;
        public const int VK_PRIOR = 0x21;
        public const int VK_DOWN = 0x28;
        public const int VK_INSERT = 0x2D;
        public const int VK_A = 0x41;
        public const int VK_NUMLOCK = 0x90;
        public const int VK_SCROLL = 0x91;
        public const int VK_PROCESSKEY = 0xE5;
        public const int VK_PACKET = 0xE7;

        public const uint LLKHF_EXTENDED = 0x01;

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_DESKTOPSWITCH = 0x0020;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        public const uint MAPVK_VK_TO_VSC = 0;
        public const uint MAPVK_VSC_TO_VK = 1;

        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MINIMIZE = 0xF020;
        public const int WS_MINIMIZEBOX = 0x00020000;
        public const uint GA_ROOTOWNER = 3;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_ASYNCWINDOWPOS = 0x4000;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenElevation = 20;

        public const uint CF_BITMAP = 2;
        public const uint CF_METAFILEPICT = 3;
        public const uint CF_PALETTE = 9;
        public const uint CF_UNICODETEXT = 13;
        public const uint CF_ENHMETAFILE = 14;
        public const uint GMEM_MOVEABLE = 0x0002;

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int ES_PASSWORD = 0x0020;
        public const int WS_EX_TOOLWINDOW = 0x0080;
        public const uint GW_OWNER = 4;
        public const int DWMWA_CLOAKED = 14;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const int ATTACH_PARENT_PROCESS = -1;
        public const int STD_OUTPUT_HANDLE = -11;

        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        // Hooks and the message loop.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG message);
        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG message);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();

        // Keyboard state and input.
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);
        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("user32.dll")]
        public static extern uint GetDoubleClickTime();
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint milliseconds);
        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint milliseconds);

        // Keyboard layouts.
        [DllImport("user32.dll")]
        public static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("user32.dll")]
        public static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] list);
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState,
            [Out, MarshalAs(UnmanagedType.LPArray)] char[] buffer, int bufferSize, uint flags, IntPtr layout);

        // Windows and processes.
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")]
        public static extern bool IsHungAppWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int size, out int returned);
        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint kind);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();

        // Clipboard.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        public static extern uint EnumClipboardFormats(uint format);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")]
        public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("gdi32.dll")]
        public static extern uint GetEnhMetaFileBits(IntPtr metafile, uint size, [Out] byte[] buffer);
        [DllImport("gdi32.dll")]
        public static extern IntPtr SetEnhMetaFileBits(uint size, byte[] buffer);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteEnhMetaFile(IntPtr metafile);

        // Window stations, for an isolated clipboard in the platform checks.
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessWindowStation(IntPtr station);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr deviceMode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetThreadDesktop(IntPtr desktop);

        [DllImport("imm32.dll")]
        public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

        // Console output for --diagnose from a windowed executable.
        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int processId);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll")]
        public static extern uint GetFileType(IntPtr handle);

        public static int GetWindowLong(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8 ? unchecked((int)(long)GetWindowLongPtr64(hwnd, index)) : GetWindowLong32(hwnd, index);
        }

        public static bool IsKeyDown(int key)
        {
            return GetAsyncKeyState(key) < 0;
        }

        public static FocusInfo GetFocusInfo()
        {
            FocusInfo info = new FocusInfo();
            info.Foreground = GetForegroundWindow();
            if (info.Foreground == IntPtr.Zero) return info;
            info.ThreadId = GetWindowThreadProcessId(info.Foreground, out info.ProcessId);
            GUITHREADINFO gui = new GUITHREADINFO();
            gui.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (GetGUIThreadInfo(info.ThreadId, ref gui) && gui.hwndFocus != IntPtr.Zero)
            {
                info.Focus = gui.hwndFocus;
                uint processId;
                uint threadId = GetWindowThreadProcessId(gui.hwndFocus, out processId);
                if (threadId != 0)
                {
                    info.ThreadId = threadId;
                    info.ProcessId = processId;
                }
            }
            if (GetKeyboardLayout(info.ThreadId) == IntPtr.Zero)
            {
                // A classic console window reports the thread of the program running in
                // it, which has no keyboard layout. The window's input method window
                // belongs to the thread that really owns it.
                uint ignored;
                uint owner = GetWindowThreadProcessId(ImmGetDefaultIMEWnd(info.Foreground), out ignored);
                if (owner != 0 && GetKeyboardLayout(owner) != IntPtr.Zero) info.ThreadId = owner;
            }
            return info;
        }

        public static string ClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            StringBuilder name = new StringBuilder(256);
            return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
        }

        public static string WindowTitle(IntPtr hwnd)
        {
            StringBuilder text = new StringBuilder(512);
            return GetWindowText(hwnd, text, text.Capacity) > 0 ? text.ToString() : "";
        }

        /// Whether a process runs as administrator: 1 when it does, 0 when it does not,
        /// and -1 when Windows will not say.
        public static int Elevation(uint processId)
        {
            if (processId == 0) return -1;
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process == IntPtr.Zero) return -1;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return -1;
                try
                {
                    int elevated;
                    int size;
                    if (!GetTokenInformation(token, TokenElevation, out elevated, sizeof(int), out size)) return -1;
                    return elevated != 0 ? 1 : 0;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        /// The executable name of a process without its extension, lowercase; "" when unknown.
        public static string ProcessName(uint processId)
        {
            if (processId == 0) return "";
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process == IntPtr.Zero) return "";
            try
            {
                StringBuilder path = new StringBuilder(1024);
                int size = path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref size)) return "";
                return System.IO.Path.GetFileNameWithoutExtension(path.ToString()).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return "";
            }
            finally
            {
                CloseHandle(process);
            }
        }
    }
}
