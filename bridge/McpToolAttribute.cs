namespace HnpfMcpBridge;

/// <summary>
/// 第三层抽象：模组专属适配器特性（P3）。
///
/// 模组（如 KE）想暴露「独有内存态」给 AI 时，只需引用本程序集，
/// 给静态方法标 [McpTool]，bridge 在 OSLoaded 时反射自动装配：
///
///   [McpTool("ke.phaseswift.state", "PhaseSwift 运行时状态")]
///   public static string PhaseSwiftState(Dictionary&lt;string, object&gt; p)
///       => Json(PhaseSwiftManager.IsRunning ...);
///
/// 支持的方法签名：无参，或单个 Dictionary&lt;string, object&gt; 参数。
/// 返回值：string（原样返回）或 object（JsonRpc 序列化）。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }

    public McpToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }
}
