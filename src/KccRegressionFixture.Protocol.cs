using System.Text;

namespace GameEntry;

public sealed partial class KccRegressionFixture : IGameClass
{
    private const string CommandPrefix = "kcc-regression:command:";
    private const string ReportPrefix = "kcc-regression:report:";

    private const string ScenarioStableBox = "stable_box";
    private const string ScenarioAdjacentSeam = "adjacent_seam";
    private const string ScenarioDeleteFirstReplace = "delete_first_replace";
    private const string ScenarioStepAndEdge = "step_and_edge";
    private const string ScenarioAddFirstReplace = "add_first_replace";
    private const string ScenarioFallbackComparison = "project_fallback_comparison";

    private static string? DecodeFixtureMessage(byte[] message, string prefix)
    {
        var text = Encoding.UTF8.GetString(message);
        return text.StartsWith(prefix, StringComparison.Ordinal)
            ? text[prefix.Length..]
            : null;
    }
}
