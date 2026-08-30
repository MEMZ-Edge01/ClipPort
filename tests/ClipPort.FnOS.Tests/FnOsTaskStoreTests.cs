using System.Text.Json;
using ClipPort.FnOS.Contracts;
using ClipPort.FnOS.Persistence;

namespace ClipPort.FnOS.Tests;

public sealed class FnOsTaskStoreTests
{
    [Fact]
    public async Task LegacyRecordsDefaultToNormalPriorityAndEmptyWaveforms()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "tasks.json"),
                """
                [{
                  "id":"legacy",
                  "displayName":"legacy",
                  "request":{
                    "mode":"copyAndVerify",
                    "sourcePath":"/source",
                    "destinationPath":"/destination",
                    "destinationSubfolder":null,
                    "existingFilePolicy":"ask",
                    "verificationAlgorithm":"sha256",
                    "verificationExecutionMode":"afterCopy"
                  },
                  "status":"completed"
                }]
                """);

            var store = new FnOsTaskStore(directory);
            FnOsTaskRecord record = Assert.Single(await store.LoadAsync(CancellationToken.None));

            Assert.False(record.Request.IsPriority);
            Assert.Empty(record.CopyByteSpeedSamples);
            Assert.Empty(record.VerifyByteSpeedSamples);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PriorityAndWaveformsRoundTrip()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var record = new FnOsTaskRecord
            {
                Request = new(
                    FnOsTaskMode.CopyOnly,
                    "/source",
                    "/destination",
                    null,
                    ClipPort.Models.ExistingFilePolicy.Skip,
                    ClipPort.Models.VerificationAlgorithmKind.Sha256,
                    ClipPort.Models.VerificationExecutionMode.AfterCopy,
                    true),
                Status = FnOsTaskStatus.Completed,
                CopyByteSpeedSamples = [1024, 2048, 0],
                CopyItemSpeedSamples = [1, 2, 0],
                CopyThroughputProgressSamples = [0.2, 1, 1],
            };
            var store = new FnOsTaskStore(directory);

            await store.SaveAsync([record], CancellationToken.None);
            FnOsTaskRecord loaded = Assert.Single(await store.LoadAsync(CancellationToken.None));

            Assert.True(loaded.Request.IsPriority);
            Assert.Equal(record.CopyByteSpeedSamples, loaded.CopyByteSpeedSamples);
            Assert.Equal(record.CopyThroughputProgressSamples, loaded.CopyThroughputProgressSamples);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "clipport-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
