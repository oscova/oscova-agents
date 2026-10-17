using Oscova.Agents.Collections;
using Oscova.Agents.Extensions;
using Oscova.Agents.Tests.Framework.TestTools;

namespace Oscova.Agents.Tests.Framework.Extensions
{
    public class AIFunctionExtensionsTests
    {
        [Fact]
        public void GetSchemaSummary_Subtract_ProducesExpectedSummary()
        {
            // Arrange: build tools from the SimpleTestTool toolset
            var tools = new AgentToolCollection();
            tools.Add(new SimpleTestTool());

            var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
            var subtract = tools.Find(ToolNaming.BuildQualifiedName(qualifier, "subtraction"));
            Assert.NotNull(subtract);

            // Act
            var summary = subtract!.GetSchemaSummary();

            // Assert: shape/contents of the schema summary
            Assert.Contains($"Tool: {ToolNaming.BuildQualifiedName(qualifier, "subtraction")}", summary);
            Assert.Contains("Arguments:", summary);
            Assert.Contains("- one (number, required)", summary);
            Assert.Contains("- two (number, required)", summary);
        }
    }
}
