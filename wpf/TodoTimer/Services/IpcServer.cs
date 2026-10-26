using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace TodoTimer.Services;

/// <summary>
/// IPC 服务（127.0.0.1:58372）：GUI 运行时监听，CLI 写盘成功后发 "reload"，
/// GUI 重读 todos.json 实时上屏。协议为行式 JSON-lite（两端同为本项目代码）。
/// </summary>
public sealed class IpcServer : IDisposable
{
    private TcpListener? _listener;
    private readonly Action _onReload;

    public IpcServer(Action onReload) => _onReload = onReload;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 58372);
        try
        {
            _listener.Start();
        }
        catch
        {
            _listener = null;      // 端口被占(残留实例)则放弃, 不影响 GUI 本体
            return;
        }
        var th = new Thread(AcceptLoop) { IsBackground = true };
        th.Start();
    }

    private void AcceptLoop()
    {
        while (_listener is not null)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            using var _ = client;
            try
            {
                using var reader = new StreamReader(client.GetStream());
                var line = reader.ReadLine();
                using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                if (line == "reload")
                {
                    Application.Current?.Dispatcher.Invoke(_onReload);
                    writer.WriteLine("ok");
                }
                else
                {
                    writer.WriteLine("unknown");
                }
            }
            catch { /* 单连接失败不影响服务 */ }
        }
    }

    /// <summary>CLI 侧：通知 GUI 重载数据。GUI 不可达时抛 InvalidOperationException。</summary>
    public static void NotifyReload()
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, 58372);
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine("reload");
            using var reader = new StreamReader(client.GetStream());
            if (reader.ReadLine() != "ok")
                throw new InvalidOperationException("ipc-bad-reply");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("ipc-unreachable", ex);
        }
    }

    public void Dispose() => _listener?.Stop();
}
