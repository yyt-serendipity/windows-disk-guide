# 磁盘清理助手

磁盘清理助手是一款中文 Windows 桌面工具。它解释 C、D 盘中的文件夹用途，将**目录总大小**和**当前可直接清理的大小**分开显示。打开程序时只扫描，点击「一键清理」才会永久删除清单中已勾选且通过复核的文件。

**下载**　到 [GitHub 发布页](https://github.com/yyt-serendipity/windows-disk-guide/releases/latest) 下载 `DiskGuide-1.2.0-windows.zip`，解压后双击 `磁盘清理助手.exe`。把同包的 `README.md` 和程序放在一起，可从窗口右下角「使用说明」离线阅读。

## 我的电脑能用吗？

| 环境 | 适用情况 |
|---|---|
| Windows 11 x64 桌面 | 已在 Windows 11 x64、系统内部版本 26100、.NET Framework 4.8.1、NTFS 上实际启动并验证。普通 Windows 账户即可使用。 |
| Windows 10 x64 桌面 | 设计上兼容，要求 .NET Framework 4.8 或 4.8.1。当前没有 Windows 10 实机验证记录。 |
| 只有 C 盘 | 可以使用。程序分别检测可用的本地固定 C、D 盘，缺少 D 盘时按 C 盘工作。 |
| 有 E、F 盘，或使用移动盘、网络盘 | 当前版本仅处理 Windows 标记为本地固定磁盘的 C、D。其他盘符、网络映射盘和标记为可移动的卷不进入清理范围。 |
| Windows ARM64、32 位 Windows、其他文件系统 | 尚未验证。程序以 AnyCPU 编译，仍依赖本机 WPF、Win32 文件 API 和文件系统返回的身份信息。 |
| macOS、Linux、无桌面的服务器 | 这个版本使用 Windows 图形界面和 Windows 专用文件接口，以上环境不属于运行目标。 |

目标运行时是 **.NET Framework 4.8**。仅安装新版 `.NET` 或 `dotnet` SDK 不能代替这个 Windows 组件。若启动失败，可在 PowerShell 执行下面的只读检查；`Release` 至少为 `528040` 表示已安装 4.8 或更高的 4.x 版本。[微软运行环境说明](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-10) · [微软版本检测说明](https://learn.microsoft.com/en-us/dotnet/framework/migration-guide/how-to-determine-which-versions-are-installed)

```powershell
(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release
```

## 第一次使用

1. 在顶部选择「全部磁盘」、C 盘或 D 盘。在「目录用途」中选中一个文件夹，右侧会显示用途、处理建议和大小；双击可进入下一级，`Ctrl+F` 搜索当前层。
2. **看清两个数字。**「目录总大小」是能读取到的文件长度总和。「可直接清理」只计入当前符合默认规则、应用状态允许清理的文件。出现 `≥` 表示统计跳过了链接或受限位置。主要空间占用列出最多三个子项。
3. 只想清一个文件夹时，点「查看清理项」。清单顶部始终显示范围。用「默认可清」「手动可选」「需退出应用」切换类别，标签旁数字是这一范围内的项目数。「本次已选」只统计当前类别里勾选的文件，点击「一键清理」也只处理这些文件。切回「全部项目」可看到先前的勾选；「解除限定」或切换磁盘可重置目录范围。
4. 结果栏显示实际删除的文件数、文件大小和跳过数。遇到「先退出应用」，先正常退出对应应用，再点「重新扫描」。

**文件会永久删除，不经过回收站。** 如果某个文件夹显示「需要判断」，可以查看子目录或在资源管理器中打开。软件没有提供将未知文件夹整夹加入删除清单的入口。

## 哪些内容会进入清单？

| 文件类别 | 默认选择 | 匹配和取舍 |
|---|---|---|
| 用户及 Windows 临时文件 | 符合条件时勾选 | 指定临时目录第一层中，最后修改已满 7 天的 `.tmp`、`.temp` 文件。子目录、普通文档和安装包保留。 |
| 浏览器和桌面应用的网页缓存 | 相关应用退出后勾选 | 支持指定布局的 Chrome、Edge、豆包、抖音、百度网盘缓存。只匹配缓存块、索引等已知文件名，不会对整个浏览器配置目录执行删除。 |
| Clash Verge 历史日志 | 符合条件时勾选 | 指定日志目录及其 `service` 子目录下，最后修改满 7 天的 `.log` 文件。订阅和代理配置保留。 |
| NVIDIA 着色器缓存 | 手动勾选 | 仅匹配指定 `DXCache` 目录和符合命名规则的 `.nvph` 文件。清理后游戏可能需要重新编译着色器。 |
| 应用崩溃转储 | 手动勾选 | 仅匹配用户 `CrashDumps` 第一层满 7 天的 `.dmp`。仍在排查闪退时应保留。 |

数据库、工程源码、EDA 历史、个人文档、驱动安装包和离线视频没有自动删除规则。不同电脑看到的清单会不同，取决于已安装的应用、目录布局、文件年龄、访问权限和正在运行的进程。文件夹名称含有 `Cache` 或 `Temp`，本身不会让它获得删除资格。

**已迁移到 D 盘的应用数据。** 对支持的 Roaming 应用，程序先验证当前账户的实际应用目录是否指向预期的 D 盘目标。验证通过才扫描目标里的指定缓存，删除前还会复核映射。即使另一台电脑上存在相同名称的备份目录，也不能仅凭路径相似进入清理清单。

## 具体怎样实现？

模块链接对应仓库中的源码。阅读离线压缩包时，可在 [GitHub 的 v1.2.0 源码目录](https://github.com/yyt-serendipity/windows-disk-guide/tree/v1.2.0/源码) 中查看同一版本。

| 模块 | 工作方式 |
|---|---|
| [窗口与交互](源码/Window.xaml) · [操作调度](源码/App.cs) | 使用 C# 和 WPF。界面负责磁盘选择、扫描快照、勾选状态和清理范围。文件枚举在后台执行，窗口仍可操作。 |
| [用途识别与规则](源码/Catalog.cs) | 从 Windows 获取当前用户的实际目录，结合系统目录名称、软件安装记录、文件结构和项目标志解释用途。解释与删除规则相互独立；来源不明会显示「需要判断」。 |
| [目录浏览](源码/Browser.cs) · [空间统计](源码/Usage.cs) | 只递归统计选中的目录，切换目录时可取消。遍历时同时累计总量和主要子项，在本次会话内复用结果。遇到目录链接或访问拒绝会跳过。 |
| [扫描与删除](源码/Cleaner.cs) | 扫描按具体路径、文件名、层级、修改时间及相关进程筛选，并记录文件身份、长度和修改时间。点击清理后，再逐个复核。 |
| [回归测试](源码/Check.cs) | 在专用临时目录造出缓存、个人文件、占用文件、硬链接、目录联接和被替换的文件，检验哪些应删、哪些应留下，并覆盖文件夹限定清理。 |

删除过程可以用一个例子理解。假设扫描时找到一个旧的 `example.tmp`，用户随后用同名文件替换了它。点击清理时，程序会检查路径各级目录，尝试独占打开文件，核对卷号、文件标识、长度、修改时间、链接数和句柄对应的最终路径。任一检查失败就跳过。复核通过后，通过**同一个已验证的句柄**提交删除请求，避免按路径重新打开时误删另一个文件。[Windows 最终路径接口](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew) · [Windows 文件信息接口](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle)

工具在本机工作，扫描无需登录，目录清单不会上传到 GitHub。普通权限无法读取的位置保持「访问受限」。目录统计使用逻辑文件长度，硬链接可能按多个名字计数，因此删除报告里的文件大小与磁盘可用空间增量可能不同。

例如，一个文件夹中有 2 GiB 数据，而当前只匹配 100 MiB 网页缓存。「目录总大小」显示已读到的约 2 GiB，「可直接清理」显示 100 MiB。即使进入该目录的清单并执行一键清理，其余约 1.9 GiB 仍保留。若应用重新打开、某个文件被占用，实际删掉的还会更少。

## 从源码构建、测试和打包

在具备桌面组件、.NET Framework 4.8/4.8.1 和 PowerShell 的 64 位 Windows 上，打开仓库根目录。此项目使用 Windows 自带的 .NET Framework C# 编译器构建，仓库目录可自行命名。

```powershell
# 构建并运行回归测试，生成根目录的“磁盘清理助手.exe”
powershell -NoProfile -ExecutionPolicy Bypass -File ".\源码\Build.ps1" -Check

# 生成经过检查的便携发布包
powershell -NoProfile -ExecutionPolicy Bypass -File ".\Release.ps1"
```

`-ExecutionPolicy Bypass` 只应用于这次启动的 PowerShell 进程。发布产物在 `dist` 中。可将下载文件的 SHA-256 与同一发布页的 `SHA256SUMS.txt` 对照，检查传输完整性。

```powershell
Get-FileHash ".\DiskGuide-1.2.0-windows.zip" -Algorithm SHA256
```

发布包使用普通 Windows 桌面可执行文件，目前没有 Authenticode 代码签名。请从本仓库的 Releases 下载并核对校验值。维护者向 main 推送或提交拉取请求时，GitHub Actions 会在 Windows runner 上执行构建、测试和打包。CI 通过只说明该环境中的检查通过，其他 Windows 版本仍需分别验证。
