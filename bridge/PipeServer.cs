using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace HnpfMcpBridge;

/// <summary>
/// NamedPipe 服务端。仅负责收发（JSON 行协议），不触碰游戏状态。
/// 收到的请求进入 _inbox，由主线程泵消费；响应从 _outbox 写回。
/// </summary>
public class PipeServer : IDisposable
{
    public const string DefaultPipeName = "hnpf-mcp-bridge";

    private readonly ConcurrentQueue<RpcRequest> _inbox = new();
    private readonly ConcurrentQueue<string> _outbox = new();
    private readonly ManualResetEventSlim _outboxSignal = new(false);

    private string _pipeName;
    private readonly string _token;
    private NamedPipeServerStream _pipe;
    private CancellationTokenSource _cts;
    private bool _fallbackApplied;
    private volatile string _effectiveName;

    public PipeServer(string pipeName, string token)
    {
        _pipeName = pipeName;
        _token = token;
        _effectiveName = pipeName;
    }

    /// <summary>当前实际监听的管道名（默认名被占用自动改 {name}-{pid} 后返回新名）。</summary>
    public string EffectivePipeName => _effectiveName;

    public bool TryDequeueRequest(out RpcRequest req) => _inbox.TryDequeue(out req);

    public void PushResponse(string jsonLine) { _outbox.Enqueue(jsonLine); _outboxSignal.Set(); }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        // 接收线程（长驻）
        Task.Run(ListenLoop);
        // 发送线程（长驻）
        Task.Run(SendLoop);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _pipe?.Dispose(); } catch { }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                HnpfMcpBridgePlugin.LogInfo($"Pipe listening: \\\\.\\pipe\\{_pipeName}");
                await _pipe.WaitForConnectionAsync(_cts.Token);

                using var reader = new StreamReader(_pipe, Encoding.UTF8);
                string line;
                while (!_cts.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    try
                    {
                        var req = TryParse(line);
                        if (req == null) continue;             // 无效消息，忽略
                        if (!Authenticate(req)) return;        // token 不符，断开
                        _inbox.Enqueue(req);
                    }
                    catch (Exception e)
                    {
                        HnpfMcpBridgePlugin.LogWarn($"Bad request line: {e.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // B3 多开：默认管道被占用（已有实例在监听）时，自动改用 {pipeName}-{pid}，
                // 避免无限刷屏重试；server 可用 HNPF_PIPE 指定或读 ping 的 pipe 字段连接
                if (!_fallbackApplied)
                {
                    _fallbackApplied = true;
                    var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                    var old = _pipeName;
                    _pipeName = _pipeName + "-" + pid;
                    _effectiveName = _pipeName;
                    HnpfMcpBridgePlugin.LogWarn($"pipe '{old}' 被占用（可能已有实例），自动改用 {_pipeName}（多开模式）");
                }
                else
                {
                    HnpfMcpBridgePlugin.LogWarn($"Pipe listen error: {e.Message}; re-listen in 1s");
                }
                try { await Task.Delay(1000, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task SendLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            _outboxSignal.Wait(_cts.Token);
            _outboxSignal.Reset();
            while (_outbox.TryDequeue(out var line))
            {
                try
                {
                    if (_pipe != null && _pipe.IsConnected)
                    {
                        var bytes = Encoding.UTF8.GetBytes(line + "\n");
                        await _pipe.WriteAsync(bytes, 0, bytes.Length, _cts.Token);
                        await _pipe.FlushAsync(_cts.Token);
                    }
                }
                catch { /* 连接断开则丢弃剩余 */ }
            }
        }
    }

    private RpcRequest TryParse(string line)
    {
        var obj = JsonRpc.DeserializeObj(line);
        if (obj == null || !obj.ContainsKey("method")) return null;

        var req = new RpcRequest
        {
            Method = obj["method"]?.ToString(),
            Params = obj.ContainsKey("params") && obj["params"] is Dictionary<string, object> p ? p : new Dictionary<string, object>()
        };
        if (obj.ContainsKey("id"))
            req.Id = Convert.ToInt64(obj["id"]);
        return req;
    }

    /// <summary>握手：配置了 token 时，首个请求必须携带正确 token。</summary>
    private bool Authenticate(RpcRequest req)
    {
        if (string.IsNullOrEmpty(_token)) return true;
        if (req.Method == "auth" && req.Params.TryGetValue("token", out var t) && t?.ToString() == _token)
            return true;
        PushResponse(JsonRpc.Serialize(new Dictionary<string, object>
        {
            ["id"] = req.Id ?? 0,
            ["error"] = new Dictionary<string, object> { ["code"] = 401, ["message"] = "unauthorized" }
        }));
        return false;
    }

    public void Dispose() => Stop();
}
