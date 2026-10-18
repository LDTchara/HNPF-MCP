// token 握手回归：fake-bridge 返回 authenticated:true；server 带 HNPF_TOKEN 连接后应能正常调用
import { spawn } from "node:child_process";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, "..");

const fake = spawn(process.execPath, [path.join(__dirname, "fake-bridge.mjs")], { stdio: "inherit" });
await new Promise((r) => setTimeout(r, 800));

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(root, "src", "index.js")],
  env: { ...process.env, HNPF_PIPE: "\\\\.\\pipe\\hnpf-mcp-bridge-test", HNPF_TOKEN: "test-token-123" },
});
const client = new Client({ name: "hnpf-auth", version: "0.0.1" });
try {
  await client.connect(transport);
  const r = await client.callTool({ name: "ping", arguments: {} });
  const txt = r.content?.[0]?.text ?? "";
  const j = JSON.parse(txt);
  if (j.pong === true) console.log("[ok] token 握手通过，ping 正常:", j.version);
  else { console.log("[FAIL] ping 结果异常:", txt.slice(0, 120)); process.exitCode = 1; }
} finally {
  await client.close();
  fake.kill();
}
