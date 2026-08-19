using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace FolderCrypto.App.Services;

/// <summary>
/// 系统托盘图标服务（Win32 Shell_NotifyIcon + 隐藏消息窗口）。
/// WinUI 3 未内置托盘控件，这里用原生消息窗口承载托盘回调，
/// 提供“打开设置/退出程序”等菜单项。
/// </summary>
public static class TrayIconService
{
    private const string WindowClassName = "FolderCrypto_TrayHost";
    private const int WM_TRAYICON = 0x0400 + 1;   // WM_APP + 1
    private const int WM_COMMAND = 0x0111;
    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x0001;
    private const int NIF_ICON = 0x0002;
    private const int NIF_TIP = 0x0004;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;

    private const int CMD_OPEN = 1;
    private const int CMD_EXIT = 2;

    private static IntPtr _hwnd = IntPtr.Zero;
    private static IntPtr _icon = IntPtr.Zero;
    private static bool _visible;
    private static DispatcherQueue? _dispatcher;
    private static Action? _onOpenSettings;
    private static Action? _onExit;

    private static IntPtr _hMenu = IntPtr.Zero;

    // 托盘图标提示文字与「任务栏重建」广播消息（Explorer 重启后恢复图标用）。
    private static string _tooltip = "";
    private static uint _taskbarCreatedMsg;

    // 开机自启时托盘区未就绪的自动重试（60 × 500ms ≈ 30 秒）。
    private const int MaxTrayAddRetries = 60;
    private static DispatcherQueueTimer? _retryTimer;
    private static int _retryCount;

    /// <summary>显示托盘图标。在主 UI 线程调用。</summary>
    public static void Show(DispatcherQueue dispatcher, string tooltip, string iconPath, Action onOpenSettings, Action onExit)
    {
        if (_visible) return;
        _dispatcher = dispatcher;
        _onOpenSettings = onOpenSettings;
        _onExit = onExit;
        _tooltip = tooltip;

        _hwnd = CreateMessageWindow();
        if (_hwnd == IntPtr.Zero) return;

        // 注册「任务栏重建」系统广播消息，Explorer 重启后托盘区重建时能自动重新添加图标。
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        // 加载托盘图标（32x32）
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 32, 32, 0x0010 /*LR_LOADFROMFILE*/);
        if (_icon == IntPtr.Zero)
        {
            _icon = LoadIcon(IntPtr.Zero, (IntPtr)32512 /*IDI_APPLICATION*/);
        }

        _visible = _hwnd != IntPtr.Zero;

        // 添加托盘图标；开机自启时托盘区可能尚未就绪，NIM_ADD 失败会自动重试。
        TryAddIcon();
    }

    /// <summary>
    /// 添加/重新添加托盘图标。若 Shell_NotifyIcon(NIM_ADD) 失败（常见于开机自启时
    /// Explorer 的托盘区尚未初始化完成，此时图标会静默丢失），则自动定时重试，
    /// 直到成功或达到最大重试次数。
    /// </summary>
    private static void TryAddIcon()
    {
        if (!_visible || _hwnd == IntPtr.Zero) return;

        var nid = BuildNid();
        if (Shell_NotifyIcon(NIM_ADD, ref nid))
        {
            StopRetry();
            return;
        }

        // 添加失败：启动重试计时器（仅在首次失败时创建）
        if (_retryTimer == null && _dispatcher != null)
        {
            _retryCount = 0;
            _retryTimer = _dispatcher.CreateTimer();
            _retryTimer.Interval = TimeSpan.FromMilliseconds(500);
            _retryTimer.IsRepeating = true;
            _retryTimer.Tick += (s, e) =>
            {
                if (!_visible || ++_retryCount > MaxTrayAddRetries)
                {
                    StopRetry();
                    return;
                }
                var retryNid = BuildNid();
                if (Shell_NotifyIcon(NIM_ADD, ref retryNid))
                {
                    StopRetry();
                }
            };
            _retryTimer.Start();
        }
    }

    private static void StopRetry()
    {
        if (_retryTimer != null)
        {
            _retryTimer.Stop();
            _retryTimer = null;
        }
    }

    /// <summary>基于当前状态构造全新的 NOTIFYICONDATA（每次调用返回新实例，避免共享结构体状态）。</summary>
    private static NOTIFYICONDATA BuildNid()
    {
        var nid = new NOTIFYICONDATA();
        nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
        nid.hWnd = _hwnd;
        nid.uID = 1;
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = _icon;
        nid.szTip = new char[128];
        _tooltip.CopyTo(0, nid.szTip, 0, Math.Min(_tooltip.Length, nid.szTip.Length - 1));
        return nid;
    }

    /// <summary>隐藏并移除托盘图标。</summary>
    public static void Hide()
    {
        if (!_visible) return;
        var nid = new NOTIFYICONDATA();
        nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
        nid.hWnd = _hwnd;
        nid.uID = 1;
        _ = Shell_NotifyIcon(NIM_DELETE, ref nid);
        _visible = false;
        StopRetry();

        if (_hMenu != IntPtr.Zero) { DestroyMenu(_hMenu); _hMenu = IntPtr.Zero; }
        if (_icon != IntPtr.Zero && _icon != LoadIcon(IntPtr.Zero, (IntPtr)32512)) { /* 由系统在 destroy 时清理，见下 */ }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    private static void WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            int evt = (short)(lParam.ToInt64() & 0xFFFF);
            if (evt == WM_LBUTTONUP || evt == WM_LBUTTONDBLCLK)
            {
                // 左键：打开设置
                _dispatcher?.TryEnqueue(() => _onOpenSettings?.Invoke());
            }
            else if (evt == WM_RBUTTONUP)
            {
                ShowContextMenu(hwnd);
            }
        }
        else if (msg == WM_COMMAND)
        {
            int id = (short)(wParam.ToInt64() & 0xFFFF);
            DispatchCommand(id);
        }
        else if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            // Explorer 重启后托盘区重建：重新添加图标。
            TryAddIcon();
        }
        else if (msg == 0x0010 /*WM_CLOSE*/)
        {
            DestroyWindow(hwnd);
        }
    }

    private static void ShowContextMenu(IntPtr hwnd)
    {
        try
        {
            if (_hMenu != IntPtr.Zero) { DestroyMenu(_hMenu); _hMenu = IntPtr.Zero; }
            _hMenu = CreatePopupMenu();
            _ = AppendMenu(_hMenu, 0 /*MF_STRING*/, (UIntPtr)CMD_OPEN, "打开设置");
            _ = AppendMenu(_hMenu, 0, (UIntPtr)CMD_EXIT, "退出程序");

            // 标记当前前台窗口，保证菜单能被 SendMessage 直接回调（TrackPopupMenu 的
            // TPM_RETURNCMD 模式会同步返回所选命令 id，无需 WM_COMMAND 消息循环）。
            _ = SetForegroundWindow(hwnd);
            POINT pt;
            _ = GetCursorPos(out pt);
            uint cmd = TrackPopupMenu(
                _hMenu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_LEFTALIGN | TPM_BOTTOMALIGN,
                pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
            _ = PostMessage(hwnd, 0/*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);
            if (cmd != 0) DispatchCommand((int)cmd);
        }
        catch { }
    }

    private static void DispatchCommand(int id)
    {
        var d = _dispatcher;
        if (d == null) return;
        switch (id)
        {
            case CMD_OPEN: d.TryEnqueue(() => _onOpenSettings?.Invoke()); break;
            case CMD_EXIT: d.TryEnqueue(() => _onExit?.Invoke()); break;
        }
    }

    private static IntPtr CreateMessageWindow()
    {
        // 注册一个仅用于接收消息的隐藏窗口类
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcCallback),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClassName,
        };
        if (RegisterClass(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /*ERROR_CLASS_ALREADY_EXISTS*/)
        {
            return IntPtr.Zero;
        }
        return CreateWindowEx(
            0, WindowClassName, "FolderCryptoTray", 0,
            int.MinValue, int.MinValue, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProcDelegate WndProcCallback = (h, m, w, l) =>
    {
        // WM_NCCREATE 必须返回 TRUE(1)，否则 CreateWindowEx 会直接失败返回 NULL，
        // 导致消息窗口建不出来、Shell_NotifyIcon 永远加不上托盘图标。
        if (m == 0x0081 /*WM_NCCREATE*/) return new IntPtr(1);
        WndProc(h, m, w, l);
        return IntPtr.Zero;
    };

    // ---------- Win32 定义 ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public char[] szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public char[] szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public char[] szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern uint RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
