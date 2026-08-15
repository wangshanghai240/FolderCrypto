using System;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FolderCrypto.App.Services;

/// <summary>WinUI 3 辅助方法。</summary>
public static class WinUIHelper
{
    /// <summary>
    /// 为 Pickers 设置窗口句柄（WinUI 3 中 FileSavePicker / FolderPicker 需要指定 hwnd）。
    /// </summary>
    public static void InitializePicker(Window? window, object picker)
    {
        if (window == null || picker == null) return;

        IntPtr hwnd = WindowNative.GetWindowHandle(window);

        switch (picker)
        {
            case FileSavePicker fsp:
                InitializeWithWindow.Initialize(fsp, hwnd);
                break;
            case FileOpenPicker fop:
                InitializeWithWindow.Initialize(fop, hwnd);
                break;
            case FolderPicker fp:
                InitializeWithWindow.Initialize(fp, hwnd);
                break;
        }
    }
}
