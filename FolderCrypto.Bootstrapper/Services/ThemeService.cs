using System.Windows;

namespace FolderCrypto.Bootstrapper.Services;

/// <summary>
/// 跟随系统深浅色。
/// 读取 HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme，
/// 在 Application 资源中切换 Light/Dark 主题字典；控件用 DynamicResource 绑定，切换即时生效。
/// </summary>
public sealed class ThemeService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Application _app;

    public ThemeService(Application app) => _app = app;

    public bool IsLightTheme
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int i ? i == 1 : true;
            }
            catch
            {
                return true;
            }
        }
    }

    public void Apply()
    {
        var name = IsLightTheme ? "Themes/Light.xaml" : "Themes/Dark.xaml";
        var dict = new ResourceDictionary { Source = new Uri(name, UriKind.Relative) };
        _app.Resources.MergedDictionaries.Clear();
        _app.Resources.MergedDictionaries.Add(dict);
    }

    /// <summary>
    /// 让 OS 标题栏跟随深浅色（DWMWA_USE_IMMERSIVE_DARK_MODE）。
    /// Windows 10 2004+ 用属性 20；1903 及以前用 19，逐个尝试，失败则忽略。
    /// </summary>
    public static void ApplyWindowTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int value = dark ? 1 : 0;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
                NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
        catch { }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
    }
}
