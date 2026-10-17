using Oscova.Agents.Secrets;

namespace Oscova.Agents.Tests.Framework.Encryption;

/// <summary>Verifies that encrypted-file previews use the supplied protection contract.</summary>
public sealed class EncryptedFileContentTests
{
    [Fact]
    public void TryDecrypt_UsesProtectorFormatAndPreservesUserBinding()
    {
        var protector = new PassThroughContentProtector();
        var ciphertext = protector.Protect("owner", EncryptedFileContent.ProtectionPurpose, "retained content");

        Assert.True(EncryptedFileContent.TryDecrypt("owner", "witness.md.enc", ciphertext, protector, out var plaintext));
        Assert.Equal("retained content", plaintext);
        Assert.False(EncryptedFileContent.TryDecrypt("other", "witness.md.enc", ciphertext, protector, out var denied));
        Assert.Equal(ciphertext, denied);
        Assert.Equal(2, protector.UnprotectCalls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAllText_DuringAtomicReplacements_ReadsCompleteContent(bool asynchronous)
    {
        var directory = Path.Combine(Path.GetTempPath(), "oscova-encrypted-file-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent.yaml");
        var protector = new PassThroughContentProtector();
        var first = new string('A', 10_000);
        var second = new string('B', 10_000);
        EncryptedFileContent.WriteAllText("owner", path, first, protector);
        try
        {
            using var barrier = new Barrier(2);
            var writes = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var index = 0; index < 500; index++)
                {
                    EncryptedFileContent.WriteAllText("owner", path, index % 2 == 0 ? first : second, protector);
                }
            });
            var reads = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                for (var index = 0; index < 2_000; index++)
                {
                    var content = asynchronous
                        ? await EncryptedFileContent.ReadAllTextAsync("owner", path, protector)
                        : EncryptedFileContent.ReadAllText("owner", path, protector);
                    Assert.True(content == first || content == second);
                }
            });

            await Task.WhenAll(writes, reads);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
