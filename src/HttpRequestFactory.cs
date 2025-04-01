﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ReusableTasks;

namespace MonoTorrent
{
    class HttpRequestFactory
    {
        static readonly HttpMessageHandler CachedIPv4HttpClient = CreateHttpClientHandler (AddressFamily.InterNetwork);
        static readonly HttpMessageHandler CachedIPv6HttpClient = CreateHttpClientHandler (AddressFamily.InterNetworkV6);
        static readonly HttpMessageHandler CachedAnyHttpClient = CreateHttpClientHandler (AddressFamily.Unspecified);

        public static HttpClient CreateHttpClient (AddressFamily family)
        {
            var handler = family switch {
                AddressFamily.InterNetwork => CachedIPv4HttpClient,
                AddressFamily.InterNetworkV6 => CachedIPv6HttpClient,
                _ => CachedAnyHttpClient
            };

            var client = new HttpClient (handler, false);
            client.DefaultRequestHeaders.Add ("User-Agent", GitInfoHelper.ClientVersion);
            client.Timeout = TimeSpan.FromSeconds (30);
            return client;
        }

        static HttpMessageHandler CreateHttpClientHandler (AddressFamily family)
        {
            TimeSpan pooledConnectionLifetime = TimeSpan.FromMinutes (2);

#if NETSTANDARD2_0 || NETSTANDARD2_1 || NETCOREAPP3_0 || NET472
            if (family != AddressFamily.InterNetwork && family != AddressFamily.InterNetworkV6)
                return new StandardSocketsHttpHandler { PooledConnectionLifetime = pooledConnectionLifetime };
            return new StandardSocketsHttpHandler { PooledConnectionLifetime = pooledConnectionLifetime };
#else
            // Let the default SocketsHttpHandler manage the connection logic, including address family selection.
            return new SocketsHttpHandler { PooledConnectionLifetime = pooledConnectionLifetime };
#endif
        }
    }
}
