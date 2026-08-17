using System.Web.Script.Serialization;

namespace HnpfMcpBridge;

/// <summary>桥接层 JSON-RPC 消息模型。每行一条 JSON（换行分隔）。</summary>
public static class JsonRpc
{
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer
    {
        MaxJsonLength = int.MaxValue,
        RecursionLimit = 100
    };

    public static string Serialize(object o) => Json.Serialize(o);

    public static Dictionary<string, object> DeserializeObj(string line) =>
        (Dictionary<string, object>)Json.DeserializeObject(line);
}

/// <summary>来自 MCP 服务器（宿主侧）的请求。</summary>
public class RpcRequest
{
    public long? Id;
    public string Method;
    public Dictionary<string, object> Params = new();
}

/// <summary>回给 MCP 服务器的响应。</summary>
public class RpcResponse
{
    public long Id;
    public object Result;
    public Dictionary<string, object> Error;   // {code, message}

    public static RpcResponse Ok(RpcRequest req, object result) => new()
    {
        Id = req.Id ?? 0,
        Result = result
    };

    public static RpcResponse Fail(RpcRequest req, int code, string message) => new()
    {
        Id = req.Id ?? 0,
        Error = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
    };

    public string ToJsonLine() =>
        JsonRpc.Serialize(Error != null
            ? new Dictionary<string, object> { ["id"] = Id, ["error"] = Error }
            : new Dictionary<string, object> { ["id"] = Id, ["result"] = Result });
}

/// <summary>Bridge 主动推送的事件（游戏状态变化）。</summary>
public static class RpcEvent
{
    public static string ToJsonLine(string name, object data) =>
        JsonRpc.Serialize(new Dictionary<string, object> { ["event"] = name, ["data"] = data });
}
