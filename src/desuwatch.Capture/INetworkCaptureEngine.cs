namespace Desuwatch.Capture;

/// <summary>
/// Contract for a network capture engine. Concrete implementations may back
/// onto ETW, raw sockets, or a test harness.
/// </summary>
public interface INetworkCaptureEngine : IDisposable
{
    /// <summary>
    /// Raised on the capture thread for every observed network transfer.
    /// Handlers should return quickly and marshal to the UI thread themselves
    /// if they need to update UI state.
    /// </summary>
    event EventHandler<NetworkEvent>? EventObserved;

    /// <summary>
    /// Starts capturing. Throws if the current process lacks the privileges
    /// required by the underlying implementation (kernel ETW requires admin).
    /// </summary>
    void Start();

    /// <summary>
    /// Stops capturing and releases OS-level resources.
    /// </summary>
    void Stop();

    bool IsRunning { get; }
}
