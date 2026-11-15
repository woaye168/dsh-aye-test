# SCE 真实连服运行证据（2026-11-13）

本目录保存同一次从 Ticket worktree 发起的 SCE 连服调试证据。本次只补充文档并采集运行证据，没有修改游戏逻辑。

## 双端编译

- 16:37 通过 `agent/Invoke-WindowsBuild.ps1` 执行 Server-Debug 与 Client-Debug，退出码均为 0；结果见 `build-evidence.json`。
- Server 源 DLL 与 `AppBundle/managed/GameEntry.dll` 的 SHA-256 均为 `1A4E5CB2766545837AC59608869341019D019276144AA8B78DFA6BEF9C088740`。
- Client 源 DLL 与 `ui/AppBundle/managed/GameEntry.dll` 的 SHA-256 均为 `D41CF6C0463D9128897950AEB615DA63BB8D635EAAA9E98EA43FC1BE611A5813`。
- 随后 `debug_start` 再次完成保存、生成、双端编译和部署；关联 build `0f5e4bb7e801413482cdda4c36755177` 为 `succeeded`，无诊断错误，详见 `debug-ready.json`。

## 连服运行

- 调试操作：`b0020528eafa4b9f9c9c86c4e7948fd9`（`debug_start`，最终 `succeeded / client_ready`）。
- 启动记录：`h3-48880-1794559710638244-2`；游戏局：`7707575269935939588`；项目：`p_kykj`。
- Runtime：`gameplay:1`；Runtime 会话：`30dd92f6a32b4a61b048b4567325f6f7`；客户端 PID：`42268`。
- `runtime-ping.json` 确认 `client_only=false`、`game_started=true`、加载界面已关闭，并且 `host_connected=true / connected`。

## 证据文件

- `screenshot.png`：本次运行中通过 `debug.capture_screenshot` 获取的 1280×720 实际客户端画面，显示已加载场景、英雄和项目调试 UI。SHA-256：`912a9dbe4348a50977a6d2e9375386c7cb90a8227ea65121ae5e8f626c096c8b`。
- `client-runtime.log`、`client-game.log`：本次客户端 Wasm 与原生运行日志。
- `server-runtime.log`、`server-core.log`：游戏局 `7707575269935939588` 的服务端 Wasm 与核心日志。
- `motor.snapshot.json`：使用项目已有 `T` 调试控制切换到物理角色模式后，订阅服务端单位 `7` 的 `MotorProbe` 并读取的真实 `motor.snapshot`。结果为 `status=ok`、`coordinate_space=server`，包含 32 个连续权威物理帧；样本包含 `grounded`、`selected_ground`、速度、胶囊参数等。
- `debug-start.json`、`debug-ready.json`、`runtime-targets.json`、`runtime-ping.json`：本次操作、Runtime 目标和连接身份的原始结果。

日志保留运行时的实际内容，包括引擎资源/统计上报等既有警告；本次验证范围是真实连服启动、画面、日志和 MotorProbe 采集，不是游戏逻辑验收。

## 停止与清理

- 对原调试操作调用 `debug_stop`，停止操作 ID 为 `9f8f7df2a6c6439f9b286b7f40951144`，结果见 `debug-stop.json`、`debug-stop-wait.json`。
- 原操作最终为 `state=stopped`、`phase=stopped`、`session_active=false`、`cleanup_pending=false`、`cleanup_verified=true`，见 `debug-stopped.json`。
- 客户端进程、launcher PID `43236`、原生辅助 PID `48880` 均已退出；远程 Host 已结束且租约已释放。随后关闭了本次启动的 Headless 宿主 PID `47448`。
- 客户端在调用停止前已经退出，因此最终状态保留了 `client_lost / runtime_unreachable` 诊断；停止与清理仍通过原调试操作完成并得到上述已验证结果。
