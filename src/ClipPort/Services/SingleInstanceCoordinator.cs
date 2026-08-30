using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ClipPort.Models;

namespace ClipPort.Services;

/// <summary>
/// Keeps packaged and unpackaged launches in one process and forwards activation
/// payloads through a per-user named pipe. Windows App SDK instance keys are not
/// sufficient here because the same executable can run with different package identities.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string DefaultInstanceName = "ClipPort.MainInstance";
    private static readonly TimeSpan DefaultRedirectTimeout = TimeSpan.FromSeconds(5);

    private readonly EventWaitHandle _lifetimeMarker;
    private readonly string _pipeName;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private bool _disposed;

    private SingleInstanceCoordinator(
        EventWaitHandle lifetimeMarker,
        string pipeName,
        bool isPrimary)
    {
        _lifetimeMarker = lifetimeMarker;
        _pipeName = pipeName;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public static SingleInstanceCoordinator Acquire(
        string instanceName = DefaultInstanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        if (instanceName.Contains('\\') || instanceName.Contains('/'))
        {
            throw new ArgumentException(
                "The instance name cannot contain path separators.",
                nameof(instanceName));
        }

        var lifetimeMarker = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            $@"Local\{instanceName}.Lifetime",
            out bool createdNew);
        return new SingleInstanceCoordinator(
            lifetimeMarker,
            GetPipeName(instanceName),
            createdNew);
    }

    internal static string GetPipeName(string instanceName)
    {
        // MSIX-packaged desktop processes require the LOCAL segment for named
        // pipes. The same session-local name is also reachable by unpackaged
        // Win32 launches, so Explorer activation can cross the identity boundary.
        return $@"LOCAL\{instanceName}.Activation";
    }

    public void StartListening(Action<AppActivationRequest> activationHandler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activationHandler);
        if (!IsPrimary)
        {
            throw new InvalidOperationException(
                "Only the primary ClipPort instance can listen for activations.");
        }
        if (_listenerTask is not null)
        {
            throw new InvalidOperationException(
                "The activation listener has already been started.");
        }

        _listenerCancellation = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenAsync(
            activationHandler,
            _listenerCancellation.Token));
    }

    public bool TryRedirect(
        AppActivationRequest request,
        TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (IsPrimary)
        {
            throw new InvalidOperationException(
                "The primary ClipPort instance cannot redirect to itself.");
        }

        TimeSpan effectiveTimeout = timeout ?? DefaultRedirectTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        string payload = JsonSerializer.Serialize(request);
        long deadline = Stopwatch.GetTimestamp() +
            (long)(effectiveTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            int remainingMilliseconds = Math.Max(
                1,
                (int)Math.Min(
                    250,
                    Stopwatch.GetElapsedTime(
                        Stopwatch.GetTimestamp(),
                        deadline).TotalMilliseconds));
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.None);
                pipe.Connect(remainingMilliseconds);
                using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true);
                writer.WriteLine(payload);
                writer.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                Thread.Sleep(25);
            }
        }

        return false;
    }

    private async Task ListenAsync(
        Action<AppActivationRequest> activationHandler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(
                    pipe,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                string? payload = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    AppActivationRequest? request =
                        JsonSerializer.Deserialize<AppActivationRequest>(payload);
                    if (request is not null)
                    {
                        activationHandler(request);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (JsonException)
            {
                // Ignore malformed local payloads and continue serving valid launches.
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A secondary process can exit between connecting and writing.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation?.Cancel();
        _listenerCancellation?.Dispose();
        _lifetimeMarker.Dispose();
    }
}
