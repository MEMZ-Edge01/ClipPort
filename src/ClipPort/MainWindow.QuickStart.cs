using ClipPort.Models;
using ClipPort.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace ClipPort;

public sealed partial class MainWindow
{
    public void ActivateAndRestore()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }
        Activate();
    }

    public async Task HandleQuickStartRequestAsync(QuickStartRequest request)
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        TaskWorkspace.Visibility = Visibility.Visible;
        AppTitleText.Text = "ClipPort-beta";
        ActivateAndRestore();

        if (_quickStartDialogOpen)
        {
            ApplyQuickStartRequestToDialog(request);
            return;
        }

        QuickStartDraft draft = new QuickStartDraft(
            _sourcePath,
            _destinationParentPath).Apply(request);
        PrepareConcurrentNewJobView();
        _sourcePath = draft.SourceDirectory;
        _destinationParentPath = draft.DestinationDirectory;
        _destinationPath = draft.DestinationDirectory;
        RefreshQuickStartDraftSummary();

        _quickStartDialogOpen = true;
        try
        {
            if (await ConfigureNewTaskAsync())
            {
                await EnqueueConfiguredJobAsync();
            }
        }
        finally
        {
            _quickStartDialogOpen = false;
        }
    }

    private void ApplyQuickStartRequestToDialog(QuickStartRequest request)
    {
        if (request.Role == QuickStartDirectoryRole.Source)
        {
            bool replaceAutomaticSubfolder =
                string.IsNullOrWhiteSpace(DialogDestinationSubfolderName.Text) ||
                string.Equals(
                    DialogDestinationSubfolderName.Text,
                    _automaticDialogSubfolderName,
                    StringComparison.Ordinal);
            _dialogSourcePath = request.DirectoryPath;
            DialogSourcePathText.Text = request.DirectoryPath;
            if (replaceAutomaticSubfolder && EnableCopyToggle.IsOn)
            {
                SetAutomaticDialogSubfolderName(request.DirectoryPath);
            }
        }
        else
        {
            _dialogDestinationParentPath = request.DirectoryPath;
            DialogDestinationPathText.Text = request.DirectoryPath;
        }
    }

    private void RefreshQuickStartDraftSummary()
    {
        SourcePathText.Text = _sourcePath ?? ResourceService.GetString("Info.NotSelected");
        DestinationPathText.Text = _destinationParentPath ?? ResourceService.GetString("Info.NotSelected");
        HeroNameText.Text = _sourcePath is null
            ? ResourceService.GetString("Info.PrepareNewTask")
            : GetDisplayName(_sourcePath);
        CurrentFileText.Text = _sourcePath is not null && _destinationParentPath is not null
            ? ResourceService.GetString("Info.DirectoriesConfigured")
            : ResourceService.GetString("Info.SelectSourceAndDest");
        UpdateStartButton();
    }
}
