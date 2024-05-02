using System;
using System.Net;

namespace MonoTorrent.Connections.Peer;

public interface IPeerConnectionLocalInfo
{
    ReadOnlyMemory<byte> LocalAddressBytes { get; }

    IPEndPoint? LocalEndPoint { get; }

    Uri? LocalUri { get; }
}
