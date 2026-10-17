using Oscova.Agents.Avatar.Contracts;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Oscova.Agents.Tests.Framework.Avatar;

public sealed class AvatarContractTests
{
    [Fact]
    public void PublicAvatarContractPropertiesPinPascalCaseJsonNames()
    {
        var contractType = typeof(AvatarBridgeOperationResult);
        var contractTypes = contractType.Assembly
            .GetTypes()
            .Where(type => type.IsClass
                && type.IsPublic
                && type.Namespace == contractType.Namespace)
            .ToArray();

        Assert.NotEmpty(contractTypes);

        foreach (var property in contractTypes.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)))
        {
            var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal(property.Name, attribute.Name);
        }
    }
}
