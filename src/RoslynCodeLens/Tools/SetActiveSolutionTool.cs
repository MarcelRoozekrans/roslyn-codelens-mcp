using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class SetActiveSolutionTool
{
    [McpServerTool(Name = "set_active_solution"),
     Description("Switch the active solution. All subsequent tool calls will operate on the selected solution. " +
                 "Use a partial, case-insensitive name (e.g. 'MyProject' matches 'C:/code/MyProject/MyProject.sln'). " +
                 "Returns the full path of the newly active solution.")]
    public static string Execute(
        MultiSolutionManager manager,
        [Description("Partial or full solution name/path to match")] string name)
    {
        return manager.SetActiveSolution(name);
    }
}
