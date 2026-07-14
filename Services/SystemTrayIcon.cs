using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Codec.Services
{
    internal sealed class SystemTrayIcon : IDisposable
    {
        private const uint CallbackMessage = 0x8001;
        private const uint OpenCommand = 1;
        private const uint QuitCommand = 2;
        private const uint WmNull = 0x0000;
        private const uint WmLeftButtonUp = 0x0202;
        private const uint WmRightButtonUp = 0x0205;
        private const uint ImageIcon = 1;
        private const uint LoadFromFile = 0x0010;
        private const uint NotifyMessage = 0x0001;
        private const uint NotifyIcon = 0x0002;
        private const uint NotifyTip = 0x0004;
        private const uint NotifyAdd = 0x00000000;
        private const uint NotifyDelete = 0x00000002;
        private const uint MenuString = 0x00000000;
        private const uint TrackRightButton = 0x0002;
        private const uint TrackReturnCommand = 0x0100;
        private const string WindowClassName = "CodecSystemTrayWindow";

        private static readonly WindowProcedureCallback WindowProcedureDelegate = HandleWindowMessage;
        private static SystemTrayIcon? _current;

        private readonly Action _open;
        private readonly Action _quit;
        private IntPtr _windowHandle;
        private IntPtr _iconHandle;
        private bool _disposed;

        public SystemTrayIcon(string iconPath, Action open, Action quit)
        {
            _open = open;
            _quit = quit;
            _current = this;

            IntPtr moduleHandle = GetModuleHandle(null);
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Instance = moduleHandle,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedureDelegate),
                ClassName = WindowClassName
            };

            RegisterClassEx(ref windowClass);
            _windowHandle = CreateWindowEx(
                0, WindowClassName, string.Empty, 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, moduleHandle, IntPtr.Zero);
            if (_windowHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            _iconHandle = LoadImage(
                IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile);
            if (_iconHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var data = CreateIconData();
            data.Flags = NotifyMessage | NotifyIcon | NotifyTip;
            data.CallbackMessage = CallbackMessage;
            data.Icon = _iconHandle;
            data.Tip = "Codec";
            if (!ShellNotifyIcon(NotifyAdd, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            var data = CreateIconData();
            ShellNotifyIcon(NotifyDelete, ref data);

            if (_iconHandle != IntPtr.Zero)
                DestroyIcon(_iconHandle);
            if (_windowHandle != IntPtr.Zero)
                DestroyWindow(_windowHandle);

            if (ReferenceEquals(_current, this))
                _current = null;
            _iconHandle = IntPtr.Zero;
            _windowHandle = IntPtr.Zero;
        }

        private NotifyIconData CreateIconData() => new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = 1
        };

        private static IntPtr HandleWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == CallbackMessage && _current is not null)
            {
                uint trayMessage = unchecked((uint)lParam.ToInt64());
                if (trayMessage == WmLeftButtonUp)
                    _current._open();
                else if (trayMessage == WmRightButtonUp)
                    _current.ShowContextMenu();
            }

            return DefWindowProc(window, message, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                return;

            AppendMenu(menu, MenuString, OpenCommand, "Open Codec");
            AppendMenu(menu, MenuString, QuitCommand, "Quit Codec");
            GetCursorPos(out Point cursor);
            SetForegroundWindow(_windowHandle);
            uint command = TrackPopupMenu(
                menu, TrackRightButton | TrackReturnCommand,
                cursor.X, cursor.Y, 0, _windowHandle, IntPtr.Zero);
            PostMessage(_windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(menu);

            if (command == OpenCommand)
                _open();
            else if (command == QuitCommand)
                _quit();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size;
            public uint Style;
            public IntPtr WindowProcedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string? MenuName;
            public string ClassName;
            public IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint Size;
            public IntPtr WindowHandle;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State;
            public uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint TimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public uint InfoFlags;
            public Guid GuidItem;
            public IntPtr BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        private delegate IntPtr WindowProcedureCallback(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
            uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
            IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImage(IntPtr instance, string name, uint type,
            int desiredWidth, int desiredHeight, uint loadFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, uint itemId, string text);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
            int reserved, IntPtr window, IntPtr rectangle);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
