# HN 命令速查（Hacknet 终端指令规范）

> 来源：Hacknet 5.069 源码（`ProgramList.cs` / `Helpfile.cs` / `ProgramRunner.cs`）+ Pathfinder 自定义命令。
> 用途：AI 执行 `execute_command` 时的正确用法参考。HN 指令与 Unix 相似但**存在关键差异**，先读本表再动手。

## 一、内置命令（23 + 补充）

| 命令 | 语法 | 说明 | 与 Unix 的差异/注意 |
|---|---|---|---|
| `ls` | `ls` | 列出当前目录文件 | 无参数 |
| `cd` | `cd [文件夹]` | 切换目录 | 无参数时**不回到 home**（保持当前） |
| `probe` | `probe` | 扫描当前连接机器的**开放端口和安防等级** | 连接后必做；Unix 无对应 |
| `scan` | `scan` | 扫描当前连接机器的**链接**并加入地图 | 连接后必做 |
| `ps` | `ps` | 列出正在运行的进程和 PID | 类似 Unix |
| `kill` | `kill [PID]` | 杀死指定 PID 进程 | 类似 Unix |
| `connect` | `connect [ip]` | 连接外部电脑 | 需先 scan/probe 知道 IP |
| `dc` / `disconnect` | `disconnect` | 断开当前连接 | 别名 dc |
| `cat` | `cat [文件名]` | 显示文件内容 | 类似 Unix |
| `scp` | `scp [文件名] [可选:目标]` | **从远程机器下载文件到本地**（默认 /bin） | ⚠️ **方向与 Unix scp 相反**：HN 的 scp 是从远程拷回本地，不是推上去 |
| `upload` | `upload [本地文件路径]` | 把本地文件**上传到当前连接目录** | 与 scp 相反方向；Unix 用 scp 做 |
| `mv` | `mv [文件] [目标]` | 移动/重命名 | 例：`mv hi.txt ../bin/hi.txt` |
| `rm` | `rm [文件名]`（`*` 通配全部） | 删除文件 | 支持 `rm *` 清空目录（⚠️ 危险，无确认） |
| `replace` | `replace [文件名] "目标" "替换"` | 替换文件中的文本 | Unix 无直接对应 |
| `append` | `append [文件名] [数据]` | 追加一行数据到文件 | Unix 用 `>>` |
| `exe` | `exe` | **列出 /bin 下所有可用程序**（含隐藏/内嵌） | ⚠️ 不是"执行"！执行程序用 `porthack.exe` 等 |
| `analyze` | `analyze` | 分析目标机器防火墙 | 防火墙破解前置 |
| `solve` | `solve [防火墙方案]` | 尝试解开防火墙（允许 UDP 流量） | 需要 analyze 后得到方案 |
| `login` | `login` | 交互式输入用户名密码登录 | 交互式：AI 需结合 get_computer 的 users 信息 |
| `reboot` | `reboot [可选:-i]` | 重启当前连接电脑；`-i` 立即重启 | Unix 无对应；可用作规避管理员检测 |
| `openCDTray` / `closeCDTray` | `openCDTray` | 打开/关闭光驱托盘（物理攻击入口） | Hacknet 特有 |
| `addNote` | `addNote [备注]` | 添加一条备忘 | Hacknet 特有 |
| `help` | `help [页码]` | 分页显示命令列表 | 游戏内权威 help |
| `clear` | `clear` | 清空终端 | 类似 Unix |

## 二、内置可执行程序（在 /bin 下，运行需带 .exe）

| 程序 | 用途 | 注意 |
|---|---|---|
| `porthack.exe` | 破解开放端口 | 目标需已有开放端口（`probe` 确认）；交互式选择端口 |
| `forkbomb.exe` | 叉爆：快速消耗目标内存 | 交互式选进程；可致目标重启/崩溃 |
| `shell.exe` | 远程 shell（含代理过载与 IP 陷阱能力） | 交互式远程操作 |
| `securitytracer.exe` | 安全追踪器 | 查看管理员追踪状态 |
| `tutorial.exe` | 教程 | 新手用 |
| `notes.exe` | 便签 | 类似 addNote |

## 三、Pathfinder 自定义命令（registry 可查）

| 命令 | 用途 |
|---|---|
| `loadmission` | 加载任务（Pathfinder 调试） |
| `loadactions` | 加载动作列表 |
| `dscan` | 调试扫描 |

（KE 等模组可注册更多自定义命令，`registry` 的 commands 字段为准。）

## 四、HN vs Unix 关键差异速记（AI 必读）

1. **`scp` 方向相反**：HN 的 `scp 文件名` 是**从远程下载到本地**；上传用 `upload`。用 Unix 直觉会做反。
2. **`exe` 是列表不是执行**：看 /bin 有什么用 `exe`；运行程序要写 `xxx.exe`。
3. **连接三连**：`connect ip` → `probe`（端口/安防）→ `scan`（链接），缺一不可。
4. **防火墙流程**：`analyze` → 得到方案 → `solve [方案]`，顺序错会失败。
5. **无确认删除**：`rm *` 直接清空，没有确认提示。
6. **`cd` 无参不回 home**。
7. **`reboot -i`** 立即重启目标（可打断其操作）。
8. **交互式命令**（`login`/`porthack`/`forkbomb`/`shell`）需要多轮交互——AI 执行时要结合 `get_computer`/`get_state` 信息提前准备输入，或用非交互替代手段。

## 五、对 AI 的操作建议

- 先 `get_state` 看当前连接，再 `probe`+`scan` 收集目标信息，最后选命令
- 写文件用 `write_file`/`append_file` 工具（比终端命令可靠）；读文件用 `read_file`
- 需要账号密码登录时，从 `get_computer` 的 users/密码记录里找
- 危险命令（`rm *`、`forkbomb`、`reboot -i`）执行前想清楚影响
