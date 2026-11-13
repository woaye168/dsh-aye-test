# 3D 单位移动控制器模板

这是从 `3DPathingMotorTest` 复制来的 3D 场景 + 物理角色移动联调项目，放入 `map_templates` 作为 `UnitMovementController` 的开发模板和测试工程。

当前仍不是面向用户发布的正式模板：

- `project/map_settings.json` 中 `Accessable=false`，默认不作为正式可选模板展示。
- 运行时代码已经把物理角色/寻路切换接到 `UnitMovementController` 薄包装 API。
- 空格跳跃、额外空中跳跃次数和移动速度 UI 已接入单位控制器/单位属性 API。
- 正式发布前仍需要走最新 SDK 发布和编辑器模板创建验证。

## 当前可用内容

- 默认场景 `new_scene` 是 `Volumetric3D`。
- 玩家主单位放在 3D 场景中。
- 客户端启动移动控制 UI。
- 调试面板使用中文显示，提供单一模式切换按钮、跳跃、额外跳跃次数、移动速度、状态刷新和独立的 overlay 切换区。
- 状态报告只保留单位当前位置的向下射线检查；静态物理碰撞由控制器在切到物理角色模式时确保。
- 左下角 `KCC 固定回归夹具` 默认不启用；手动选择后会在当前 SCE 场景的同一个 PhysicsWorld 中创建高空临时碰撞体。

## KCC 固定回归夹具

夹具覆盖六组相互独立的场景：

1. 单个稳定静态盒的站立、跳起和落地。
2. 两个共面盒的接缝站立与横穿。
3. 先删除旧碰撞根、再创建同形新根，并与真正挖空对照。
4. 32 单位台阶、64 单位台阶和平台边角悬空。
5. 先创建同形新碰撞根、再删除旧根，与先删后建使用相同几何和出生点。
6. 原项目“脚上方 16 向下 80、接受脚下 72 内任意命中”的射线补丁对照；该结果只用于观察，不参与 KCC 或跳跃判定。

每次只保留一个测试场景，并且同一游戏局只允许一个玩家占用夹具，避免多名玩家在同一 PhysicsWorld 和固定坐标上创建重叠碰撞体。碰撞体挂在 `unit.Scene.GetSyncRoot()` 下的 local 临时根中，不创建独立 `SceneGraph`；玩家断开、主动清理或退出游戏局时释放。场景切换按钮会把物理角色移动到固定高空区域，普通模板调试不会自动移动玩家。

服务端报告包含 `run_id`、场景、动作、碰撞根 generation、mutation strategy、盒体位置/尺寸/创建顺序、Motor 参数和服务端时间。客户端通过只读 Runtime MCP 工具 `project.kcc_fixture_status` 返回最后一份报告。报告里的 `server_time_ms` 用于关联动作，不是 physics frame；逐物理帧证据只从正式 `motor.snapshot` 读取。

推荐自动化顺序：

1. 用 `ui.click` 点击唯一文本的 `KCC ...` 场景按钮，再调用 `project.kcc_fixture_status` 取得 `unit_id` 与 `run_id`。
2. 调用 `debug.overlay`，传 `mode=MotorProbe`、该 `unit_id` 和 `snapshot_interval_ms=33`。
3. 调用 `motor.snapshot(max_frames=1)` 记录当前 `physics_frame`。
4. 通过 `ui.click` 触发跳跃、跨接缝、替换、挖空、恢复、台阶或边角动作。
5. 调用 `motor.snapshot(max_frames=128, after_physics_frame=<上次帧>)`，对照连续帧的 `grounded`、`selected_ground`、法线、速度、扫掠与穿透。
6. 重新读取 `project.kcc_fixture_status`，用 action、generation 和 mutation strategy 确认采集对应的几何变化。

固定盒也出现错误时再进入 Bullet/KCC 算法排查；仅同形替换中断时优先检查碰撞提交顺序；只有项目射线与 KCC 不一致时，应收紧或移除项目补丁。不要根据截图或单次向下射线宣称 grounded 正确。

## 后续改造目标

正式模板发布前需要完成：

1. 用正式模板验证新建项目、客户端启动和服务端同步链路。
2. 评估是否保留运行时参数调节按钮，或仅作为内部调试模板能力。
3. 清理测试命名，把 `PhysicalCharacterMotorTest` 改成面向模板的系统名。

## 验证

此模板依赖最新 WasiCoreSDK 和带 `motor.snapshot` 原生桥的客户端。验证时优先使用编辑器模板创建项目，再按项目内 `src/GameEntry.csproj` 执行服务端和客户端编译。旧客户端会返回 `motor_snapshot_native_unavailable`，不能作为夹具失败结论。
