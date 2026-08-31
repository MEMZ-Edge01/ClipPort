using ClipPort.Models;

namespace ClipPort.FnOS.Contracts;

public sealed record SessionResponse(
    bool IsAdmin,
    int UserId,
    string Username,
    string CsrfToken,
    string Language,
    string SystemVersion,
    bool IsCompatible);

public sealed record AuthorizedFolderDto(
    string Path,
    string SemanticPath,
    bool Readable,
    bool Writable,
    string PermissionState = "confirmed",
    string SemanticPathState = "confirmed");

public sealed record RevokeAuthorizedFolderRequest(string Path);
public sealed record ValidateAuthorizedFolderRequest(string Path, bool RequireWrite = false);

public sealed record BatchTaskRequest(IReadOnlyList<string> TaskIds);

public sealed record BatchReportExportRequest(
    IReadOnlyList<string> TaskIds,
    string DestinationDirectory);

public sealed record BatchReportExportResponse(
    int ExportedCount,
    IReadOnlyList<string> FileNames);

public enum FnOsTaskMode
{
    CopyAndVerify,
    CopyOnly,
    VerifyOnly
}

public enum FnOsTaskStatus
{
    Queued,
    Running,
    Paused,
    AwaitingDuplicateDecision,
    AwaitingFailureDecision,
    Completed,
    CompletedWithErrors,
    VerificationFailed,
    Failed,
    Cancelled,
    Interrupted
}

public sealed record CreateTaskRequest(
    FnOsTaskMode Mode,
    string SourcePath,
    string DestinationPath,
    string? DestinationSubfolder,
    ExistingFilePolicy ExistingFilePolicy,
    VerificationAlgorithmKind VerificationAlgorithm,
    VerificationExecutionMode VerificationExecutionMode,
    bool IsPriority = false);

public sealed record DuplicateDecisionDto(
    string RelativePath,
    ExistingFilePolicy Decision);

public sealed record DuplicateDecisionRequest(
    IReadOnlyList<DuplicateDecisionDto> Decisions);

public enum FailureActionKind
{
    Retry,
    Overwrite,
    Skip
}

public sealed record FailureActionRequest(
    FailureActionKind Action,
    IReadOnlyList<string> RelativePaths);

public sealed record TaskProgressDto(
    CopyPhase Phase,
    long TotalBytes,
    long ProcessedBytes,
    int TotalFiles,
    int ProcessedFiles,
    string CurrentFile,
    double BytesPerSecond,
    double ElapsedSeconds,
    bool IsTotalKnown,
    bool IsPhaseActive);

public sealed class FnOsTaskRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public CreateTaskRequest Request { get; set; } = new(
        FnOsTaskMode.CopyAndVerify,
        string.Empty,
        string.Empty,
        null,
        ExistingFilePolicy.Ask,
        VerificationAlgorithmKind.Sha256,
        VerificationExecutionMode.AfterCopy);
    public FnOsTaskStatus Status { get; set; } = FnOsTaskStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public TaskProgressDto? Progress { get; set; }
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public long CopiedBytes { get; set; }
    public int CopiedFiles { get; set; }
    public long VerifiedBytes { get; set; }
    public int VerifiedFiles { get; set; }
    public double CopySeconds { get; set; }
    public double VerifySeconds { get; set; }
    public List<double> CopyByteSpeedSamples { get; set; } = [];
    public List<double> CopyItemSpeedSamples { get; set; } = [];
    public List<double> CopyThroughputProgressSamples { get; set; } = [];
    public List<double> VerifyByteSpeedSamples { get; set; } = [];
    public List<double> VerifyItemSpeedSamples { get; set; } = [];
    public List<double> VerifyThroughputProgressSamples { get; set; } = [];
    public List<DuplicateFileConflict> DuplicateFiles { get; set; } = [];
    public Dictionary<string, ExistingFilePolicy> DuplicateDecisions { get; set; } =
        new(PathSemantics.Comparer);
    public List<FileOperationFailure> FailedFiles { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? ReportFileName { get; set; }
}

public sealed record TaskEvent(string Type, object Data, DateTimeOffset Timestamp);

public sealed record ErrorResponse(
    string Code,
    string Message,
    object? Details = null);
