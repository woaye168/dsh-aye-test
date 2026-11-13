using EngineInterface.BaseType;
using EngineInterface.Enum;
using GameCore.MovementSystem;
using GameCore.ResourceType.Data;
using GameData;
using GameGraph.NodeSystem;
using gamesparkcore.UnitSystem;
using System.Text;

namespace GameEntry;

public class PhysicalCharacterMotorTest : IGameClass
{
    // Mode switching, jump, and extra-air-jump consumption use UnitMovementController.
    private const string MessagePrefix = "3d-pathing-motor-test:";
    private const string StatusMessagePrefix = MessagePrefix + "status-report:";
    private const string CommandToggleMovement = "toggle-movement";
    private const string CommandJump = "jump";
    private const string CommandStatus = "status";
    private const string CommandSetExtraAirJumpsPrefix = "set-extra-air-jumps:";
    private const string CommandAdjustMoveSpeedPrefix = "adjust-move-speed:";

    private const string PhysicalModeDisplayText = "物理角色";
    private const float GroundProbeStartHeight = 1000f;
    private const float GroundProbeDistance = 3000f;
    private const int GroundProbeMaxHitsToLog = 6;
    private const int DefaultExtraAirJumps = 0;
    private const int MaxExtraAirJumps = 5;
    private const double MoveSpeedStep = 50;
    private const double MinMoveSpeed = 50;
    private const double MaxMoveSpeed = 1200;
    private const string Controlled3DHeroModelId = "$gamesparkcore.ScopeData.GameDataUnit.Controlled3DHeroSample.HeroModel";
    private const string CodePatchLocomotionProfileId = "$GameEntry.ScopeData.GameDataUnitLocomotionAnimationProfile.Controlled3DHeroSamplePatch.Root";
    private const float LocomotionMovementAnimationSpeedRatio = 0.003f;
    private static bool locomotionDataPatchApplied;
    // private const float InitialSpawnLiftHeight = 300f;

    private static void OnGameDataInitialization()
    {
        ApplyUnitLocomotionAnimationDataPatch("GameDataInitialization");
    }

    private static void ApplyUnitLocomotionAnimationDataPatch(string phase)
    {
        if (locomotionDataPatchApplied)
        {
            return;
        }

        var model = new GameLink<GameDataModel, GameDataModel>(Controlled3DHeroModelId).Data;
        if (model == null)
        {
            Game.Logger.LogWarning(
                "3D unit movement controller locomotion patch skipped at {Phase}: model {ModelId} was not found.",
                phase,
                Controlled3DHeroModelId);
            return;
        }

        var profileLink = new GameLink<GameDataUnitLocomotionAnimationProfile, GameDataUnitLocomotionAnimationProfile>(
            CodePatchLocomotionProfileId);
        _ = new GameDataUnitLocomotionAnimationProfile(profileLink)
        {
            Idle = CreateLocomotionClip("anim/human/barehanded_anim/hand_07/idle_01.ani", isLooping: true, blendInMilliseconds: 150),
            Move = CreateLocomotionClip("anim/human/barehanded_anim/hand_07/run_01.ani", isLooping: true, blendInMilliseconds: 120),
            JumpStart = CreateLocomotionClip("anim/human/barehanded_anim/hand_07/jump_qi.ani", isLooping: false, blendInMilliseconds: 60),
            AirJumpStart = CreateLocomotionClip("anim/human/barehanded_anim/hand_07/jump_full.ani", isLooping: false, blendInMilliseconds: 60),
            AirRise = CreateLocomotionClip("anim/human/barehanded_anim/hand_07/jump_kong.ani", isLooping: true, blendInMilliseconds: 100),
            AirFall = CreateLocomotionClip("anim/human/barehanded_anim/hand_07/jump_kong.ani", isLooping: true, blendInMilliseconds: 100),
            Land = CreateLocomotionClip("anim/human/barehanded_anim/hand_07/jump_luo.ani", isLooping: false, blendInMilliseconds: 80),
            EnterMoveSpeed = 20f,
            ExitMoveSpeed = 5f,
            FallSpeedThreshold = -50f,
            MinAirTimeForLand = TimeSpan.FromMilliseconds(120),
            LandSpeedThreshold = -120f,
            InterruptLandOnMove = true,
            MinLandTimeForMoveInterrupt = TimeSpan.FromMilliseconds(80),
            LandMoveInterruptSpeed = 20f,
            MoveSpeedScaleMode = UnitLocomotionAnimationSpeedScaleMode.HorizontalSpeed,
            MovementAnimationSpeedRatio = LocomotionMovementAnimationSpeedRatio,
        };

        model.UnitLocomotionAnimationProfile = profileLink;
        if (model.MovementAnimationSpeedRatio == 0f)
        {
            model.MovementAnimationSpeedRatio = LocomotionMovementAnimationSpeedRatio;
        }

        locomotionDataPatchApplied = true;
        Game.Logger.LogInformation(
            "3D unit movement controller locomotion patch applied at {Phase}: model={ModelId}, profile={ProfileId}.",
            phase,
            Controlled3DHeroModelId,
            CodePatchLocomotionProfileId);
    }

    private static UnitLocomotionAnimationClip CreateLocomotionClip(
        string animation,
        bool isLooping,
        double blendInMilliseconds)
    {
        return new()
        {
            Animation = animation,
            LogicLayer = AnimationLogicLayer.Rollback,
            Priority = 2,
            BodyPart = AnimationBodyPart.FullBody,
            IsLooping = isLooping,
            BlendIn = TimeSpan.FromMilliseconds(blendInMilliseconds),
            Speed = 1f,
        };
    }

#if SERVER
    private static void OnGameTriggerInitialization()
    {
        ApplyUnitLocomotionAnimationDataPatch("ServerTriggerInitialization");

        new Trigger<EventGameStart>(static async (_, _) =>
        {
            var configuredCount = 0;
            foreach (var player in Player.AllPlayers)
            {
                var unit = player.MainUnit;
                if (unit == null)
                {
                    continue;
                }

                // LiftUnitForPhysicsTest(unit);
                var movement = EnsurePhysicalCharacterMovement(unit);
                _ = unit.ReturnUnitToSceneNavigation();
                configuredCount++;
                ReportStatus("初始化为寻路模式", player, unit, movement);
            }

            if (configuredCount == 0)
            {
                Game.Logger.LogWarning("3D pathing motor test did not find any player main unit to configure.");
            }

            await Task.CompletedTask;
            return true;
        }).Register(Game.Instance);

        new Trigger<EventClientMessage>(static async (_, args) =>
        {
            var command = DecodeCommand(args.Message);
            if (command == null)
            {
                return false;
            }

            var unit = args.Player.MainUnit;
            if (unit == null)
            {
                Game.Logger.LogWarning(
                    "3D pathing motor test command ignored because player {PlayerId} has no main unit. Command={Command}",
                    args.Player.Id,
                    command);
                return true;
            }

            var movement = EnsurePhysicalCharacterMovement(unit);
            if (TryHandleSetExtraAirJumpsCommand(command, args.Player, unit, movement))
            {
                await Task.CompletedTask;
                return true;
            }

            if (TryHandleAdjustMoveSpeedCommand(command, args.Player, unit, movement))
            {
                await Task.CompletedTask;
                return true;
            }

            switch (command)
            {
                case CommandToggleMovement:
                    if (unit.GetUnitMovementMode() == UnitMovementMode.PhysicalCharacter)
                    {
                        var returned = unit.ReturnUnitToSceneNavigation();
                        movement = EnsurePhysicalCharacterMovement(unit);
                        ReportStatus(returned ? "已切回寻路模式" : "切回寻路模式失败", args.Player, unit, movement);
                    }
                    else
                    {
                        var toggled = unit.ActivateUnitPhysicalCharacterMovement();
                        movement = EnsurePhysicalCharacterMovement(unit);
                        ReportStatus(toggled ? "已切换到物理角色模式" : "切换到物理角色模式失败", args.Player, unit, movement);
                    }
                    break;
                case CommandJump:
                    if (unit.GetUnitMovementMode() != UnitMovementMode.PhysicalCharacter)
                    {
                        Game.Logger.LogWarning(
                            "3D unit movement controller jump rejected because unit {UnitId} is not in physical character mode.",
                            unit.EntityId);
                        ReportStatus("警告：当前不是物理角色模式，不能跳跃", args.Player, unit, movement);
                        break;
                    }

                    var jumped = unit.TryJumpUnit();
                    movement = EnsurePhysicalCharacterMovement(unit);
                    ReportStatus(jumped ? "已请求跳跃" : "跳跃被拒绝", args.Player, unit, movement);
                    break;
                case CommandStatus:
                    ReportStatus("刷新快照", args.Player, unit, movement);
                    break;
            }

            await Task.CompletedTask;
            return true;
        }).Register(Game.Instance);
    }

    private static PhysicalCharacterMovement EnsurePhysicalCharacterMovement(Unit unit)
    {
        var shouldStayActive = unit.GetUnitMovementMode() == UnitMovementMode.PhysicalCharacter;
        var movement = unit.GetPhysicalCharacterMovement();
        if (movement == null)
        {
            _ = unit.ActivateUnitPhysicalCharacterMovement();
            movement = unit.GetPhysicalCharacterMovement()
                ?? throw new InvalidOperationException("MovementController data did not create physical character movement.");
        }

        if (shouldStayActive)
        {
            movement.Activate();
        }
        else
        {
            movement.Deactivate(syncUnitPosition: false);
        }
        return movement;
    }

    private static bool TryHandleSetExtraAirJumpsCommand(string command, Player player, Unit unit, PhysicalCharacterMovement movement)
    {
        if (!command.StartsWith(CommandSetExtraAirJumpsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var valueText = command[CommandSetExtraAirJumpsPrefix.Length..];
        if (!int.TryParse(valueText, out var value))
        {
            ReportStatus($"额外空中跳跃次数无效: {valueText}", player, unit, movement);
            return true;
        }

        unit.SetUnitExtraAirJumpCount(Math.Clamp(value, 0, MaxExtraAirJumps));
        ReportStatus("额外空中跳跃次数已更新", player, unit, movement);
        return true;
    }

    private static bool TryHandleAdjustMoveSpeedCommand(string command, Player player, Unit unit, PhysicalCharacterMovement movement)
    {
        if (!command.StartsWith(CommandAdjustMoveSpeedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var valueText = command[CommandAdjustMoveSpeedPrefix.Length..];
        if (!double.TryParse(valueText, out var delta))
        {
            ReportStatus($"移动速度调整值无效: {valueText}", player, unit, movement);
            return true;
        }

        var currentSpeed = unit.GetAttributeValue(GSC.GameDataUnitProperty.MoveSpeed);
        var nextSpeed = Math.Clamp(currentSpeed + delta, MinMoveSpeed, MaxMoveSpeed);
        unit.SetAttributeValue(GSC.GameDataUnitProperty.MoveSpeed, nextSpeed);
        ReportStatus($"移动速度已调整为 {nextSpeed:F0}", player, unit, movement);
        return true;
    }

    private static string? DecodeCommand(byte[] message)
    {
        var text = Encoding.UTF8.GetString(message);
        return text.StartsWith(MessagePrefix, StringComparison.Ordinal)
            ? text[MessagePrefix.Length..]
            : null;
    }

    private static void ReportStatus(string phase, Player player, Unit unit, PhysicalCharacterMovement movement)
    {
        var report = BuildStatusReport(phase, player, unit, movement);
        Game.Logger.LogInformation(
            "3D pathing motor test status report:{NewLine}{Report}",
            Environment.NewLine,
            report);
        SendStatusReport(player, report);
    }

    private static string BuildStatusReport(string phase, Player player, Unit unit, PhysicalCharacterMovement movement)
    {
        var position = unit.Position;
        var velocity = movement.Velocity;
        var movementMode = unit.GetUnitMovementMode();
        var controller = unit.GetUnitMovementControllerInfo();
        var extraAirJumps = controller?.ExtraAirJumpCount ?? 0;
        var remainingAirJumps = controller?.RemainingAirJumps ?? 0;
        var canJump = controller?.CanJump == true;
        var canUseAirJump = remainingAirJumps > 0;
        var lastJumpFailure = controller?.LastJumpFailureReason;
        var moveSpeed = unit.GetAttributeValue(GSC.GameDataUnitProperty.MoveSpeed);
        var jumpSpeed = unit.GetAttributeValue(GSC.GameDataUnitProperty.JumpLaunchSpeed);
        var builder = new StringBuilder();
        builder.AppendLine($"状态快照: t={Game.TotalElapsedTickInMilliseconds / 1000.0:F2}s  触发={phase}");
        builder.AppendLine($"玩家: {player.Id}  单位: {unit.EntityId}");
        builder.AppendLine($"移动模式: {FormatMovementMode(movementMode)}  物理Motor: {FormatBool(movement.IsActive)}  着地: {FormatBool(movement.IsGrounded)}");
        builder.AppendLine($"跳跃: 可跳={FormatBool(canJump)}  空中追加跳跃可用={FormatBool(canUseAirJump)}");
        builder.AppendLine($"额外空中跳跃: {extraAirJumps} / {MaxExtraAirJumps}  剩余空中跳跃: {remainingAirJumps}");
        if (!string.IsNullOrWhiteSpace(lastJumpFailure))
        {
            builder.AppendLine($"上次跳跃失败: {lastJumpFailure}");
        }
        builder.AppendLine($"单位坐标: X={position.X:F1}  Y={position.Y:F1}  Z={position.Z:F1}");
        builder.AppendLine($"速度向量: X={velocity.X:F1}  Y={velocity.Y:F1}  Z={velocity.Z:F1}");
        builder.AppendLine($"移动速度: {moveSpeed:F0}  跳跃初速: {jumpSpeed:F0}");
        builder.AppendLine($"胶囊: 半径={movement.Motor.Radius:F1}  高度={movement.Motor.Height:F1}  台阶={movement.Motor.StepHeight:F1}  坡度={movement.Motor.MaxSlopeDegrees:F1}");
        AppendGroundProbeReport(builder, phase, unit);
        return builder.ToString();
    }

    private static void AppendGroundProbeReport(StringBuilder builder, string phase, Unit unit)
    {
        var physicsWorld = unit.GetOwnerPhysicsWorld();
        if (physicsWorld == null)
        {
            Game.Logger.LogWarning(
                "3D pathing motor test ground probe skipped: phase={Phase}, unit={UnitId}, reason=no physics world",
                phase,
                unit.EntityId);
            builder.AppendLine("向下射线: 跳过，当前场景没有物理世界");
            return;
        }

        var position = unit.Position;
        var origin = new Vector3(position.X, position.Y, position.Z + GroundProbeStartHeight);
        try
        {
            var hits = physicsWorld.RaycastAll(origin, new Vector3(0f, 0f, -1f), GroundProbeDistance);
            builder.AppendLine($"向下射线: 起点=({origin.X:F1}, {origin.Y:F1}, {origin.Z:F1})，最大距离={GroundProbeDistance:F1}，命中={hits.Length}");
            Game.Logger.LogInformation(
                "3D pathing motor test ground probe: phase={Phase}, unit={UnitId}, origin=({X:F1},{Y:F1},{Z:F1}), maxDistance={MaxDistance}, hitCount={HitCount}",
                phase,
                unit.EntityId,
                origin.X,
                origin.Y,
                origin.Z,
                GroundProbeDistance,
                hits.Length);

            var hitCount = Math.Min(hits.Length, GroundProbeMaxHitsToLog);
            for (var i = 0; i < hitCount; i++)
            {
                var hit = hits[i];
                var body = hit.GetRigidBody();
                var bodyLayer = body?.CollisionLayer.ToString() ?? "n/a";
                var bodyMask = body?.CollisionMask.ToString() ?? "n/a";
                builder.AppendLine(
                    $"  [{i}] Z={hit.Position.Z:F1}  距离={hit.Distance:F1}  法线=({hit.Normal.X:F2}, {hit.Normal.Y:F2}, {hit.Normal.Z:F2})  layer={bodyLayer}  mask={bodyMask}");
                Game.Logger.LogInformation(
                    "3D pathing motor test ground probe hit: phase={Phase}, unit={UnitId}, index={Index}, pos=({X:F1},{Y:F1},{Z:F1}), distance={Distance:F1}, normal=({NX:F2},{NY:F2},{NZ:F2}), bodyLayer={BodyLayer}, bodyMask={BodyMask}",
                    phase,
                    unit.EntityId,
                    i,
                    hit.Position.X,
                    hit.Position.Y,
                    hit.Position.Z,
                    hit.Distance,
                    hit.Normal.X,
                    hit.Normal.Y,
                    hit.Normal.Z,
                    bodyLayer,
                    bodyMask);
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"向下射线: 不可用 ({ex.GetType().Name})，起点=({origin.X:F1}, {origin.Y:F1}, {origin.Z:F1})，最大距离={GroundProbeDistance:F1}");
            Game.Logger.LogWarning(
                "3D pathing motor test ground probe unavailable: phase={Phase}, unit={UnitId}, exception={ExceptionType}, message={Message}, origin=({X:F1},{Y:F1},{Z:F1}), maxDistance={MaxDistance}",
                phase,
                unit.EntityId,
                ex.GetType().Name,
                ex.Message,
                origin.X,
                origin.Y,
                origin.Z,
                GroundProbeDistance);
        }
    }

    private static string FormatBool(bool value) => value ? "是" : "否";

    private static string FormatMovementMode(UnitMovementMode mode)
    {
        return mode switch
        {
            UnitMovementMode.NoLocomotion => "无连续移动",
            UnitMovementMode.SurfaceNavigation => "2.5D寻路",
            UnitMovementMode.VolumetricNavigation => "3D寻路",
            UnitMovementMode.PhysicalCharacter => PhysicalModeDisplayText,
            UnitMovementMode.Rigidbody => "刚体",
            UnitMovementMode.Scripted => "脚本",
            _ => mode.ToString(),
        };
    }

    private static void SendStatusReport(Player player, string report)
    {
        var sent = new ProtoCustomMessage
        {
            Message = Encoding.UTF8.GetBytes(StatusMessagePrefix + report),
        }.SendTo(player);

        if (!sent)
        {
            Game.Logger.LogWarning("3D pathing motor test failed to send status report to player {PlayerId}.", player.Id);
        }
    }
#endif

#if CLIENT
    private static Panel? debugPanel;
    private static Button? bottomRightJumpButton;
    private static Button? bottomRightModeButton;
    private static Label? lastCommandLabel;
    private static Label? overlayStatusLabel;
    private static Label? statusReportLabel;
    private static Label? extraAirJumpsLabel;
    private static Label? moveSpeedLabel;
    private static Label? movementToggleButtonLabel;
    private static Trigger<EventServerMessage>? serverStatusTrigger;
    private static string latestStatusReport = "等待服务器状态快照。点击“刷新快照”或按 O。";
    private static int requestedExtraAirJumps = DefaultExtraAirJumps;

    private static void OnGameTriggerInitialization()
    {
        ApplyUnitLocomotionAnimationDataPatch("ClientTriggerInitialization");
        RegisterServerStatusHandler();

        new Trigger<EventGameStart>(static async (_, _) =>
        {
            CreateDebugPanel();
            RegisterKeyCommand(GameCore.Platform.SDL.VirtualKey.T, CommandToggleMovement);
            RegisterKeyCommand(GameCore.Platform.SDL.VirtualKey.Space, CommandJump);
            RegisterKeyCommand(GameCore.Platform.SDL.VirtualKey.O, CommandStatus);
            RegisterLocomotionDebugKey(GameCore.Platform.SDL.VirtualKey.G);
            SendCommand(CommandStatus);

            Game.Logger.LogInformation("3D unit movement controller UI created: buttons plus T/Space/O/G shortcuts.");
            await Task.CompletedTask;
            return true;
        }).Register(Game.Instance);
    }

    private static void RegisterServerStatusHandler()
    {
        if (serverStatusTrigger != null)
        {
            return;
        }

        serverStatusTrigger = new Trigger<EventServerMessage>(static async (_, args) =>
        {
            var report = DecodeStatusReport(args.Message);
            if (report == null)
            {
                return false;
            }

            ApplyStatusReport(report);
            await Task.CompletedTask;
            return true;
        }, true);
        serverStatusTrigger.Register(Game.Instance);
    }

    private static void CreateDebugPanel()
    {
        if (debugPanel != null)
        {
            ApplyStatusReport(latestStatusReport);
            CreateBottomRightModeButton();
            CreateBottomRightJumpButton();
            return;
        }

        debugPanel = new Panel
        {
            Width = 640,
            Height = AutoMode.Auto,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 20, 0),
            Padding = new Thickness(16, 16, 16, 16),
            Background = new SolidColorBrush(Color.FromArgb(210, 12, 16, 22)),
            CornerRadius = 6,
            FlowOrientation = Orientation.Vertical,
            VerticalContentAlignment = VerticalContentAlignment.Top,
        };

        _ = new Label
        {
            Text = "3D单位移动控制器",
            FontSize = 20,
            Bold = true,
            TextColor = Color.FromArgb(255, 236, 240, 245),
            Width = AutoMode.Auto,
            Height = AutoMode.Auto,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            IsStatic = true,
            Parent = debugPanel,
        };

        CreateSectionTitle("移动控制");
        var controlRow = new Panel
        {
            Width = AutoMode.Auto,
            Height = AutoMode.Auto,
            FlowOrientation = Orientation.Horizontal,
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            Parent = debugPanel,
        };

        movementToggleButtonLabel = CreateCommandButton(controlRow, FormatMovementToggleButtonText(false), CommandToggleMovement, Color.FromArgb(230, 34, 111, 201), 210);
        CreateCommandButton(controlRow, "跳跃 (Space)", CommandJump, Color.FromArgb(230, 182, 111, 38), 160);
        CreateCommandButton(controlRow, "刷新快照 (O)", CommandStatus, Color.FromArgb(230, 112, 92, 170), 160);

        CreateSectionTitle("参数调节");
        var extraAirJumpRow = new Panel
        {
            Width = AutoMode.Auto,
            Height = AutoMode.Auto,
            FlowOrientation = Orientation.Horizontal,
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            Parent = debugPanel,
        };

        extraAirJumpsLabel = new Label
        {
            Text = FormatExtraAirJumpsText(),
            FontSize = 15,
            TextColor = Color.FromArgb(255, 196, 220, 230),
            Width = 270,
            Height = 44,
            Margin = new Thickness(0, 0, 10, 0),
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = extraAirJumpRow,
        };

        CreateExtraAirJumpButton(extraAirJumpRow, "减少", -1, Color.FromArgb(230, 88, 98, 110));
        CreateExtraAirJumpButton(extraAirJumpRow, "增加", 1, Color.FromArgb(230, 44, 132, 118));

        var moveSpeedRow = new Panel
        {
            Width = AutoMode.Auto,
            Height = AutoMode.Auto,
            FlowOrientation = Orientation.Horizontal,
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            Parent = debugPanel,
        };

        moveSpeedLabel = new Label
        {
            Text = "移动速度：等待服务器",
            FontSize = 15,
            TextColor = Color.FromArgb(255, 196, 220, 230),
            Width = 270,
            Height = 44,
            Margin = new Thickness(0, 0, 10, 0),
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = moveSpeedRow,
        };

        CreateMoveSpeedButton(moveSpeedRow, "减速", -MoveSpeedStep, Color.FromArgb(230, 88, 98, 110));
        CreateMoveSpeedButton(moveSpeedRow, "加速", MoveSpeedStep, Color.FromArgb(230, 44, 132, 118));

        CreateSectionTitle("调试覆盖层");
        var overlayButtonRow = new Panel
        {
            Width = AutoMode.Auto,
            Height = AutoMode.Auto,
            FlowOrientation = Orientation.Horizontal,
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            Parent = debugPanel,
        };

        CreateOverlayButton(overlayButtonRow, "关闭", GameUI.Debug.DebugOverlayMode.Off, Color.FromArgb(230, 72, 80, 88));
        CreateOverlayButton(overlayButtonRow, "导航网格", GameUI.Debug.DebugOverlayMode.Nav3DMesh, Color.FromArgb(230, 38, 126, 166));
        CreateOverlayButton(overlayButtonRow, "体素", GameUI.Debug.DebugOverlayMode.Nav3DVoxels, Color.FromArgb(230, 36, 140, 112));
        CreateOverlayButton(overlayButtonRow, "物理", GameUI.Debug.DebugOverlayMode.PhysicsWorld, Color.FromArgb(230, 165, 92, 45));

        var overlayButtonRow2 = new Panel
        {
            Width = AutoMode.Auto,
            Height = AutoMode.Auto,
            FlowOrientation = Orientation.Horizontal,
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            Parent = debugPanel,
        };

        CreateOverlayButton(overlayButtonRow2, "Motor", GameUI.Debug.DebugOverlayMode.MotorProbe, Color.FromArgb(230, 175, 70, 88));
        CreateOverlayButton(overlayButtonRow2, "光照密度", GameUI.Debug.DebugOverlayMode.LightmapDensity, Color.FromArgb(230, 126, 104, 40));
        CreateOverlayButton(overlayButtonRow2, "LOD", GameUI.Debug.DebugOverlayMode.LodView, Color.FromArgb(230, 92, 108, 170));
        CreateOverlayButton(overlayButtonRow2, "遮挡相机", GameUI.Debug.DebugOverlayMode.OccludingCamera, Color.FromArgb(230, 102, 82, 132));

        overlayStatusLabel = new Label
        {
            Text = "覆盖层：未切换",
            FontSize = 15,
            TextColor = Color.FromArgb(255, 178, 213, 255),
            Width = 608,
            Height = AutoMode.Auto,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            IsStatic = true,
            Parent = debugPanel,
        };

        lastCommandLabel = new Label
        {
            Text = "最近命令：无",
            FontSize = 15,
            TextColor = Color.FromArgb(255, 196, 206, 218),
            Width = 608,
            Height = AutoMode.Auto,
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            IsStatic = true,
            Parent = debugPanel,
        };

        var reportPanel = new Panel
        {
            Width = 608,
            Height = AutoMode.Auto,
            Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromArgb(180, 2, 7, 12)),
            CornerRadius = 4,
            Parent = debugPanel,
        };

        statusReportLabel = new Label
        {
            Text = latestStatusReport,
            FontSize = 15,
            TextColor = Color.FromArgb(255, 232, 235, 240),
            Width = 584,
            Height = AutoMode.Auto,
            TextWrap = true,
            LineSpacingRatio = 1.1f,
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            VerticalContentAlignment = VerticalContentAlignment.Top,
            IsStatic = true,
            Parent = reportPanel,
        };

        GameUI.Control.UIRoot.Instance.AddChild(debugPanel);
        CreateBottomRightModeButton();
        CreateBottomRightJumpButton();
    }

    private static void CreateBottomRightModeButton()
    {
        if (bottomRightModeButton != null)
        {
            return;
        }

        bottomRightModeButton = new Button
        {
            Width = 124,
            Height = 124,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 192, 48),
            Background = new SolidColorBrush(Color.FromArgb(230, 34, 111, 201)),
            CornerRadius = 62,
        };

        _ = new Label
        {
            Text = "切换模式",
            FontSize = 22,
            Bold = true,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalContentAlignment.Center,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = bottomRightModeButton,
        };

        bottomRightModeButton.OnPointerClicked += (_, _) => SendCommand(CommandToggleMovement);
        GameUI.Control.UIRoot.Instance.AddChild(bottomRightModeButton);
    }

    private static void CreateBottomRightJumpButton()
    {
        if (bottomRightJumpButton != null)
        {
            return;
        }

        bottomRightJumpButton = new Button
        {
            Width = 124,
            Height = 124,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 48, 48),
            Background = new SolidColorBrush(Color.FromArgb(230, 182, 111, 38)),
            CornerRadius = 62,
        };

        _ = new Label
        {
            Text = "跳跃",
            FontSize = 24,
            Bold = true,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalContentAlignment.Center,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = bottomRightJumpButton,
        };

        bottomRightJumpButton.OnPointerClicked += (_, _) => SendCommand(CommandJump);
        GameUI.Control.UIRoot.Instance.AddChild(bottomRightJumpButton);
    }

    private static void CreateSectionTitle(string text)
    {
        _ = new Label
        {
            Text = text,
            FontSize = 15,
            Bold = true,
            TextColor = Color.FromArgb(255, 214, 226, 238),
            Width = 608,
            Height = AutoMode.Auto,
            Margin = new Thickness(0, 6, 0, 8),
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            IsStatic = true,
            Parent = debugPanel,
        };
    }

    private static Label CreateCommandButton(Panel parent, string text, string command, Color background, int width = 150)
    {
        var button = new Button
        {
            Width = width,
            Height = 44,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(background),
            CornerRadius = 4,
            Parent = parent,
        };

        var label = new Label
        {
            Text = text,
            FontSize = 15,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalContentAlignment.Center,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = button,
        };

        button.OnPointerClicked += (_, _) => SendCommand(command);
        return label;
    }

    private static void CreateExtraAirJumpButton(Panel parent, string text, int delta, Color background)
    {
        var button = new Button
        {
            Width = 104,
            Height = 44,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(background),
            CornerRadius = 4,
            Parent = parent,
        };

        _ = new Label
        {
            Text = text,
            FontSize = 15,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalContentAlignment.Center,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = button,
        };

        button.OnPointerClicked += (_, _) => SetRequestedExtraAirJumps(requestedExtraAirJumps + delta);
    }

    private static void CreateMoveSpeedButton(Panel parent, string text, double delta, Color background)
    {
        var button = new Button
        {
            Width = 104,
            Height = 44,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(background),
            CornerRadius = 4,
            Parent = parent,
        };

        _ = new Label
        {
            Text = text,
            FontSize = 15,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalContentAlignment.Center,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = button,
        };

        button.OnPointerClicked += (_, _) => SendCommand($"{CommandAdjustMoveSpeedPrefix}{delta}");
    }

    private static void CreateOverlayButton(Panel parent, string text, GameUI.Debug.DebugOverlayMode mode, Color background)
    {
        var button = new Button
        {
            Width = 138,
            Height = 44,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new SolidColorBrush(background),
            CornerRadius = 4,
            Parent = parent,
        };

        _ = new Label
        {
            Text = text,
            FontSize = 15,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalContentAlignment.Center,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = button,
        };

        button.OnPointerClicked += (_, _) => SetDebugOverlayMode(mode);
    }

    private static void RegisterKeyCommand(GameCore.Platform.SDL.VirtualKey key, string command)
    {
        new Trigger<EventGameKeyDown>(async (_, args) =>
        {
            if (args.IsRepeat || args.Key != key)
            {
                return false;
            }

            SendCommand(command);
            await Task.CompletedTask;
            return true;
        }).Register(Game.Instance);
    }

    private static void RegisterLocomotionDebugKey(GameCore.Platform.SDL.VirtualKey key)
    {
        new Trigger<EventGameKeyDown>(async (_, args) =>
        {
            if (args.IsRepeat || args.Key != key)
            {
                return false;
            }

            ReportClientLocomotionDebug("G键调试");
            await Task.CompletedTask;
            return true;
        }).Register(Game.Instance);
    }

    private static void ReportClientLocomotionDebug(string phase)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Locomotion调试: {phase}");
        builder.AppendLine($"数据补丁已应用: {FormatBool(locomotionDataPatchApplied)}");

        var model = new GameLink<GameDataModel, GameDataModel>(Controlled3DHeroModelId).Data;
        var profileLink = new GameLink<GameDataUnitLocomotionAnimationProfile, GameDataUnitLocomotionAnimationProfile>(
            CodePatchLocomotionProfileId);
        var patchProfile = profileLink.Data;
        builder.AppendLine($"补丁模型存在: {FormatBool(model != null)}  modelId={Controlled3DHeroModelId}");
        builder.AppendLine($"补丁profile存在: {FormatBool(patchProfile != null)}  profileId={CodePatchLocomotionProfileId}");
        if (model != null)
        {
            builder.AppendLine($"模型profile链接: {FormatProfileLink(model.UnitLocomotionAnimationProfile)}");
            builder.AppendLine($"模型移动动画倍率: {model.MovementAnimationSpeedRatio:F4}");
        }
        AppendProfileDebug(builder, "补丁profile", patchProfile);

        Player localPlayer;
        try
        {
            localPlayer = Player.LocalPlayer;
        }
        catch (Exception ex)
        {
            builder.AppendLine($"LocalPlayer获取失败: {ex.GetType().Name}: {ex.Message}");
            FlushClientLocomotionDebug(builder.ToString());
            return;
        }

        var unit = localPlayer.MainUnit;
        builder.AppendLine($"LocalPlayer: {localPlayer.Id}  MainUnit存在: {FormatBool(unit != null)}");
        if (unit == null)
        {
            FlushClientLocomotionDebug(builder.ToString());
            return;
        }

        builder.AppendLine($"Unit: id={unit.EntityId}  link={unit.LinkOrigin}");
        builder.AppendLine($"Unit模型profile链接: {FormatProfileLink(unit.Cache.Model?.Data?.UnitLocomotionAnimationProfile)}");
        AppendProfileDebug(builder, "Unit模型profile", unit.Cache.Model?.Data?.UnitLocomotionAnimationProfile?.Data);

        var component = unit.GetUnitLocomotionAnimation();
        builder.AppendLine($"组件(自动路径)存在: {FormatBool(component != null)}");
        AppendProfileDebug(builder, "组件profile(创建前)", component?.Profile);

        if (component == null)
        {
            component = unit.GetOrCreateUnitLocomotionAnimation();
            builder.AppendLine($"手动GetOrCreate组件: {FormatBool(component != null)}");
            if (component == null)
            {
                FlushClientLocomotionDebug(builder.ToString());
                return;
            }
        }

        if (component.Profile == null && patchProfile != null)
        {
            var applied = component.ApplyProfile(patchProfile);
            builder.AppendLine($"手动Apply补丁profile: {FormatBool(applied)}");
        }
        else if (component.Profile == null)
        {
            builder.AppendLine("手动Apply补丁profile: 跳过，补丁profile不存在");
        }
        else
        {
            builder.AppendLine("手动Apply补丁profile: 跳过，组件已有profile");
        }

        component = unit.GetUnitLocomotionAnimation();
        builder.AppendLine($"组件(调试后)存在: {FormatBool(component != null)}");
        AppendProfileDebug(builder, "组件profile(调试后)", component?.Profile);
        FlushClientLocomotionDebug(builder.ToString());
    }

    private static void AppendProfileDebug(StringBuilder builder, string label, GameDataUnitLocomotionAnimationProfile? profile)
    {
        if (profile == null)
        {
            builder.AppendLine($"{label}: null");
            return;
        }

        builder.AppendLine(
            $"{label}: idle={FormatClip(profile.Idle)} move={FormatClip(profile.Move)} jumpStart={FormatClip(profile.JumpStart)} airJumpStart={FormatClip(profile.AirJumpStart)} airRise={FormatClip(profile.AirRise)} airFall={FormatClip(profile.AirFall)} land={FormatClip(profile.Land)} landMoveInterrupt={FormatBool(profile.InterruptLandOnMove)} minLandMoveInterrupt={profile.MinLandTimeForMoveInterrupt.TotalMilliseconds:F0}ms landMoveInterruptSpeed={profile.LandMoveInterruptSpeed:F1} ratio={profile.MovementAnimationSpeedRatio:F4} speedMode={profile.MoveSpeedScaleMode}");
    }

    private static string FormatBool(bool value) => value ? "是" : "否";

    private static string FormatProfileLink(IGameLink<GameDataUnitLocomotionAnimationProfile>? link)
    {
        return link == null
            ? "null"
            : $"{link} data={FormatBool(link.Data != null)}";
    }

    private static string FormatClip(UnitLocomotionAnimationClip? clip)
    {
        return clip == null ? "null" : clip.Animation.Path.ToString();
    }

    private static void FlushClientLocomotionDebug(string report)
    {
        Game.Logger.LogInformation(
            "3D unit locomotion client debug:{NewLine}{Report}",
            Environment.NewLine,
            report);

        latestStatusReport = report;
        if (statusReportLabel != null)
        {
            statusReportLabel.Text = report;
        }

        if (lastCommandLabel != null)
        {
            lastCommandLabel.Text = "最近命令：Locomotion调试 (G)";
        }
    }

    private static string? DecodeStatusReport(byte[] message)
    {
        var text = Encoding.UTF8.GetString(message);
        return text.StartsWith(StatusMessagePrefix, StringComparison.Ordinal)
            ? text[StatusMessagePrefix.Length..]
            : null;
    }

    private static void ApplyStatusReport(string report)
    {
        latestStatusReport = report;
        if (statusReportLabel != null)
        {
            statusReportLabel.Text = report;
        }

        UpdateMovementToggleButton(report.Contains($"移动模式: {PhysicalModeDisplayText}", StringComparison.Ordinal));

        var extraAirJumpsText = ExtractReportValue(report, "额外空中跳跃:");
        if (extraAirJumpsText != null)
        {
            if (extraAirJumpsLabel != null)
            {
                extraAirJumpsLabel.Text = $"额外空中跳跃：{extraAirJumpsText}";
            }

            var slashIndex = extraAirJumpsText.IndexOf('/', StringComparison.Ordinal);
            var countText = slashIndex >= 0
                ? extraAirJumpsText[..slashIndex].Trim()
                : extraAirJumpsText.Trim();
            if (int.TryParse(countText, out var count))
            {
                requestedExtraAirJumps = Math.Clamp(count, 0, MaxExtraAirJumps);
            }
        }

        var moveSpeedText = ExtractReportValue(report, "移动速度:");
        if (moveSpeedText != null && moveSpeedLabel != null)
        {
            moveSpeedLabel.Text = $"移动速度：{moveSpeedText}";
        }
    }

    private static void SetRequestedExtraAirJumps(int value)
    {
        requestedExtraAirJumps = Math.Clamp(value, 0, MaxExtraAirJumps);
        if (extraAirJumpsLabel != null)
        {
            extraAirJumpsLabel.Text = FormatExtraAirJumpsText();
        }

        SendCommand($"{CommandSetExtraAirJumpsPrefix}{requestedExtraAirJumps}");
    }

    private static string FormatExtraAirJumpsText()
    {
        return $"额外空中跳跃：{requestedExtraAirJumps} / {MaxExtraAirJumps}";
    }

    private static void UpdateMovementToggleButton(bool isPhysicalMode)
    {
        if (movementToggleButtonLabel != null)
        {
            movementToggleButtonLabel.Text = FormatMovementToggleButtonText(isPhysicalMode);
        }
    }

    private static string FormatMovementToggleButtonText(bool isPhysicalMode)
    {
        return isPhysicalMode ? "切回寻路模式 (T)" : "切换到物理模式 (T)";
    }

    private static string? ExtractReportValue(string report, string prefix)
    {
        foreach (var rawLine in report.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[prefix.Length..].Trim();
            var separatorIndex = value.IndexOf("  ", StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                value = value[..separatorIndex].Trim();
            }

            return value;
        }

        return null;
    }

    private static void SetDebugOverlayMode(GameUI.Debug.DebugOverlayMode mode)
    {
        var applied = GameUI.Debug.DebugOverlay.SetMode(mode);
        if (overlayStatusLabel != null)
        {
            overlayStatusLabel.Text = applied
                ? $"覆盖层：{FormatOverlayMode(mode)}"
                : $"覆盖层切换失败：{FormatOverlayMode(mode)}";
        }

        if (applied)
        {
            Game.Logger.LogInformation("3D pathing motor test debug overlay mode set to {Mode}.", mode);
        }
        else
        {
            Game.Logger.LogWarning("3D pathing motor test debug overlay mode failed: mode={Mode}", mode);
        }
    }

    private static void SendCommand(string command)
    {
        var sent = new ProtoCustomMessage
        {
            Message = Encoding.UTF8.GetBytes(MessagePrefix + command),
        }.SendToServer();

        if (lastCommandLabel != null)
        {
            lastCommandLabel.Text = sent
                ? $"最近命令：{GetCommandDisplayText(command)}"
                : $"命令发送失败：{GetCommandDisplayText(command)}";
        }

        if (!sent)
        {
            Game.Logger.LogWarning("3D pathing motor test failed to send command {Command}.", command);
        }
    }

    private static string GetCommandDisplayText(string command)
    {
        return command switch
        {
            CommandToggleMovement => "切换移动模式 (T)",
            CommandJump => "跳跃 (Space)",
            CommandStatus => "刷新快照 (O)",
            _ when command.StartsWith(CommandSetExtraAirJumpsPrefix, StringComparison.Ordinal) => $"额外空中跳跃 = {command[CommandSetExtraAirJumpsPrefix.Length..]}",
            _ when command.StartsWith(CommandAdjustMoveSpeedPrefix, StringComparison.Ordinal) => $"移动速度 {command[CommandAdjustMoveSpeedPrefix.Length..]}",
            _ => command,
        };
    }

    private static string FormatOverlayMode(GameUI.Debug.DebugOverlayMode mode)
    {
        return mode switch
        {
            GameUI.Debug.DebugOverlayMode.Off => "关闭",
            GameUI.Debug.DebugOverlayMode.Nav3DMesh => "导航网格",
            GameUI.Debug.DebugOverlayMode.Nav3DVoxels => "导航体素",
            GameUI.Debug.DebugOverlayMode.PhysicsWorld => "物理世界",
            GameUI.Debug.DebugOverlayMode.MotorProbe => "Motor探测",
            GameUI.Debug.DebugOverlayMode.LightmapDensity => "光照密度",
            GameUI.Debug.DebugOverlayMode.LodView => "LOD",
            GameUI.Debug.DebugOverlayMode.OccludingCamera => "遮挡相机",
            _ => mode.ToString(),
        };
    }
#endif

    public static void OnRegisterGameClass()
    {
        if (GameDataGlobalConfig.TestGameMode != ScopeData.GameDataGameMode.MapGameMode)
        {
            return;
        }

        Game.OnGameTriggerInitialization += OnGameTriggerInitialization;
        Game.OnGameDataInitialization += OnGameDataInitialization;
    }
}
