using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace FolderCrypto.App.Services;

/// <summary>
/// 简单的单实例协调器：
///  - 主实例持有命名互斥锁，并监听命名管道接收从新实例转发来的指令。
///  - 新实例启动时，若发现已有实例，则把命令行参数写入管道后退出。
/// </summary>
public static class SingleInstanceManager
{
    private const string MutexName = "Local\\FolderCrypto.SingleInstance";
    private const string PipeName = "FolderCrypto.CommandPipe";

    private static Mutex? _mutex;
    private static Action<string[]>? _onCommand;

    /// <summary>
    /// 尝试成为主实例；若存在「活动的」主实例，则把 args 转发给主实例并返回 true（调用方应退出）。
    ///
    /// 关键点：用非初始拥有的 Mutex + WaitOne(0) 判断。
    ///  - 若互斥锁可立即获得（包括「被遗弃/悬挂」的锁），我们就是主实例 → 打开窗口。
    ///  - 仅当锁被「活的主实例」持有（WaitOne(0) 返回 false）时才转发并退出，
    ///    避免出现「上次崩溃遗留下的悬挂锁让新实例误以为已有实例、从而无法打开窗口」。
    /// </summary>
    public static bool TryForwardOrBecomePrimary(string[] args, Action<string[]> onCommand)
    {
        _onCommand = onCommand;

        _mutex = new Mutex(false, MutexName, out _);
        bool acquired = false;
        try
        {
            // 非阻塞获取；若失败说明有活的主实例正在运行。
            acquired = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例崩溃/被杀而遗留的锁：视为我们获得主实例身份。
            acquired = true;
        }

        if (!acquired)
        {
            // 有活的主实例：转发参数后返回 true，让调用方退出（调用方会 Exit）。
            ForwardToPrimary(args);
            return true;
        }

        // 我们是主实例：后台监听命名管道
        _ = Task.Run(ListenLoop);
        return false;
    }

    private static void ForwardToPrimary(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // 用 ConnectAsync + 取消令牌提供【可靠】的超时（Connect(TimeSpan) 有时不会按时超时，
            // 会让新实例卡死在转发上，导致“从开始菜单无法打开主程序”）。
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            client.ConnectAsync(cts.Token).GetAwaiter().GetResult();

            string payload = string.Join('\u001f', args); // 用分隔符拼接
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch
        {
            // 转发失败：忽略（主实例会自行读取命令行）
        }
    }

    private static async Task ListenLoop()
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? payload = await reader.ReadToEndAsync();
                if (string.IsNullOrEmpty(payload)) continue;

                string[] args = payload.Split('\u001f');
                _onCommand?.Invoke(args);
            }
            catch
            {
                // 监听错误后短暂等待再重试
                try { await Task.Delay(300); } catch { }
            }
        }
    }
}
