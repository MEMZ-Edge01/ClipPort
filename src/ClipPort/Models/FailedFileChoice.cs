using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClipPort.Models;

public sealed class FailedFileChoice : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public FailedFileChoice(FileOperationFailure failure)
    {
        Failure = failure;
    }

    public FileOperationFailure Failure { get; }
    public string RelativePath => Failure.RelativePath;
    public string SourcePath => Failure.SourcePath;
    public string DestinationPath => Failure.DestinationPath;
    public string StageText => Failure.StageText;
    public string SizeText => Failure.SizeText;
    public string Error => Failure.Error;
    public bool CanOverwrite => Failure.IsVerificationMismatch;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
