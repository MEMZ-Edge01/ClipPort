using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EZDIT.Models;

public sealed class DuplicateConflictChoice : INotifyPropertyChanged
{
    private ExistingFilePolicy? _decision;
    private bool _isSelected;
    private bool _canChoose;

    public DuplicateConflictChoice(
        DuplicateFileConflict conflict,
        ExistingFilePolicy? decision = null,
        bool canChoose = false)
    {
        Conflict = conflict;
        _decision = decision;
        _canChoose = canChoose;
    }

    public DuplicateFileConflict Conflict { get; }
    public string RelativePath => Conflict.RelativePath;
    public string FileName => Path.GetFileName(Conflict.RelativePath);
    public string SourcePath => Conflict.SourcePath;
    public string DestinationPath => Conflict.DestinationPath;
    public string SizeText => FormatBytes(Conflict.Length);
    public ExistingFilePolicy? Decision => _decision;
    public bool IsDecided => _decision is not null;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            OnPropertyChanged();
        }
    }
    public bool CanChoose
    {
        get => _canChoose;
        private set
        {
            if (_canChoose == value)
            {
                return;
            }
            _canChoose = value;
            OnPropertyChanged();
        }
    }
    public string DecisionText => _decision switch
    {
        ExistingFilePolicy.Overwrite => "覆盖",
        ExistingFilePolicy.Skip => "跳过",
        ExistingFilePolicy.CreateCopy => "创建副本",
        _ => "等待选择"
    };

    public void SetCanChoose(bool value) => CanChoose = value;
    public void SetDecision(ExistingFilePolicy decision)
    {
        if (decision == ExistingFilePolicy.Ask || _decision == decision)
        {
            return;
        }

        _decision = decision;
        OnPropertyChanged(nameof(Decision));
        OnPropertyChanged(nameof(IsDecided));
        OnPropertyChanged(nameof(DecisionText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
}