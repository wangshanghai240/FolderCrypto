# 发布说明 & 用户安装指南

本文件说明如何把 FolderCrypto（文件夹加密）发布到 GitHub，供其他用户下载安装，以及用户安装 MSIX 的正确步骤。

---

## 一、发布到 GitHub 的两种内容

发布时**分两条通道**：

| 内容 | 方式 | 说明 |
| ---- | ---- | ---- |
| **源代码** | Git 提交并 push | 仓库 `wangshanghai240/FolderCrypto`，供开发者查看/复现，不含私钥 |
| **安装包 + 证书** | GitHub **Release 附件** | 供普通用户直接下载安装，**不进入 git** |

> ⚠️ **安全警告**：`packages\FolderCrypto.pfx` 包含**签名私钥，绝对不能上传/提交**到任何公开位置（包括 GitHub Release、git 仓库）。一旦泄露，他人可用你的身份签名恶意安装包。
>
> 目前 `.gitignore` 已忽略 `packages/`，`FolderCrypto.pfx` 未被 git 跟踪，仓库是安全的。

---

## 二、发布流程（打新版本 Release）

### 1. 生成 / 确认安装包与证书

> **发布策略（方案一：主推 MSI）**：本次只发布 **MSI**（含右键菜单 + 锁图标）+ 证书。MSIX 暂不随本次发布——因现有 MSIX 用**旧证书**签名，而本机证书已更换为新自签名证书，新旧证书并存会造成混淆。若后续需要 MSIX，请单独用**新证书重打包重签**（见下文"MSIX 重签说明"）。

构建产物位于 `packages\`：

- `packages\FolderCrypto-Setup-<版本>-x64.msi` —— **MSI 安装包（推荐，已内置右键菜单 + 锁图标，已签名）**
- `packages\FolderCrypto.cer` —— 自签名签名证书（用户首次安装时信任用）

- **MSI**：由 `wix\build-msi.ps1` 构建（trims App → 生成 `FolderCrypto.wxs` → `wix build` → 签名）。生成器 `wix\gen-wxs.ps1` 会注入 `ShellIntegration` 组件（右键菜单注册 + 锁图标覆盖层），安装即含右键，卸载自动清理。用法：
  ```powershell
  $env:FOLDCRYPTO_PFX_PASS = '你的pfx密码'   # 从 new-cert.ps1 生成时设定
  powershell -ExecutionPolicy Bypass -File wix\build-msi.ps1
  ```
- **证书**：`wix\new-cert.ps1` 生成新的自签名代码签名证书并导出 pfx/cer（密码从 `FOLDCRYPTO_PFX_PASS` 读取）。`build-msi.ps1` 会自动从 pfx 读取指纹，无需手动改 `$thumb`。

### 2. 打 git tag 并推送源码

```powershell
cd F:\VScode\文件夹加密
# 先提交源码改动（如果有）
git add -A
git commit -m "v1.0.14 发布: ..."
# 打 tag
git tag v1.0.14
git push origin main --tags
```

> 源码 push 提示：`.gitignore` 已忽略 `packages/`、`bin/`、`obj/`、`x64/`、`preview/` 等，不会把私钥/安装包提交进去。

### 3. 上传安装包到 GitHub Release

在 GitHub 网页操作：

1. 打开仓库 `https://github.com/wangshanghai240/FolderCrypto`
2. 右侧 **Releases** → **Draft a new release**
3. 选择/输入 tag `v1.0.14`
4. 标题：`FolderCrypto 文件夹加密 v1.0.14`
5. 正文：粘贴下文"发布模板"，可补充更新日志
6. **附件（Attach binaries）**上传以下文件：
   - `packages\FolderCrypto-Setup-<版本>-x64.msi`（如 `FolderCrypto-Setup-1.0.14-x64.msi`）
   - `packages\FolderCrypto.cer`（新证书；用户首次安装需信任）
7. 点击 **Publish release**

> 普通用户**只下载 MSI**（双击即装、自带右键/锁图标、无需手动信任证书）；`FolderCrypto.cer` 为自签名证书，一般仅在 Windows 提示“未知发布者”而用户选择信任时才需要。

---

## 三、发布模板（Release 正文）

```markdown
# FolderCrypto 文件夹加密 v1.0.14

Windows 11 文件夹/文件加密工具（WinUI 3）。支持资源管理器右键加密/解密、锁图标叠加。

## 下载安装

下载 `FolderCrypto-Setup-1.0.14-x64.msi`，**双击即可安装**（首次会弹出 UAC 确认）。安装后无需任何额外配置，右键菜单与锁图标自动生效。

## 更新日志
（在此填写）

## 系统要求
- Windows 10/11（x64）
```

---

## 四、用户安装 MSI 的正确方法

> **推荐方式。** MSI 已内置右键菜单 + 锁图标，且已代码签名，**直接双击** `FolderCrypto-Setup-<版本>-x64.msi` 即可安装（需管理员确认 UAC）。安装后：
>
> - 开始菜单 / 桌面出现 **FolderCrypto** 快捷方式
> - 资源管理器右键任意文件/文件夹出现 **加密/解密**（对所有用户生效）
> - 已加密文件/锁定文件夹显示**锁图标**（可能需要重启资源管理器或重新登录后出现）
>
> 卸载：控制面板 → 程序和功能 → 找到 FolderCrypto 卸载即可（右键菜单与锁图标会被自动清理）。

---

## 五、用户安装 MSIX 的正确方法（备选 / 暂不随本次发布）

> ⚠️ **说明**：当前仓库中的 MSIX 是用**旧证书** `E25B41DD...` 签名的，而本机签名证书已更换为新证书。为避免新旧证书并存造成混淆，**本次 Release 默认只发布 MSI**，不随附 MSIX。下述 MSIX 安装步骤仅作参考；如需发布 MSIX，请先按"MSIX 重签说明"用新证书重新打包签名。
>
> **MSIX 是用自签名证书签名的。** 第一次安装前，必须先在电脑上**信任签名证书**，否则会报签名错误（如 `0x800B0100` 不受信任）。不需要反复信任，首次信任一次即可。

### 方法 A：一键脚本（推荐，最简单）

1. 下载 **两个文件** 到本地同一文件夹：`...msix` 和 `FolderCrypto.cer`
2. **右键** PowerShell → **以管理员身份运行**，执行：

```powershell
cd "你的下载文件夹路径"
# 信任证书（管理员）：
Import-Certificate -FilePath .\FolderCrypto.cer -CertStoreLocation Cert:\LocalMachine\Root
# 安装：
Add-AppxPackage -Path .\FolderCrypto.App_1.0.14.0_x64.msix
```

3. 开始菜单找到 **FolderCrypto** 即可启动

### 方法 B：图形界面信任证书 + 双击安装

1. 双击 `FolderCrypto.cer` → “安装证书” → **本地计算机** → “将所有证书放入下列存储” → “受信任的根证书颁发机构” → 完成
2. 双击 `...msix`，点“安装”

> 方法 B 卸载时如需彻底移除证书，进入 `certmgr.msc` → 受信任的根证书颁发机构 → 证书 → 找到并删除 FolderCrypto 证书。

### 右键菜单 / 锁图标（可选）

MSIX 自带的右键菜单可能存在受限或需额外步骤。如要完整的“资源管理器右键加密/解密 + 锁图标”，请管理员运行：

```powershell
FolderCrypto.Shell install "<FolderCrypto.App.exe 的完整路径>" --dll "<FolderCrypto.ShellNative.dll 的路径>"
```

> 普通用户若只需要“打开 App、选择文件加密”，可跳过此步。

---

## 六、关于证书（重要）

- 本机使用**自签名代码签名证书**（`wix\new-cert.ps1` 生成），优点是不花钱、可自己生成；缺点是：
  - MSIX 用户首次安装需信任证书
  - 部分环境 / SmartScreen 可能提示“未知发布者”（MSI 双击安装时可能见 UAC 的“发布者: 未知”，点“是”即可）
- 若未来想让用户**双击即可安装、无任何签名提示**，可购买正式**代码签名证书**（类型选 Windows 桌面 / Microsoft Store 支持的 Code Signing），用 `signtool` 重新签名后分发，这样无需用户信任证书。
- ❗ **`packages\FolderCrypto.pfx` 含私钥，绝不可上传到 GitHub Release / 仓库**；仅 `FolderCrypto.cer`（公钥）可随包分发供用户信任。

### 证书已更换记录（2026-08-16）

- 旧证书：指纹 `E25B41DD...`，密码 `FolderCrypto_Pfx_Pass2026!`（已弃用）
- 新证书：指纹 `DA635E69430FDBAB33423734763962CCE104D4DF`，Subject `CN=FolderCrypto 文件夹加密`，密码见 `wix\new-cert.ps1` 生成时设定的 `FOLDCRYPTO_PFX_PASS`
- `build-msi.ps1` 现在**运行时自动从 pfx 读取指纹**，更换证书后无需手改 `$thumb`

### 历史 git 历史中的密码

旧版 `wix\build-msi.ps1` 曾把 PKCS#12 私钥密码硬编码并提交进了 git 历史（现已改为从环境变量 `FOLDCRYPTO_PFX_PASS` 读取）。由于 `FolderCrypto.pfx`（私钥文件）**从未被 git 跟踪**（`packages/` 一直被忽略），攻击者虽能看到密码，但没有私钥文件也无法签名，实际风险较低。我们已经**更换了证书与密码**，进一步降低了风险。如需彻底清除历史中残留的旧密码，可重写 git 历史（`git filter-repo` 等，较复杂且会改写提交号），非必须。

---

## 七、MSIX 重签说明（需要时需要）

当前仓库的旧 MSIX 用旧证书签名，且 `Package.appxmanifest` 的 `Publisher="CN=FolderCrypto"` 与新证书 Subject `CN=FolderCrypto 文件夹加密` **不完全一致**。MSIX 安装要求签名证书 CN 与 manifest Publisher **完全一致**。因此用新证书重签 MSIX 需三步：

1. 把 `FolderCrypto.App\Package.appxmanifest` 的 `Publisher` 改为 `CN=FolderCrypto 文件夹加密`（与新证书 Subject 一致）
2. 解包旧 MSIX → MakeAppx 重新打包，或直接用 VS 重新生成 MSIX 包
3. `signtool sign` 用新 pfx 签名新 MSIX

> 每次换证书后，MSIX 与 MSI 的证书必须统一，且要重新打包 MSIX（不能只改签名）。若你之后需要发布 MSIX，可回到本项目让我帮你执行上述重签流程。
```