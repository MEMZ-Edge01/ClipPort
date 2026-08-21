using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClipPort.Models;

namespace ClipPort.Services;

internal sealed record VerificationWorkItem(SourceFile Source, string DestinationPath);

internal sealed record BackgroundVerificationOutcome(
    VerificationWorkItem WorkItem,
    FileVerificationResult Verification,
    FileOperationFailure? Failure);

internal sealed record BackgroundVerificationSummary(
    IReadOnlyList<BackgroundVerificationOutcome> Outcomes,
    TimeSpan Duration,
    bool UsedBackgroundIoPriority);

/// <summary>
/// Verifies already committed files on one dedicated Windows background-I/O thread.
/// The bounded queue is intentionally non-blocking for producers so copying always wins.
/// </summary>
internal sealed class BackgroundVerificationWorker : IDisposable
{
    private delegate void AppendBlock(ReadOnlySpan<byte> block);
    private const int BufferSize = 4 * 1024 * 1024;
    private const int QueueCapacity = 8;
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    private readonly BlockingCollection<VerificationWorkItem> _queue = new(QueueCapacity);
    private readonly List<BackgroundVerificationOutcome> _outcomes = [];
    private readonly VerificationAlgorithmKind _algorithm;
    private readonly IProgress<CopyProgressInfo> _progress;
    private readonly Func<CancellationToken, Task> _waitWhilePaused;
    private readonly CancellationToken _cancellationToken;
    private readonly long _totalBytes;
    private readonly int _totalFiles;
    private readonly ManualResetEventSlim _initialized = new(false);
    private readonly TaskCompletionSource<BackgroundVerificationSummary> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private bool _backgroundPriorityEnabled;
    private bool _disposed;

    public BackgroundVerificationWorker(
        VerificationAlgorithmKind algorithm,
        long totalBytes,
        int totalFiles,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        _algorithm = VerificationAlgorithms.Normalize(algorithm);
        _totalBytes = totalBytes;
        _totalFiles = totalFiles;
        _progress = progress;
        _waitWhilePaused = waitWhilePaused;
        _cancellationToken = cancellationToken;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClipPort background verification"
        };
        _thread.Start();
        // Priority setup is immediate. Waiting without the task token prevents a
        // cancellation race from abandoning a live worker before it can be disposed.
        _initialized.Wait();
    }

    public bool IsAvailable => _backgroundPriorityEnabled;

    public bool TryQueue(VerificationWorkItem item) =>
        !_disposed && _backgroundPriorityEnabled && _queue.TryAdd(item);

    public async Task<BackgroundVerificationSummary> CompleteAsync()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }
        return await _completion.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        var watch = new Stopwatch();
        try
        {
            _backgroundPriorityEnabled = TrySetBackgroundMode(ThreadModeBackgroundBegin);
            _initialized.Set();
            if (!_backgroundPriorityEnabled)
            {
                _completion.TrySetResult(new BackgroundVerificationSummary([], TimeSpan.Zero, false));
                return;
            }

            watch.Start();
            long processedBytes = 0;
            int processedFiles = 0;
            foreach (VerificationWorkItem item in _queue.GetConsumingEnumerable(_cancellationToken))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _waitWhilePaused(_cancellationToken).GetAwaiter().GetResult();
                BackgroundVerificationOutcome outcome = Verify(item);
                _outcomes.Add(outcome);
                processedBytes += item.Source.Length;
                processedFiles++;
                _progress.Report(new CopyProgressInfo(
                    CopyPhase.Verifying,
                    _totalBytes,
                    processedBytes,
                    _totalFiles,
                    processedFiles,
                    item.Source.RelativePath,
                    processedBytes / Math.Max(watch.Elapsed.TotalSeconds, 0.001),
                    watch.Elapsed)
                {
                    SuccessfulBytes = _outcomes
                        .Where(result => result.Failure is null)
                        .Sum(result => result.WorkItem.Source.Length),
                    SuccessfulFiles = _outcomes.Count(result => result.Failure is null)
                });
            }

            watch.Stop();
            _completion.TrySetResult(new BackgroundVerificationSummary(
                _outcomes.ToArray(),
                watch.Elapsed,
                true));
        }
        catch (OperationCanceledException)
        {
            _completion.TrySetCanceled(_cancellationToken);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
        finally
        {
            _initialized.Set();
            if (_backgroundPriorityEnabled)
            {
                TrySetBackgroundMode(ThreadModeBackgroundEnd);
            }
        }
    }

    private BackgroundVerificationOutcome Verify(VerificationWorkItem item)
    {
        try
        {
            byte[] sourceHash = ComputeHash(item.Source.FullPath);
            byte[] destinationHash = ComputeHash(item.DestinationPath);
            bool isMatch = CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
            string? error = isMatch
                ? null
                : ResourceService.Format("Format.VerificationMismatch", item.Source.RelativePath);
            var verification = new FileVerificationResult(
                item.Source.RelativePath,
                item.Source.Length,
                Convert.ToHexString(sourceHash),
                Convert.ToHexString(destinationHash),
                isMatch,
                error);
            FileOperationFailure? failure = isMatch
                ? null
                : new FileOperationFailure(
                    item.Source.RelativePath,
                    item.Source.FullPath,
                    item.DestinationPath,
                    item.Source.Length,
                    FileOperationStage.Verifying,
                    error!,
                    FileOperationFailureReason.VerificationMismatch);
            return new BackgroundVerificationOutcome(item, verification, failure);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            string error = ResourceService.Format(
                "Format.CannotVerifyFile",
                item.Source.RelativePath,
                ex.Message);
            return new BackgroundVerificationOutcome(
                item,
                new FileVerificationResult(
                    item.Source.RelativePath,
                    item.Source.Length,
                    string.Empty,
                    string.Empty,
                    false,
                    error),
                new FileOperationFailure(
                    item.Source.RelativePath,
                    item.Source.FullPath,
                    item.DestinationPath,
                    item.Source.Length,
                    FileOperationStage.Verifying,
                    error,
                    FileOperationFailureReason.VerificationIo));
        }
    }

    private byte[] ComputeHash(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            if (_algorithm == VerificationAlgorithmKind.XxHash64)
            {
                var xxHash = new XxHash64();
                ReadAll(stream, buffer, block => xxHash.Append(block));
                return xxHash.GetHashAndReset();
            }

            using IncrementalHash cryptographicHash = IncrementalHash.CreateHash(GetHashName(_algorithm));
            ReadAll(stream, buffer, block => cryptographicHash.AppendData(block));
            return cryptographicHash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ReadAll(FileStream stream, byte[] buffer, AppendBlock append)
    {
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _waitWhilePaused(_cancellationToken).GetAwaiter().GetResult();
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return;
            }
            append(buffer.AsSpan(0, read));
        }
    }

    private static HashAlgorithmName GetHashName(VerificationAlgorithmKind algorithm) =>
        algorithm switch
        {
            VerificationAlgorithmKind.Sha256 => HashAlgorithmName.SHA256,
            VerificationAlgorithmKind.Sha512 => HashAlgorithmName.SHA512,
            VerificationAlgorithmKind.Sha1 => HashAlgorithmName.SHA1,
            VerificationAlgorithmKind.Md5 => HashAlgorithmName.MD5,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null)
        };

    private static bool TrySetBackgroundMode(int mode) =>
        OperatingSystem.IsWindows() && SetThreadPriority(GetCurrentThread(), mode);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }
        if (_thread.IsAlive && !ReferenceEquals(Thread.CurrentThread, _thread))
        {
            _thread.Join();
        }
        _queue.Dispose();
        _initialized.Dispose();
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
}
