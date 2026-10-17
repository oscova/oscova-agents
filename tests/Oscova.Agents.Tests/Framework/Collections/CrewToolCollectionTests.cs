using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Policies;
using Oscova.Agents.Tests.Framework.TestTools;
using System.ComponentModel;

namespace Oscova.Agents.Tests.Framework.Collections
{
    public class CrewToolCollectionTests
    {
        [Fact]
        public void BothReflectionPaths_ExposeOnlyMarkedTools()
        {
            var toolset = new ReflectionBoundaryToolset();
            var collection = new AgentToolCollection();

            collection.Add(toolset);
            var direct = AgentToolCollection.CreateToolsFromToolset(toolset).ToArray();

            Assert.Equal(2, collection.Count);
            Assert.Equal(collection.Select(tool => tool.Name), direct.Select(tool => tool.Name));
            var owner = ToolNaming.GetToolsetOwner(typeof(ReflectionBoundaryToolset));
            var marked = ToolNaming.BuildQualifiedName(owner, "marked");
            Assert.Equal("Visible method", collection.Find(marked)?.Description);
            Assert.Equal(NetworkScope.Egress, collection.GetNetworkScope(marked));
            Assert.Null(collection.Find(ToolNaming.BuildQualifiedName(owner, "Helper")));
            Assert.DoesNotContain(direct, tool => tool.UnderlyingMethod?.IsSpecialName == true);
        }

        [Fact]
        public void TypedToolNames_MatchActualRegistration()
        {
            var toolset = new SimpleTestTool();
            var collection = new AgentToolCollection();
            collection.Add(toolset);
            collection.Add(toolset, "sample.plugin");
            foreach (var method in new[] { nameof(SimpleTestTool.Add), nameof(SimpleTestTool.Subtract), nameof(SimpleTestTool.OpenApp) })
            {
                var local = ToolNaming.BuildToolsetQualifiedName<SimpleTestTool>(method);
                var plugin = ToolNaming.BuildToolsetQualifiedName<SimpleTestTool>(method, "sample.plugin");
                Assert.NotNull(collection.Find(local));
                Assert.NotNull(collection.Find(plugin));
                Assert.Contains(AgentToolCollection.CreateToolsFromToolset(toolset), tool => tool.Name == local);
            }
            Assert.Throws<ArgumentException>(() =>
                ToolNaming.BuildToolsetQualifiedName<SimpleTestTool>("MissingMethod"));
        }

        [Fact]
        public void Adds_SimpleTestTool_Methods_AreAdded()
        {
            // Arrange
            var collection = new AgentToolCollection();
            var toolset = new SimpleTestTool();

            // Act
            collection.Add(toolset);

            // Assert
            Assert.Equal(7, collection.Count); // Add, Risky, Subtract, OpenApp, TurnOnMusic, PlaySong, Multiply

            var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));

            Assert.NotNull(collection.Find(ToolNaming.BuildQualifiedName(qualifier, "Add")));
            Assert.NotNull(collection.Find(ToolNaming.BuildQualifiedName(qualifier, "subtraction")));
            Assert.NotNull(collection.Find(ToolNaming.BuildQualifiedName(qualifier, "open-application")));
            Assert.NotNull(collection.Find(ToolNaming.BuildQualifiedName(qualifier, "TurnOnMusic")));
            Assert.NotNull(collection.Find(ToolNaming.BuildQualifiedName(qualifier, "PlaySong")));

            // Multiply has [DisplayName("multiply")] so it is found under the class-prefix qualified name
            Assert.NotNull(collection.Find(ToolNaming.BuildQualifiedName(qualifier, "multiply")));
            // Old tools/math/multiply style is no longer generated
            Assert.Null(collection.Find("tools/math/multiply"));
        }

        [Fact]
        public void Function_Names_Follow_DisplayNameAttribute()
        {
            // Arrange
            var collection = new AgentToolCollection();
            var toolset = new SimpleTestTool();
            collection.Add(toolset);

            // Act
            var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
            var addInfo = collection.Find(ToolNaming.BuildQualifiedName(qualifier, "Add"));
            var subtractInfo = collection.Find(ToolNaming.BuildQualifiedName(qualifier, "subtraction"));
            var openAppInfo = collection.Find(ToolNaming.BuildQualifiedName(qualifier, "open-application"));
            var multiplyInfo = collection.Find(ToolNaming.BuildQualifiedName(qualifier, "multiply"));

            // Assert: AIFunction derives from AITool and has correct names
            Assert.NotNull(addInfo);
            Assert.IsAssignableFrom<AITool>(addInfo);
            Assert.Equal(ToolNaming.BuildQualifiedName(qualifier, "Add"), addInfo!.Name);
            // OriginalName and ServerName via collection metadata
            Assert.Equal("add", collection.GetOriginalName(addInfo));
            Assert.Equal(qualifier, collection.GetServerName(addInfo));

            Assert.NotNull(subtractInfo);
            Assert.IsAssignableFrom<AITool>(subtractInfo);
            Assert.Equal(ToolNaming.BuildQualifiedName(qualifier, "subtraction"), subtractInfo!.Name);
            Assert.Equal("Subtract", subtractInfo.UnderlyingMethod?.Name);
            // OriginalName follows [DisplayName]-provided name
            Assert.Equal("subtraction", collection.GetOriginalName(subtractInfo));

            Assert.NotNull(openAppInfo);
            Assert.IsAssignableFrom<AITool>(openAppInfo);
            Assert.Equal(ToolNaming.BuildQualifiedName(qualifier, "open-application"), openAppInfo!.Name);
            Assert.Equal("OpenApp", openAppInfo.UnderlyingMethod?.Name);
            Assert.Equal("open-application", collection.GetOriginalName(openAppInfo));

            Assert.NotNull(multiplyInfo);
            Assert.IsAssignableFrom<AITool>(multiplyInfo);
            Assert.Equal(ToolNaming.BuildQualifiedName(qualifier, "multiply"), multiplyInfo!.Name);
            Assert.Equal("Multiply", multiplyInfo.UnderlyingMethod?.Name);
            Assert.Equal("multiply", collection.GetOriginalName(multiplyInfo));
        }

        [Fact]
        public void Function_Description_From_DescriptionAttribute()
        {
            // Arrange
            var collection = new AgentToolCollection();
            var toolset = new SimpleTestTool();
            collection.Add(toolset);

            // Act
            var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
            var turnOnMusicInfo = collection.Find(ToolNaming.BuildQualifiedName(qualifier, "TurnOnMusic"));

            // Assert
            Assert.NotNull(turnOnMusicInfo);
            Assert.Equal("Turns on music in an embedded player.", turnOnMusicInfo!.Description);
        }

        [Fact]
        public void ToolInfo_Metadata_IsTracked()
        {
            var collection = new AgentToolCollection();
            var qualifier = ToolNaming.GetToolsetOwner(typeof(SimpleTestTool));
            var addQualifiedName = ToolNaming.BuildQualifiedName(qualifier, "Add");

            collection.Add(new SimpleTestTool());

            var info = collection.Find(addQualifiedName);
            Assert.NotNull(info);
            Assert.Equal(addQualifiedName, info!.Name);
            // OriginalName for Add is lowercase after normalization
            Assert.Equal("add", collection.GetOriginalName(info));

            var registeredFunction = collection.Find(addQualifiedName);
            Assert.NotNull(registeredFunction);
            Assert.Equal(addQualifiedName, registeredFunction!.Name);

            var toolsByServer = collection.GetToolsByServer(qualifier);
            Assert.Contains(toolsByServer, ti => ti.Name == addQualifiedName);

            Assert.True(collection.Remove(addQualifiedName));
            Assert.Null(collection.Find(addQualifiedName));
            Assert.Equal(6, collection.Count);
        }

        [Fact]
        public void Add_StaticToolMethod_IsDiscovered()
        {
            var collection = new AgentToolCollection();

            collection.Add(new StaticToolset());

            var qualifier = ToolNaming.GetToolsetOwner(typeof(StaticToolset));
            var qualifiedName = ToolNaming.BuildQualifiedName(qualifier, "ping");
            var tool = collection.Find(qualifiedName);

            Assert.NotNull(tool);
            Assert.Equal("StaticPing", tool!.UnderlyingMethod?.Name);
            Assert.Equal("ping", collection.GetOriginalName(tool));
            Assert.Equal(qualifier, collection.GetServerName(tool));
        }

        [Fact]
        public void ToolNaming_TruncatesLongWireNamesToModelSafeLimit()
        {
            var name = ToolNaming.BuildToolsetQualifiedName(
                "example.plugin.with.a.very.long.identifier",
                "extremely_long_toolset_owner_name",
                "method_with_a_very_long_display_name");

            Assert.True(name.Length <= ToolNaming.MaxWireNameLength);
            Assert.Contains(ToolNaming.WireSeparator, name);
        }

        [Fact]
        public void GetNetworkScope_Uses_ReflectedMethodAttribute()
        {
            var collection = new AgentToolCollection();
            var qualifier = ToolNaming.GetToolsetOwner(typeof(NetworkDeclarationToolset));

            collection.Add(new NetworkDeclarationToolset());

            Assert.Equal(NetworkScope.Egress, collection.GetNetworkScope(ToolNaming.BuildQualifiedName(qualifier, "internet-tool")));
            Assert.Equal(NetworkScope.Incidental, collection.GetNetworkScope(ToolNaming.BuildQualifiedName(qualifier, "shell-tool")));
            Assert.Equal(NetworkScope.None, collection.GetNetworkScope(ToolNaming.BuildQualifiedName(qualifier, "local-tool")));
        }

        [Fact]
        public void GetNetworkScope_ExternalTool_DefaultsToNone()
        {
            var collection = new AgentToolCollection();
            var method = typeof(CrewToolCollectionTests).GetMethod(nameof(ExternalToolStub))!;
            var function = AIFunctionFactory.Create(
                method,
                target: null,
                options: new AIFunctionFactoryOptions { Name = "external_provider_external_tool" });

            collection.Add(function, "external-provider", "external-tool");

            Assert.Equal(NetworkScope.None, collection.GetNetworkScope(function.Name));
        }

        [Fact]
        public void TrySetNetworkScope_ChangesEffectiveScope_ForExistingTool()
        {
            var collection = new AgentToolCollection();
            var method = typeof(CrewToolCollectionTests).GetMethod(nameof(ExternalToolStub))!;
            var function = AIFunctionFactory.Create(
                method,
                target: null,
                options: new AIFunctionFactoryOptions { Name = "external_provider__external_tool" });
            collection.Add(function, "external-provider", "external-tool");

            var updated = collection.TrySetNetworkScope(function.Name, NetworkScope.Egress);

            Assert.True(updated);
            Assert.Equal(NetworkScope.Egress, collection.GetNetworkScope(function.Name));
        }

        [Fact]
        public void TrySetNetworkScope_NormalizesQualifiedNameSeparators()
        {
            var collection = new AgentToolCollection();
            var method = typeof(CrewToolCollectionTests).GetMethod(nameof(ExternalToolStub))!;
            var function = AIFunctionFactory.Create(
                method,
                target: null,
                options: new AIFunctionFactoryOptions { Name = "external_provider__external_tool" });
            collection.Add(function, "external-provider", "external-tool");

            var updated = collection.TrySetNetworkScope("external_provider/external_tool", NetworkScope.Egress);

            Assert.True(updated);
            Assert.Equal(NetworkScope.Egress, collection.GetNetworkScope(function.Name));
        }

        [Fact]
        public void TrySetNetworkScope_PreservesDeclaredScope_ForExistingTool()
        {
            var collection = new AgentToolCollection();
            var qualifier = ToolNaming.GetToolsetOwner(typeof(NetworkDeclarationToolset));
            var qualifiedName = ToolNaming.BuildQualifiedName(qualifier, "internet-tool");
            collection.Add(new NetworkDeclarationToolset());

            var updated = collection.TrySetNetworkScope(qualifiedName, NetworkScope.None);

            Assert.True(updated);
            Assert.Equal(NetworkScope.Egress, collection.GetDeclaredNetworkScope(qualifiedName));
            Assert.Equal(NetworkScope.None, collection.GetNetworkScope(qualifiedName));
        }

        [Fact]
        public void TrySetNetworkScope_UnknownTool_ReturnsFalse()
        {
            var collection = new AgentToolCollection();

            var updated = collection.TrySetNetworkScope("missing__tool", NetworkScope.Egress);

            Assert.False(updated);
            Assert.Equal(NetworkScope.None, collection.GetNetworkScope("missing__tool"));
        }

        public static string ExternalToolStub()
        {
            return "ok";
        }

        private sealed class StaticToolset
        {
            [AgentTool]
            [DisplayName("ping")]
            public static string StaticPing()
            {
                return "pong";
            }
        }

        private sealed class ReflectionBoundaryToolset
        {
            [AgentTool]
            [DisplayName("marked")]
            [Description("Visible method")]
            [ToolNetworkScope(NetworkScope.Egress)]
            public static string Marked() => "ok";

            [AgentTool]
            public string Instance() => Property;

            [Description("A described helper is not callable.")]
            public string Helper() => Property;

            public string Property { get; } = "hidden";
        }

        private sealed class NetworkDeclarationToolset
        {
            [AgentTool]
            [DisplayName("internet-tool")]
            [ToolNetworkScope(NetworkScope.Egress)]
            public static string InternetTool()
            {
                return "ok";
            }

            [AgentTool]
            [DisplayName("shell-tool")]
            [ToolNetworkScope(NetworkScope.Incidental)]
            public static string ShellTool()
            {
                return "ok";
            }

            [AgentTool]
            [DisplayName("local-tool")]
            [ToolNetworkScope(NetworkScope.None)]
            public static string LocalTool()
            {
                return "ok";
            }
        }
    }
}
