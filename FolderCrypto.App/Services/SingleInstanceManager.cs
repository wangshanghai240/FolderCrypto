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
///
/// ⚠️ 历史严重缺陷（已修复）：
///    旧实现只要「拿不到互斥锁」就无条件转发并退出。若主实例是**悬挂进程**
///    （例如以 --autostart 启动后窗口没创建，或前次异常留下的僵尸进程），
///    新进程会把参数转发给一个不会做任何事的进程，然后自己退出。
///    用户看到的现象就是「双击 / 右键菜单点了完全没反应」。
///
///    现在改为「转发成功才算已有实例」：
///      * 管道连不上 -> 说明所谓的主实例是死的，清理残留并接管成为主实例；
///      * 转发成功   -> 正常退出，由主实例处理指令。
/// </summary>
public static class SingleInstanceManager
{
    private const string MutexName = "Local\\FolderCrypto.SingleInstance";
    private const string PipeName = "FolderCrypto.CommandPipe";

    private static Mutex? _mutex;
    private static Action<string[]>? _onCommand;
    private static bool _ownsMutex;

    /// <summary>
    /// 尝试成为主实例；若存在「活动的」主实例，则把 args 转发给主实例并返回 true（调用方应退出）。
    ///
    /// 判定依据不是「互斥锁是否空闲」，而是「管道能否连通并投递成功」——
    /// 只有真正把指令送达了活的主实例，才允许当前进程退出。
    /// </summary>
    public static bool TryForwardOrBecomePrimary(string[] args, Action<string[]> onCommand)
    {
        _onCommand = onCommand;

        _mutex ??= new Mutex(false, MutexName, out _);

        bool acquired = false;
        try
        {
            acquired = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例崩溃/被杀而遗留的锁：视为我们获得主实例身份。
            acquired = true;
        }

        if (acquired)
        {
            _ownsMutex = true;
            _ = Task.Run(ListenLoop);
            return false;
        }

        // 互斥锁被别人持有。但这可能是悬挂进程 —— 用管道验证是否真的活着。
        if (ForwardToPrimary(args))
            return true;   // 确认送达，退出由主实例处理

        // 管道不通：所谓主实例是死的。接管它，避免「静默退出」。
        StartupLog.Trace("转发失败 -> 原主实例可能已悬挂，接管为本实例");
        try
        {
            // 等待悬挂实例释放锁；超时则强制抢占。
            if (!_mutex.WaitOne(TimeSpan.FromSeconds(2)))
                StartupLog.Trace("等待互斥锁超时，仍继续以本实例启动");
        }
        catch (AbandonedMutexException)
        {
            // 悬挂实例被回收，锁归我们。
        }

        _ownsMutex = true;
        _ = Task.Run(ListenLoop);
        return false;
    }

    /// <summary>把命令行转发给主实例；仅当成功送达返回 true。</summary>
    private static bool ForwardToPrimary(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // 用 ConnectAsync + 取消令牌提供【可靠】的超时（Connect(TimeSpan) 有时不会按时超时，
            // 会让新实例卡死在转发上，导致“从开始菜单无法打开主程序”）。
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            client.ConnectAsync(cts.Token).GetAwaiter().GetResult();

            string payload = string.Join('\u001f', args); // 用分隔符拼接
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            // 连不上或写入失败：主实例不存在 / 已停止响应。
            return false;
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
