namespace Desuwatch.Capture;

/// <summary>
/// A single network transfer event observed by the capture engine.
/// Bytes can be sent or received; direction indicates which.
/// </summary>
public readonly record struct NetworkEvent(
    DateTime Timestamp,
    int ProcessId,
    string ProcessName,
    TransferDirection Direction,
    int Bytes,
    string RemoteAddress,
    int RemotePort,
    Protocol Protocol);

public enum TransferDirection
{
    Sent,
    Received
}

public enum Protocol
{
    Tcp,
    Udp
}
