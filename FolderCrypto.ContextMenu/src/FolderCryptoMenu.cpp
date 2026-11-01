// FolderCryptoMenu.cpp
// IExplorerCommand 实现：让"加密 / 解密"出现在 Windows 11 一级右键菜单。
//
// 设计要点：
//  * 菜单构建路径（GetTitle/GetIcon/GetState/GetFlags）必须极快，不做 IO。
//  * 真正的判断（是否已加密）推迟到 Invoke 之后，或仅读一个注册表值。
//  * 通过 IShellItemArray 枚举当前选中项，逐个调用 FolderCrypto.App.exe toggle。

#define STRICT
#include <windows.h>
#include <objbase.h>
#include <shlwapi.h>      // SHStrDupW —— 必须在 shlobj 之前并依赖其原型
#include <shlobj.h>
#include <shlobj_core.h>
#include <shobjidl_core.h>
#include <atlbase.h>
#include <atlcom.h>
#include <new>
#include <string>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "advapi32.lib")

// 与现有 FolderCrypto 安装保持一致的注册表位置
static const wchar_t* kAppKey    = L"Software\\FolderCrypto";
static const wchar_t* kAppPathVal = L"AppPath";

// {F8A2D000-1234-4A5B-9C6D-7E8F9A0B1C2D}
static const CLSID CLSID_FolderCryptoMenu =
    { 0xF8A2D000, 0x1234, 0x4A5B, { 0x9C, 0x6D, 0x7E, 0x8F, 0x9A, 0x0B, 0x1C, 0x2D } };

// 图标：包内 Assets\LockIcon.ico（与主程序同目录）
static const wchar_t* kIconRel = L"Assets\\LockIcon.ico";

// ---------------------------------------------------------------- 辅助函数

// 取本 DLL 所在目录。包身份下 DLL 与 FolderCrypto.App.exe 同目录。
static std::wstring GetModuleDirectory()
{
    HMODULE hMod = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&GetModuleDirectory), &hMod))
        return std::wstring();

    wchar_t path[MAX_PATH * 2] = {};
    DWORD n = GetModuleFileNameW(hMod, path, ARRAYSIZE(path));
    if (n == 0 || n >= ARRAYSIZE(path))
        return std::wstring();

    std::wstring s(path);
    size_t slash = s.find_last_of(L"\\/");
    if (slash == std::wstring::npos)
        return std::wstring();
    s.erase(slash + 1);
    return s;
}

// 读取注册表中的主程序路径（经典/未打包安装方式使用）。失败返回空串。
static std::wstring GetRegistryAppPath()
{
    wchar_t buf[MAX_PATH * 2] = {};
    DWORD cb = sizeof(buf) - sizeof(wchar_t);
    HKEY hKey = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kAppKey, 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return std::wstring();

    DWORD type = 0;
    LONG rc = RegQueryValueExW(hKey, kAppPathVal, nullptr, &type,
                               reinterpret_cast<LPBYTE>(buf), &cb);
    RegCloseKey(hKey);

    if (rc != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ))
        return std::wstring();
    if (buf[0] == L'\0')
        return std::wstring();

    if (type == REG_EXPAND_SZ)
    {
        wchar_t expanded[MAX_PATH * 2] = {};
        DWORD n = ExpandEnvironmentStringsW(buf, expanded, ARRAYSIZE(expanded));
        if (n > 0 && n <= ARRAYSIZE(expanded))
            return std::wstring(expanded);
    }
    return std::wstring(buf);
}

// 解析主程序完整路径。
// 顺序：① DLL 同目录下的 FolderCrypto.App.exe（MSIX 包身份场景）
//      ② 注册表 AppPath（经典安装场景）
// 两者都不存在时返回空串。
static std::wstring GetAppPath()
{
    std::wstring dir = GetModuleDirectory();
    if (!dir.empty())
    {
        std::wstring candidate = dir + L"FolderCrypto.App.exe";
        if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES)
            return candidate;
    }

    std::wstring reg = GetRegistryAppPath();
    if (!reg.empty() && GetFileAttributesW(reg.c_str()) != INVALID_FILE_ATTRIBUTES)
        return reg;

    return std::wstring();
}

// 图标：优先用主程序所在目录下的 Assets\LockIcon.ico。
static std::wstring GetIconPath(const std::wstring& appPath)
{
    std::wstring dir;

    if (!appPath.empty())
    {
        dir = appPath;
        size_t slash = dir.find_last_of(L"\\/");
        if (slash != std::wstring::npos)
            dir.erase(slash + 1);
    }
    if (dir.empty())
        dir = GetModuleDirectory();
    if (dir.empty())
        return std::wstring();

    std::wstring icon = dir + kIconRel;
    if (GetFileAttributesW(icon.c_str()) == INVALID_FILE_ATTRIBUTES)
        return std::wstring();
    return icon;
}

// 取单个 IShellItem 的完整文件系统路径。
static std::wstring GetItemPath(IShellItem* psi)
{
    if (!psi)
        return std::wstring();

    PWSTR raw = nullptr;
    if (FAILED(psi->GetDisplayName(SIGDN_FILESYSPATH, &raw)) || !raw)
    {
        // 非文件系统项（如虚拟文件夹）回退到解析名
        if (SUCCEEDED(psi->GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, &raw)) && raw)
        {
            std::wstring s(raw);
            CoTaskMemFree(raw);
            return s;
        }
        return std::wstring();
    }
    std::wstring s(raw);
    CoTaskMemFree(raw);
    return s;
}

// 用引号包裹并追加到命令行，避免路径空格问题。
static void AppendQuoted(std::wstring& cmd, const std::wstring& arg)
{
    cmd.push_back(L' ');
    cmd.push_back(L'"');
    cmd += arg;
    cmd.push_back(L'"');
}

// 启动主程序：FolderCrypto.App.exe toggle "<path>"
static bool LaunchToggle(const std::wstring& appPath, const std::wstring& target)
{
    if (appPath.empty() || target.empty())
        return false;

    std::wstring cmd;
    cmd.reserve(appPath.size() + target.size() + 32);
    cmd.push_back(L'"');
    cmd += appPath;
    cmd.push_back(L'"');
    cmd += L" toggle";
    AppendQuoted(cmd, target);

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};

    BOOL ok = CreateProcessW(nullptr, &cmd[0], nullptr, nullptr, FALSE,
                             0, nullptr, nullptr, &si, &pi);
    if (!ok)
        return false;

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return true;
}

// ---------------------------------------------------------------- 命令实现

class __declspec(uuid("F8A2D000-1234-4A5B-9C6D-7E8F9A0B1C2D"))
    FolderCryptoCommand final : public IExplorerCommand
{
public:
    // --- IUnknown ---
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;
        *ppv = nullptr;

        if (IsEqualIID(riid, IID_IUnknown) ||
            IsEqualIID(riid, IID_IExplorerCommand))
        {
            *ppv = static_cast<IExplorerCommand*>(this);
        }
        else
        {
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&m_ref));
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG c = InterlockedDecrement(&m_ref);
        if (c == 0)
            delete this;
        return static_cast<ULONG>(c);
    }

    // --- IExplorerCommand ---

    // 静态标题。GetTitle 在菜单构建路径上被调用，必须立即返回。
    IFACEMETHODIMP GetTitle(IShellItemArray* /*psiItemArray*/, PWSTR* ppszName) override
    {
        if (!ppszName)
            return E_POINTER;
        return SHStrDupW(L"加密 / 解密", ppszName);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray* /*psiItemArray*/, PWSTR* ppszIcon) override
    {
        if (!ppszIcon)
            return E_POINTER;
        *ppszIcon = nullptr;

        std::wstring icon = GetIconPath(GetAppPath());
        if (icon.empty())
            return E_NOTIMPL;

        return SHStrDupW(icon.c_str(), ppszIcon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray* /*psiItemArray*/, PWSTR* ppszInfotip) override
    {
        if (!ppszInfotip)
            return E_POINTER;
        *ppszInfotip = nullptr;
        return SHStrDupW(L"对选中的文件夹加密或解密（FoldCrypto）", ppszInfotip);
    }

    // 关键：判断是否可点、是否隐藏。此处只做极轻量的检查。
    IFACEMETHODIMP GetState(IShellItemArray* psiItemArray, BOOL /*fOkToBeSlow*/,
                            EXPCMDSTATE* pCmdState) override
    {
        if (!pCmdState)
            return E_POINTER;
        *pCmdState = ECS_ENABLED;

        // 主程序路径缺失时置为禁用，而不是隐藏——
        // 让用户能看到条目并提示"需重装"，避免无声消失。
        if (GetAppPath().empty())
        {
            *pCmdState = ECS_DISABLED;
            return S_OK;
        }

        // 只对文件夹生效；选中项里没有文件夹就隐藏。
        if (psiItemArray)
        {
            DWORD count = 0;
            if (SUCCEEDED(psiItemArray->GetCount(&count)))
            {
                bool hasFolder = false;
                for (DWORD i = 0; i < count && !hasFolder; ++i)
                {
                    CComPtr<IShellItem> psi;
                    if (FAILED(psiItemArray->GetItemAt(i, &psi)) || !psi)
                        continue;

                    // 只接受文件系统内的文件夹；驱动器根目录由系统
                    // 自身的"加密/解密"属性页处理，这里排除掉。
                    SFGAOF attrs = SFGAO_FOLDER | SFGAO_FILESYSTEM;
                    if (SUCCEEDED(psi->GetAttributes(attrs, &attrs)) &&
                        (attrs & SFGAO_FOLDER) && (attrs & SFGAO_FILESYSTEM))
                    {
                        std::wstring p = GetItemPath(psi);
                        // 根目录形如 "C:\"，长度 3 且以冒号结尾
                        bool isDriveRoot = (p.size() == 3 && p[1] == L':' &&
                                            (p[2] == L'\\' || p[2] == L'/'));
                        if (!isDriveRoot)
                            hasFolder = true;
                    }
                }
                if (!hasFolder)
                    *pCmdState = ECS_HIDDEN;
            }
        }
        return S_OK;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) override
    {
        if (!pguidCommandName)
            return E_POINTER;
        *pguidCommandName = CLSID_FolderCryptoMenu;
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) override
    {
        if (!pFlags)
            return E_POINTER;
        // ECF_DEFAULT：普通命令项，不展开子菜单
        *pFlags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) override
    {
        if (!ppEnum)
            return E_POINTER;
        *ppEnum = nullptr;
        return E_NOTIMPL;
    }

    // 真正干活的入口。此处才允许做重活。
    IFACEMETHODIMP Invoke(IShellItemArray* psiItemArray, IBindCtx* /*pbc*/) override
    {
        std::wstring appPath = GetAppPath();
        if (appPath.empty())
        {
            MessageBoxW(nullptr,
                        L"未找到 FolderCrypto 主程序路径。\n"
                        L"请重新运行 FolderCrypto 安装程序进行修复。",
                        L"FolderCrypto 右键菜单",
                        MB_OK | MB_ICONERROR);
            return E_FAIL;
        }

        if (!psiItemArray)
            return E_INVALIDARG;

        DWORD count = 0;
        HRESULT hr = psiItemArray->GetCount(&count);
        if (FAILED(hr))
            return hr;

        bool anyLaunched = false;
        for (DWORD i = 0; i < count; ++i)
        {
            CComPtr<IShellItem> psi;
            if (FAILED(psiItemArray->GetItemAt(i, &psi)) || !psi)
                continue;

            // 只处理文件夹，跳过普通文件，避免误触发
            SFGAOF attrs = SFGAO_FOLDER;
            if (FAILED(psi->GetAttributes(attrs, &attrs)))
                continue;
            if (!(attrs & SFGAO_FOLDER))
                continue;

            std::wstring target = GetItemPath(psi);
            if (target.empty())
                continue;

            if (LaunchToggle(appPath, target))
                anyLaunched = true;
        }

        if (!anyLaunched)
        {
            MessageBoxW(nullptr,
                        L"未能启动 FolderCrypto 主程序。\n"
                        L"请确认程序已正确安装。",
                        L"FolderCrypto 右键菜单",
                        MB_OK | MB_ICONERROR);
            return E_FAIL;
        }
        return S_OK;
    }

private:
    LONG m_ref = 1;
};

// ---------------------------------------------------------------- COM 导出

// 类工厂：仅支持一个 CLSID
class FolderCryptoClassFactory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;
        *ppv = nullptr;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }  // 静态单例
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        if (pUnkOuter)
            return CLASS_E_NOAGGREGATION;
        if (!ppv)
            return E_POINTER;

        FolderCryptoCommand* obj = new (std::nothrow) FolderCryptoCommand();
        if (!obj)
            return E_OUTOFMEMORY;

        HRESULT hr = obj->QueryInterface(riid, ppv);
        obj->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL /*fLock*/) override { return S_OK; }
};

static FolderCryptoClassFactory g_factory;
static LONG g_dllRefs = 0;

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv)
        return E_POINTER;
    *ppv = nullptr;

    if (!IsEqualCLSID(rclsid, CLSID_FolderCryptoMenu))
        return CLASS_E_CLASSNOTAVAILABLE;

    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return (g_dllRefs == 0) ? S_OK : S_FALSE;
}
