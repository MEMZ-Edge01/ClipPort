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
    bool SkipCopy = false);

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
    TimeSpan Elapsed);

public sealed record FileVerificationResult(
    string RelativePath,
    long Length,
    string SourceSha256,
    string DestinationSha256,
    bool IsMatch,
    string? Error);

public sealed record FileOperationFailure(
    string RelativePath,
    string SourcePath,
    string DestinationPath,
    long Length,
    FileOperationStage Stage,
    string Error)
{
    public string StageText => Stage == FileOperationStage.Copying ? "\u62F7\u8D1D" : "\u6821\u9A8C";
    public string SizeText => FormatBytes(Length);

    [JsonIgnore]
    public bool IsVerificationMismatch =>
        Stage == FileOperationStage.Verifying &&
        Error.StartsWith("\u6821\u9A8C\u4E0D\u4E00\u81F4\uFF1A", StringComparison.Ordinal);

    private static string FormatBytes(double bytes) => bytes >= 1024 * 1024 * 1024
        ? $"{bytes / (1024 * 1024 * 1024):F2} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024):F2} MB"
        : bytes >= 1024 ? $"{bytes / 1024:F2} KB"
        : $"{bytes:F0} B";
}

public sealed record FileRetryResult(
    IReadOnlyList<FileOperationFailure> FailedFiles,
    TimeSpan CopyDuration,
    TimeSpan VerifyDuration);

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
    IReadOnlyList<string> Errors);
