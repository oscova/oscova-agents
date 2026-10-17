using System.Text;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Tests.Framework.Toolsets;

public sealed class ToolOutputTests
{
    [Fact]
    public void LimitText_ReturnsTextUnchangedWithinBudget()
    {
        var store = new RecordingToolOutputStore();
        string text = new('a', ToolOutput.InlineTextChars);

        Assert.Equal(text, ToolOutput.LimitText(store, "web", text));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void LimitText_ShowsStartAndSavesFullText()
    {
        var store = new RecordingToolOutputStore();
        string text = new string('a', 20_000) + "answer";

        string result = ToolOutput.LimitText(store, "web", text);

        Assert.Equal(text, store.Saved);
        Assert.StartsWith(new string('a', 16_000), result);
        Assert.Contains($"Showing the first 16000 of {text.Length} characters", result);
        Assert.Contains($"Full output saved to {store.Path}", result);
        Assert.DoesNotContain("answer", result);
    }

    [Fact]
    public void LimitCommandOutput_ShowsStartAndEndAndSavesFullText()
    {
        var store = new RecordingToolOutputStore();
        string text = new string('a', 10_000) + new string('b', 10_000);

        string result = ToolOutput.LimitCommandOutput(store, "stdout", text, false);

        Assert.Equal(text, store.Saved);
        Assert.StartsWith(new string('a', 8_000), result);
        Assert.EndsWith(new string('b', 8_000), result);
        Assert.Contains("Full output saved", result);
    }

    [Fact]
    public void LimitText_SavesUtf8BoundedPrefixWhenTextExceedsCeiling()
    {
        var store = new RecordingToolOutputStore();
        string text = new string('界', 500_000);

        string result = ToolOutput.LimitText(store, "pdf", text);

        Assert.True(Encoding.UTF8.GetByteCount(store.Saved!) <= BoundedOutputBuffer.DefaultCapacityBytes);
        Assert.Contains($"First {store.Saved!.Length} characters saved", result);
        Assert.Equal(text[..store.Saved.Length], store.Saved);
    }

    [Fact]
    public void LimitText_DoesNotSplitSurrogatePairs()
    {
        var store = new RecordingToolOutputStore();
        string text = new string('a', 15_999) + "😀" + new string('b', 100);

        string result = ToolOutput.LimitText(store, "web", text);

        Assert.StartsWith(new string('a', 15_999) + "\n\n", result);
        Assert.DoesNotContain('\uD83D', result);
        Assert.Equal(text, store.Saved);
    }

    [Fact]
    public void LimitText_OmitsPathWhenStoreReturnsNull()
    {
        var store = new RecordingToolOutputStore { Path = null };

        string result = ToolOutput.LimitText(store, "web", new string('x', 20_000));

        Assert.Contains("[Showing the first 16000 of 20000 characters.]", result);
        Assert.DoesNotContain("saved to", result);
    }
}

internal sealed class RecordingToolOutputStore : IToolOutputStore
{
    public string? Path { get; set; } = "/mnt/tool-output-for-this-run/output.txt";
    public string? Saved { get; private set; }
    public int Calls { get; private set; }

    public string? Save(string label, string content)
    {
        Calls++;
        Saved = content;
        return Path;
    }
}
