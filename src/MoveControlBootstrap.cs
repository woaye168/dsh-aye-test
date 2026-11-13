using GameCore.GameSystem.Data;

#if CLIENT
using GameUI.Control;
using GameUI.Control.Data;
#endif

namespace GameEntry;

public class MoveControlBootstrap : IGameClass
{
#if CLIENT
    private static void OnGameTriggerInitialization()
    {
        new Trigger<EventGameStart>(static async (sender, args) =>
        {
            var player = Player.LocalPlayer;
            if (player == null)
            {
                Game.Logger.LogWarning("Move control bootstrap skipped because LocalPlayer is null.");
                return true;
            }

            _ = GSUI.GameDataControl.MoveControl.Data!
                .CreatePlatformAdaptivePanel().AddToVisualTree();

            await Task.CompletedTask;
            return true;
        }).Register(Game.Instance);
    }
#endif

    public static void OnRegisterGameClass()
    {
#if CLIENT
        if (GameDataGlobalConfig.TestGameMode != ScopeData.GameDataGameMode.MapGameMode)
        {
            return;
        }

        Game.OnGameTriggerInitialization += OnGameTriggerInitialization;
#endif
    }
}