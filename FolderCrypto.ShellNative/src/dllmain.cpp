// dllmain.cpp : ATL COM 服务器入口与对象映射
#include "pch.h"
#include "resource.h"

// midl 生成的 GUID 定义（CLSID_OverlayHandler / LIBID_FolderCryptoShellNativeLib）
#include "../FolderCryptoShellNative_i.c"

// 覆盖层处理器类
class ATL_NO_VTABLE COverlayHandler :
    public CComObjectRootEx<CComSingleThreadModel>,
    public CComCoClass<COverlayHandler, &CLSID_COverlayHandler>,
    public IShellIconOverlayIdentifier
{
public:
    COverlayHandler() = default;

    DECLARE_NO_REGISTRY()

    BEGIN_COM_MAP(COverlayHandler)
        COM_INTERFACE_ENTRY(IShellIconOverlayIdentifier)
    END_COM_MAP()

    // IShellIconOverlayIdentifier
    STDMETHODIMP GetOverlayInfo(LPWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags) override;
    STDMETHODIMP GetPriority(int* pPriority) override;
    STDMETHODIMP IsMemberOf(LPCWSTR pwszPath, DWORD dwAttrib) override;
};

OBJECT_ENTRY_AUTO(CLSID_COverlayHandler, COverlayHandler)

// ---------------------------------------------------------------------------
// 锁图标路径：覆盖层图标固定命名为 overlay-lock.ico，放在本 DLL 同目录。
// 这样安装时无需额外注册图标绝对路径，Explorer 加载 DLL 的文件夹即可找到图标。
// ---------------------------------------------------------------------------
STDMETHODIMP COverlayHandler::GetOverlayInfo(LPWSTR pwszIconFile, int cchMax, int* pIndex, DWORD* pdwFlags)
{
    if (pwszIconFile == nullptr || cchMax <= 0)
        return E_INVALIDARG;

    // 取得本 DLL 所在目录
    wchar_t szDll[MAX_PATH] = { 0 };
    HMODULE hMod = _AtlBaseModule.GetModuleInstance();
    if (hMod == nullptr || GetModuleFileNameW(hMod, szDll, MAX_PATH) == 0)
        return E_FAIL;

    wchar_t szDir[MAX_PATH] = { 0 };
    wchar_t* pSlash = wcsrchr(szDll, L'\\');
    if (pSlash)
    {
        *pSlash = L'\0';
        wcscpy_s(szDir, MAX_PATH, szDll);
    }

    wchar_t szIcon[MAX_PATH] = { 0 };
    swprintf_s(szIcon, MAX_PATH, L"%s\\overlay-lock.ico", szDir);

    if (pIndex)      *pIndex = 0;
    if (pdwFlags)    *pdwFlags = ISIOI_ICONFILE | ISIOI_ICONINDEX;

    if (pwszIconFile)
    {
        // 复制到调用方缓冲区（含终止符）
        size_t len = wcslen(szIcon);
        size_t toCopy = (len < (size_t)(cchMax - 1)) ? len : (size_t)(cchMax - 1);
        memcpy(pwszIconFile, szIcon, toCopy * sizeof(wchar_t));
        pwszIconFile[toCopy] = L'\0';
    }

    return S_OK;
}

STDMETHODIMP COverlayHandler::GetPriority(int* pPriority)
{
    // 优先级 0 为最高；希望覆盖层稳定显示。
    if (pPriority) *pPriority = 0;
    return S_OK;
}

// 对已原地加密的文件（FCENC000 头）或已锁定文件夹（.folderlock）显示锁图标
STDMETHODIMP COverlayHandler::IsMemberOf(LPCWSTR pwszPath, DWORD /*dwAttrib*/)
{
    if (pwszPath == nullptr)
        return S_FALSE;

    // 文件夹：检查是否存在 .folderlock 标记
    DWORD attr = ::GetFileAttributesW(pwszPath);
    if (attr == INVALID_FILE_ATTRIBUTES)
        return S_FALSE;

    if (attr & FILE_ATTRIBUTE_DIRECTORY)
    {
        // 拼接 .folderlock 路径并检查存在性
        wchar_t marker[MAX_PATH] = { 0 };
        if (wcslen(pwszPath) + 16 < MAX_PATH)
        {
            swprintf_s(marker, MAX_PATH, L"%s\\%s", pwszPath, L".folderlock");
            DWORD mAttr = ::GetFileAttributesW(marker);
            if (mAttr != INVALID_FILE_ATTRIBUTES && (mAttr & FILE_ATTRIBUTE_HIDDEN))
                return S_OK;   // 锁定文件夹
        }
        return S_FALSE;
    }

    // 文件：读取前 8 字节与 "FCENC000" 比对
    HANDLE hFile = ::CreateFileW(
        pwszPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE)
        return S_FALSE;

    char head[8] = { 0 };
    DWORD read = 0;
    bool matched = false;
    if (::ReadFile(hFile, head, 8, &read, nullptr) && read == 8)
    {
        static const char magic[8] = { 'F', 'C', 'E', 'N', 'C', '0', '0', '0' };
        matched = (memcmp(head, magic, 8) == 0);
    }
    ::CloseHandle(hFile);

    return matched ? S_OK : S_FALSE;
}

// ---------------------------------------------------------------------------
// 共享：判断路径是否“已加密/已锁定”（文件=FCENC000 头，文件夹=.folderlock）。
// ---------------------------------------------------------------------------
static bool IsPathEncrypted(LPCWSTR pwszPath)
{
    if (pwszPath == nullptr)
        return false;

    DWORD attr = ::GetFileAttributesW(pwszPath);
    if (attr == INVALID_FILE_ATTRIBUTES)
        return false;

    if (attr & FILE_ATTRIBUTE_DIRECTORY)
    {
        wchar_t marker[MAX_PATH] = { 0 };
        if (wcslen(pwszPath) + 16 < MAX_PATH)
        {
            swprintf_s(marker, MAX_PATH, L"%s\\%s", pwszPath, L".folderlock");
            DWORD mAttr = ::GetFileAttributesW(marker);
            if (mAttr != INVALID_FILE_ATTRIBUTES && (mAttr & FILE_ATTRIBUTE_HIDDEN))
                return true;   // 锁定文件夹
        }
        return false;
    }

    HANDLE hFile = ::CreateFileW(
        pwszPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE)
        return false;

    char head[8] = { 0 };
    DWORD read = 0;
    bool matched = false;
    if (::ReadFile(hFile, head, 8, &read, nullptr) && read == 8)
    {
        static const char magic[8] = { 'F', 'C', 'E', 'N', 'C', '0', '0', '0' };
        matched = (memcmp(head, magic, 8) == 0);
    }
    ::CloseHandle(hFile);

    return matched;
}

// ---------------------------------------------------------------------------
// 右键菜单状态处理器（IExplorerCommandState）：
//   用于在右键时按“是否已加密”动态显示/隐藏“加密/解密”。
//   - CEncryptStateHandler：未加密时显示“加密”
//   - CDecryptStateHandler：已加密时显示“解密”
// ---------------------------------------------------------------------------
// 共享：计算右键命令状态。showWhenEncrypted 为 true 则“已加密时显示”，否则“未加密时显示”。
static void ComputeCmdState(IShellItemArray* psiItemArray, bool showWhenEncrypted, EXPCMDSTATE* pCmdState)
{
    *pCmdState = ECS_HIDDEN;
    if (psiItemArray == nullptr)
        return;

    // 取第一个选中项
    IShellItem* psiItem = nullptr;
    if (FAILED(psiItemArray->GetItemAt(0, &psiItem)) || psiItem == nullptr)
        return;

    PWSTR pszPath = nullptr;
    bool encrypted = false;
    if (SUCCEEDED(psiItem->GetDisplayName(SIGDN_FILESYSPATH, &pszPath)) && pszPath != nullptr)
    {
        encrypted = IsPathEncrypted(pszPath);
        CoTaskMemFree(pszPath);
    }
    psiItem->Release();

    bool show = (showWhenEncrypted == encrypted);
    *pCmdState = show ? ECS_ENABLED : ECS_HIDDEN;
}

class ATL_NO_VTABLE CEncryptStateHandler :
    public CComObjectRootEx<CComSingleThreadModel>,
    public CComCoClass<CEncryptStateHandler, &CLSID_CEncryptStateHandler>,
    public IExplorerCommandState
{
public:
    DECLARE_NO_REGISTRY()
    DECLARE_NOT_AGGREGATABLE(CEncryptStateHandler)
    BEGIN_COM_MAP(CEncryptStateHandler)
        COM_INTERFACE_ENTRY(IExplorerCommandState)
    END_COM_MAP()

    // IExplorerCommandState：未加密时显示“加密”
    STDMETHODIMP GetState(IShellItemArray* psiItemArray, BOOL /*fOkToBeSlow*/, EXPCMDSTATE* pCmdState)
    {
        if (pCmdState == nullptr) return E_POINTER;
        ComputeCmdState(psiItemArray, /*showWhenEncrypted=*/false, pCmdState);
        return S_OK;
    }
};

class ATL_NO_VTABLE CDecryptStateHandler :
    public CComObjectRootEx<CComSingleThreadModel>,
    public CComCoClass<CDecryptStateHandler, &CLSID_CDecryptStateHandler>,
    public IExplorerCommandState
{
public:
    DECLARE_NO_REGISTRY()
    DECLARE_NOT_AGGREGATABLE(CDecryptStateHandler)
    BEGIN_COM_MAP(CDecryptStateHandler)
        COM_INTERFACE_ENTRY(IExplorerCommandState)
    END_COM_MAP()

    // IExplorerCommandState：已加密时显示“解密”
    STDMETHODIMP GetState(IShellItemArray* psiItemArray, BOOL /*fOkToBeSlow*/, EXPCMDSTATE* pCmdState)
    {
        if (pCmdState == nullptr) return E_POINTER;
        ComputeCmdState(psiItemArray, /*showWhenEncrypted=*/true, pCmdState);
        return S_OK;
    }
};

OBJECT_ENTRY_AUTO(CLSID_CEncryptStateHandler, CEncryptStateHandler)
OBJECT_ENTRY_AUTO(CLSID_CDecryptStateHandler, CDecryptStateHandler)

// ---------------------------------------------------------------------------
// ATL 模块实例
// ---------------------------------------------------------------------------
class COverlayModule : public CAtlDllModuleT<COverlayModule>
{
public:
    DECLARE_LIBID(LIBID_FolderCryptoShellNativeLib)
};

COverlayModule _AtlModule;

// DLL 入口点
extern "C" BOOL WINAPI DllMain(HINSTANCE hInstance, DWORD dwReason, LPVOID reserved)
{
    hInstance;
    return _AtlModule.DllMain(dwReason, reserved);
}

// 标准 COM 导出
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    return _AtlModule.DllGetClassObject(rclsid, riid, ppv);
}

STDAPI DllCanUnloadNow(void)
{
    return _AtlModule.DllCanUnloadNow();
}

// 手动注册/注销一组 COM 类（避免依赖 .rgs 资源解析）。
static HRESULT RegisterClsid(REFCLSID rclsid)
{
    wchar_t szDll[MAX_PATH] = { 0 };
    HMODULE hMod = _AtlBaseModule.GetModuleInstance();
    if (hMod == nullptr || GetModuleFileNameW(hMod, szDll, MAX_PATH) == 0)
        return E_FAIL;

    wchar_t szClsid[64] = { 0 };
    StringFromGUID2(rclsid, szClsid, _countof(szClsid));

    // HKCU\Software\Classes\CLSID\{...} —— 用户级注册，无需管理员权限
    wchar_t szClsidKey[MAX_PATH] = { 0 };
    swprintf_s(szClsidKey, MAX_PATH, L"Software\\Classes\\CLSID\\%s", szClsid);

    HKEY hKey = nullptr;
    LONG lr = RegCreateKeyExW(HKEY_CURRENT_USER, szClsidKey, 0, nullptr,
                              REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hKey, nullptr);
    if (lr != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(lr);

    // InprocServer32
    wchar_t szInproc[MAX_PATH] = { 0 };
    swprintf_s(szInproc, MAX_PATH, L"%s\\InprocServer32", szClsidKey);
    HKEY hInproc = nullptr;
    lr = RegCreateKeyExW(HKEY_CURRENT_USER, szInproc, 0, nullptr,
                         REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hInproc, nullptr);
    if (lr == ERROR_SUCCESS)
    {
        RegSetValueExW(hInproc, nullptr, 0, REG_SZ, (BYTE*)szDll, (DWORD)((wcslen(szDll) + 1) * sizeof(wchar_t)));
        LPCWSTR threading = L"Apartment";
        RegSetValueExW(hInproc, L"ThreadingModel", 0, REG_SZ,
                       (BYTE*)threading, (DWORD)((wcslen(threading) + 1) * sizeof(wchar_t)));
        RegCloseKey(hInproc);
    }

    RegCloseKey(hKey);
    return lr == ERROR_SUCCESS ? S_OK : HRESULT_FROM_WIN32(lr);
}

static void UnregisterClsid(REFCLSID rclsid)
{
    wchar_t szClsid[64] = { 0 };
    StringFromGUID2(rclsid, szClsid, _countof(szClsid));
    wchar_t szClsidKey[MAX_PATH] = { 0 };
    swprintf_s(szClsidKey, MAX_PATH, L"Software\\Classes\\CLSID\\%s", szClsid);
    RegDeleteTreeW(HKEY_CURRENT_USER, szClsidKey);
}

STDAPI DllRegisterServer(void)
{
    HRESULT hr = RegisterClsid(CLSID_COverlayHandler);
    if (FAILED(hr)) return hr;
    hr = RegisterClsid(CLSID_CEncryptStateHandler);
    if (FAILED(hr)) return hr;
    return RegisterClsid(CLSID_CDecryptStateHandler);
}

STDAPI DllUnregisterServer(void)
{
    UnregisterClsid(CLSID_COverlayHandler);
    UnregisterClsid(CLSID_CEncryptStateHandler);
    UnregisterClsid(CLSID_CDecryptStateHandler);
    return S_OK;
}

// 用于 regsvr32 的类型库/导出入口
STDAPI DllInstall(BOOL bInstall, LPCWSTR pszCmdLine)
{
    HRESULT hr = E_FAIL;
    static const wchar_t szUserSwitch[] = L"user";

    if (pszCmdLine != nullptr &&
        _wcsnicmp(pszCmdLine, szUserSwitch, _countof(szUserSwitch) - 1) == 0)
    {
        ATL::AtlSetPerUserRegistration(true);
    }

    if (bInstall)
    {
        hr = DllRegisterServer();
        if (FAILED(hr))
            hr = DllUnregisterServer();
    }
    else
    {
        hr = DllUnregisterServer();
    }

    return hr;
}
