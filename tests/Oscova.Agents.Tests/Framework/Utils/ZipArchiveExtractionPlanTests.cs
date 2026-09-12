using System.IO.Compression;
using System.Text;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Tests.Framework.Utils;

public sealed class ZipArchiveExtractionPlanTests
{
    [Fact]
    public void Create_ThrowsWhenArchiveExceedsUncompressedSizeLimit()
    {
        using var archiveStream = CreateZipStream([("skill/file.txt", "01234567890")]);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        var exception = Assert.Throws<ArgumentException>(() =>
            ZipArchiveExtractionPlan.Create(
                archive,
                Path.Combine(Path.GetTempPath(), "zip-plan-tests"),
                maxUncompressedSizeBytes: 10,
                entry => entry.FullName.Replace('\\', '/')));

        Assert.Contains("10 B", exception.Message, StringComparison.Ordinal);
    }

    private static MemoryStream CreateZipStream(IEnumerable<(string Path, string Content)> files)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
