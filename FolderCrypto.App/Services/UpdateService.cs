using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace FolderCrypto.App.Services;

/// <summary>软件更新检查结果。</summary>
public sealed class UpdateCheckResult
{
    /// <summary>是否存在可用的新版本。</summary>
    public bool Available { get; set; }

    /// <summary>检查失败（网络/接口错误）。</summary>
    public bool CheckFailed { get; set; }

    /// <summary>仓库没有任何 Release。</summary>
    public bool NoRelease { get; set; }

    /// <summary>最新版本号（如 1.0.15）。</summary>
    public string LatestVersion { get; set; } = "";

    /// <summary>安装包下载地址（优先安装器 EXE/MSI，其次便携版 ZIP）。</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>GitHub Release 页面地址。</summary>
    public string? ReleasePageUrl { get; set; }

    /// <summary>更新说明（截断后展示）。</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// 软件更新服务：通过 GitHub Releases API 检查最新版本并下载安装包。
/// 仓库：wangshanghai240/FolderCrypto（MSI 为主推安装包）。
/// </summary>
public static class UpdateService
{
    private const string RepoOwner = "wangshanghai240";
    private const string RepoName = "FolderCrypto";
    private const string ApiLatestUrl = "https://api.github.com/repos/wangshanghai240/FolderCrypto/releases/latest";
    private const string ReleasePageUrl = "https://github.com/wangshanghai240/FolderCrypto/releases/latest";

    /// <summary>当前应用版本号（从程序集读取，如 1.0.14）。</summary>
    public static string CurrentVersion
    {
        get
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null && v.Major > 0)
                    return v.Revision >= 0 ? v.ToString(4) : v.ToString(3);
            }
            catch { }
            return "1.0.0";
        }
    }

    /// <summary>
    /// 检查最新版本。失败/无 Release 时不抛异常，返回带标识的结果。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync()
    {
        var result = new UpdateCheckResult();
        try
        {
            using var client = CreateClient();
            using var resp = await client.GetAsync(ApiLatestUrl).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 没有 Release（或最新版是草稿/预发布）
                result.NoRelease = true;
                return result;
            }
            if (!resp.IsSuccessStatusCode)
            {
                result.CheckFailed = true;
                return result;
            }

            string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string latest = NormalizeVersion(GetString(root, "tag_name"));
            result.LatestVersion = latest;
            result.Notes = Truncate(GetString(root, "body"), 800);
            result.ReleasePageUrl = ReleasePageUrl;

            // 从 assets 里挑安装包：优先安装器(EXE/MSI)，其次便携版 ZIP
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                string? installer = null, zip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = GetString(a, "name");
                    string? url = GetString(a, "browser_download_url");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                    if ((name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) && installer == null)
                        installer = url;
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && zip == null)
                        zip = url;
                }
                result.DownloadUrl = installer ?? zip;
            }

            result.Available = CompareVersions(latest, CurrentVersion) > 0;
            return result;
        }
        catch
        {
            result.CheckFailed = true;
            return result;
        }
    }

    /// <summary>
    /// 把更新包以「流式」方式下载到「下载」目录。
    /// 返回 (文件完整路径, 错误信息)；成功时 Error 为 null。
    /// </summary>
    public static async Task<(string? Path, string? Error)> DownloadAsync(string url, string fileName)
    {
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Directory.CreateDirectory(downloads);
        string dest = Path.Combine(downloads, fileName);

        // 安装包可能上百 MB：流式下载边下边写盘，整体放宽到 10 分钟。
        // 失败时最多重试 2 次（SSL/握手/证书校验等瞬时错误常在重试后成功）。
        Exception? last = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("FolderCrypto-UpdateChecker/1.0");

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return (null, $"HTTP {(int)resp.StatusCode}");

                await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, cts.Token).ConfigureAwait(false);
                return (dest, null);
            }
            catch (OperationCanceledException)
            {
                return (null, "下载超时，请检查网络后重试");
            }
            catch (Exception ex)
            {
                last = ex;   // 最后再尝试一次
            }
        }
        return (null, DescribeError(last));
    }

    /// <summary>拼接异常链消息（便于定位 SSL/证书/协议等具体原因）。</summary>
    private static string DescribeError(Exception? ex)
    {
        if (ex == null) return "未知错误";
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(e.Message)) parts.Add(e.Message);
        }
        return string.Join(" → ", parts);
    }

    /// <summary>启动安装包（MSI 会用系统默认方式打开并触发 UAC/安装向导）。</summary>
    public static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API 要求提供 User-Agent，否则返回 403。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FolderCrypto-UpdateChecker/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string NormalizeVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "0.0.0";
        string t = tag.Trim();
        if (t.StartsWith('v')) t = t.Substring(1);
        return t;
    }

    /// <summary>比较版本号："1.0.15" &gt; "1.0.14"。无法解析时按字符串比较。</summary>
    private static int CompareVersions(string a, string b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
            return va.CompareTo(vb);
        return string.CompareOrdinal(a, b);
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
