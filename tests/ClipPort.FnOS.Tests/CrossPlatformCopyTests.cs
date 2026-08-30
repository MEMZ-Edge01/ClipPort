using System.Security.Cryptography;
using ClipPort.Models;
using ClipPort.Services;

namespace ClipPort.FnOS.Tests;

public sealed class CrossPlatformCopyTests
{
    [Theory]
    [InlineData(VerificationAlgorithmKind.Sha256)]
    [InlineData(VerificationAlgorithmKind.Sha512)]
    [InlineData(VerificationAlgorithmKind.Sha1)]
    [InlineData(VerificationAlgorithmKind.Md5)]
    [InlineData(VerificationAlgorithmKind.XxHash64)]
    public async Task AllHashAlgorithmsCopyUnicodePathsAndDetectDamage(
        VerificationAlgorithmKind algorithm)
    {
        await WithFoldersAsync(async (source, destination) =>
        {
            string relative = Path.Combine("相册", "夏日-Clip.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relative))!);
            await File.WriteAllBytesAsync(
                Path.Combine(source, relative),
                RandomNumberGenerator.GetBytes(96 * 1024 + 17));
            var options = new CopyOptions(
                ExistingFilePolicy.Overwrite,
                VerifyFiles: true,
                UseFastCopyAlgorithm: false,
                SkipCopy: false,
                VerificationAlgorithm: algorithm);

            CopyResult copied = await RunAsync(source, destination, options);
            Assert.True(copied.Success);
            Assert.Equal(algorithm, copied.VerificationAlgorithm);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories),
                path => path.EndsWith(".clipport-partial", StringComparison.Ordinal));

            await File.WriteAllTextAsync(Path.Combine(destination, relative), "damaged");
            CopyResult verified = await RunAsync(source, destination, options with { SkipCopy = true });
            Assert.False(verified.Success);
            Assert.True(verified.FailedFiles.Single().IsVerificationMismatch);
        });
    }

    [Fact]
    public async Task CopyOnlyAndVerifyOnlyKeepTheirOperationBoundaries()
    {
        await WithFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "case-sensitive.txt"), "payload");
            CopyResult copyOnly = await RunAsync(source, destination, new CopyOptions(
                ExistingFilePolicy.Overwrite,
                VerifyFiles: false,
                UseFastCopyAlgorithm: false));
            Assert.True(copyOnly.Success);
            Assert.False(copyOnly.VerificationPerformed);

            CopyResult verifyOnly = await RunAsync(source, destination, new CopyOptions(
                ExistingFilePolicy.Overwrite,
                VerifyFiles: true,
                UseFastCopyAlgorithm: false,
                SkipCopy: true));
            Assert.True(verifyOnly.Success);
            Assert.Equal(0, verifyOnly.CopiedFiles);
            Assert.True(verifyOnly.VerificationPerformed);
        });
    }

    [Fact]
    public async Task DuplicateDecisionsAndFailureRetriesUseThePortableEngine()
    {
        await WithFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "duplicate.txt");
            string destinationFile = Path.Combine(destination, "duplicate.txt");
            await File.WriteAllTextAsync(sourceFile, "new payload");
            await File.WriteAllTextAsync(destinationFile, "old payload");
            var service = new FileCopyService();
            var options = new CopyOptions(
                ExistingFilePolicy.Ask,
                VerifyFiles: false,
                UseFastCopyAlgorithm: false);

            CopyResult resolved = await service.CopyAndVerifyAsync(
                source,
                destination,
                options,
                new InlineTestProgress<CopyProgressInfo>(_ => { }),
                new InlineTestProgress<DuplicateFileConflict>(_ => { }),
                (conflicts, _) => Task.FromResult<IReadOnlyDictionary<string, ExistingFilePolicy>>(
                    conflicts.ToDictionary(
                        item => item.RelativePath,
                        _ => ExistingFilePolicy.Overwrite,
                        PathSemantics.Comparer)),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert.True(resolved.Success);
            Assert.Equal("new payload", await File.ReadAllTextAsync(destinationFile));

            File.Delete(destinationFile);
            var failure = new FileOperationFailure(
                "duplicate.txt",
                sourceFile,
                destinationFile,
                new FileInfo(sourceFile).Length,
                FileOperationStage.Copying,
                "simulated copy failure",
                FileOperationFailureReason.CopyIo);
            FileRetryResult retried = await service.RetryFailedFilesAsync(
                [failure],
                options with { ExistingFilePolicy = ExistingFilePolicy.Overwrite, VerifyFiles = true },
                new InlineTestProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert.Empty(retried.FailedFiles);
            Assert.Equal("new payload", await File.ReadAllTextAsync(destinationFile));
        });
    }

    [Fact]
    public async Task PauseGateStopsAnInFlightCopyUntilItIsResumed()
    {
        await WithFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllBytesAsync(
                Path.Combine(source, "pause.bin"),
                RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
            var enteredPause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task<CopyResult> copy = new FileCopyService().CopyAndVerifyAsync(
                source,
                destination,
                new CopyOptions(VerifyFiles: false, UseFastCopyAlgorithm: false),
                new InlineTestProgress<CopyProgressInfo>(_ => { }),
                async cancellationToken =>
                {
                    if (Directory.EnumerateFiles(
                            destination,
                            "*.clipport-partial",
                            SearchOption.TopDirectoryOnly).Any())
                    {
                        enteredPause.TrySetResult();
                        await resume.Task.WaitAsync(cancellationToken);
                    }
                },
                CancellationToken.None);

            await enteredPause.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(copy.IsCompleted);
            resume.TrySetResult();
            Assert.True((await copy).Success);
        });
    }

    [Fact]
    public async Task CancellationKeepsTheCommittedDestinationAndRemovesThePartialFile()
    {
        await WithFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "atomic.bin");
            string destinationFile = Path.Combine(destination, "atomic.bin");
            await File.WriteAllBytesAsync(
                sourceFile,
                RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
            await File.WriteAllTextAsync(destinationFile, "committed data");
            using var cancellation = new CancellationTokenSource();

            Task<CopyResult> copy = new FileCopyService().CopyAndVerifyAsync(
                source,
                destination,
                new CopyOptions(
                    ExistingFilePolicy.Overwrite,
                    VerifyFiles: false,
                    UseFastCopyAlgorithm: false),
                new InlineTestProgress<CopyProgressInfo>(_ => { }),
                cancellationToken =>
                {
                    if (Directory.EnumerateFiles(
                            destination,
                            "*.clipport-partial",
                            SearchOption.TopDirectoryOnly).Any())
                    {
                        cancellation.Cancel();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cancellation.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
            Assert.Equal("committed data", await File.ReadAllTextAsync(destinationFile));
            Assert.Empty(Directory.EnumerateFiles(
                destination,
                "*.clipport-partial",
                SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void LinuxRejectsSymbolicLinkTraversal()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "clipport-links-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "clipport-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
            Assert.Throws<IOException>(() =>
                PathSafety.EnsureDestinationDoesNotTraverseReparsePoint(Path.Combine(root, "escape", "child")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static Task<CopyResult> RunAsync(
        string source,
        string destination,
        CopyOptions options) =>
        new FileCopyService().CopyAndVerifyAsync(
            source,
            destination,
            options,
            new Progress<CopyProgressInfo>(),
            _ => Task.CompletedTask,
            CancellationToken.None);

    private static async Task WithFoldersAsync(Func<string, string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "clipport-linux-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            await action(source, destination);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InlineTestProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
