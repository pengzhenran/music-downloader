using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MusicDownloader;

/// <summary>
/// 独立窗口应用主窗体：内嵌 WebView2 显示本地界面，
/// 复用系统自带的 WebView2 运行时（无需随包分发浏览器内核）。
/// </summary>
public sealed class MainForm : Form
{
    private readonly string _url;
    private readonly WebView2 _webView = new();

    public MainForm(string url)
    {
        _url = url;

        Text = "音乐下载器";
        Width = 1120;
        Height = 780;
        MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 17, 21);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* 图标可选 */ }

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);

        Load += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => Application.Exit();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // 用户数据放在本地应用数据目录，避免写到程序所在目录
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "音乐下载器", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;   // 关掉右键菜单，更像原生应用
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            // 站内导航留在窗口内；外部链接交给系统浏览器
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenExternal(e.Uri);
                }
            };

            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            ShowStartupError(ex);
        }
    }

    private static void OpenExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* 忽略 */ }
    }

    private void ShowStartupError(Exception ex)
    {
        var msg = "界面初始化失败。\n\n" +
                  "可能原因：系统缺少 WebView2 运行时（Windows 10/11 通常自带）。\n" +
                  "可前往 https://developer.microsoft.com/microsoft-edge/webview2/ 安装后重试。\n\n" +
                  $"错误详情：{ex.Message}";
        MessageBox.Show(msg, "音乐下载器", MessageBoxButtons.OK, MessageBoxIcon.Error);

        // 兜底：改用系统默认浏览器打开，保证功能可用
        OpenExternal(_url);
    }
}
