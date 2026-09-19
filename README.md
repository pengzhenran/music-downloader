# 🎵 音乐下载器

一个**单文件、免安装**的网易云音乐下载工具，自带独立窗口界面。

- **形态**：独立窗口应用（Win32 原生窗口 + 内嵌 WebView2）
- **体积**：约 **11 MB**（自包含运行时，双击即用）
- **平台**：Windows 10/11 x64
- **依赖**：无（WebView2 运行时 Windows 10/11 自带）

---

## 快速开始

1. 双击 `音乐下载器.exe` → 弹出独立窗口
2. 点右上角 **设置** → 粘贴你的网易云 **Cookie**（下载无损需要 VIP）
3. 搜索歌曲 → 勾选 → **下载选中**

---

## 功能

| 功能 | 说明 |
|---|---|
| **搜索** | 按歌名 / 歌手 / 专辑关键词搜索 |
| **专辑批量** | 点击结果中的**专辑名**展开整张专辑，一键全选下载 |
| **账号检测** | 真实请求接口校验 Cookie：显示昵称、VIP 状态；伪造/过期 Cookie 会被识别为无效 |
| **音质可调** | 无损优先（无无损自动降级 MP3）／仅无损／MP3 320k／MP3 192k |
| **歌词** | 自动保存同名 `.lrc` |
| **封面** | 自动下载专辑封面（800×800），并可内嵌到音频标签 |
| **标签** | FLAC 写 Vorbis Comment，MP3 写 ID3v2.3；含 标题/艺术家/专辑/专辑艺术家/曲序/年份 |
| **选择文件夹** | 设置里点「浏览…」调用 Windows 原生文件夹选择器 |
| **关于面板** | 显示版本、运行环境、账号状态、下载目录与免责声明 |
| **命名规则** | 可选 `01. 歌名`（按曲序）或 `歌手 - 歌名` |
| **抗限流** | 请求失败自动重试（4 次递增退避）+ 曲目间间隔 |

---

## 如何获取 Cookie

1. 浏览器登录 <https://music.163.com>
2. 按 `F12` → **Network** 标签
3. 刷新页面，点任意一个 `music.163.com` 请求
4. 在 **Request Headers** 中找到 `Cookie:`，整段复制
5. 粘贴到「设置 → 网易云 Cookie」并保存

> Cookie 仅保存在本机 `%APPDATA%\音乐下载器\config.json`，不会上传到任何地方。

---

## 🔐 制作「内置账号」分发给朋友

如果你想让朋友**开箱即用**（不需要自己配 Cookie），可以用脚本把登录态编进 exe：

```powershell
# 从文件读取 cookie 并构建
.\build-friend.ps1 -CookieFile "D:\path\to\cookie.txt"

# 或直接传入
.\build-friend.ps1 -Cookie "MUSIC_U=xxxx; __csrf=xxxx; ..."
```

脚本会：读取 cookie → XOR+Base64 混淆 → 生成被 `.gitignore` 排除的 `Secrets.Local.cs` → 编译 → **删除该文件**。

产物在 `dist-friend\音乐下载器.exe`，朋友双击即可用自己的账号下载。

### 📤 分享时只需要发**一个 exe**

原生库（`WebView2Loader.dll`、`D3DCompiler_47_cor3.dll` 等）已通过
`IncludeNativeLibrariesForSelfExtract` 内嵌进 exe，因此**不需要附带任何其他文件**。

> 实测：把 exe 单独拷到空目录运行，WebView2 正常初始化（数据目录 173 个文件）；
> 开启此选项前，单独运行会因缺少原生加载器而导致界面空白。

### ⚠️ 分发前请务必了解风险

| 事项 | 说明 |
|---|---|
| **凭证可被提取** | XOR+Base64 只是提高门槛，**不是加密**。能运行 exe 的人理论上都能还原出 Cookie |
| **建议用小号** | 请不要用主账号制作分发版；或专为此建一个网易云小号 |
| **可随时失效** | 若怀疑外泄，去网易云「设置 → 账号安全 → 退出所有设备」即可让该 Cookie 失效 |
| **仅私下分享** | 不要上传到公开网盘、群文件或任何公开位置 |

> 💡 更安全的替代：直接分发**不带 Cookie** 的普通版，让朋友用自己的账号（在设置里填一次即可）。

---

## 常见问题

**Q：提示"无法获取播放地址"？**
- 未配置 Cookie，或该曲目本身没有无损/受版权地区限制

**Q：提示"服务端返回空响应（可能被限流）"？**
- 已内置自动重试（4 次）。若仍失败，稍后再试或减少一次下载数量

**Q：窗口打不开 / 提示 WebView2 错误？**
- 会提示安装 WebView2 运行时；作为兜底，程序会自动改用系统浏览器打开界面

**Q：下载目录在哪？**
- 默认 `桌面\音乐下载`，可在设置中修改；每张专辑自动建独立子目录

**Q：能下载 MP3 吗？**
- 可以，设置里「音质偏好」四档可选；MP3 同样写入 ID3v2.3 标签与内嵌封面

---

## 技术说明

- **.NET 8** + **Win32 原生窗口**（P/Invoke 建窗与消息循环）+ **WebView2 Core 接口**
  - 刻意**不使用 WinForms**：.NET 8 禁止对 WinForms 裁剪（`NETSDK1175`），改用原生窗口后
    得以继续 `PublishTrimmed`，体积从 59 MB 降到 **11 MB**
- 后端是本地 `HttpListener` 服务，前端为内嵌的 HTML/JS 界面
- **零第三方依赖**（仅 WebView2 SDK）：HTTP 用 `SocketsHttpHandler`，JSON 手工构建
- **FLAC 标签**：自行实现 Vorbis Comment 与 PICTURE 块读写，并解析 JPEG/PNG 尺寸
- **MP3 标签**：自行实现 ID3v2.3 帧写入（文本用 UTF-16+BOM，APIC 内嵌封面）
- **文件夹选择**：`SHBrowseForFolderW`，**刻意避开结构体编组**——改为 `AllocHGlobal` 手工按内存布局写字段，否则裁剪后会抛 `TypeLoadException`
- **单文件分发**：`IncludeNativeLibrariesForSelfExtract` 把 `WebView2Loader.dll` 等原生库
  一并嵌入 exe，否则单独运行会因缺少加载器导致界面空白
- **weapi 协议**：AES-128-CBC 双重加密 + 裸 RSA（`BigInteger.ModPow`），与网易云客户端一致
- **异步初始化**：自建 `SynchronizationContext` 把 await 续体投递到窗口消息队列，
  保证 WebView2 的 COM 调用始终在创建它的 STA 线程上执行

## 从源码构建

```powershell
dotnet publish -c Release -o dist
# 产物：dist\音乐下载器.exe
```

---

## 免责声明

本工具仅供**个人学习与备份已购/已授权内容**使用。请遵守网易云音乐用户协议与相关著作权法律，勿用于传播或商业用途。因分发内置账号版本导致的账号风险，由使用者自行承担。
