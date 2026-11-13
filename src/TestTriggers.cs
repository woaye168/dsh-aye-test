using GameCore.GameSystem.Data;

namespace GameEntry;

public class TestTriggers : IGameClass
{
    private static void Game_OnGameTriggerInitialization()
    {
        new Trigger<EventGameStart>(static (s, d) =>
        {
            Game.Logger.LogInformation("Hello World!");
#if SERVER
            Game.Logger.LogInformation("This is running on the server side.");
#elif CLIENT
            Game.Logger.LogInformation("This is running on the client side.");
#endif
            return Task.CompletedTask;
        }).Register(Game.Instance);
    }

    public static void OnRegisterGameClass()
    {
        // 当前模板默认测试模式来自项目数据 GameEntry.ScopeData.GameDataGameMode.MapGameMode。
        if (GameDataGlobalConfig.TestGameMode != ScopeData.GameDataGameMode.MapGameMode)
        {
            return;
        }
        Game.OnGameTriggerInitialization += Game_OnGameTriggerInitialization;
    }
}
