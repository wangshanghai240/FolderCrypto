using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderCrypto.App.Services;

/// <summary>应用设置的 JSON 序列化上下文（裁剪友好的源生成器）。</summary>
[JsonSerializable(typeof(SettingsService.SettingsConfig))]
internal partial class SettingsConfigJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 应用设置服务：负责持久化通用设置（如是否显示系统托盘图标），
/// 保存到 %LOCALAPPDATA%\FolderCrypto\settings.json。
/// </summary>
public static class SettingsService
{
    private const string ConfigDirName = "FolderCrypto";
    private const string ConfigFileName = "settings.json";

    private static bool _showTrayIcon = true;   // 默认在系统托盘中显示图标
    private static string? _configPath;
    private static bool _initialized;

    /// <summary>是否在系统托盘中显示程序图标。</summary>
    public static bool ShowTrayIcon
    {
        get { EnsureLoaded(); return _showTrayIcon; }
        set
        {
            EnsureLoaded();
            if (_showTrayIcon == value) return;
            _showTrayIcon = value;
            Save();
        }
    }

    /// <summary>初始化并加载配置（幂等，应用启动时调用一次）。</summary>
    public static void Init() => EnsureLoaded();

    private static void EnsureLoaded()
    {
        if (_initialized) return;
        _initialized = true;

        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ConfigDirName,
            ConfigFileName);

        try
        {
            if (!File.Exists(_configPath)) return;
            string json = File.ReadAllText(_configPath);
            var cfg = JsonSerializer.Deserialize(json, SettingsConfigJsonContext.Default.SettingsConfig);
            if (cfg == null) return;
            _showTrayIcon = cfg.ShowTrayIcon;
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
            var cfg = new SettingsConfig { ShowTrayIcon = _showTrayIcon };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, SettingsConfigJsonContext.Default.SettingsConfig));
        }
        catch
        {
            // 忽略写入失败
        }
    }

    internal sealed class SettingsConfig
    {
        public bool ShowTrayIcon { get; set; } = true;
    }
}
