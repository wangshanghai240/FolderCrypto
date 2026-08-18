using System.Windows;
using FolderCrypto.Bootstrapper.Services;

namespace FolderCrypto.Bootstrapper;

/// <summary>
/// WPF 安装引导程序入口。启动时按系统深浅色加载对应主题字典。
/// </summary>
public partial class App : Application
{
    private ThemeService? _theme;

    public ThemeService Theme => _theme ??= new ThemeService(this);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply();
    }
}
