using System;
using System.Net;
using System.Net.Sockets;

namespace MonoTorrent.Connections;

public static class UriExtensions
{
    public static Uri ToPeerUri (this IPEndPoint endPoint)
    {
        string proto = endPoint.AddressFamily switch {
            AddressFamily.InterNetwork => "ipv4",
            AddressFamily.InterNetworkV6 => "ipv6",
            _ => throw new NotSupportedException ($"AddressFamily.{endPoint.AddressFamily} is not supported.")
        };
        return new Uri ($"{proto}://{endPoint}");
    }
}
