// 冒烟测试：验证 MCP 协议层可用（游戏可不运行，此时调用应返回友好离线错误）
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const serverEntry = path.join(__dirname, "..", "src", "index.js");

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [serverEntry],
});

const client = new Client({ name: "hnpf-smoke", version: "0.0.1" });
await client.connect(transport);
console.log("[ok] MCP 握手成功 (initialize)");

const tools = await client.listTools();
console.log(`[ok] tools/list 返回 ${tools.tools.length} 个工具:`);
for (const t of tools.tools) {
  console.log(`     - ${t.name} :: ${(t.description ?? "").slice(0, 48)}`);
}

// 游戏未启动：get_state 应被 server 捕获并给出友好错误
try {
  const r = await client.callTool({ name: "get_state", arguments: {} });
  console.log("[?] get_state 意外成功:", JSON.stringify(r).slice(0, 120));
} catch (err) {
  console.log(`[ok] get_state 离线处理正常: ${err.message.slice(0, 70)}...`);
}

const res = await client.listResources();
console.log(`[ok] resources/list 返回 ${res.resources?.length ?? 0} 个资源`);

await client.close();
console.log("[done] 冒烟测试通过");
