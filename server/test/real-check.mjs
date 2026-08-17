// 真机验证：连默认管道（游戏真实运行中的 bridge），只读操作
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(__dirname, "..", "src", "index.js")],
});

const client = new Client({ name: "hnpf-real", version: "0.0.1" });
await client.connect(transport);

async function show(name, args = {}) {
  try {
    const r = await client.callTool({ name, arguments: args });
    const text = r.content?.[0]?.text ?? JSON.stringify(r);
    console.log(`\n=== ${name} ===`);
    console.log(text.length > 900 ? text.slice(0, 900) + "\n...(截断)" : text);
  } catch (e) {
    console.log(`\n=== ${name} === ERROR: ${e.message}`);
  }
}

await show("ping");
await show("get_state");
await show("get_network_map");
await show("get_computer", {});          // 当前连接节点
await show("terminal_history", { lines: 8 });
await show("get_flags");
await show("get_mission");

await client.close();
console.log("\n[real-check] 完成");
