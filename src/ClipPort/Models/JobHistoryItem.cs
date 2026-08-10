using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipPort.Models;

public enum JobStatus
{
    Queued,
    CompletedWithErrors,
    Running,
    Completed,
    VerificationFailed,
    Failed,
    Cancelled,
    Interrupted
}

public sealed class JobHistoryItem : INotifyPropertyChanged
{
    private DateTimeOffset _startedAt;
    private long _totalBytes;
    private bool _isAcknowledged = true;
    private JobStatus _status;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public DateTimeOffset StartedAt
    {
        get => _startedAt;
        set
        {
            if (SetProperty(ref _startedAt, value))
                OnPropertyChanged(nameof(MetaText));
        }
    }
    public DateTimeOffset? FinishedAt
    {
        get => _finishedAt;
        set
        {
            if (SetProperty(ref _finishedAt, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }
    private DateTimeOffset? _finishedAt;
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
                OnPropertyChanged(nameof(MetaText));
        }
    }
    public int FileCount { get; set; }
    public long CopiedBytes { get; set; }
    public int CopiedFiles { get; set; }
    public int VerifiedFiles { get; set; }
    public List<double> CopyByteSpeedSamples { get; set; } = [];
    public List<double> CopyItemSpeedSamples { get; set; } = [];
    public List<double> CopyThroughputProgressSamples { get; set; } = [];
    public List<double> VerifyByteSpeedSamples { get; set; } = [];
    public List<double> VerifyItemSpeedSamples { get; set; } = [];
    public List<double> VerifyThroughputProgressSamples { get; set; } = [];
    public double CopySeconds
    {
        get => _copySeconds;
        set
        {
            if (SetProperty(ref _copySeconds, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }
    private double _copySeconds;
    public double VerifySeconds
    {
        get => _verifySeconds;
        set
        {
            if (SetProperty(ref _verifySeconds, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }
    private double _verifySeconds;
    public bool CopyEnabled { get; set; } = true;
    public bool VerificationEnabled { get; set; } = true;
    public VerificationAlgorithmKind VerificationAlgorithm { get; set; } =
        VerificationAlgorithmKind.Sha256;
    public bool UseFastCopyAlgorithm { get; set; }
    public bool IsPriority { get; set; }
    public bool PreventSleep { get; set; } = true;
    public bool IsAcknowledged
    {
        get => _isAcknowledged;
        set
        {
            if (SetProperty(ref _isAcknowledged, value))
                OnPropertyChanged(nameof(NeedsAttention));
        }
    }
    public List<FileOperationFailure> FailedFiles { get; set; } = [];
    public List<DuplicateFileConflict> DuplicateFiles { get; set; } = [];
    public Dictionary<string, ExistingFilePolicy> DuplicateDecisions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public JobStatus Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value))
                return;

            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(NeedsAttention));
        }
    }
    public string? ErrorMessage { get; set; }
    public string? ReportFileName { get; set; }
    public string? ReportPath { get; set; }
    public Dictionary<string, string> DestinationFiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string MetaText => $"{DisplayFormatting.FormatBytes(TotalBytes)} · {StartedAt:MM/dd HH:mm:ss}";

    [JsonIgnore]
    public string StatusText => Status == JobStatus.CompletedWithErrors ? "Result.CompletedWithErrors" : Status switch
    {
        JobStatus.Queued => "Status.Queued",
        JobStatus.Running => CopyEnabled ? "Status.Copying" : "Status.Verifying",
        JobStatus.Completed => CopyEnabled && VerificationEnabled
            ? "Result.TaskCompleted"
            : CopyEnabled
                ? "Result.CopyCompletedShort"
                : "Result.VerificationCompleted",
        JobStatus.VerificationFailed => "Error.VerificationFailed",
        JobStatus.Failed => "Result.TaskFailedStatus",
        JobStatus.Cancelled => "Result.Cancelled",
        JobStatus.Interrupted => "Result.Interrupted",
        _ => "Result.UnknownStatus"
    };

    [JsonIgnore]
    public string StatusGlyph => Status == JobStatus.CompletedWithErrors ? "\uE7BA" : Status switch
    {
        JobStatus.Completed => "\uE73E",
        JobStatus.Queued => "\uE823",
        JobStatus.Running => "\uE895",
        JobStatus.Cancelled => "\uE711",
        _ => "\uE783"
    };
    [JsonIgnore]
    public bool NeedsAttention =>
        !IsAcknowledged && Status is not JobStatus.Queued and not JobStatus.Running;

    [JsonIgnore]
    public bool CanExportReport =>
        Status is not JobStatus.Queued and not JobStatus.Running;

    [JsonIgnore]
    public string DurationText => DisplayFormatting.FormatDuration(
        TimeSpan.FromSeconds(CopySeconds + VerifySeconds));

    [JsonIgnore]
    public bool CanStartVerification => Status == JobStatus.Completed;

    [JsonIgnore]
    public bool CanRestart =>
        Status is JobStatus.CompletedWithErrors
            or JobStatus.VerificationFailed
            or JobStatus.Failed
            or JobStatus.Cancelled
            or JobStatus.Interrupted;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
