using System.Text;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Tests.Framework.Utils;

public sealed class FileUtilTests
{
    /// <summary>File writers accept bare relative names and absolute paths for both creation and replacement.</summary>
    [Theory]
    [InlineData("sync", false)]
    [InlineData("async", false)]
    [InlineData("stream", false)]
    [InlineData("append", false)]
    [InlineData("sync", true)]
    [InlineData("async", true)]
    [InlineData("stream", true)]
    [InlineData("append", true)]
    public async Task FileWriters_RelativeOrAbsolutePath_PersistContent(string operation, bool absolute)
    {
        var name = $"oscova-file-{Guid.NewGuid():N}.txt";
        var path = absolute ? Path.Combine(Path.GetTempPath(), name) : name;
        try
        {
            foreach (var content in new[] { "first", "second" })
            {
                switch (operation)
                {
                    case "sync":
                        FileUtil.WriteAllTextAtomic(path, content);
                        break;
                    case "async":
                        await FileUtil.WriteAllTextAtomicAsync(path, content);
                        break;
                    case "stream":
                        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                        {
                            await FileUtil.WriteStreamAtomicAsync(path, stream);
                        }
                        break;
                    case "append":
                        await FileUtil.AppendLineSharedAsync(path, content);
                        break;
                }
            }

            var expected = operation == "append"
                ? $"first{Environment.NewLine}second{Environment.NewLine}"
                : "second";
            Assert.Equal(expected, await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
