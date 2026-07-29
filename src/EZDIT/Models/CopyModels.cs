using System.Text.Json.Serialization;

namespace EZDIT.Models;

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

public sealed record CopyOptions(
    ExistingFilePolicy ExistingFilePolicy = ExistingFilePolicy.Overwrite,
    bool VerifyFiles = true,
    bool UseFastCopyAlgorithm = false,
    bool SkipCopy = false)
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
}

public sealed record FileVerificationResult(
    string RelativePath,
    long Length,
    string SourceSha256,
    string DestinationSha256,
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
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
