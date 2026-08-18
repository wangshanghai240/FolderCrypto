using System.IO;
using System.Windows;
using System.Windows.Interop;
using FolderCrypto.Bootstrapper.Services;

namespace FolderCrypto.Bootstrapper;

public partial class MainWindow : Window
{
    private const int WM_SETTINGCHANGE = 0x001A;

    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        PathBox.Text = DefaultInstallDir();
        VersionText.Text = "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.14") +
                           " · 跟随系统深浅色";
    }

    private static string DefaultInstallDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FolderCrypto");

    private ThemeService Theme => ((App)Application.Current).Theme;

    // 监听 WM_SETTINGCHANGE（系统主题切换），实时刷新深浅色与标题栏
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
        {
            src.AddHook(WndProc);
            ApplyTitleBarTheme(src.Handle);
        }
    }

    private void ApplyTitleBarTheme(IntPtr hwnd)
        => ThemeService.ApplyWindowTitleBar(hwnd, !Theme.IsLightTheme);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE)
        {
            Theme.Apply();
            ApplyTitleBarTheme(hwnd);
        }
        return IntPtr.Zero;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = PathBox.Text.Trim();
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择安装目录",
            Multiselect = false,
            InitialDirectory = Directory.Exists(dir) ? dir : DefaultInstallDir(),
        };

        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName;
            SetStatus(string.Empty);
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var dir = PathBox.Text.Trim();
        if (dir.Length > 0 && !Path.IsPathRooted(dir))
        {
            SetStatus("安装目录必须是完整路径，例如 D:\\FolderCrypto", error: true);
            return;
        }

        SetBusy(true);
        SetStatus("正在请求管理员权限并安装，请稍候…");
        try
        {
            var result = await System.Threading.Tasks.Task.Run(
                () => MsiInstaller.Run(dir.Length > 0 ? dir : null));

            if (result.Success)
            {
                SetStatus(result.Message, success: true);
                FinishInstall();
            }
            else
            {
                SetStatus(result.Message, error: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus("安装失败：" + ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void FinishInstall()
    {
        InstallButton.Content = "完成";
        InstallButton.Click -= InstallButton_Click;
        InstallButton.Click += (_, _) => Close();
        CancelButton.Content = "关闭";
        CancelButton.Click -= CancelButton_Click;
        CancelButton.Click += (_, _) => Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, bool error = false, bool success = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error
            ? (System.Windows.Media.Brush)FindResource("ErrorBrush")
            : success
                ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
                : (System.Windows.Media.Brush)FindResource("TextSecondary");
    }
}
