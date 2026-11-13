#if CLIENT
using GameUI.Debug;
using System.Text;
using System.Text.Json.Nodes;

namespace GameEntry;

public sealed partial class KccRegressionFixture
{
    private static Trigger<EventServerMessage>? reportTrigger;
    private static Panel? fixturePanel;
    private static Label? fixtureStatusLabel;
    private static string? latestReportJson;
    private static string lastCommand = "none";
    private static bool lastCommandSent;

    private static void OnGameTriggerInitialization()
    {
        RegisterReportHandler();
        RegisterRuntimeTool();
        new Trigger<EventGameStart>(static async (_, _) =>
        {
            CreateFixturePanel();
            await Task.CompletedTask;
            return true;
        }).Register(Game.Instance);
    }

    private static void RegisterReportHandler()
    {
        if (reportTrigger != null)
        {
            return;
        }

        reportTrigger = new Trigger<EventServerMessage>(static async (_, args) =>
        {
            var json = DecodeFixtureMessage(args.Message, ReportPrefix);
            if (json == null)
            {
                return false;
            }

            latestReportJson = json;
            UpdateStatusLabel();
            await Task.CompletedTask;
            return true;
        }, true);
        reportTrigger.Register(Game.Instance);
    }

    private static void RegisterRuntimeTool()
    {
        _ = RuntimeDebugToolRegistry.Register(
            "project.kcc_fixture_status",
            "Return the latest authoritative KCC regression fixture setup or action report.",
            new JsonObject { ["type"] = "object" },
            _ => BuildFixtureStatus());
    }

    private static JsonObject BuildFixtureStatus()
    {
        JsonNode? report = null;
        if (!string.IsNullOrWhiteSpace(latestReportJson))
        {
            try
            {
                report = JsonNode.Parse(latestReportJson);
            }
            catch (Exception ex)
            {
                report = new JsonObject
                {
                    ["status"] = "invalid_server_report",
                    ["error"] = ex.Message,
                };
            }
        }

        return new JsonObject
        {
            ["tool"] = "project.kcc_fixture_status",
            ["last_command"] = lastCommand,
            ["last_command_sent"] = lastCommandSent,
            ["report"] = report,
        };
    }

    private static void CreateFixturePanel()
    {
        if (fixturePanel != null)
        {
            UpdateStatusLabel();
            return;
        }

        fixturePanel = new Panel
        {
            Width = 760,
            Height = AutoMode.Auto,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(20, 0, 0, 20),
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromArgb(220, 14, 18, 24)),
            CornerRadius = 6,
            FlowOrientation = Orientation.Vertical,
        };

        _ = new Label
        {
            Text = "KCC 固定回归夹具",
            FontSize = 17,
            Bold = true,
            TextColor = Color.White,
            Width = AutoMode.Auto,
            Height = AutoMode.Auto,
            Margin = new Thickness(0, 0, 0, 8),
            IsStatic = true,
            Parent = fixturePanel,
        };

        var scenarios = CreateRow();
        AddButton(scenarios, "KCC 稳定盒", $"setup/{ScenarioStableBox}");
        AddButton(scenarios, "KCC 接缝", $"setup/{ScenarioAdjacentSeam}");
        AddButton(scenarios, "KCC 先删后建", $"setup/{ScenarioDeleteFirstReplace}");
        AddButton(scenarios, "KCC 先建后删", $"setup/{ScenarioAddFirstReplace}");
        AddButton(scenarios, "KCC 台阶边角", $"setup/{ScenarioStepAndEdge}");
        AddButton(scenarios, "KCC 射线对照", $"setup/{ScenarioFallbackComparison}");

        var actions = CreateRow();
        AddButton(actions, "KCC 跳跃", "jump");
        AddButton(actions, "KCC 站接缝", "stand_on_seam");
        AddButton(actions, "KCC 跨接缝", "cross_seam");
        AddButton(actions, "KCC 同形替换", "replace_same");
        AddButton(actions, "KCC 真挖空", "dig");
        AddButton(actions, "KCC 恢复", "restore");

        var positions = CreateRow();
        AddButton(positions, "KCC 32台阶", "step_32");
        AddButton(positions, "KCC 64台阶", "step_64");
        AddButton(positions, "KCC 边角", "edge");
        AddButton(positions, "KCC 64间隙", "fallback_gap");
        AddButton(positions, "KCC 状态", "status");
        AddButton(positions, "KCC 清理", "destroy");

        fixtureStatusLabel = new Label
        {
            Text = "尚未启用夹具。先选择场景，再通过 MotorProbe + motor.snapshot 采集。",
            FontSize = 13,
            TextColor = Color.FromArgb(255, 195, 210, 224),
            Width = 736,
            Height = AutoMode.Auto,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            IsStatic = true,
            Parent = fixturePanel,
        };

        GameUI.Control.UIRoot.Instance.AddChild(fixturePanel);
    }

    private static Panel CreateRow()
    {
        return new Panel
        {
            Width = AutoMode.Auto,
            Height = AutoMode.Auto,
            FlowOrientation = Orientation.Horizontal,
            HorizontalContentAlignment = HorizontalContentAlignment.Left,
            Margin = new Thickness(0, 0, 0, 6),
            Parent = fixturePanel,
        };
    }

    private static void AddButton(Panel row, string text, string command)
    {
        var button = new Button
        {
            Width = 116,
            Height = 38,
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromArgb(235, 48, 92, 122)),
            CornerRadius = 4,
            Parent = row,
        };
        _ = new Label
        {
            Text = text,
            FontSize = 12,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalContentAlignment.Center,
            VerticalContentAlignment = VerticalContentAlignment.Center,
            IsStatic = true,
            Parent = button,
        };
        button.OnPointerClicked += (_, _) => SendFixtureCommand(command);
    }

    private static void SendFixtureCommand(string command)
    {
        lastCommand = command;
        lastCommandSent = new ProtoCustomMessage
        {
            Message = Encoding.UTF8.GetBytes(CommandPrefix + command),
        }.SendToServer();
        UpdateStatusLabel();
    }

    private static void UpdateStatusLabel()
    {
        if (fixtureStatusLabel == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(latestReportJson))
        {
            fixtureStatusLabel.Text = $"最近命令: {lastCommand}  已发送: {lastCommandSent}";
            return;
        }

        try
        {
            var report = JsonNode.Parse(latestReportJson) as JsonObject;
            fixtureStatusLabel.Text =
                $"状态: {report?["status"]}  场景: {report?["scenario"]}  动作: {report?["action"]}  run: {report?["run_id"]}  generation: {report?["root_generation"]}";
        }
        catch
        {
            fixtureStatusLabel.Text = "服务器夹具报告无法解析；请查看 project.kcc_fixture_status。";
        }
    }

    public static void OnRegisterGameClass()
    {
        Game.OnGameTriggerInitialization += OnGameTriggerInitialization;
        Game.OnGameEnd += ClearClientState;
    }

    private static void ClearClientState()
    {
        fixturePanel?.Destroy();
        fixturePanel = null;
        fixtureStatusLabel = null;
        reportTrigger?.Destroy();
        reportTrigger = null;
        latestReportJson = null;
        lastCommand = "none";
        lastCommandSent = false;
    }
}
#endif
