// P1 新方法真机验证（含无害写操作：terminal 写一行、对玩家本机开/关一个端口）
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(__dirname, "..", "src", "index.js")],
});

const client = new Client({ name: "hnpf-p1", version: "0.0.1" });
await client.connect(transport);

async function show(name, args = {}) {
  try {
    const r = await client.callTool({ name, arguments: args });
    const text = r.content?.[0]?.text ?? JSON.stringify(r);
    console.log(`\n=== ${name} ===`);
    console.log(text.length > 600 ? text.slice(0, 600) + "\n...(截断)" : text);
  } catch (e) {
    console.log(`\n=== ${name} === ERROR: ${e.message}`);
  }
}

console.log("—— 新方法在线检查 ——");
await show("terminal_history", { lines: 10 });     // P1 新方法，此前 unknown method

console.log("\n—— run_action（游戏终端写一行字，无害）——");
await show("run_action", { xml: "<TerminalWrite Content='[mcp-p1-test] hello from MCP'/>" });
await show("terminal_history", { lines: 5 });

console.log("\n—— 端口操作（对玩家本机 25 号，开→查→关）——");
await show("open_port", { port: 25 });             // 缺省 ip = 当前连接（玩家本机）
await show("get_computer", {});                    // 看 portsOpen
await show("close_port", { port: 25 });

await client.close();
console.log("\n[p1-check] 完成");
