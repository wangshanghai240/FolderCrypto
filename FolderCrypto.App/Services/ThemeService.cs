using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace FolderCrypto.App.Services;

/// <summary>主题模式的 JSON 序列化上下文（裁剪友好的源生成器）。</summary>
[JsonSerializable(typeof(ThemeService.ThemeConfig))]
internal partial class ThemeConfigJsonContext : JsonSerializerContext
{
}

/// <summary>主题模式：跟随系统 / 浅色 / 深色 / 自定义。</summary>
public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2,
    Custom = 3,
}

/// <summary>
/// 主题服务：负责持久化、切换和应用主题（浅色/深色/自定义强调色），
/// 并在系统主题变化时根据模式决定是否跟随。
/// </summary>
public static class ThemeService
{
    private const string ConfigDirName = "FolderCrypto";
    private const string ConfigFileName = "theme.json";
    private const string DefaultAccent = "#0078D4";

    private static ThemeMode _mode = ThemeMode.System;
    private static bool _customLight = true;          // 自定义主题的基准(浅/深)
    private static string _accentHex = DefaultAccent; // 自定义主题的强调色
    private static string? _configPath;
    private static bool _initialized;
    private static ApplicationTheme _resolved = ApplicationTheme.Light;

    // 已打开并注册的窗口（主窗口 + 各提示窗口）。
    private static readonly List<Window> _windows = new();

    /// <summary>当前主题模式。</summary>
    public static ThemeMode Mode => _mode;

    /// <summary>自定义主题的基准是否为浅色。</summary>
    public static bool CustomIsLight => _customLight;

    /// <summary>自定义主题的强调色（十六进制，如 #0078D4）。</summary>
    public static string AccentHex => _accentHex;

    /// <summary>当前解析出的深浅主题（供标题栏等使用）。</summary>
    public static ApplicationTheme ResolvedTheme => _resolved;

    /// <summary>主题变化事件（供 UI 同步选中状态等）。</summary>
    public static event Action? Changed;

    /// <summary>初始化：读取配置、订阅系统主题变化事件（本方法不直接应用到窗口，
    /// 等窗口创建后由 RegisterWindow / Apply 应用）。</summary>
    public static void Init()
    {
        if (_initialized || App.Current == null) return;
        _initialized = true;

        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ConfigDirName,
            ConfigFileName);

        Load();

        // 先解析出当前主题，保证窗口创建时 ThemeService.RegisterWindow 就能用正确主题。
        _resolved = ResolveTheme();

        // 记录 UI 线程调度器，供系统主题变化事件（非 UI 线程）投递回 UI 线程。
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // 订阅系统主题变化：仅在「跟随系统」模式下自动切换。
        var ui = new UISettings();
        ui.ColorValuesChanged += (s, e) =>
        {
            if (_mode == ThemeMode.System)
            {
                dispatcher?.TryEnqueue(Apply);
            }
        };
    }

    /// <summary>注册一个窗口，应用主题并在后续主题变化时自动刷新。</summary>
    public static void RegisterWindow(Window window)
    {
        if (window == null || _windows.Contains(window)) return;
        _windows.Add(window);
        ApplyToWindow(window);
        FireChanged();
    }

    /// <summary>注销窗口（窗口关闭时调用）。</summary>
    public static void UnregisterWindow(Window window)
    {
        if (window != null) _windows.Remove(window);
    }

    /// <summary>切换主题模式并应用、持久化。</summary>
    public static void SetMode(ThemeMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        Save();
        Apply();
        FireChanged();
    }

    /// <summary>设置自定义主题的基准(浅/深)并应用、持久化。</summary>
    public static void SetCustomBase(bool light)
    {
        if (_customLight == light) return;
        if (_mode != ThemeMode.Custom) _mode = ThemeMode.Custom;
        _customLight = light;
        Save();
        Apply();
        FireChanged();
    }

    /// <summary>设置自定义主题的强调色并应用、持久化。</summary>
    public static void SetCustomAccent(string hex)
    {
        hex = NormalizeHex(hex);
        if (string.Equals(_accentHex, hex, StringComparison.OrdinalIgnoreCase)) return;
        if (_mode != ThemeMode.Custom) _mode = ThemeMode.Custom;
        _accentHex = hex;
        Save();
        Apply();
        FireChanged();
    }

    /// <summary>应用当前配置的主题到所有已注册窗口（含自定义强调色）。</summary>
    public static void Apply()
    {
        if (App.Current == null) return;

        _resolved = ResolveTheme();

        // 全局资源：自定义模式下覆盖强调色，否则清除覆盖。
        if (_mode == ThemeMode.Custom)
        {
            ApplyCustomAccent(_accentHex);
        }
        else
        {
            ClearCustomAccent();
        }

        // 应用到所有已注册窗口（并按需纳入主窗口兜底）。
        var processed = new HashSet<Window>();
        foreach (var w in _windows)
        {
            ApplyToWindow(w);
            processed.Add(w);
        }
        if (App.CurrentWindow != null && processed.Add(App.CurrentWindow))
        {
            ApplyToWindow(App.CurrentWindow);
        }
    }

    private static ApplicationTheme ResolveTheme()
    {
        switch (_mode)
        {
            case ThemeMode.Light:
                return ApplicationTheme.Light;
            case ThemeMode.Dark:
                return ApplicationTheme.Dark;
            case ThemeMode.Custom:
                return _customLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
            default: // System
                var bg = new UISettings().GetColorValue(UIColorType.Background);
                double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                return luminance > 0.5 ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }
    }

    /// <summary>
    /// 将解析出的主题应用到单个窗口：设置其根元素 RequestedTheme（必要时用临时
    /// 对切刷新所有 ThemeResource），并刷新标题栏配色。
    /// 注意：WInUI 中设置 Application.RequestedTheme 会抛 0x80131515，因此必须用
    /// 窗口根元素的 FrameworkElement.RequestedTheme。
    /// </summary>
    private static void ApplyToWindow(Window window)
    {
        try
        {
            if (window.Content is not FrameworkElement root) return;

            // 跟随系统：用 ElementTheme.Default（自动响应系统浅/深切换，无需手动检测）。
            ElementTheme target = _mode == ThemeMode.System
                ? ElementTheme.Default
                : (_resolved == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light);

            if (root.RequestedTheme != target)
            {
                root.RequestedTheme = target;
            }
            else if (_mode != ThemeMode.System)
            {
                // 仅固定主题(浅/深/自定义)需要临时对切，强制 {ThemeResource} 重新解析。
                var alt = target == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                root.RequestedTheme = alt;
                root.RequestedTheme = target;
            }

            // 用窗口「实际解析出的主题」刷新 _resolved（跟随系统时 = 系统真实主题）。
            _resolved = root.ActualTheme == ElementTheme.Dark
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
        }
        catch
        {
            // 忽略单个窗口的刷新失败
        }

        App.ApplyWindowTitleBar(window);
    }

    private static readonly string[] AccentBrushKeys =
    {
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentFillColorDisabledBrush",
        "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush",
        "AccentTextFillColorDisabledBrush",
    };

    private static readonly string[] AccentColorKeys =
    {
        "SystemAccentColor",
        "AccentButtonBackground",
        "AccentButtonBackgroundPointerOver",
        "AccentButtonBackgroundPressed",
        "AccentButtonBackgroundDisabled",
        "AccentButtonForeground",
        "AccentButtonForegroundPointerOver",
        "AccentButtonForegroundPressed",
        "AccentButtonForegroundDisabled",
    };

    private static void ApplyCustomAccent(string hex)
    {
        Color accent = ParseHex(hex);
        var res = App.Current.Resources;

        // 强调色 —— 直接覆盖主题资源键（放在 Application.Resources 顶层即可覆盖主题字典）。
        var fillBrush = new SolidColorBrush(accent);
        var fill2Brush = new SolidColorBrush(Shift(accent, -12));
        var fill3Brush = new SolidColorBrush(Shift(accent, -24));
        var fillDisabledBrush = new SolidColorBrush(Colors.Gray);

        var textBrush = new SolidColorBrush(accent);
        var text2Brush = new SolidColorBrush(Shift(accent, -20));
        var text3Brush = new SolidColorBrush(Shift(accent, 30));
        var textDisabledBrush = new SolidColorBrush(Colors.Gray);

        res["AccentFillColorDefaultBrush"] = fillBrush;
        res["AccentFillColorSecondaryBrush"] = fill2Brush;
        res["AccentFillColorTertiaryBrush"] = fill3Brush;
        res["AccentFillColorDisabledBrush"] = fillDisabledBrush;
        res["AccentTextFillColorPrimaryBrush"] = textBrush;
        res["AccentTextFillColorSecondaryBrush"] = text2Brush;
        res["AccentTextFillColorTertiaryBrush"] = text3Brush;
        res["AccentTextFillColorDisabledBrush"] = textDisabledBrush;

        // 按钮相关（AccentButtonStyle 依赖这些颜色键）。
        res["SystemAccentColor"] = accent;
        res["AccentButtonBackground"] = accent;
        res["AccentButtonBackgroundPointerOver"] = Shift(accent, -25);
        res["AccentButtonBackgroundPressed"] = Shift(accent, 35);
        res["AccentButtonBackgroundDisabled"] = Color.FromArgb(255, 120, 120, 120);
        Color white = Colors.White;
        res["AccentButtonForeground"] = white;
        res["AccentButtonForegroundPointerOver"] = white;
        res["AccentButtonForegroundPressed"] = white;
        res["AccentButtonForegroundDisabled"] = white;
    }

    private static void ClearCustomAccent()
    {
        if (App.Current == null) return;
        var res = App.Current.Resources;
        foreach (var key in AccentBrushKeys) res.Remove(key);
        foreach (var key in AccentColorKeys) res.Remove(key);
    }

    // ---------- 工具方法 ----------

    private static void Load()
    {
        try
        {
            if (_configPath == null || !File.Exists(_configPath)) return;
            string json = File.ReadAllText(_configPath);
            var cfg = JsonSerializer.Deserialize(json, ThemeConfigJsonContext.Default.ThemeConfig);
            if (cfg == null) return;
            _mode = (ThemeMode)cfg.Mode;
            _customLight = cfg.CustomIsLight;
            if (!string.IsNullOrWhiteSpace(cfg.Accent)) _accentHex = NormalizeHex(cfg.Accent);
        }
        catch
        {
            // 配置损坏时使用默认值
        }
    }

    private static void Save()
    {
        try
        {
            if (_configPath == null) return;
            string? dir = Path.GetDirectoryName(_configPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var cfg = new ThemeConfig
            {
                Mode = (int)_mode,
                CustomIsLight = _customLight,
                Accent = _accentHex,
            };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, ThemeConfigJsonContext.Default.ThemeConfig));
        }
        catch
        {
            // 忽略写入失败
        }
    }

    private static void FireChanged()
    {
        Changed?.Invoke();
    }

    private static string NormalizeHex(string hex)
    {
        hex = hex.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (hex.Length == 4) // #RGB
        {
            hex = "#" + hex[1] + hex[1] + hex[2] + hex[2] + hex[3] + hex[3];
        }
        if (hex.Length < 7)
        {
            // 补齐到 #RRGGBB
            hex = hex.PadRight(7, '0');
        }
        if (hex.Length > 7) hex = hex.Substring(0, 7);
        return hex;
    }

    private static Color ParseHex(string hex)
    {
        hex = NormalizeHex(hex);
        try
        {
            byte r = Convert.ToByte(hex.Substring(1, 2), 16);
            byte g = Convert.ToByte(hex.Substring(3, 2), 16);
            byte b = Convert.ToByte(hex.Substring(5, 2), 16);
            return Color.FromArgb(255, r, g, b);
        }
        catch
        {
            return Color.FromArgb(255, 0, 120, 212);
        }
    }

    /// <summary>按偏移量微调颜色亮度（正值变浅、负值变深）。</summary>
    private static Color Shift(Color c, int delta)
    {
        int r = c.R + delta, g = c.G + delta, b = c.B + delta;
        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);
        return Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
    }

    internal sealed class ThemeConfig
    {
        public int Mode { get; set; }
        public bool CustomIsLight { get; set; } = true;
        public string? Accent { get; set; }
    }
}
