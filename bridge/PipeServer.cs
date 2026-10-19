using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace HnpfMcpBridge;

/// <summary>
/// NamedPipe 服务：每行一条 JSON 请求/响应；支持多客户端并发（每连接独立鉴权与发送队列），
/// 游戏状态读写仍统一由 Executor / MenuExecutor 在主线程消费。
///
/// 管道安全：ACL 限制为当前用户 + SYSTEM（默认 DACL 允许任意本机进程连接）。
/// </summary>
public sealed class PipeServer
{
    public const string DefaultPipeName = "hnpf-mcp-bridge";
    private const int MaxClients = 8;

    private readonly string _baseName;
    private readonly string _token;
    private string _pipeName;
    private CancellationTokenSource _cts;
    private bool _fallbackApplied;

    private readonly ConcurrentDictionary<Client, byte> _clients = new();
    private readonly ConcurrentQueue<QueuedRequest> _inbox = new();

    public PipeServer(string pipeName, string token)
    {
        _baseName = pipeName;
        _pipeName = pipeName;
        _token = token ?? "";
    }

    public string EffectivePipeName => _pipeName;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        foreach (var c in _clients.Keys) { try { c.Close(); } catch { } }
        _clients.Clear();
        _cts = null;
    }

    // ---------------- 主线程泵接口 ----------------

    public bool TryDequeueRequest(out QueuedRequest request) => _inbox.TryDequeue(out request);

    /// <summary>把响应推回发请求的那个客户端。</summary>
    public void PushResponse(string line, Client client) => client?.Send(line);

    /// <summary>广播事件给所有已连接客户端。</summary>
    public void Broadcast(string line)
    {
        foreach (var c in _clients.Keys) { try { c.Send(line); } catch { } }
    }

    // ---------------- 监听循环 ----------------

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct);
                var client = new Client(this, pipe, _token);
                _clients.TryAdd(client, 0);
                _ = Task.Run(() => client.RunAsync(ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                try { pipe?.Dispose(); } catch { }
                if (_clients.IsEmpty && !_fallbackApplied && IsNameConflict(e))
                {
                    _fallbackApplied = true;
                    var pid = Process.GetCurrentProcess().Id;
                    var old = _pipeName;
                    _pipeName = _baseName + "-" + pid;
                    HnpfMcpBridgePlugin.LogWarn($"pipe '{old}' unavailable (likely in use); switching to '{_pipeName}' (multi-instance mode)");
                }
                else
                {
                    HnpfMcpBridgePlugin.LogWarn($"Pipe listen error: {e.Message}; retry in 1s");
                }
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    /// <summary>只有"名字被占用"才触发自动改名（资源耗尽等异常不改名，避免把合法名字改坏）。</summary>
    private static bool IsNameConflict(Exception e)
    {
        if (e is not IOException) return false;
        var msg = e.Message ?? "";
        // .NET Framework 4.7.2 的 string.Contains 没有 StringComparison 重载
        return msg.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0
            || msg.IndexOf("占用", StringComparison.Ordinal) >= 0
            || msg.IndexOf("in use", StringComparison.OrdinalIgnoreCase) >= 0
            || msg.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = TryBuildPipeSecurity();
        if (security != null)
        {
            try
            {
                return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxClients,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
            }
            catch (Exception e)
            {
                HnpfMcpBridgePlugin.LogWarn($"pipe ACL apply failed; falling back to default security: {e.Message}");
            }
        }
        return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxClients,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
    }

    /// <summary>管道 ACL：仅当前用户 + SYSTEM 可连接（默认 NamedPipe DACL 允许任意本机进程）。</summary>
    private static PipeSecurity TryBuildPipeSecurity()
    {
        try
        {
            var security = new PipeSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user == null) return null;
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }
        catch (Exception e)
        {
            HnpfMcpBridgePlugin.LogWarn($"cannot build pipe ACL (default security will be used): {e.Message}");
            return null;
        }
    }

    // ---------------- 请求 / 连接 ----------------

    /// <summary>一次请求（带来源客户端，用于把响应发回正确连接）。</summary>
    public sealed class QueuedRequest
    {
        public RpcRequest Request;
        public Client Client;
    }

    /// <summary>单个客户端连接：独立鉴权状态 + 独立发送队列。</summary>
    public sealed class Client
    {
        private readonly PipeServer _owner;
        private readonly NamedPipeServerStream _pipe;
        private readonly string _token;
        private readonly ConcurrentQueue<string> _outbox = new();
        private readonly SemaphoreSlim _signal = new(0);
        private bool _authenticated;
        private int _closed;

        public Client(PipeServer owner, NamedPipeServerStream pipe, string token)
        {
            _owner = owner;
            _pipe = pipe;
            _token = token ?? "";
        }

        public async Task RunAsync(CancellationToken ct)
        {
            _ = Task.Run(() => SendLoopAsync(ct), ct);
            try
            {
                using var reader = new StreamReader(_pipe, Encoding.UTF8);
                while (!ct.IsCancellationRequested)
                {
                    string line;
                    try { line = await reader.ReadLineAsync(); }
                    catch { break; }
                    if (line == null) break;
                    if (line.Trim().Length == 0) continue;

                    RpcRequest req;
                    try { req = ParseRequest(line); }
                    catch (Exception e)
                    {
                        HnpfMcpBridgePlugin.LogWarn($"Bad request line: {e.Message}");
                        Send(ErrorLine(0, 500, "bad request: " + e.Message));
                        continue;
                    }
                    if (req == null) continue;

                    // token 鉴权：每条连接首次请求必须是正确 auth；通过后本连接不再逐条校验
                    if (!string.IsNullOrEmpty(_token) && !_authenticated)
                    {
                        if (req.Method == "auth"
                            && req.Params.TryGetValue("token", out var t)
                            && t?.ToString() == _token)
                        {
                            _authenticated = true;
                            Send(ResultLine(req.Id ?? 0,
                                new Dictionary<string, object> { ["ok"] = true, ["authenticated"] = true }));
                            continue;
                        }
                        Send(ErrorLine(req.Id ?? 0, 401, "unauthorized"));
                        // 给发送线程一点时间把 401 冲出去，再断开（否则客户端只看到 pipe closed）
                        try { await Task.Delay(200); } catch { }
                        break;   // 断开本条连接，但监听循环继续
                    }
                    if (req.Method == "auth") continue;   // 已认证后的重复 auth 忽略
                    _owner._inbox.Enqueue(new QueuedRequest { Request = req, Client = this });
                }
            }
            catch (Exception e) { HnpfMcpBridgePlugin.LogWarn($"client error: {e.Message}"); }
            finally { Close(); }
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _signal.WaitAsync(ct);
                    while (_outbox.TryDequeue(out var line))
                    {
                        try
                        {
                            if (_pipe.IsConnected)
                            {
                                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                                await _pipe.WriteAsync(bytes, 0, bytes.Length, ct);
                                await _pipe.FlushAsync(ct);
                            }
                        }
                        catch { return; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        public void Send(string line)
        {
            if (_closed != 0 || line == null) return;
            _outbox.Enqueue(line);
            try { _signal.Release(); } catch (SemaphoreFullException) { } catch { }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _owner._clients.TryRemove(this, out _);
            try { _pipe.Dispose(); } catch { }
            try { _signal.Release(); } catch { }
        }

        private static RpcRequest ParseRequest(string line)
        {
            var obj = JsonRpc.DeserializeObj(line);
            if (obj == null || !obj.ContainsKey("method")) return null;
            var req = new RpcRequest
            {
                Method = obj["method"]?.ToString(),
                Params = obj.ContainsKey("params") && obj["params"] is Dictionary<string, object> p
                    ? p
                    : new Dictionary<string, object>()
            };
            if (obj.ContainsKey("id")) req.Id = Convert.ToInt64(obj["id"]);
            return req;
        }

        private static string ResultLine(long id, object result) =>
            JsonRpc.Serialize(new Dictionary<string, object> { ["id"] = id, ["result"] = result });

        private static string ErrorLine(long id, int code, string message) =>
            JsonRpc.Serialize(new Dictionary<string, object>
            {
                ["id"] = id,
                ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
            });
    }
}
