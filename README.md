# Folder Crypto 文件夹加密（WinUI 3）

一个小型的 Windows 11 文件夹/文件加密软件，符合 WinUI 设计规范，并与资源管理器深度集成。

## 下载安装（普通用户）

请前往 **[Releases](https://github.com/wangshanghai240/FolderCrypto/releases)** 页面下载最新版本。

需要下载两个文件：
- `FolderCrypto.App_<版本>_x64.msix` —— 安装包
- `FolderCrypto.cer` —— 签名证书

> ⚠️ **MSIX 采用自签名证书签名，首次安装前须先信任证书**，否则会报签名错误。请按下方步骤操作（管理员 PowerShell）：

```powershell
# 1) 信任签名证书（只需一次）
Import-Certificate -FilePath .\FolderCrypto.cer -CertStoreLocation Cert:\LocalMachine\Root
# 2) 安装
Add-AppxPackage -Path .\FolderCrypto.App_<版本>_x64.msix
```

完整教程见 **[RELEASE.md](RELEASE.md)**。

> 源码开发者请跳过本节，直接看下方 [构建](#构建) 与 [安装 Shell 集成](#安装-shell-集成右键--关联--锁图标)。

## 功能

- **原地加密（不再使用容器）**：在资源管理器中右键任意文件，选择“加密”即可就地加密该文件；右键文件夹“加密”则锁定该文件夹（不加密内部内容），文件可单独逐个加密。
- **右键加密/解密**：为文件/文件夹注册右键“加密”“解密”菜单项，点击弹出密码框即可操作。
- **锁图标叠加**：已加密的文件（FCENC000 头）或已锁定文件夹（.folderlock 标记）在资源管理器中显示右下角小锁图标（原生 C++ ATL 覆盖层实现）。
- **强密码校验**：密码必须**超过 6 位**，且同时包含**数字、字母和特殊字符**，不满足则不允许继续。
- **三次限制**：解密时密码连续输错 **3 次**会自动关闭密码输入框。
- **浅色/深色模式**：全部界面自动跟随系统浅色/深色主题。
- **Win11 风格 UI**：圆角卡片、圆角对话框、现代控件。

## 项目结构

```
FolderCrypto.sln
├─ FolderCrypto.Core/          加密核心类库（无 UI 依赖，可单元测试）
│  ├─ Security/PasswordPolicy.cs       密码强度校验
│  ├─ Security/PasswordHasher.cs       PBKDF2 密钥派生 + 验证器
│  ├─ Encryption/AesGcmEncryption.cs   AES-256-GCM 对称加密
│  └─ Services/ContainerService.cs     容器实现（含原地加密：InPlaceEncryptionService）
├─ FolderCrypto.Core.Tests/     xUnit 单元测试（密码策略、加解密往返、错误密码）
├─ FolderCrypto.App/            WinUI 3 主应用（Windows App SDK）
│  ├─ App.xaml(.cs)             应用入口 + 单实例 + 命令行/参数处理 + 系统主题跟随
│  ├─ MainWindow.xaml(.cs)      主窗口（Win11 圆角卡片布局）
│  ├─ Dialogs/                  设置密码对话框、输入密码对话框（圆角、主题自适应）
│  └─ Services/                 指令调度、对话框、Pickers 辅助
└─ FolderCrypto.Shell/          Shell 集成（注册表 + 安装/卸载工具）
   ├─ ContextMenuRegistrar.cs          右键菜单注册/卸载
   ├─ ContainerAssociationRegistrar.cs .fenc 文件关联（双击）
   ├─ OverlayRegistrar.cs              锁图标覆盖层注册（调用原生 DLL）
   └─ Program.cs                       install / uninstall 命令
└─ FolderCrypto.ShellNative/    原生 C++ ATL DLL（锁图标覆盖层，取代托管 COM 实现）
   ├─ src/dllmain.cpp                  ATL 服务器 + IShellIconOverlayIdentifier
   ├─ src/OverlayHandler.rgs           COM 注册脚本
   ├─ FolderCryptoShellNative.idl      类型库
   └─ FolderCrypto.ShellNative.vcxproj MSVC 工程
```

## 技术要点

- **加密算法**：AES-256-GCM（认证加密）。密钥由密码经 **PBKDF2-SHA256**（200,000 次迭代、随机盐）派生。
- **容器格式**：`magic + version + salt + verifier + metaLength + 加密元数据(JSON清单) + 加密负载`。元数据与负载使用 **HKDF** 分离的密钥。
- **密钥/验证分离**：verifier = SHA256(派生主密钥)，用于比对密码是否正确（固定时间比较防时序攻击）。
- **单实例**：命名互斥锁 + 命名管道，把 Shell 传入的指令转发给已在运行的实例。
- **密码绝不落盘**：只存储随机盐与验证哈希，无明文。

## 环境要求（构建）

- Windows 10/11
- **.NET SDK 8.0**（已安装 8.0.424）
- **Visual Studio 2022 Build Tools**（含“使用 C++ 的桌面开发” + **ATL** 组件）—— 用于编译原生 `FolderCrypto.ShellNative.dll`
- Visual Studio 2022（可选，含“Windows 应用 SDK / WinUI”工作负载）或 `dotnet` CLI
- Windows App SDK 1.6（通过 NuGet 自动还原）

> ⚠️ 本机已安装 .NET SDK；但**尚无 C++ 编译工具链**。编译原生覆盖层 DLL 需先安装：
> ```bash
> winget install Microsoft.VisualStudio.2022.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
> # 然后通过 Visual Studio Installer 勾选“适用于最新 v143 生成工具的 C++ ATL”组件
> ```

## 构建

```bash
# 核心库 + Shell 集成工具 + 单元测试（Any CPU 即可）
dotnet build FolderCrypto.Core -c Release
dotnet build FolderCrypto.Shell -c Release
dotnet test FolderCrypto.Core.Tests

# WinUI 3 主应用（MSIX 打包应用不能为 AnyCPU，需指定平台/RID）
dotnet build FolderCrypto.App -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64

# 或完整还原整个解决方案
dotnet restore FolderCrypto.sln

# 构建原生 C++ ATL DLL（需 MSVC + ATL，用 MSBuild 或 Visual Studio 打开 FolderCrypto.ShellNative.vcxproj）
# 产出：<SolutionDir>\x64\Release\FolderCrypto.ShellNative.dll（连同 overlay-lock.ico 一起复制到输出目录）
```

> 已验证（.NET SDK 8.0.424 + VS Build Tools 17.14 / MSVC 14.44 / ATL）：核心库 17/17 单测通过；Core/Shell/App(x64) 均 0 错误构建；**原生 `FolderCrypto.ShellNative.dll` 已成功编译**（`x64\Release\`，含 DllGetClassObject/DllRegisterServer/DllUnregisterServer 等导出）。

## 安装 Shell 集成（右键 / 关联 / 锁图标）

```bash
# 使用主应用 exe 的完整路径；默认在解决方案构建输出中定位原生 DLL，
# 也可用 --dll 显式指定。(建议以管理员权限运行以写入 HKLM 的 ShellIconOverlayIdentifiers)
FolderCrypto.Shell install "C:\path\to\FolderCrypto.App.exe" --dll "C:\path\to\FolderCrypto.ShellNative.dll"
# 卸载
FolderCrypto.Shell uninstall --dll "C:\path\to\FolderCrypto.ShellNative.dll"
```

## 手动验证清单

1. `dotnet test FolderCrypto.Core.Tests`：密码策略（>6 位、含数字/字母/特殊字符）、AES 加解密往返、错误密码拒绝、内容非明文。
2. 打包/运行主应用：选择文件/文件夹加密 → 设置强密码 → 生成 `.fenc`。
3. 安装 Shell 集成后：资源管理器右键出现“加密/解密”；`.fenc` 右下角显示锁图标（可能需重启资源管理器或重新登录）。
4. 双击 `.fenc` → 弹出 WinUI 密码框 → 连续输错 3 次自动关闭。

## 已知限制与说明

- **锁图标覆盖层** 采用**原生 C++ ATL DLL**（`FolderCrypto.ShellNative.dll`），由 Explorer 直接进程内加载，稳定可靠。其安装需要在 HKLM 的 `ShellIconOverlayIdentifiers` 登记覆盖层，需**管理员权限**；Windows 限制叠加层总数最多 15 个。
- 右键菜单与文件关联写 HKCU，无需管理员权限。
- 原生 DLL 的图标文件 `overlay-lock.ico` 需与 DLL 放在同一目录（`GetOverlayInfo` 从 DLL 所在目录定位该图标）。
- 容器化后资源管理器无法直接预览内容（默认不实现缩略图处理器），如需可后续扩展。
- 若要卸载旧的托管 COM 覆盖层注册，残留的 CLSID `{F8A2C000-...}` 由新 `OverlayRegistrar.UninstallOverlay` 一并清理。
