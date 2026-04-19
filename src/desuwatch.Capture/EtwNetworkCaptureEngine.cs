using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace Desuwatch.Capture;

/// <summary>
/// ETW-backed implementation that taps the kernel TCP/IP and UDP/IP providers
/// to observe per-process byte transfers. Requires administrator privileges
/// to create a kernel trace session.
/// </summary>
public sealed class EtwNetworkCaptureEngine : INetworkCaptureEngine
{
    private const string SessionName = "desuwatch-kernel-session";

    private TraceEventSession? _session;
    private Thread? _processingThread;
    private readonly Dictionary<int, string> _processNameCache = new();
    private readonly object _cacheLock = new();

    public event EventHandler<NetworkEvent>? EventObserved;

    public bool IsRunning => _session is not null;

public void Start()
    {
        if (_session is not null)
            throw new InvalidOperationException("Capture is already running.");

        if (!IsRunningAsAdministrator())
            throw new UnauthorizedAccessException(
                "Kernel ETW capture requires administrator privileges. " +
                "Restart the application elevated.");

        // Orphaned sessions survive process crashes. If a prior desuwatch
        // instance went down hard, the kernel still has our named session
        // registered and creating a new one with the same name will throw.
        // Clean it up first; this is a no-op in the common case.
        TryCleanupStaleSession(SessionName);

        var sessionName = ResolveSessionName();
        try
        {
            _session = new TraceEventSession(sessionName);
        }
        catch (UnauthorizedAccessException)
        {
            // Bubble as-is — the elevation check above should have caught
            // this, but Windows can still deny for other reasons (AppLocker,
            // restricted token, etc.)
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not create kernel ETW session. This can happen on " +
                $"older Windows builds or if another tool is holding an " +
                $"incompatible session. Underlying error: {ex.Message}", ex);
        }

        try
        {
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP);
        }
        catch (Exception ex)
        {
            // EnableKernelProvider can fail independently of session creation
            // (e.g. on Win7-era builds where only NT Kernel Logger can enable
            // kernel providers). Dispose the session so we don't leak it.
            _session.Dispose();
            _session = null;
            throw new InvalidOperationException(
                $"Kernel network provider unavailable. desuwatch requires " +
                $"Windows 10 build 9200 or later. Underlying error: {ex.Message}", ex);
        }

        WireUpHandlers(_session.Source.Kernel);

        _processingThread = new Thread(() =>
        {
            try
            {
                _session.Source.Process();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[desuwatch] ETW processing loop crashed: {ex}");
            }
        })
        {
            Name = "desuwatch-etw-processor",
            IsBackground = true
        };
        _processingThread.Start();
    }

    /// <summary>
    /// If a session with the given name is already registered with the
    /// kernel (typically because a prior desuwatch instance crashed),
    /// attempts to stop and remove it. Failures are logged and swallowed
    /// — if we can't clean up, the subsequent session creation will fail
    /// with a clearer error anyway.
    /// </summary>
    private static void TryCleanupStaleSession(string name)
    {
        try
        {
            var active = TraceEventSession.GetActiveSessionNames();
            if (!active.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

            Debug.WriteLine($"[desuwatch] Cleaning up stale ETW session '{name}'.");
            using var stale = new TraceEventSession(name)
            {
                StopOnDispose = true
            };
            // The ctor attaches to the existing session; Dispose stops it.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[desuwatch] Stale session cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the primary session name unless it's unexpectedly still
    /// registered after cleanup, in which case falls back to a unique
    /// suffix. This only fires in weird cases — e.g. another tool
    /// managed to recreate our session name between cleanup and creation.
    /// </summary>
    private static string ResolveSessionName()
    {
        try
        {
            var active = TraceEventSession.GetActiveSessionNames();
            if (!active.Contains(SessionName, StringComparer.OrdinalIgnoreCase))
                return SessionName;

            var fallback = $"{SessionName}-{Environment.ProcessId}";
            Debug.WriteLine(
                $"[desuwatch] Primary session name still in use after cleanup, " +
                $"using fallback '{fallback}'.");
            return fallback;
        }
        catch
        {
            // If we can't even enumerate sessions, just try the primary
            // name and let TraceEventSession's ctor surface the real error.
            return SessionName;
        }
    }

    private void WireUpHandlers(KernelTraceEventParser kernel)
    {
        kernel.TcpIpSend += e => Publish(e, TransferDirection.Sent, Protocol.Tcp);
        kernel.TcpIpRecv += e => Publish(e, TransferDirection.Received, Protocol.Tcp);
        kernel.TcpIpSendIPV6 += e => Publish(e, TransferDirection.Sent, Protocol.Tcp);
        kernel.TcpIpRecvIPV6 += e => Publish(e, TransferDirection.Received, Protocol.Tcp);

        kernel.UdpIpSend += e => Publish(e, TransferDirection.Sent, Protocol.Udp);
        kernel.UdpIpRecv += e => Publish(e, TransferDirection.Received, Protocol.Udp);
        kernel.UdpIpSendIPV6 += e => Publish(e, TransferDirection.Sent, Protocol.Udp);
        kernel.UdpIpRecvIPV6 += e => Publish(e, TransferDirection.Received, Protocol.Udp);
    }

    private void Publish(TcpIpSendTraceData e, TransferDirection dir, Protocol proto)
    {
        EmitEvent(e.TimeStamp, e.ProcessID, e.ProcessName, dir, (int)e.size,
            e.daddr.ToString(), e.dport, proto);
    }

    private void Publish(TcpIpTraceData e, TransferDirection dir, Protocol proto)
    {
        EmitEvent(e.TimeStamp, e.ProcessID, e.ProcessName, dir, e.size,
            e.daddr.ToString(), e.dport, proto);
    }

    private void Publish(TcpIpV6SendTraceData e, TransferDirection dir, Protocol proto)
    {
        EmitEvent(e.TimeStamp, e.ProcessID, e.ProcessName, dir, (int)e.size,
            e.daddr.ToString(), e.dport, proto);
    }

    private void Publish(TcpIpV6TraceData e, TransferDirection dir, Protocol proto)
    {
        EmitEvent(e.TimeStamp, e.ProcessID, e.ProcessName, dir, e.size,
            e.daddr.ToString(), e.dport, proto);
    }

    private void Publish(UdpIpTraceData e, TransferDirection dir, Protocol proto)
    {
        EmitEvent(e.TimeStamp, e.ProcessID, e.ProcessName, dir, e.size,
            e.daddr.ToString(), e.dport, proto);
    }

    private void Publish(UpdIpV6TraceData e, TransferDirection dir, Protocol proto)
    {
        EmitEvent(e.TimeStamp, e.ProcessID, e.ProcessName, dir, e.size,
            e.daddr.ToString(), e.dport, proto);
    }

    private void EmitEvent(DateTime timestamp, int pid, string? fallbackName,
        TransferDirection direction, int bytes, string remoteAddr, int remotePort,
        Protocol protocol)
    {
        if (bytes <= 0) return;

        var name = ResolveProcessName(pid, fallbackName);
        if (string.IsNullOrEmpty(name)) return;

        var evt = new NetworkEvent(timestamp, pid, name, direction, bytes,
            remoteAddr, remotePort, protocol);

        EventObserved?.Invoke(this, evt);
    }

    private string ResolveProcessName(int pid, string? fromEtw)
    {
        if (!string.IsNullOrEmpty(fromEtw))
            return fromEtw;

        lock (_cacheLock)
        {
            if (_processNameCache.TryGetValue(pid, out var cached))
                return cached;

            string resolved;
            try
            {
                using var p = Process.GetProcessById(pid);
                resolved = p.ProcessName;
            }
            catch
            {
                resolved = $"pid:{pid}";
            }

            _processNameCache[pid] = resolved;
            return resolved;
        }
    }

    public void Stop()
    {
        if (_session is null) return;

        try
        {
            _session.Source.StopProcessing();
            _session.Dispose();
        }
        finally
        {
            _session = null;
            _processingThread?.Join(TimeSpan.FromSeconds(2));
            _processingThread = null;
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose() => Stop();
}
