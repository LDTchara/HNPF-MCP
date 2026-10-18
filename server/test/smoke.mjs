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

// 游戏未启动：get_state 应被 server 捕获并给出友好错误。
// 注意：SDK 1.30 会把 handler 异常转成 { isError: true } 的**正常返回**，不会 throw。
const r = await client.callTool({ name: "get_state", arguments: {} });
const rText = r?.content?.[0]?.text ?? "";
if (r?.isError === true) {
  console.log(`[ok] get_state 离线处理正常 (isError=true): ${String(rText).slice(0, 70)}...`);
} else {
  console.log(`[FAIL] get_state 离线时未返回 isError: ${String(rText).slice(0, 120)}`);
  process.exitCode = 1;
}

const res = await client.listResources();
const uris = (res.resources ?? []).map((x) => x.uri);
console.log(`[ok] resources/list 返回 ${uris.length} 个资源: ${uris.join(", ")}`);
if (!uris.includes("hnpf://state") || !uris.includes("hnpf://network")) {
  console.log("[FAIL] resources URI 不正确（期望 hnpf://state、hnpf://network）");
  process.exitCode = 1;
}

await client.close();
console.log("[done] 冒烟测试通过");
