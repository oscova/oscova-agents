using System.ComponentModel;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Tests.Framework.TestTools
{
    public class SimpleTestTool
    {
        public static double Add(double one, double two)
        {
            return one + two;
        }

        [DisplayName("risky")]
        [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
        public static bool Risky() => true;

        [DisplayName("subtraction")]
        public static double Subtract(double one, double two)
        {
            return one - two;
        }

        [DisplayName("open-application")]
        public static bool OpenApp(string applicationName)
        {
            return true;
        }

        [Description("Turns on music in an embedded player.")]
        public static bool TurnOnMusic()
        {
            return true;
        }

        public static bool PlaySong(string songName)
        {
            return true;
        }

        [DisplayName("multiply")]
        public static double Multiply(double one, double two)
        {
            return one * two;
        }
    }
}
