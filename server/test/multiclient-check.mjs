// 多客户端并发回归：桥现在支持多条连接（默认最多 8），两个 MCP server 同时连同一游戏应各自正常。
// 用法：游戏运行中执行 node test/multiclient-check.mjs
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, "..");

async function makeClient(name) {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [path.join(root, "src", "index.js")],
  });
  const client = new Client({ name, version: "0.0.1" });
  await client.connect(transport);
  return client;
}

const [a, b] = await Promise.all([makeClient("hnpf-mc-a"), makeClient("hnpf-mc-b")]);
try {
  const results = await Promise.all([a, b].map(async (c, i) => {
    const ping = JSON.parse((await c.callTool({ name: "ping", arguments: {} })).content[0].text);
    const st = JSON.parse((await c.callTool({ name: "get_state", arguments: {} })).content[0].text);
    return { client: i + 1, pong: ping.pong, build: ping.build, ramTotal: st.ramTotal, connectedIP: st.connectedIP };
  }));
  console.log("[multiclient] 结果:", JSON.stringify(results, null, 2));
  const ok = results.every((r) => r.pong === true && r.ramTotal > 0);
  console.log(ok ? "[ok] 多客户端并发正常（两条连接同时在线）" : "[FAIL] 多客户端结果异常");
  if (!ok) process.exitCode = 1;
} finally {
  await a.close();
  await b.close();
}
