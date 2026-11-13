#if SERVER
using GameCore.MovementSystem;
using GameGraph.NodeSystem;
using GameGraph.NodeSystem.Component.Physics;
using System.Text;
using System.Text.Json.Nodes;

namespace GameEntry;

public sealed partial class KccRegressionFixture
{
    private const float FixtureTopZ = 1536f;
    private static readonly Vector3 FixtureOrigin = new(4096f, 4096f, FixtureTopZ);
    private static readonly Dictionary<string, FixtureState> States = new(StringComparer.Ordinal);
    private static string? activeOwnerKey;

    private sealed class FixtureState
    {
        public int RunId { get; set; }
        public int RootGeneration { get; set; }
        public int CreateOrder { get; set; }
        public string Scenario { get; set; } = "none";
        public string LastAction { get; set; } = "none";
        public string MutationStrategy { get; set; } = "none";
        public string ExpectedObservation { get; set; } = "No fixture is active.";
        public Vector3 Spawn { get; set; }
        public Node? Root { get; set; }
        public List<BoxSnapshot> Boxes { get; set; } = [];
    }

    private sealed record BoxSnapshot(
        uint NodeId,
        string Name,
        Vector3 Center,
        Vector3 Size,
        uint CollisionLayer,
        uint CollisionMask,
        int CreateOrder);

    private static void OnGameTriggerInitialization()
    {
        new Trigger<EventClientMessage>(HandleFixtureCommand, true).Register(Game.Instance);
        new Trigger<EventPlayerUserDisconnected>(static async (_, args) =>
        {
            ClearPlayerState(args.Player);
            await Task.CompletedTask;
            return true;
        }, true).Register(Game.Instance);
    }

    private static async Task<bool> HandleFixtureCommand(object? sender, EventClientMessage args)
    {
        var command = DecodeFixtureMessage(args.Message, CommandPrefix);
        if (command == null)
        {
            return false;
        }

        var unit = args.Player.MainUnit;
        if (unit == null)
        {
            SendErrorReport(args.Player, command, "player_main_unit_missing");
            return true;
        }

        var movement = EnsureFixtureMovement(unit);
        if (movement == null)
        {
            SendErrorReport(args.Player, command, "physical_character_unavailable");
            return true;
        }

        var key = args.Player.Id.ToString();
        if (!States.TryGetValue(key, out var state))
        {
            state = new FixtureState();
            States[key] = state;
        }

        try
        {
            if (command != "status")
            {
                // Any new mutation cancels an in-flight fixed-duration move for this player.
                movement.Stop();
            }

            if (command.StartsWith("setup/", StringComparison.Ordinal))
            {
                SetupScenario(args.Player, unit, movement, state, key, command["setup/".Length..]);
                return true;
            }

            switch (command)
            {
                case "status":
                    SendReport(args.Player, unit, movement, state, "status");
                    break;
                case "destroy":
                    BeginRun(state, "destroy", "destroy", "The fixture is removed and the motor may fall to the underlying scene.");
                    DestroyRoot(state);
                    state.Scenario = "none";
                    state.ExpectedObservation = "No fixture is active.";
                    if (activeOwnerKey == key) activeOwnerKey = null;
                    SendReport(args.Player, unit, movement, state, "completed");
                    break;
                case "jump":
                    BeginRun(state, "jump", "none", "Capture takeoff, airborne, selected ground, and landing frames.");
                    _ = unit.TryJumpUnit();
                    SendReport(args.Player, unit, movement, state, "started");
                    break;
                case "stand_on_seam":
                    if (!RequireScenario(args.Player, state, ScenarioAdjacentSeam, command)) break;
                    BeginRun(state, "stand_on_seam", "none", "Grounded should remain stable while centered on the coplanar seam.");
                    state.Spawn = new Vector3(FixtureOrigin.X, FixtureOrigin.Y, StandingZ(movement));
                    WarpMotor(movement, state.Spawn);
                    SendReport(args.Player, unit, movement, state, "completed");
                    break;
                case "cross_seam":
                    if (!RequireScenario(args.Player, state, ScenarioAdjacentSeam, command)) break;
                    BeginRun(state, "cross_seam", "none", "Grounded and selected ground should remain stable while crossing the coplanar seam.");
                    var crossRunId = state.RunId;
                    state.Spawn = new Vector3(FixtureOrigin.X - 96f, FixtureOrigin.Y, StandingZ(movement));
                    WarpMotor(movement, state.Spawn);
                    movement.Move(new Vector3(180f, 0f, 0f));
                    SendReport(args.Player, unit, movement, state, "started");
                    _ = await Game.Delay(TimeSpan.FromMilliseconds(1200));
                    if (state.RunId != crossRunId) break;
                    movement.Stop();
                    SendReport(args.Player, unit, movement, state, "completed");
                    break;
                case "replace_same":
                    ReplaceSameCollider(args.Player, unit, movement, state);
                    break;
                case "dig":
                    if (!RequireReplacementScenario(args.Player, state, command)) break;
                    BeginRun(state, "dig", "delete_only", "The motor should lose ground and fall because the supporting collider is intentionally absent.");
                    DestroyRoot(state);
                    SendReport(args.Player, unit, movement, state, "completed");
                    break;
                case "restore":
                    if (!RequireReplacementScenario(args.Player, state, command)) break;
                    BeginRun(state, "restore", "create_after_gap", "The restored collider should become selectable ground again.");
                    RecreateReplacementRoot(unit, movement, state);
                    state.Spawn = new Vector3(FixtureOrigin.X, FixtureOrigin.Y, StandingZ(movement));
                    WarpMotor(movement, state.Spawn);
                    SendReport(args.Player, unit, movement, state, "completed");
                    break;
                case "step_32":
                    if (!RequireScenario(args.Player, state, ScenarioStepAndEdge, command)) break;
                    BeginRun(state, "step_32", "none", "Compare the 32-unit rise with the reported motor StepHeight.");
                    state.Spawn = new Vector3(FixtureOrigin.X - 128f, FixtureOrigin.Y - 260f, StandingZ(movement));
                    await MoveAcrossStep(args.Player, unit, movement, state, state.RunId);
                    break;
                case "step_64":
                    if (!RequireScenario(args.Player, state, ScenarioStepAndEdge, command)) break;
                    BeginRun(state, "step_64", "none", "Compare the 64-unit rise with the reported motor StepHeight.");
                    state.Spawn = new Vector3(FixtureOrigin.X - 128f, FixtureOrigin.Y + 260f, StandingZ(movement));
                    await MoveAcrossStep(args.Player, unit, movement, state, state.RunId);
                    break;
                case "edge":
                    if (!RequireScenario(args.Player, state, ScenarioStepAndEdge, command)) break;
                    BeginRun(state, "edge", "none", "Capture selected ground and penetration as the capsule overhangs a platform corner.");
                    state.Spawn = new Vector3(FixtureOrigin.X + 112f, FixtureOrigin.Y + 612f, StandingZ(movement));
                    WarpMotor(movement, state.Spawn);
                    SendReport(args.Player, unit, movement, state, "completed");
                    break;
                case "fallback_gap":
                    if (!RequireScenario(args.Player, state, ScenarioFallbackComparison, command)) break;
                    BeginRun(state, "fallback_gap", "none", "Comparison only: the project ray may accept support while the motor remains airborne.");
                    state.Spawn = new Vector3(
                        FixtureOrigin.X,
                        FixtureOrigin.Y,
                        FixtureTopZ + 64f + movement.Motor.Height * 0.5f);
                    WarpMotor(movement, state.Spawn);
                    SendReport(args.Player, unit, movement, state, "completed", includeFallback: true);
                    break;
                default:
                    SendErrorReport(args.Player, command, "unknown_fixture_command");
                    break;
            }
        }
        catch (Exception ex)
        {
            Game.Logger.LogError(ex, "KCC regression fixture command failed: {Command}", command);
            SendErrorReport(args.Player, command, ex.GetType().Name);
        }

        return true;
    }

    private static PhysicalCharacterMovement? EnsureFixtureMovement(Unit unit)
    {
        if (unit.GetUnitMovementMode() != UnitMovementMode.PhysicalCharacter)
        {
            _ = unit.ActivateUnitPhysicalCharacterMovement();
        }

        var movement = unit.GetPhysicalCharacterMovement();
        movement?.Activate();
        return movement;
    }

    private static async Task MoveAcrossStep(
        Player player, Unit unit, PhysicalCharacterMovement movement, FixtureState state, int runId)
    {
        WarpMotor(movement, state.Spawn);
        movement.Move(new Vector3(180f, 0f, 0f));
        SendReport(player, unit, movement, state, "started");
        _ = await Game.Delay(TimeSpan.FromMilliseconds(1200));
        if (state.RunId != runId)
        {
            return;
        }
        movement.Stop();
        SendReport(player, unit, movement, state, "completed");
    }

    private static void SetupScenario(
        Player player, Unit unit, PhysicalCharacterMovement movement, FixtureState state, string ownerKey, string scenario)
    {
        if (scenario is not (ScenarioStableBox or ScenarioAdjacentSeam or ScenarioDeleteFirstReplace
            or ScenarioStepAndEdge or ScenarioAddFirstReplace or ScenarioFallbackComparison))
        {
            SendErrorReport(player, scenario, "unknown_fixture_scenario");
            return;
        }
        if (activeOwnerKey != null && activeOwnerKey != ownerKey)
        {
            SendErrorReport(player, scenario, "fixture_in_use_by_another_player");
            return;
        }

        BeginRun(state, "setup", "new_fixture_root", ExpectedObservationFor(scenario));
        state.Scenario = scenario;
        DestroyRoot(state);
        try
        {
            CreateScenarioRoot(unit, movement, state, scenario);
            activeOwnerKey = ownerKey;
            WarpMotor(movement, state.Spawn);
            SendReport(player, unit, movement, state, "completed", scenario == ScenarioFallbackComparison);
        }
        catch
        {
            DestroyRoot(state);
            state.Scenario = "none";
            state.ExpectedObservation = "No fixture is active because setup failed.";
            if (activeOwnerKey == ownerKey) activeOwnerKey = null;
            throw;
        }
    }

    private static void CreateScenarioRoot(
        Unit unit, PhysicalCharacterMovement movement, FixtureState state, string scenario)
    {
        var syncRoot = unit.Scene.GetSyncRoot()
            ?? throw new InvalidOperationException("The current SCE scene has no sync root.");
        var root = syncRoot.CreateTemporaryChild("kcc_regression_fixture", NodeReplication.Local)
            ?? throw new InvalidOperationException("Failed to create the KCC regression fixture root.");

        state.RootGeneration++;
        state.Root = root;
        state.Boxes = [];
        var layer = SelectFixtureLayer(movement.Motor.CollisionMask);
        var mask = movement.Motor.CollisionLayer == 0 ? uint.MaxValue : movement.Motor.CollisionLayer;

        switch (scenario)
        {
            case ScenarioStableBox:
                AddBox(state, root, "stable_box", FixtureOrigin with { Z = FixtureTopZ - 32f }, new Vector3(512f, 512f, 64f), layer, mask);
                state.Spawn = new Vector3(FixtureOrigin.X, FixtureOrigin.Y, StandingZ(movement));
                break;
            case ScenarioAdjacentSeam:
                AddBox(state, root, "seam_left", new Vector3(FixtureOrigin.X - 128f, FixtureOrigin.Y, FixtureTopZ - 32f), new Vector3(256f, 512f, 64f), layer, mask);
                AddBox(state, root, "seam_right", new Vector3(FixtureOrigin.X + 128f, FixtureOrigin.Y, FixtureTopZ - 32f), new Vector3(256f, 512f, 64f), layer, mask);
                state.Spawn = new Vector3(FixtureOrigin.X, FixtureOrigin.Y, StandingZ(movement));
                break;
            case ScenarioDeleteFirstReplace:
            case ScenarioAddFirstReplace:
                AddBox(state, root, "replace_chunk", new Vector3(FixtureOrigin.X, FixtureOrigin.Y, FixtureTopZ - 32f), new Vector3(256f, 256f, 64f), layer, mask);
                state.Spawn = new Vector3(FixtureOrigin.X, FixtureOrigin.Y, StandingZ(movement));
                break;
            case ScenarioStepAndEdge:
                AddStepAndEdgeBoxes(state, root, layer, mask);
                state.Spawn = new Vector3(FixtureOrigin.X - 128f, FixtureOrigin.Y - 260f, StandingZ(movement));
                break;
            case ScenarioFallbackComparison:
                AddBox(state, root, "fallback_ground", new Vector3(FixtureOrigin.X, FixtureOrigin.Y, FixtureTopZ - 32f), new Vector3(256f, 256f, 64f), layer, mask);
                state.Spawn = new Vector3(
                    FixtureOrigin.X,
                    FixtureOrigin.Y,
                    FixtureTopZ + 64f + movement.Motor.Height * 0.5f);
                break;
        }
    }

    private static void AddStepAndEdgeBoxes(FixtureState state, Node root, uint layer, uint mask)
    {
        AddBox(state, root, "step32_approach", new Vector3(FixtureOrigin.X - 256f, FixtureOrigin.Y - 260f, FixtureTopZ - 32f), new Vector3(512f, 192f, 64f), layer, mask);
        AddBox(state, root, "step32_rise", new Vector3(FixtureOrigin.X + 64f, FixtureOrigin.Y - 260f, FixtureTopZ - 16f), new Vector3(128f, 192f, 96f), layer, mask);
        AddBox(state, root, "step64_approach", new Vector3(FixtureOrigin.X - 256f, FixtureOrigin.Y + 260f, FixtureTopZ - 32f), new Vector3(512f, 192f, 64f), layer, mask);
        AddBox(state, root, "step64_rise", new Vector3(FixtureOrigin.X + 64f, FixtureOrigin.Y + 260f, FixtureTopZ), new Vector3(128f, 192f, 128f), layer, mask);
        AddBox(state, root, "edge_platform", new Vector3(FixtureOrigin.X, FixtureOrigin.Y + 500f, FixtureTopZ - 32f), new Vector3(256f, 256f, 64f), layer, mask);
    }

    private static void AddBox(
        FixtureState state, Node root, string name, Vector3 center, Vector3 size, uint layer, uint mask)
    {
        var node = root.CreateTemporaryChild(name, NodeReplication.Local)
            ?? throw new InvalidOperationException($"Failed to create fixture box {name}.");
        node.Position = center;
        node.LocalScale = size;
        var body = node.CreateComponent<RigidBody>()
            ?? throw new InvalidOperationException($"Failed to create RigidBody for fixture box {name}.");
        body.Mass = 0f;
        body.Friction = 0.8f;
        body.SetCollisionLayerAndMask(layer, mask);
        var shape = node.CreateComponent<CollisionShape>()
            ?? throw new InvalidOperationException($"Failed to create CollisionShape for fixture box {name}.");
        shape.SetBox(Vector3.One);
        state.Boxes.Add(new BoxSnapshot(node.Id, name, center, size, layer, mask, ++state.CreateOrder));
    }

    private static void ReplaceSameCollider(
        Player player, Unit unit, PhysicalCharacterMovement movement, FixtureState state)
    {
        if (state.Scenario is not (ScenarioDeleteFirstReplace or ScenarioAddFirstReplace) || state.Root == null)
        {
            SendErrorReport(player, "replace_same", "replacement_scenario_required");
            return;
        }

        var deleteFirst = state.Scenario == ScenarioDeleteFirstReplace;
        BeginRun(
            state,
            "replace_same",
            deleteFirst ? "delete_first" : "add_first",
            "The same-shape replacement is compared with a true dig using authoritative motor frames.");

        if (deleteFirst)
        {
            DestroyRoot(state);
            RecreateReplacementRoot(unit, movement, state);
        }
        else
        {
            var oldRoot = state.Root;
            var oldBoxes = state.Boxes;
            state.Root = null;
            state.Boxes = [];
            RecreateReplacementRoot(unit, movement, state);
            oldRoot?.Dispose();
            oldBoxes.Clear();
        }

        SendReport(player, unit, movement, state, "completed");
    }

    private static bool RequireReplacementScenario(Player player, FixtureState state, string action)
    {
        var isReplacement = state.Scenario is ScenarioDeleteFirstReplace or ScenarioAddFirstReplace;
        var rootRequired = action != "restore";
        var canRestore = action != "restore" || state.LastAction == "dig";
        if (isReplacement && (!rootRequired || state.Root != null) && canRestore)
        {
            return true;
        }

        SendErrorReport(player, action, "active_replacement_scenario_required");
        return false;
    }

    private static bool RequireScenario(Player player, FixtureState state, string scenario, string action)
    {
        if (state.Scenario == scenario && state.Root != null)
        {
            return true;
        }

        SendErrorReport(player, action, $"scenario_required:{scenario}");
        return false;
    }

    private static void RecreateReplacementRoot(Unit unit, PhysicalCharacterMovement movement, FixtureState state)
    {
        var scenario = state.Scenario is ScenarioAddFirstReplace ? ScenarioAddFirstReplace : ScenarioDeleteFirstReplace;
        CreateScenarioRoot(unit, movement, state, scenario);
    }

    private static void BeginRun(FixtureState state, string action, string mutation, string expected)
    {
        state.RunId++;
        state.LastAction = action;
        state.MutationStrategy = mutation;
        state.ExpectedObservation = expected;
    }

    private static void DestroyRoot(FixtureState state)
    {
        state.Root?.Dispose();
        state.Root = null;
        state.Boxes.Clear();
    }

    private static float StandingZ(PhysicalCharacterMovement movement)
    {
        return FixtureTopZ + movement.Motor.Height * 0.5f + 2f;
    }

    private static void WarpMotor(PhysicalCharacterMovement movement, Vector3 position)
    {
        movement.Stop();
        movement.Activate();
        movement.Warp(position);
    }

    private static uint SelectFixtureLayer(uint motorMask)
    {
        if (motorMask == 0)
        {
            return 1;
        }

        return motorMask & unchecked(0u - motorMask);
    }

    private static string ExpectedObservationFor(string scenario)
    {
        return scenario switch
        {
            ScenarioStableBox => "Stable ground is the baseline for stand, jump, and landing frames.",
            ScenarioAdjacentSeam => "Coplanar boxes isolate standing on and crossing a collision seam.",
            ScenarioDeleteFirstReplace => "Delete-first same-shape replacement is compared with a true dig.",
            ScenarioStepAndEdge => "32/64-unit rises and a corner overhang are compared with the actual StepHeight.",
            ScenarioAddFirstReplace => "Add-first same-shape replacement is compared with delete-first using identical geometry.",
            ScenarioFallbackComparison => "The project ray fallback is observation-only and never controls jump eligibility.",
            _ => "Unknown fixture scenario.",
        };
    }

    private static void SendReport(
        Player player,
        Unit unit,
        PhysicalCharacterMovement movement,
        FixtureState state,
        string phase,
        bool includeFallback = false)
    {
        var boxes = new JsonArray();
        foreach (var box in state.Boxes)
        {
            boxes.Add(new JsonObject
            {
                ["node_id"] = box.NodeId,
                ["name"] = box.Name,
                ["center"] = VectorToJson(box.Center),
                ["size"] = VectorToJson(box.Size),
                ["collision_layer"] = box.CollisionLayer,
                ["collision_mask"] = box.CollisionMask,
                ["create_order"] = box.CreateOrder,
            });
        }

        var report = new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = "ok",
            ["run_id"] = state.RunId,
            ["scenario"] = state.Scenario,
            ["phase"] = phase,
            ["action"] = state.LastAction,
            ["player_id"] = player.Id.ToString(),
            ["unit_id"] = unit.EntityId,
            ["fixture_origin"] = VectorToJson(FixtureOrigin),
            ["spawn"] = VectorToJson(state.Spawn),
            ["root_generation"] = state.RootGeneration,
            ["mutation_strategy"] = state.MutationStrategy,
            ["server_time_ms"] = Game.TotalElapsedTickInMilliseconds,
            ["expected_observation"] = state.ExpectedObservation,
            ["boxes"] = boxes,
            ["motor"] = new JsonObject
            {
                ["radius"] = movement.Motor.Radius,
                ["height"] = movement.Motor.Height,
                ["step_height"] = movement.Motor.StepHeight,
                ["max_slope_degrees"] = movement.Motor.MaxSlopeDegrees,
                ["collision_layer"] = movement.Motor.CollisionLayer,
                ["collision_mask"] = movement.Motor.CollisionMask,
                ["use_character_collision_roles"] = movement.Motor.UseCharacterCollisionRoles,
            },
        };

        if (includeFallback || state.Scenario == ScenarioFallbackComparison)
        {
            report["fallback"] = BuildFallbackComparison(unit, movement, state);
        }

        SendReportJson(player, report);
    }

    private static JsonObject BuildFallbackComparison(
        Unit unit, PhysicalCharacterMovement movement, FixtureState state)
    {
        // Use the commanded motor center so the immediate report cannot observe a stale Unit binding position.
        var foot = state.Spawn - new Vector3(0f, 0f, movement.Motor.Height * 0.5f);
        var origin = foot + new Vector3(0f, 0f, 16f);
        const float distance = 80f;
        var result = new JsonObject
        {
            ["comparison_only"] = true,
            ["position_source"] = "commanded_motor_spawn",
            ["foot_position"] = VectorToJson(foot),
            ["origin"] = VectorToJson(origin),
            ["distance"] = distance,
            ["accepted"] = false,
        };

        var world = unit.GetOwnerPhysicsWorld();
        var hits = world?.RaycastAll(origin, new Vector3(0f, 0f, -1f), distance) ?? [];
        foreach (var hit in hits)
        {
            if (hit.Position.Z < foot.Z - 72f)
            {
                continue;
            }

            var body = hit.GetRigidBody();
            result["accepted"] = true;
            result["hit_position"] = VectorToJson(hit.Position);
            result["hit_normal"] = VectorToJson(hit.Normal);
            result["hit_distance"] = hit.Distance;
            result["collision_layer"] = body?.CollisionLayer;
            result["collision_mask"] = body?.CollisionMask;
            break;
        }

        return result;
    }

    private static JsonObject VectorToJson(Vector3 value)
    {
        return new JsonObject { ["x"] = value.X, ["y"] = value.Y, ["z"] = value.Z };
    }

    private static void SendErrorReport(Player player, string command, string error)
    {
        SendReportJson(player, new JsonObject
        {
            ["schema_version"] = 1,
            ["status"] = "error",
            ["command"] = command,
            ["error"] = error,
            ["server_time_ms"] = Game.TotalElapsedTickInMilliseconds,
        });
    }

    private static void SendReportJson(Player player, JsonObject report)
    {
        var sent = new ProtoCustomMessage
        {
            Message = Encoding.UTF8.GetBytes(ReportPrefix + report.ToJsonString()),
        }.SendTo(player);

        if (!sent)
        {
            Game.Logger.LogWarning("KCC regression fixture failed to send report to player {PlayerId}.", player.Id);
        }
    }

    private static void ClearPlayerState(Player player)
    {
        var key = player.Id.ToString();
        if (!States.Remove(key, out var state))
        {
            return;
        }

        state.RunId++;
        DestroyRoot(state);
        if (activeOwnerKey == key) activeOwnerKey = null;
    }

    private static void ClearAllStates()
    {
        foreach (var state in States.Values)
        {
            state.RunId++;
            DestroyRoot(state);
        }
        States.Clear();
        activeOwnerKey = null;
    }

    public static void OnRegisterGameClass()
    {
        Game.OnGameTriggerInitialization += OnGameTriggerInitialization;
        Game.OnGameEnd += ClearAllStates;
    }
}
#endif
