using System.Collections.ObjectModel;
using System.ComponentModel;
using EZDIT.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace EZDIT;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<JobHistoryItem> _newJobs = [];
    private readonly ObservableCollection<JobHistoryItem> _visibleHistory = [];
    private readonly Dictionary<FrameworkElement, Storyboard> _attentionAnimations = [];
    private readonly Dictionary<FrameworkElement, (JobHistoryItem Item, PropertyChangedEventHandler Handler)> _attentionPropertyHandlers = [];


    private void RebuildTaskSections()
    {
        _newJobs.Clear();
        _visibleHistory.Clear();
        foreach (JobHistoryItem item in _history)
        {
            GetTaskSection(item).Add(item);
        }
        UpdateTaskSectionEmptyStates();
    }

    private void SyncTaskSection(JobHistoryItem item)
    {
        ObservableCollection<JobHistoryItem> target = GetTaskSection(item);
        ObservableCollection<JobHistoryItem> other = ReferenceEquals(target, _newJobs)
            ? _visibleHistory
            : _newJobs;
        other.Remove(item);

        int masterIndex = _history.IndexOf(item);
        int desiredIndex = masterIndex < 0
            ? target.Count
            : _history.Take(masterIndex).Count(candidate => ReferenceEquals(GetTaskSection(candidate), target));
        int currentIndex = target.IndexOf(item);
        desiredIndex = Math.Clamp(desiredIndex, 0, target.Count);
        if (currentIndex < 0)
        {
            target.Insert(desiredIndex, item);
        }
        else
        {
            int existingTargetIndex = Math.Clamp(desiredIndex, 0, target.Count - 1);
            if (currentIndex != existingTargetIndex)
            {
                target.Move(currentIndex, existingTargetIndex);
            }
        }
        UpdateTaskSectionEmptyStates();
    }

    private ObservableCollection<JobHistoryItem> GetTaskSection(JobHistoryItem item) =>
        item.IsAcknowledged ? _visibleHistory : _newJobs;

    private void RemoveTaskFromSections(JobHistoryItem item)
    {
        _newJobs.Remove(item);
        _visibleHistory.Remove(item);
        UpdateTaskSectionEmptyStates();
    }

    private void UpdateTaskSectionEmptyStates()
    {
        NewJobsEmptyText.Visibility = _newJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyText.Visibility = _visibleHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectInitialTask()
    {
        if (_isMultiSelectMode)
        {
            return;
        }
        if (_newJobs.Count > 0)
        {
            NewJobsList.SelectedIndex = 0;
        }
        else if (_visibleHistory.Count > 0)
        {
            HistoryList.SelectedIndex = 0;
        }
        else
        {
            PrepareConcurrentNewJobView();
        }
    }

    private void NewJobsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isMultiSelectMode)
        {
            if (!_isChangingMultiSelectMode)
            {
                UpdateBatchSelectionUi();
            }
            return;
        }
        if (NewJobsList.SelectedItem is not JobHistoryItem item)
        {
            return;
        }

        HistoryList.SelectedItem = null;
        _selectedJob = item;
        if (_jobRuntimes.TryGetValue(item.Id, out CopyJobRuntime? runtime) && runtime is not null)
        {
            ShowRuntimeJob(runtime);
        }
        else
        {
            ShowHistoryJob(item);
        }
    }

    private async void NewJobsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_isMultiSelectMode)
        {
            return;
        }
        if (e.ClickedItem is not JobHistoryItem item || !item.NeedsAttention)
        {
            return;
        }

        item.IsAcknowledged = true;
        NewJobsList.SelectedItem = null;
        SyncTaskSection(item);
        _selectedJob = item;
        HistoryList.SelectedItem = item;
        ShowHistoryJob(item);
        await SaveHistorySafeAsync();
    }

    private void NewJobCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JobHistoryItem item } card)
        {
            return;
        }

        DetachAttentionObserver(card);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(JobHistoryItem.NeedsAttention))
            {
                return;
            }

            void RefreshAttention()
            {
                if (card.IsLoaded && ReferenceEquals(card.DataContext, item))
                {
                    UpdateAttentionAnimation(card, item);
                }
            }

            if (DispatcherQueue.HasThreadAccess)
            {
                RefreshAttention();
            }
            else
            {
                DispatcherQueue.TryEnqueue(RefreshAttention);
            }
        };
        _attentionPropertyHandlers[card] = (item, handler);
        item.PropertyChanged += handler;
        UpdateAttentionAnimation(card, item);
    }

    private void NewJobCard_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement card)
        {
            return;
        }

        DetachAttentionObserver(card);
        StopAttentionAnimation(card);
    }

    private void UpdateAttentionAnimation(FrameworkElement card, JobHistoryItem item)
    {
        if (!item.NeedsAttention)
        {
            StopAttentionAnimation(card);
            return;
        }

        if (_attentionAnimations.ContainsKey(card))
        {
            return;
        }

        var pulse = new DoubleAnimation
        {
            From = 0.48,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(850)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulse, card);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(pulse);
        _attentionAnimations[card] = storyboard;
        storyboard.Begin();
    }

    private void StopAttentionAnimation(FrameworkElement card)
    {
        if (_attentionAnimations.Remove(card, out Storyboard? storyboard))
        {
            storyboard.Stop();
        }

        card.Opacity = 1;
    }

    private void DetachAttentionObserver(FrameworkElement card)
    {
        if (_attentionPropertyHandlers.Remove(card, out var subscription))
        {
            subscription.Item.PropertyChanged -= subscription.Handler;
        }
    }
}
