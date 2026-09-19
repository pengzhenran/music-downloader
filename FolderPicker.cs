using System.Runtime.InteropServices;
using System.Text;

namespace MusicDownloader;

/// <summary>
/// 调用 Windows 原生「选择文件夹」对话框（SHBrowseForFolder）。
///
/// 这里刻意<b>不</b>使用带结构体编组的 P/Invoke：PublishTrimmed 会移除
/// 结构体的编组元数据，导致运行时 TypeLoadException。改为用
/// Marshal.AllocHGlobal 手工按 BROWSEINFOW 的内存布局写入字段，
/// 只涉及 IntPtr/int 的写入，因此在裁剪后依然稳定。
/// 对话框使用 BIF_NEWDIALOGSTYLE，具备现代样式（地址栏、可调整大小）。
/// </summary>
public static class FolderPicker
{
    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_EDITBOX = 0x0010;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;

    // BROWSEINFOW 在 x64 下的字段偏移
    private const int OffHwndOwner = 0;
    private const int OffPidlRoot = 8;
    private const int OffDisplayName = 16;
    private const int OffTitle = 24;
    private const int OffFlags = 32;
    private const int OffCallback = 40;
    private const int OffLParam = 48;
    private const int OffImage = 56;
    private const int StructSizeX64 = 64;
    private const int StructSizeX86 = 40;

    /// <summary>弹出选择对话框；用户取消时返回 null。</summary>
    public static string? Pick(string title, string? initialDir)
    {
        string? result = null;
        Exception? error = null;

        // shell 对话框要求 STA 线程
        var thread = new Thread(() =>
        {
            try { result = ShowDialog(title); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (error is not null) throw error;
        return result;
    }

    private static string? ShowDialog(string title)
    {
        var size = IntPtr.Size == 8 ? StructSizeX64 : StructSizeX86;
        var buf = Marshal.AllocHGlobal(size);
        var titlePtr = Marshal.StringToHGlobalUni(title);
        var displayBuf = Marshal.AllocHGlobal(520); // 260 个 wchar

        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteIntPtr(buf, OffHwndOwner, GetForegroundWindow());
            Marshal.WriteIntPtr(buf, OffPidlRoot, IntPtr.Zero);
            Marshal.WriteIntPtr(buf, OffDisplayName, displayBuf);
            Marshal.WriteIntPtr(buf, OffTitle, titlePtr);
            Marshal.WriteInt32(buf, OffFlags, unchecked((int)(BIF_RETURNONLYFSDIRS | BIF_EDITBOX | BIF_NEWDIALOGSTYLE)));
            Marshal.WriteIntPtr(buf, OffCallback, IntPtr.Zero);
            Marshal.WriteIntPtr(buf, OffLParam, IntPtr.Zero);
            Marshal.WriteInt32(buf, OffImage, 0);

            var pidl = SHBrowseForFolderW(buf);
            if (pidl == IntPtr.Zero) return null; // 用户取消

            try
            {
                var path = new StringBuilder(260);
                if (!SHGetPathFromIDListW(pidl, path)) return null;
                var picked = path.ToString();
                return string.IsNullOrWhiteSpace(picked) ? null : picked;
            }
            finally
            {
                CoTaskMemFree(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(titlePtr);
            Marshal.FreeHGlobal(displayBuf);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHBrowseForFolderW")]
    private static extern IntPtr SHBrowseForFolderW(IntPtr lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetPathFromIDListW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
