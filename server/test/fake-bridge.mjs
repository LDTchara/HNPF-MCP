// 模拟游戏侧 bridge 的 NamedPipe 服务，用于端到端验证（游戏未运行时的完整链路测试）
import net from "node:net";

const PIPE = "\\\\.\\pipe\\hnpf-mcp-bridge-test";

const server = net.createServer((sock) => {
  sock.on("data", (buf) => {
    for (const line of buf.toString().split("\n")) {
      if (!line.trim()) continue;
      const msg = JSON.parse(line);
      const result = fakeResult(msg.method, msg.params);
      sock.write(JSON.stringify({ id: msg.id, result }) + "\n");
    }
  });
});

function fakeResult(method, params = {}) {
  switch (method) {
    case "ping":
      return { pong: true, version: "0.1.0-fake", os: "hacknet+pathfinder" };
    case "state.get":
      return {
        connectedIP: "10.0.0.2", connectedComp: "10.0.0.2", connectedName: "TargetServer",
        homeNodeID: "entropy00", navigationPath: [0, 3], thisComputerIP: "10.0.0.1",
        ramFree: 42, ramTotal: 200, flags: ["PhaseSwift_Demo"], hasMission: true,
        missionTitle: "Fake Mission", terminalText: "> ", admin: false,
      };
    case "network.map":
      return { count: 3, nodes: [
        { ip: "10.0.0.1", idName: "home", name: "Home", links: [1], ports: [], portsOpen: [] },
        { ip: "10.0.0.2", idName: "target", name: "TargetServer", links: [0, 2], ports: [22, 80], portsOpen: [22] },
        { ip: "10.0.0.3", idName: "mail", name: "MailServer", links: [1], ports: [25], portsOpen: [25] },
      ]};
    case "computer.get":
      return { ip: params.ip, idName: "target", name: "TargetServer", ports: [22, 80], portsOpen: [22],
               currentUser: "root", users: [{ name: "root", pass: "toor", type: 2 }], daemons: [{ name: "SSH" }],
               files: [{ type: "folder", name: "home", children: [{ type: "file", name: "passwd", size: 512 }] }] };
    case "fs.list":
      return { ip: params.ip, path: params.path, entries: [{ type: "file", name: "passwd", size: 512 }] };
    case "fs.read":
      return { name: params.file, size: 512, data: "root:x:0:0::/root:/bin/sh" };
    case "game.execute_command":
      return { ok: true, cmd: params.cmd, note: "submitted; run terminal.history to read output" };
    case "game.run_hack_script":
      return { ok: true, script: params.script, note: "submitted" };
    case "mail.list":
      return { ip: params.ip, users: [{ user: "root", mailboxes: [{ name: "inbox", mails: [{ subject: "Mission Brief", size: 512 }] }] }] };
    case "irc.read":
      return { ip: params.ip || "cur", count: 2, messages: [{ name: "admin", message: "server down soon", time: "12:00" }, { name: "user1", message: "ok", time: "12:01" }] };
    case "board.read":
      return { ip: params.ip || "cur", board: "/el/", threadCount: 1, threads: [{ name: "thread1.txt", content: "id=1\nadmin: hi" }] };
    case "mail.read":
      return { user: params.user, mailbox: "inbox", subject: params.subject, sender: "admin@corp", body: "Meet me at the server.", rawSize: 512 };
    case "save.list":
      return {
        note: "load_game is not supported at runtime",
        saveRoot: "D:/Documents/My Games/HacknetPathfinder/Accounts",
        currentUser: "user1",
        currentExtension: "TestExt",
        saves: [
          { name: "save_user1.xml", path: "C:/fake/root/save_user1.xml" },
          { name: "save_other.xml", path: "C:/fake/root/save_other.xml" },
          { name: "save_user1.xml", path: "C:/fake/ext/save_user1.xml", extension: "TestExt", current: true },
          { name: "save_user1.xml", path: "C:/fake/ext2/save_user1.xml", extension: "OtherExt" },
        ],
      };
    case "port.open":
      return { ok: true, ip: params.ip, port: params.port, state: "open" };
    case "port.close":
      return { ok: true, ip: params.ip, port: params.port, state: "closed" };
    case "admin.take":
      return { ok: true, admin: params.ip ?? "10.0.0.2" };
    case "game.launch_exe":
      return { ok: true, exe: params.exeName, cmd: params.exeName + (params.args ? " " + params.args : ""), note: "submitted" };
    case "game.run_action":
      return { ok: true, action: "PlaySound" };
    case "terminal.history":
      return { lines: ["> connect 10.0.0.2", "Connected to 10.0.0.2"], count: 2 };
    case "mission.detail":
      return { active: true, title: "Test Mission", goalCount: 2, goalComplete: 1, goals: [{ type: "ConnectGoal", complete: true }, { type: "ActionGoal", complete: false }], xmlPath: "Content/Missions/TestMission.xml", xml: "<mission><goal type=\"ConnectGoal\"/></mission>" };
    case "menu.enter_extension":
      return { ok: true, mode: "new account", username: params.username || "mcp" };
    case "menu.load_extension_save":
      return { ok: true, mode: "load save", username: params.username, userFile: params.userFile };
    case "events.get":
      return { events: [{ id: 1, event: "node.connected", data: { ip: "10.0.0.2" }, t: "12:00:00" }], nextId: 2 };
    case "registry.list":
      return { commands: ["mcp"], actions: ["PlaySound", "TerminalWrite", "PhaseSwiftScene"], executables: ["#CUSTOMTRIAL#"], daemons: ["FlightDaemon"] };
    case "flags.get":
      return { flags: ["PhaseSwift_Demo", "Kernel_VMInfected_A"] };
    default:
      return { ok: true, method };
  }
}

server.listen(PIPE, () => {
  console.log("[fake-bridge] listening on", PIPE);
});
process.on("SIGINT", () => { server.close(); process.exit(0); });
