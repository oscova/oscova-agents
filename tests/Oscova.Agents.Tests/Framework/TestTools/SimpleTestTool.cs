using System.ComponentModel;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Tests.Framework.TestTools
{
    public class SimpleTestTool
    {
        [AgentTool]
        public static double Add(double one, double two)
        {
            return one + two;
        }

        [AgentTool]
        [DisplayName("risky")]
        [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
        public static bool Risky() => true;

        [AgentTool]
        [DisplayName("subtraction")]
        public static double Subtract(double one, double two)
        {
            return one - two;
        }

        [AgentTool]
        [DisplayName("open-application")]
        public static bool OpenApp(string applicationName)
        {
            return true;
        }

        [AgentTool]
        [Description("Turns on music in an embedded player.")]
        public static bool TurnOnMusic()
        {
            return true;
        }

        [AgentTool]
        public static bool PlaySong(string songName)
        {
            return true;
        }

        [AgentTool]
        [DisplayName("multiply")]
        public static double Multiply(double one, double two)
        {
            return one * two;
        }
    }
}
