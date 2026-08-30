using System.Text.Json.Serialization;

namespace ClipPort.Models;

public enum CopyPhase
{
    Scanning,
    Copying,
    Verifying,
    WaitingForDuplicateDecision,
    Completed,
    Cancelled,
    Failed
}

public enum ExistingFilePolicy
{
    Ask,
    Overwrite,
    Skip,
    CreateCopy
}

public enum FileOperationStage
{
    Copying,
    Verifying
}

public enum VerificationExecutionMode
{
    AfterCopy,
    OpportunisticDuringCopy
}

/// <summary>
/// Hash algorithms available for file integrity verification.
/// </summary>
public enum VerificationAlgorithmKind
{
    Sha256,
    Sha512,
    Sha1,
    Md5,
    XxHash64
}

/// <summary>
/// Centralizes the user-facing names and resource keys for verification algorithms.
/// </summary>
public static class VerificationAlgorithms
{
    public static VerificationAlgorithmKind Normalize(VerificationAlgorithmKind algorithm) =>
        Enum.IsDefined(typeof(VerificationAlgorithmKind), algorithm)
            ? algorithm
            : VerificationAlgorithmKind.Sha256;

    public static string GetDisplayName(VerificationAlgorithmKind algorithm) =>
        Normalize(algorithm) switch
        {
            VerificationAlgorithmKind.Sha256 => "SHA-256",
            VerificationAlgorithmKind.Sha512 => "SHA-512",
            VerificationAlgorithmKind.Sha1 => "SHA-1",
            VerificationAlgorithmKind.Md5 => "MD5",
            VerificationAlgorithmKind.XxHash64 => "xxHash64",
            _ => "SHA-256"
        };

    public static string GetDescriptionResourceKey(VerificationAlgorithmKind algorithm) =>
        Normalize(algorithm) switch
        {
            VerificationAlgorithmKind.Sha256 => "Info.VerificationAlgorithmSha256",
            VerificationAlgorithmKind.Sha512 => "Info.VerificationAlgorithmSha512",
            VerificationAlgorithmKind.Sha1 => "Info.VerificationAlgorithmSha1",
            VerificationAlgorithmKind.Md5 => "Info.VerificationAlgorithmMd5",
            VerificationAlgorithmKind.XxHash64 => "Info.VerificationAlgorithmXxHash64",
            _ => "Info.VerificationAlgorithmSha256"
        };
}

/// <summary>
/// Options that control how a copy-and-verify job executes.
/// </summary>
/// <remarks>
/// <see cref="UseFastCopyAlgorithm"/> controls both the file-copy pipeline
/// (managed-pipelined / native engine vs. sequential) and whether source and
/// destination hashes are computed in parallel during verification.
/// A future revision may split these concerns into independent flags.
/// </remarks>
public sealed record CopyOptions(
    ExistingFilePolicy ExistingFilePolicy = ExistingFilePolicy.Overwrite,
    bool VerifyFiles = true,
    bool UseFastCopyAlgorithm = false,
    bool SkipCopy = false,
    VerificationAlgorithmKind VerificationAlgorithm = VerificationAlgorithmKind.Sha256,
    VerificationExecutionMode VerificationExecutionMode = VerificationExecutionMode.AfterCopy)
{
    public IReadOnlyDictionary<string, string>? DestinationPaths { get; init; }
}

public sealed record DuplicateFileConflict(
    string RelativePath,
    string SourcePath,
    string DestinationPath,
    long Length);

public sealed record CopyProgressInfo(
    CopyPhase Phase,
    long TotalBytes,
    long ProcessedBytes,
    int TotalFiles,
    int ProcessedFiles,
    string CurrentFile,
    double BytesPerSecond,
    TimeSpan Elapsed)
{
    public long SuccessfulBytes { get; init; } = ProcessedBytes;
    public int SuccessfulFiles { get; init; } = ProcessedFiles;
    /// <summary>
    /// False when a background phase is waiting for more work. This lets the
    /// UI clear a stale throughput value without treating the phase as finished.
    /// </summary>
    public bool IsPhaseActive { get; init; } = true;
    public bool IsTotalKnown { get; init; } = true;
    public int ScannedDirectories { get; init; }
}

public sealed record FileVerificationResult(
    string RelativePath,
    long Length,
    string SourceHash,
    string DestinationHash,
    bool IsMatch,
    string? Error);

public enum FileOperationFailureReason
{
    Unknown,
    CopyIo,
    VerificationMismatch,
    VerificationIo
}

public sealed record FileOperationFailure(
    string RelativePath,
    string SourcePath,
    string DestinationPath,
    long Length,
    FileOperationStage Stage,
    string Error,
    FileOperationFailureReason Reason = FileOperationFailureReason.Unknown)
{
    public string StageText => Stage == FileOperationStage.Copying ? "Status.CopyingFiles" : "Status.Verifying";
    public string SizeText => DisplayFormatting.FormatBytes(Length);

    [JsonIgnore]
    public bool IsVerificationMismatch =>
        Reason == FileOperationFailureReason.VerificationMismatch;
}

public sealed record FileRetryResult(
    IReadOnlyList<FileOperationFailure> FailedFiles,
    TimeSpan CopyDuration,
    TimeSpan VerifyDuration)
{
    public int CopiedFiles { get; init; }
    public long CopiedBytes { get; init; }
    public IReadOnlyList<FileVerificationResult> VerificationResults { get; init; } = [];
    public IReadOnlyDictionary<string, string> DestinationPaths { get; init; } =
        new Dictionary<string, string>(PathSemantics.Comparer);
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record CopyResult(
    bool Success,
    int FileCount,
    long TotalBytes,
    TimeSpan CopyDuration,
    TimeSpan VerifyDuration,
    bool VerificationPerformed,
    IReadOnlyList<DuplicateFileConflict> DuplicateFiles,
    IReadOnlyList<FileVerificationResult> VerifiedFiles,
    IReadOnlyList<FileOperationFailure> FailedFiles,
    IReadOnlyList<string> Errors)
{
    public int CopiedFiles { get; init; }
    public long CopiedBytes { get; init; }
    public int VerifiedFileCount { get; init; }
    public long VerifiedBytes { get; init; }
    public IReadOnlyDictionary<string, string> DestinationPaths { get; init; } =
        new Dictionary<string, string>(PathSemantics.Comparer);
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public VerificationAlgorithmKind VerificationAlgorithm { get; init; } =
        VerificationAlgorithmKind.Sha256;
}
