using System.Text.Json;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Tests.Framework.Tasks;

public sealed class TaskExternalTriggerValidatorTests
{
    [Fact]
    public void ValidateParameters_WhenSourceDocumentIsDisposed_ReturnsUsableClones()
    {
        IReadOnlyDictionary<string, JsonElement> result;
        using (var document = JsonDocument.Parse("{\"name\":\"build\",\"count\":3,\"flag\":true,\"payload\":{\"id\":1}}"))
        {
            var supplied = document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value);
            result = TaskExternalTriggerValidator.ValidateParameters(CreateTask(), supplied);
        }

        Assert.Equal("build", result["name"].GetString());
        Assert.Equal(3, result["count"].GetInt32());
        Assert.True(result["flag"].GetBoolean());
        Assert.Equal(1, result["payload"].GetProperty("id").GetInt32());
    }

    [Fact]
    public void ValidateParameters_WhenSuppliedNameDiffersByCase_UsesDeclaredName()
    {
        var supplied = new Dictionary<string, JsonElement>
        {
            ["NAME"] = JsonSerializer.SerializeToElement("build")
        };

        var result = TaskExternalTriggerValidator.ValidateParameters(CreateTask(), supplied);

        Assert.Equal("name", Assert.Single(result).Key);
        Assert.Equal("build", result["NAME"].GetString());
    }

    [Theory]
    [InlineData("{\"extra\":\"x\"}", TaskExternalTriggerValidationFailureKind.UnknownParameter, "extra", null)]
    [InlineData("{\"count\":3}", TaskExternalTriggerValidationFailureKind.RequiredParameterMissing, "name", null)]
    [InlineData("{\"name\":3}", TaskExternalTriggerValidationFailureKind.ParameterKindMismatch, "name", TaskExternalParameterKind.String)]
    public void ValidateParameters_WhenPayloadIsInvalid_ReportsTypedFailure(
        string json,
        TaskExternalTriggerValidationFailureKind expectedFailure,
        string expectedParameter,
        TaskExternalParameterKind? expectedKind)
    {
        using var document = JsonDocument.Parse(json);
        var supplied = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

        var exception = Assert.Throws<TaskExternalTriggerValidationException>(
            () => TaskExternalTriggerValidator.ValidateParameters(CreateTask(), supplied));

        Assert.Equal(expectedFailure, exception.FailureKind);
        Assert.Equal(expectedParameter, exception.ParameterName);
        Assert.Equal(expectedKind, exception.ExpectedKind);
    }

    [Fact]
    public void ValidateParameters_WhenExternalTriggerIsDisabled_ReportsTypedFailure()
    {
        var task = CreateTask();
        task.ExternalTrigger.IsEnabled = false;

        var exception = Assert.Throws<TaskExternalTriggerValidationException>(
            () => TaskExternalTriggerValidator.ValidateParameters(task, new Dictionary<string, JsonElement>()));

        Assert.Equal(TaskExternalTriggerValidationFailureKind.TriggerDisabled, exception.FailureKind);
        Assert.Null(exception.ParameterName);
        Assert.Null(exception.ExpectedKind);
    }

    [Fact]
    public void Render_ReplacesOnlyParameterPlaceholders()
    {
        using var document = JsonDocument.Parse("{\"name\":\"build\",\"count\":3}");
        var parameters = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());

        var rendered = TaskInstructionRenderer.Render("Run {{parameters.name}} {{parameters.count}} {{agent.id}}", parameters);

        Assert.Equal("Run build 3 {{agent.id}}", rendered);
    }

    [Fact]
    public void Render_WithObjectAndArray_UsesRawJson()
    {
        var parameters = new Dictionary<string, JsonElement>
        {
            ["payload"] = JsonSerializer.SerializeToElement(new { id = 1 }),
            ["items"] = JsonSerializer.SerializeToElement(new[] { 1, 2 })
        };

        var rendered = TaskInstructionRenderer.Render(
            "Use {{parameters.payload}} {{parameters.items}} {{agent.id}}",
            parameters);

        Assert.Equal("Use {\"id\":1} [1,2] {{agent.id}}", rendered);
    }

    [Fact]
    public void Render_WhenValueContainsAnotherPlaceholder_DoesNotRescanReplacementText()
    {
        var parameters = new Dictionary<string, JsonElement>
        {
            ["first"] = JsonSerializer.SerializeToElement("{{parameters.second}}"),
            ["second"] = JsonSerializer.SerializeToElement("expanded")
        };

        var rendered = TaskInstructionRenderer.Render(
            "Use {{parameters.first}}, {{parameters.second}}, and {{parameters.unknown}}.",
            parameters);

        Assert.Equal("Use {{parameters.second}}, expanded, and {{parameters.unknown}}.", rendered);
    }

    private static AgentTaskDefinition CreateTask()
    {
        return new AgentTaskDefinition
        {
            ExternalTrigger = new TaskExternalTrigger
            {
                IsEnabled = true,
                Slug = "nightly-build",
                Parameters =
                [
                    new TaskExternalParameter { Name = "name", Kind = TaskExternalParameterKind.String, IsRequired = true },
                    new TaskExternalParameter { Name = "count", Kind = TaskExternalParameterKind.Number },
                    new TaskExternalParameter { Name = "flag", Kind = TaskExternalParameterKind.Boolean },
                    new TaskExternalParameter { Name = "payload", Kind = TaskExternalParameterKind.Json }
                ]
            }
        };
    }
}
