using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClipPort.Models;
using ClipPort.Services;

namespace ClipPort.Views;

internal sealed record NotificationChannelKindOption(
    NotificationChannelKind Kind,
    string DisplayName);

internal sealed class NotificationChannelEditorItem : INotifyPropertyChanged
{
    private readonly Action _changed;
    private string _testStatus = string.Empty;

    public NotificationChannelEditorItem(
        NotificationChannelSettings channel,
        Action changed)
    {
        Channel = channel;
        _changed = changed;
        KindOptions = Enum.GetValues<NotificationChannelKind>()
            .Select(kind => new NotificationChannelKindOption(
                kind,
                ResourceService.GetString($"Notification.ChannelKind.{kind}")))
            .ToArray();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NotificationChannelSettings Channel { get; }
    public IReadOnlyList<NotificationChannelKindOption> KindOptions { get; }

    public NotificationChannelKindOption SelectedKind
    {
        get => KindOptions.First(option => option.Kind == Channel.Kind);
        set
        {
            if (value is null || value.Kind == Channel.Kind)
            {
                return;
            }
            Channel.Kind = value.Kind;
            if (string.IsNullOrWhiteSpace(Channel.DisplayName))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHttpChannel));
            OnPropertyChanged(nameof(IsSmtpChannel));
            OnPropertyChanged(nameof(EndpointHeader));
            OnPropertyChanged(nameof(EndpointPlaceholder));
            _changed();
        }
    }

    public string DisplayName
    {
        get => string.IsNullOrWhiteSpace(Channel.DisplayName)
            ? SelectedKind.DisplayName
            : Channel.DisplayName;
        set => SetValue(Channel.DisplayName, value, updated => Channel.DisplayName = updated);
    }

    public bool IsEnabled
    {
        get => Channel.IsEnabled;
        set => SetValue(Channel.IsEnabled, value, updated => Channel.IsEnabled = updated);
    }

    public string Endpoint
    {
        get => Channel.Endpoint;
        set => SetValue(Channel.Endpoint, value, updated => Channel.Endpoint = updated);
    }

    public string SmtpHost
    {
        get => Channel.SmtpHost;
        set => SetValue(Channel.SmtpHost, value, updated => Channel.SmtpHost = updated);
    }

    public double SmtpPort
    {
        get => Channel.SmtpPort;
        set
        {
            int port = double.IsNaN(value) ? 0 : (int)value;
            SetValue(Channel.SmtpPort, port, updated => Channel.SmtpPort = updated);
        }
    }

    public string SmtpUsername
    {
        get => Channel.SmtpUsername;
        set => SetValue(Channel.SmtpUsername, value, updated => Channel.SmtpUsername = updated);
    }

    public string SmtpPassword
    {
        get => Channel.SmtpPassword;
        set => SetValue(Channel.SmtpPassword, value, updated => Channel.SmtpPassword = updated);
    }

    public string SmtpFrom
    {
        get => Channel.SmtpFrom;
        set => SetValue(Channel.SmtpFrom, value, updated => Channel.SmtpFrom = updated);
    }

    public string SmtpRecipients
    {
        get => Channel.SmtpRecipients;
        set => SetValue(Channel.SmtpRecipients, value, updated => Channel.SmtpRecipients = updated);
    }

    public bool IsHttpChannel => Channel.Kind != NotificationChannelKind.Smtp;
    public bool IsSmtpChannel => Channel.Kind == NotificationChannelKind.Smtp;

    public string EndpointHeader => Channel.Kind == NotificationChannelKind.Bark
        ? ResourceService.GetString("Notification.BarkAddress")
        : ResourceService.GetString("Notification.WebhookAddress");

    public string EndpointPlaceholder => Channel.Kind == NotificationChannelKind.Bark
        ? "https://api.day.app/your-device-key"
        : "https://...";

    public string TestStatus
    {
        get => _testStatus;
        set
        {
            if (_testStatus == value)
            {
                return;
            }
            _testStatus = value;
            OnPropertyChanged();
        }
    }

    private void SetValue<T>(T current, T value, Action<T> assign, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }
        assign(value);
        OnPropertyChanged(propertyName);
        _changed();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
