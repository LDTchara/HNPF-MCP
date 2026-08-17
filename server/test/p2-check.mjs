// P2 真机验证：registry（应自动列出 KE 等模组的 22 个 Action）+ events 增量拉取
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(__dirname, "..", "src", "index.js")],
});

const client = new Client({ name: "hnpf-p2", version: "0.0.1" });
await client.connect(transport);

async function show(name, args = {}) {
  try {
    const r = await client.callTool({ name, arguments: args });
    const text = r.content?.[0]?.text ?? JSON.stringify(r);
    console.log(`\n=== ${name} ===`);
    console.log(text.length > 1500 ? text.slice(0, 1500) + "\n...(截断)" : text);
  } catch (e) {
    console.log(`\n=== ${name} === ERROR: ${e.message}`);
  }
}

await show("registry");
await show("get_events", { since: 0 });
await show("get_state");

await client.close();
console.log("\n[p2-check] 完成");
