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

构建产物位于 `packages\`：

- `packages\FolderCrypto.App_<版本>_x64.msix` —— MSIX 安装包
- `packages\FolderCrypto.cer` —— 自签名签名证书（给用户信任用）

MSIX 由 VS 打包（`FolderCrypto.App.Package.appxmanifest` 中的版本号决定），证书由 `FolderCrypto.pfx` 生成。每次发布新版本需重新打包生成新的 MSIX。

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
6. **附件（Attach binaries）**上传这两个文件：
   - `packages\FolderCrypto.App_1.0.14.0_x64.msix`
   - `packages\FolderCrypto.cer`
7. 点击 **Publish release**

---

## 三、发布模板（Release 正文）

```markdown
# FolderCrypto 文件夹加密 v1.0.14

Windows 11 文件夹/文件加密工具（WinUI 3）。支持资源管理器右键加密/解密、锁图标叠加。

## 下载安装

下载 `FolderCrypto.App_1.0.14.0_x64.msix` 和 `FolderCrypto.cer`，然后**按下方教程安装**。

## 更新日志
（在此填写）

## 系统要求
- Windows 10/11（x64）
```

---

## 四、用户安装 MSIX 的正确方法

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

## 五、关于证书（重要）

- 目前 MSIX 使用**自签名测试证书**签名，优点是不花钱、可自己生成；缺点是：
  - 用户首次安装需信任证书（如上教程）
  - 部分环境 / SmartScreen 可能提示“未知发布者”
- 若未来想让用户**双击即可安装、无证书报错**，可购买正式**代码签名证书**（类型选 Windows 桌面 / Microsoft Store 支持的 Code Signing），用 `signtool` 重新签名 MSIX 后分发，这样无需用户信任证书。

### 历史 git 历史中的密码

旧版 `wix\build-msi.ps1` 曾把 PKCS#12 私钥密码硬编码并提交进了 git 历史（现已改为从环境变量 `FOLDCRYPTO_PFX_PASS` 读取）。由于 `FolderCrypto.pfx`（私钥文件）**从未被 git 跟踪**（`packages/` 一直被忽略），攻击者虽能看到密码，但没有私钥文件也无法签名，实际风险较低。

如需彻底止损，可任选其一：
- **更换签名证书与密码**（推荐，彻底）：重新生成一对新证书/私钥，用新 pfx 重签 MSIX，并发布对应新 `.cer`；
- 或重写 git 历史删除该密码（`git filter-repo` 等，操作较复杂且会改写提交号）。
```