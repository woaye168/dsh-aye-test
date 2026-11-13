# DSH Agent execution helpers

这些脚本把 dsh-superagent 的 WSL 控制面与 Windows SCE 执行连接起来。

## Prepare-WslTicket.sh

由 dsh-superagent 的 prepare 配置调用。它从当前 Git 主 clone 创建 Ticket worktree，并通过 `powershell.exe` 在新 worktree 中执行 SCE `prepare --offline`。

机器相关的 SCE 入口通过 `SCE_CMD` 传入，例如：

```sh
SCE_CMD='D:/sce_new/sce.cmd' /home/woaye/src/dsh-aye-test/agent/Prepare-WslTicket.sh
```

Ticket 目录必须位于 WSL 挂载的 Windows 驱动器上，例如 `/mnt/d/sce_new/agent-worktrees/<ticket>`。脚本会读取 dsh-superagent 注入的 `DSH_SUPERAGENT_*` 变量；失败时只清理刚创建的未完成 worktree。

离线初始化证明已安装资源可用，不声称完成了线上 SDK 更新。需要在线更新时，应先单独解决安装资源包的占用或权限问题。

## Invoke-WindowsBuild.ps1

从 WSL 调用 Windows 双端编译并核对部署产物：

```sh
WIN_DIR=$(wslpath -w "$DSH_SUPERAGENT_DIR")
SCRIPT=$(wslpath -w "$DSH_SUPERAGENT_DIR/agent/Invoke-WindowsBuild.ps1")
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$SCRIPT" -ProjectPath "$WIN_DIR"
```

脚本使用项目现有 `BuildAndCopy.bat`，分别构建 Server-Debug 和 Client-Debug，并返回 DLL 与 AppBundle 的 SHA-256。

真实游戏调试仍应遵循项目生成的 `AGENTS.md`、Headless Editor 和 Runtime MCP 指南，不能用编译成功代替运行验证。
