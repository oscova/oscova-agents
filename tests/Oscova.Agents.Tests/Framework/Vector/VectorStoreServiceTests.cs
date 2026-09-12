using Microsoft.Extensions.VectorData;
using Oscova.Agents.Vector;

namespace Oscova.Agents.Tests.Framework.Vector;

public sealed class VectorStoreServiceTests
{
    [Fact]
    public async Task DeleteWhereAsync_RemovesAllMatchesAcrossBatches()
    {
        var tempDirectory = CreateTempDirectory();
        var databasePath = Path.Combine(tempDirectory, "vectors.db");
        var store = new VectorStoreService(databasePath);

        try
        {
            var matchingRecords = Enumerable.Range(0, 450)
                .Select(index => CreateRecord(index.ToString(), "delete"))
                .ToArray();
            var remainingRecord = CreateRecord("keep", "keep");
            var definition = BuildCollectionDefinition();

            await store.UpdateBatchAsync<string, TestVectorRecord>(
                "records",
                [.. matchingRecords, remainingRecord],
                definition);

            var deletedCount = await store.DeleteWhereAsync<string, TestVectorRecord>(
                "records",
                record => record.Group == "delete",
                static record => record.Key,
                definition);

            var collection = await store.GetCollectionAsync<string, TestVectorRecord>(
                "records",
                definition);
            var deletedRecords = await collection
                .GetAsync(record => record.Group == "delete", 500)
                .ToListAsync();
            var remainingRecords = await collection
                .GetAsync(record => record.Group == "keep", 500)
                .ToListAsync();

            Assert.Equal(450, deletedCount);
            Assert.Empty(deletedRecords);
            Assert.Single(remainingRecords);
            Assert.Equal(remainingRecord.Key, remainingRecords[0].Key);
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "oscova-vector-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static TestVectorRecord CreateRecord(string key, string group)
    {
        return new TestVectorRecord
        {
            Key = key,
            Group = group,
            Content = $"Content {key}",
            Vector = new float[] { 0.1f, 0.2f, 0.3f }
        };
    }

    private static VectorStoreCollectionDefinition BuildCollectionDefinition()
    {
        return new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(TestVectorRecord.Key), typeof(string)),
                new VectorStoreDataProperty(nameof(TestVectorRecord.Group), typeof(string)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(TestVectorRecord.Content), typeof(string)),
                new VectorStoreVectorProperty(
                    nameof(TestVectorRecord.Vector),
                    typeof(ReadOnlyMemory<float>),
                    dimensions: 3)
            ]
        };
    }

    private sealed class TestVectorRecord
    {
        public string Key { get; set; } = string.Empty;

        public string Group { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public ReadOnlyMemory<float> Vector { get; set; }
    }
}
