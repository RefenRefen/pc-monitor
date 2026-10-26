using System.Runtime.InteropServices;

namespace PcMonitor.Widget.Native;

/// <summary>
/// Notification-area icon via Shell_NotifyIcon on a message-only window (no WinForms, no extra dependency).
/// Left click toggles the widget; right click shows a native popup menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_APP_TRAY = 0x8000 + 1;
    private const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;
    private const uint WM_CONTEXTMENU = 0x7B;
    private const uint TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    public sealed record MenuItem(int Id, string Text, bool Checked = false, bool Separator = false);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private readonly WndProc _proc;   // kept alive for the lifetime of the window class
    private readonly uint _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly string _tooltip;
    private readonly Func<IReadOnlyList<MenuItem>> _menuProvider;
    private IntPtr _hwnd, _icon;
    private bool _added;

    public event Action? Activated;
    public event Action<int>? MenuCommand;

    public TrayIcon(string tooltip, Func<IReadOnlyList<MenuItem>> menuProvider)
    {
        _tooltip = tooltip;
        _menuProvider = menuProvider;
        _proc = HandleMessage;

        IntPtr inst = GetModuleHandleW(null);
        string cls = "PcMonitorTray_" + Environment.ProcessId;
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = inst, lpszClassName = cls };
        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0, cls, "PcMonitorTray", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, inst, IntPtr.Zero);

        string? exe = Environment.ProcessPath;
        if (exe != null && ExtractIconExW(exe, 0, out _, out var small, 1) > 0) _icon = small;
        AddIcon();
    }

    private NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, uFlags = flags,
        uCallbackMessage = WM_APP_TRAY, hIcon = _icon, szTip = _tooltip, szInfo = "", szInfoTitle = "",
    };

    private void AddIcon()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
        d.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref d);
    }

    public void SetTooltip(string text)
    {
        var d = Data(NIF_TIP);
        d.szTip = text.Length > 120 ? text[..120] : text;
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt == NIN_SELECT || evt == NIN_KEYSELECT) Activated?.Invoke();
            else if (evt == WM_CONTEXTMENU)
            {
                int x = (short)(wParam.ToInt64() & 0xFFFF);
                int y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                ShowMenu(x, y);
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            AddIcon(); // Explorer restarted
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            foreach (var item in _menuProvider())
            {
                if (item.Separator) AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                else AppendMenuW(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), (UIntPtr)(uint)item.Id, item.Text);
            }
            Win32.SetForegroundWindow(_hwnd);   // required so the menu closes when the user clicks elsewhere
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, x, y, _hwnd, IntPtr.Zero);
            Win32.PostMessageW(_hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0) MenuCommand?.Invoke(cmd);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref d);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
