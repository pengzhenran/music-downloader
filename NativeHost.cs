using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace MusicDownloader;

/// <summary>
/// 极简 Win32 窗口宿主：用 P/Invoke 创建原生窗口与消息循环，
/// 并用 WebView2 的 Core 接口（不使用 WinForms/WPF）承载界面。
///
/// 这样做是为了能继续使用 <c>PublishTrimmed</c>——WinForms 在 .NET 8 上
/// 被 SDK 明令禁止裁剪（NETSDK1175），而纯 Win32 + Core COM 没有该限制。
/// </summary>
internal static class NativeHost
{
    private const string WindowClass = "MusicDownloaderWnd";
    private const string DefaultWindowTitle = "音乐下载器";

    private const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SW_SHOW = 5;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_EXECUTE_POSTED = 0x0400 + 1; // WM_APP + 1
    private const uint PM_REMOVE = 0x0001;
    private const uint MB_ICONERROR = 0x00000010;

    private const int DefaultWidth = 1120;
    private const int DefaultHeight = 780;

    private static IntPtr _hwnd;
    private static CoreWebView2Controller? _controller;
    private static string _windowTitle = DefaultWindowTitle;
    private static WndProcDelegate? _wndProcRef;   // 保持委托引用，防止被 GC 回收
    private static readonly Queue<(SendOrPostCallback Callback, object? State)> PostQueue = new();

    /// <summary>显示一个原生错误提示框（不依赖 WinForms）。</summary>
    public static void ShowError(string message)
        => MessageBoxW(IntPtr.Zero, message, _windowTitle, MB_ICONERROR);

    public static void Run(string url, string userDataDir, string? windowTitle = null)
    {
        _wndProcRef = WndProc;
        if (!string.IsNullOrWhiteSpace(windowTitle)) _windowTitle = windowTitle!;
        var hInstance = GetModuleHandleW(null);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcRef),
            hInstance = hInstance,
            lpszClassName = WindowClass,
        };
        TryApplyExeIcon(ref wc);
        RegisterClassExW(ref wc);

        _hwnd = CreateWindowExW(
            0, WindowClass, _windowTitle, WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, DefaultWidth, DefaultHeight,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建窗口失败");

        ShowWindow(_hwnd, SW_SHOW);
        UpdateWindow(_hwnd);

        // 让所有 await 的续体回到本线程（WebView2 的 COM 对象要求 STA 亲和）
        SynchronizationContext.SetSynchronizationContext(new Win32SyncContext());
        var initTask = InitializeWebViewAsync(url, userDataDir);
        PumpUntil(() => initTask.IsCompleted);

        if (initTask.IsFaulted)
        {
            MessageBoxW(IntPtr.Zero,
                "界面初始化失败。\n\n系统可能缺少 WebView2 运行时（Windows 10/11 通常自带）。\n" +
                "可前往 https://developer.microsoft.com/microsoft-edge/webview2/ 安装后重试。\n\n" +
                "错误：" + initTask.Exception?.GetBaseException().Message,
                _windowTitle, 0x10);
            OpenExternal(url); // 兜底：改用系统浏览器
        }

        // 主消息循环
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private static async Task InitializeWebViewAsync(string url, string userDataDir)
    {
        Directory.CreateDirectory(userDataDir);

        var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
        _controller = await env.CreateCoreWebView2ControllerAsync(_hwnd);

        var core = _controller.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        // 站内导航留在窗口内，外部链接交给系统浏览器
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(url, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };

        _controller.IsVisible = true;
        ResizeWebView();
        core.Navigate(url);
    }

    /// <summary>把 WebView2 的边界对齐到窗口客户区。</summary>
    private static void ResizeWebView()
    {
        if (_controller is null || _hwnd == IntPtr.Zero) return;
        if (!GetClientRect(_hwnd, out var rc)) return;
        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return;
        _controller.Bounds = new Rectangle(0, 0, w, h);
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                ResizeWebView();
                return IntPtr.Zero;

            case WM_EXECUTE_POSTED:
                DrainPostQueue();
                return IntPtr.Zero;

            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                try { _controller?.Close(); } catch { /* 关闭失败不阻塞退出 */ }
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ---------- 消息泵与续体调度 ----------

    private sealed class Win32SyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (PostQueue) PostQueue.Enqueue((d, state));
            if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_EXECUTE_POSTED, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void DrainPostQueue()
    {
        while (true)
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (PostQueue)
            {
                if (PostQueue.Count == 0) return;
                item = PostQueue.Dequeue();
            }
            try { item.Callback(item.State); }
            catch { /* 续体异常不影响消息循环 */ }
        }
    }

    /// <summary>在等待异步任务完成的同时继续泵消息（避免 STA 死锁）。</summary>
    private static void PumpUntil(Func<bool> done)
    {
        while (!done())
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(10);
        }
    }

    private static void OpenExternal(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* 忽略 */ }
    }

    /// <summary>从自身 exe 提取图标，让窗口与任务栏显示应用图标。</summary>
    private static void TryApplyExeIcon(ref WNDCLASSEXW wc)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            if (ExtractIconExW(exe, 0, out var large, out var small, 1) > 0)
            {
                wc.hIcon = large;
                wc.hIconSm = small;
            }
        }
        catch { /* 图标非必需 */ }
    }

    // ---------- P/Invoke ----------

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
