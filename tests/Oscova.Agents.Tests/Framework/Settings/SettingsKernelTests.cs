using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Persistence;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Tests.Framework.Settings;

public sealed class SettingsKernelTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"oscova-settings-kernel-{Guid.NewGuid():N}");

    [Fact]
    public async Task FileRepository_ResolvesDirectoryPerOperationAndRoundTripsContent()
    {
        var firstDirectory = Path.Combine(_tempDirectory, "first");
        var secondDirectory = Path.Combine(_tempDirectory, "second");
        var currentDirectory = firstDirectory;
        var resolvedScopeIds = new List<string>();
        var repository = new FileSettingsRepository(scopeId =>
        {
            resolvedScopeIds.Add(scopeId);
            return currentDirectory;
        });

        await repository.SaveSettingsAsync("scope-1", "sample", "first");

        var firstPath = Path.Combine(firstDirectory, "sample.yaml");
        Assert.Equal("first", await File.ReadAllTextAsync(firstPath));

        currentDirectory = secondDirectory;
        await repository.SaveSettingsAsync("scope-2", "sample", "second");

        var secondPath = Path.Combine(secondDirectory, "sample.yaml");
        Assert.Equal("first", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("second", await File.ReadAllTextAsync(secondPath));

        await File.WriteAllTextAsync(secondPath, "authoritative");
        Assert.Equal("authoritative", await repository.GetSettingsAsync("scope-2", "sample"));
        Assert.Equal(["scope-1", "scope-2", "scope-2"], resolvedScopeIds);
    }

    [Fact]
    public async Task FileRepository_ReturnsNullForMissingOrWhitespaceContent()
    {
        var repository = new FileSettingsRepository(_ => _tempDirectory);

        Assert.Null(await repository.GetSettingsAsync("scope-1", "missing"));

        Directory.CreateDirectory(_tempDirectory);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "blank.yaml"), " \r\n\t");

        Assert.Null(await repository.GetSettingsAsync("scope-1", "blank"));
    }

    [Fact]
    public async Task FileRepository_WhenSettingsKeyTraversesOutsideDirectory_RejectsWrite()
    {
        var settingsDirectory = Path.Combine(_tempDirectory, "settings");
        var outsidePath = Path.Combine(_tempDirectory, "outside", "escaped.yaml");
        var settingsKey = Path.GetRelativePath(settingsDirectory, Path.ChangeExtension(outsidePath, null));
        var repository = new FileSettingsRepository(_ => settingsDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.SaveSettingsAsync("scope-1", settingsKey, "escaped"));

        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public async Task FileRepository_WhenSettingsKeyIsRooted_RejectsWrite()
    {
        var settingsDirectory = Path.Combine(_tempDirectory, "settings");
        var rootedKey = Path.Combine(_tempDirectory, "rooted");
        var repository = new FileSettingsRepository(_ => settingsDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.SaveSettingsAsync("scope-1", rootedKey, "escaped"));

        Assert.False(File.Exists(rootedKey + ".yaml"));
    }

    [Fact]
    public async Task FileRepository_WhenSettingsKeyIsBlank_RejectsWrite()
    {
        var repository = new FileSettingsRepository(_ => _tempDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.SaveSettingsAsync("scope-1", string.Empty, "invalid"));

        Assert.False(File.Exists(Path.Combine(_tempDirectory, ".yaml")));
    }

    [Fact]
    public async Task SettingsBase_LoadsOnceReloadsAndSuppressesLoadNotifications()
    {
        var repository = new TestSettingsRepository { Content = "value: first" };
        var settings = new TestSettings(repository);
        var changedCount = 0;
        var propertyChangedCount = 0;
        settings.Changed += (_, _) => changedCount++;
        settings.PropertyChanged += (_, _) => propertyChangedCount++;

        await settings.LoadAsync();

        Assert.Equal("first", settings.Value);
        Assert.Equal(1, repository.ReadCount);
        Assert.Equal(0, changedCount);
        Assert.Equal(0, propertyChangedCount);

        repository.Content = "value: second";
        await settings.LoadAsync();

        Assert.Equal("first", settings.Value);
        Assert.Equal(1, repository.ReadCount);

        await settings.ReloadAsync();

        Assert.Equal("second", settings.Value);
        Assert.Equal(2, repository.ReadCount);
        Assert.Equal(0, changedCount);
        Assert.Equal(0, propertyChangedCount);

        settings.Value = "changed";
        Assert.Equal(1, changedCount);
        Assert.Equal(1, propertyChangedCount);
    }

    [Fact]
    public async Task SettingsBase_WhenFirstLoadsOverlap_StillRaisesNotificationsAfterwards()
    {
        var firstRead = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new TestSettingsRepository
        {
            ReadAsync = readCount => readCount == 1 ? firstRead.Task : secondRead.Task
        };
        var settings = new TestSettings(repository);
        var changedCount = 0;
        settings.Changed += (_, _) => changedCount++;

        var firstLoad = settings.LoadAsync();
        var secondLoad = settings.LoadAsync();
        Assert.Equal(2, repository.ReadCount);

        firstRead.SetResult("value: same");
        await firstLoad;
        secondRead.SetResult("value: same");
        await secondLoad;
        settings.Value = "changed";

        Assert.Equal(1, changedCount);
    }

    [Fact]
    public async Task SettingsBase_TrySaveAsync_ReturnsFalseWhenPersistenceFails()
    {
        var repository = new TestSettingsRepository { SaveException = new IOException("Write failed.") };
        var settings = new TestSettings(repository) { Value = "changed" };

        Assert.False(await settings.TrySaveAsync());
        Assert.Equal("changed", settings.Value);
    }

    [Fact]
    public async Task SettingsBase_SaveAsync_WhenPersistenceFails_RestoresLoadedState()
    {
        var repository = new TestSettingsRepository
        {
            Content = "value: persisted",
            SaveException = new IOException("Write failed.")
        };
        var settings = new TestSettings(repository);
        await settings.LoadAsync();
        settings.Value = "changed";

        await Assert.ThrowsAsync<IOException>(() => settings.SaveAsync());

        Assert.Equal("persisted", settings.Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsBase_ConvertedLoad_RetriesFailedWriteAndPreservesConvertedValue()
    {
        var repository = new TestSettingsRepository { Content = "minutes: 27", SaveException = new IOException("Write failed.") };
        var yaml = new YamlConverter();
        var settings = new TestSettings(repository, 2, content =>
        {
            if (yaml.ReadSchemaVersion(content) != 1) { return content; }
            var fields = yaml.Deserialize<Dictionary<string, int>>(content);
            return YamlConverter.WithSchemaVersion($"value: {fields["minutes"] * 60}", 2);
        });

        await Assert.ThrowsAsync<IOException>(() => settings.LoadAsync());
        Assert.False(settings.IsLoaded);
        Assert.Equal("minutes: 27", repository.Content);
        repository.SaveException = null;
        await settings.LoadAsync();
        Assert.True(settings.IsLoaded);
        Assert.Equal("1620", settings.Value);
        Assert.Equal(2, yaml.ReadSchemaVersion(repository.Content!));
        var restarted = new TestSettings(repository, 2);
        await restarted.LoadAsync();
        Assert.Equal("1620", restarted.Value);
        await restarted.SaveAsync();
        Assert.Equal(2, yaml.ReadSchemaVersion(repository.Content!));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("0")]
    [InlineData("invalid")]
    public async Task SettingsBase_UnsupportedMarker_PreservesOriginalDocument(string marker)
    {
        var content = $"schema_version: {marker}\nvalue: original";
        var repository = new TestSettingsRepository { Content = content };
        var settings = new TestSettings(repository);
        await Assert.ThrowsAsync<InvalidDataException>(() => settings.LoadAsync());
        Assert.False(settings.IsLoaded);
        Assert.Equal(content, repository.Content);
        Assert.Equal(string.Empty, settings.Value);
    }

    private sealed class TestSettings(IAppSettingsRepository repository, int schemaVersion = 1, Func<string, string>? prepare = null)
        : SettingsBase(repository, "scope-1", NullLoggerFactory.Instance)
    {
        private string _value = string.Empty;

        public override string SettingsKey => "test";
        protected override int SchemaVersion => schemaVersion;
        protected override string PreparePersistedYaml(string yaml) => prepare?.Invoke(yaml) ?? yaml;

        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public override string ToYaml() => Yaml.Serialize(new Dictionary<string, string> { ["value"] = Value });

        public override void FromYaml(string yaml) => Value = Yaml.Deserialize<Dictionary<string, string>>(yaml)["value"];
    }

    private sealed class TestSettingsRepository : IAppSettingsRepository
    {
        public string? Content { get; set; }

        public int ReadCount { get; private set; }

        public Exception? SaveException { get; set; }

        public Func<int, Task<string?>>? ReadAsync { get; init; }

        public Task<string?> GetSettingsAsync(string scopeId, string settingsKey, CancellationToken ct = default)
        {
            ReadCount++;
            return ReadAsync?.Invoke(ReadCount) ?? Task.FromResult(Content);
        }

        public Task SaveSettingsAsync(string scopeId, string settingsKey, string content, CancellationToken ct = default)
        {
            if (SaveException is not null) { return Task.FromException(SaveException); }
            Content = content;
            return Task.CompletedTask;
        }
    }
}
