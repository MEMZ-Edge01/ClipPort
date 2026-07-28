using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EZDIT.Models;

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
    public string DisplayName { get; set; } = "拷卡任务";
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
    public DateTimeOffset? FinishedAt { get; set; }
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
    public double CopySeconds { get; set; }
    public double VerifySeconds { get; set; }
    public bool CopyEnabled { get; set; } = true;
    public bool VerificationEnabled { get; set; } = true;
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

    [JsonIgnore]
    public string MetaText => $"{FormatBytes(TotalBytes)} · {StartedAt:MM/dd HH:mm:ss}";

    [JsonIgnore]
    public string StatusText => Status == JobStatus.CompletedWithErrors ? "\u90E8\u5206\u5B8C\u6210" : Status switch
    {
        JobStatus.Queued => "\u7B49\u5F85\u4F18\u5148\u4EFB\u52A1",
        JobStatus.Running => CopyEnabled ? "正在拷贝" : "正在校验",
        JobStatus.Completed => CopyEnabled && VerificationEnabled
            ? "\u4EFB\u52A1\u5B8C\u6210"
            : CopyEnabled
                ? "\u62F7\u8D1D\u5B8C\u6210"
                : "\u6821\u9A8C\u5B8C\u6210",
        JobStatus.VerificationFailed => "校验失败",
        JobStatus.Failed => "任务失败",
        JobStatus.Cancelled => "已取消",
        JobStatus.Interrupted => "意外中断",
        _ => "未知状态"
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
    public string DurationText => TimeSpan.FromSeconds(CopySeconds + VerifySeconds).ToString(@"hh\:mm\:ss");

    [JsonIgnore]
    public bool CanStartVerification =>
        Status == JobStatus.Completed && CopyEnabled && !VerificationEnabled;

    [JsonIgnore]
    public bool CanRestart =>
        Status is JobStatus.CompletedWithErrors
            or JobStatus.VerificationFailed
            or JobStatus.Failed
            or JobStatus.Cancelled
            or JobStatus.Interrupted;

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:F0} {units[unit]}" : $"{bytes:F2} {units[unit]}";
    }

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
