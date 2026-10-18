import net from "node:net";
import { EventEmitter } from "node:events";

const DEFAULT_PIPE = "\\\\.\\pipe\\hnpf-mcp-bridge";

/**
 * NamedPipe 客户端：连接游戏进程内的 bridge。
 * 协议：每行一条 JSON（请求/响应按 id 匹配，事件通过 'event' 事件发出）。
 */
export class BridgeClient extends EventEmitter {
  constructor({ pipeName = DEFAULT_PIPE, token = "", timeoutMs = 8000, connectTimeoutMs = 3000 } = {}) {
    super();
    this.pipeName = pipeName;
    this.token = token;
    this.timeoutMs = timeoutMs;
    this.connectTimeoutMs = connectTimeoutMs;
    this.socket = null;
    this.buffer = "";
    this.nextId = 1;
    this.pending = new Map();
    this._connectPromise = null;
  }

  get connected() {
    return this.socket !== null && !this.socket.destroyed;
  }

  connect() {
    // 并发调用去重：多个工具同时触发 ensureBridge 时只建一条连接
    if (this._connectPromise) return this._connectPromise;
    this._connectPromise = new Promise((resolve, reject) => {
      const sock = net.connect({ path: this.pipeName });
      this.socket = sock;
      sock.setNoDelay(true);

      // 连接超时：管道半开/不存在时 net.connect 本身可能一直不返回
      const connectTimer = setTimeout(() => {
        sock.destroy();
        this.socket = null;
        reject(new Error(`bridge connect timeout (${this.connectTimeoutMs}ms, pipe=${this.pipeName})`));
      }, this.connectTimeoutMs);

      sock.on("connect", async () => {
        clearTimeout(connectTimer);
        console.error(`[hnpf] bridge connected (pipe=${this.pipeName})`);
        if (this.token) {
          // token 鉴权：bridge 对每条新连接要求先 auth；必须等它回包，否则后续请求会被 401
          try {
            const r = await this._send({ id: 0, method: "auth", params: { token: this.token } });
            if (!r || r.authenticated !== true) throw new Error("unexpected auth reply");
            console.error("[hnpf] bridge auth ok");
          } catch (e) {
            sock.destroy();
            this.socket = null;
            reject(new Error(`bridge auth failed: ${e.message}（检查 HNPF_TOKEN 与 BepInEx/config/com.HnpfMcp.Bridge.cfg 的 Pipe.Token 是否一致）`));
            return;
          }
        }
        resolve();
      });
      sock.on("error", (err) => {
        clearTimeout(connectTimer);
        this.socket = null;
        console.error(`[hnpf] bridge error: ${err.message}`);
        reject(err);
      });
      sock.on("close", () => {
        clearTimeout(connectTimer);
        this.socket = null;
        this.buffer = "";
        for (const [, waiter] of this.pending) waiter.reject(new Error("bridge pipe closed"));
        this.pending.clear();
        console.error("[hnpf] bridge disconnected — 下次调用自动重连。若游戏开了鉴权（自动生成 Token），请设置 HNPF_TOKEN 与游戏 cfg 一致");
      });
      sock.on("data", (chunk) => this._onData(chunk.toString()));
    });
    // 连接结束（成功或失败）后清掉缓存，允许下次重连
    this._connectPromise = this._connectPromise.finally(() => { this._connectPromise = null; });
    return this._connectPromise;
  }

  close() {
    this.socket?.destroy();
    this.socket = null;
    this.buffer = "";
    this._connectPromise = null;
  }

  /** 发送请求并等待对应 id 的响应。timeoutMs 可覆盖实例默认（如等主菜单进扩展要很久） */
  call(method, params = {}, timeoutMs = this.timeoutMs) {
    if (!this.connected) throw new Error("game bridge not connected (is Hacknet running?)");
    const id = this.nextId++;
    return this._send({ id, method, params }, timeoutMs);
  }

  _send(msg, timeoutMs = this.timeoutMs) {
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(msg.id);
        reject(new Error(`bridge timeout waiting for '${msg.method}'`));
      }, timeoutMs);
      this.pending.set(msg.id, { resolve, reject, timeout });
      this.socket.write(JSON.stringify(msg) + "\n", (err) => {
        if (err) {
          clearTimeout(timeout);
          this.pending.delete(msg.id);
          reject(err);
        }
      });
    });
  }

  _onData(text) {
    this.buffer += text;
    let nl;
    while ((nl = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, nl).trim();
      this.buffer = this.buffer.slice(nl + 1);
      if (!line) continue;
      try {
        const msg = JSON.parse(line);
        if (msg.event) {
          this.emit?.("event", msg);
          continue;
        }
        const waiter = this.pending.get(msg.id);
        if (waiter) {
          clearTimeout(waiter.timeout);
          this.pending.delete(msg.id);
          if (msg.error) waiter.reject(new Error(`${msg.error.code}: ${msg.error.message}`));
          else waiter.resolve(msg.result);
        }
      } catch { /* 忽略坏行 */ }
    }
  }
}
