// 真机 token 握手验证：带正确 HNPF_TOKEN 应成功；不带应被 401（且 bridge 继续监听）
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, "..");

async function run(token) {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [path.join(root, "src", "index.js")],
    env: { ...process.env, ...(token ? { HNPF_TOKEN: token } : {}) },
  });
  const client = new Client({ name: "hnpf-token-real", version: "0.0.1" });
  await client.connect(transport);
  try {
    // 主菜单阶段 ping 不可用（设计如此）；用 menu_enter_extension 验证"鉴权通过 + 请求真正被执行"
    const r = await client.callTool({
      name: "menu_enter_extension",
      arguments: { username: "mcpverify3", ext: "KernelExtensionTEST123123" },
    });
    return { ok: r.isError !== true, text: (r.content?.[0]?.text ?? "").slice(0, 160) };
  } catch (e) {
    return { ok: false, text: String(e.message).slice(0, 120) };
  } finally {
    await client.close();
  }
}
const good = await run("verify-token-2026");
const bad = await run("wrong-token");
console.log("[token-real] 正确 token:", JSON.stringify(good));
console.log("[token-real] 错误 token:", JSON.stringify(bad));
if (!good.ok) { console.log("[FAIL] 正确 token 未通过"); process.exitCode = 1; }
else if (bad.ok) { console.log("[FAIL] 错误 token 也通过了"); process.exitCode = 1; }
else { console.log("[ok] token 鉴权真机验证通过（正确通过 / 错误被拒）"); }
