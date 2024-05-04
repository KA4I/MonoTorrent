//
// MonoNatPortForwarder.cs
//
// Authors:
//   Alan McGovern <alan.mcgovern@gmail.com>
//
// Copyright (C) 2020 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Mono.Nat;

using MonoTorrent.Logging;

namespace MonoTorrent.PortForwarding
{
    public class MonoNatPortForwarder : IPortForwarder, IDisposable
    {
        readonly ILogger logger = LoggerFactory.Create (nameof (MonoNatPortForwarder));
        CancellationTokenSource stop = new CancellationTokenSource ();
        readonly List<(INatDevice dev, Mono.Nat.Mapping map)> active = new List<(INatDevice, Mono.Nat.Mapping)> ();

        public event EventHandler? MappingsChanged;

        public bool Active => NatUtility.IsSearching;

        IReadOnlyList<INatDevice> Devices { get; set; }

        SemaphoreSlim Locker { get; } = new SemaphoreSlim (1);

        public Mappings Mappings { get; private set; }

        public MonoNatPortForwarder ()
        {
            Devices = new List<INatDevice> ();
            Mappings = Mappings.Empty;

            NatUtility.DeviceFound += this.OnDeviceFound;
        }

        async void OnDeviceFound(object? _, DeviceEventArgs e)
        {
            using (await Locker.EnterAsync ()) {
                if (Devices.Contains (e.Device))
                    return;
                Devices = Devices.Concat (new[] { e.Device }).ToArray ();
            }

            foreach (var mapping in Mappings.Pending)
                await CreateOrFailMapping (e.Device, mapping);

            RaiseMappingsChangedAsync ();
        }

        public async Task RegisterMappingAsync (Mapping mapping)
        {
            using (await Locker.EnterAsync ()) {
                Mappings = Mappings.WithPending (mapping);
                if (!Active)
                    return;

                foreach (var device in Devices)
                    await CreateOrFailMapping (device, mapping);
            }
            RaiseMappingsChangedAsync ();
        }

        public async Task UnregisterMappingAsync (Mapping mapping, CancellationToken token)
        {
            using (await Locker.EnterAsync ()) {
                Mappings = Mappings.Remove (mapping, out bool wasCreated);
                if (!Active)
                    return;

                if (wasCreated) {
                    foreach (var device in Devices) {
                        token.ThrowIfCancellationRequested ();
                        await DeletePortMapping (device, mapping);
                    }
                }
            }
            RaiseMappingsChangedAsync ();
        }

        public async Task StartAsync (CancellationToken token)
        {
            using (await Locker.EnterAsync ()) {
                stop = new CancellationTokenSource ();
                if (!Active) {
                    await new ThreadSwitcher ();
                    NatUtility.StartDiscovery (NatProtocol.Pmp, NatProtocol.Upnp);
                }
                Tick (stop.Token);
            }
        }

        public Task StopAsync (CancellationToken token)
            => StopAsync (true, token);

        public async Task StopAsync (bool removeExisting, CancellationToken token)
        {
            using (await Locker.EnterAsync ()) {
                // cancel asynchronously
                stop.CancelAfter (0);

                NatUtility.StopDiscovery ();

                var created = Mappings.Created;
                Mappings = Mappings.WithAllPending ();
                try {
                    if (removeExisting) {
                        foreach (var mapping in created) {
                            foreach (var device in Devices) {
                                token.ThrowIfCancellationRequested ();
                                await DeletePortMapping (device, mapping);
                            }
                        }
                    }
                } finally {
                    Devices = new List<INatDevice> ();
                    RaiseMappingsChangedAsync ();
                }
            }
        }

        async Task CreateOrFailMapping (INatDevice device, Mapping mapping)
        {
            var map = new Mono.Nat.Mapping (
                mapping.Protocol == Protocol.Tcp ? Mono.Nat.Protocol.Tcp : Mono.Nat.Protocol.Udp,
                mapping.PrivatePort,
                mapping.PublicPort
            );

            try {
                await device.CreatePortMapAsync (map);
                Mappings = Mappings.WithCreated (mapping);
                lock (active)
                    active.Add ((device, map));
                this.logger.Info ($"{Display(device)} successfully created mapping: {mapping} {map}");
            } catch (Exception e) {
                this.logger.Error ($"{Display (device)} failed to create mapping: {mapping}\n{e}");
                Mappings = Mappings.WithFailed (mapping);
            }
        }

        async Task DeletePortMapping (INatDevice device, Mapping mapping)
        {
            var map = new Mono.Nat.Mapping (
                mapping.Protocol == Protocol.Tcp ? Mono.Nat.Protocol.Tcp : Mono.Nat.Protocol.Udp,
                mapping.PrivatePort,
                mapping.PublicPort
            );

            try {
                await device.DeletePortMapAsync (map).ConfigureAwait (false);
                lock (active)
                    active.Remove ((device, map));
                this.logger.Info ($"{Display(device)} successfully deleted mapping: {mapping}");
            } catch (Exception e) {
                this.logger.Error ($"{Display(device)} failed to delete mapping: {mapping}\n{e}");
            }
        }

        async void Tick (CancellationToken token)
        {
            await new ThreadSwitcher ();

            while (!token.IsCancellationRequested) {
                (INatDevice dev, Mono.Nat.Mapping map)[] activeSnapshot;
                lock (active)
                    activeSnapshot = active.ToArray ();

                var replace = new List<(Mono.Nat.Mapping, Mono.Nat.Mapping?)> ();

                foreach (var mapping in activeSnapshot) {
                    if (token.IsCancellationRequested)
                        return;

                    if (mapping.map.Lifetime == 0) {
                        Mono.Nat.Mapping existing;
                        try {
                            existing = await mapping.dev.GetSpecificMappingAsync (mapping.map.Protocol, mapping.map.PublicPort).ConfigureAwait (false);
                            continue;
                        } catch (MappingException e) {
                            this.logger.Debug ($"{Display (mapping.dev)} mapping {mapping.map.Protocol}({mapping.map.PublicPort}) not found: {e}");
                        }
                    } else {
                        double timeLeft = (mapping.map.Expiration - DateTime.Now).TotalSeconds / (float) mapping.map.Lifetime;
                        if (timeLeft > 0.666)
                            continue;
                    }

                    this.logger.Debug ($"{Display (mapping.dev)} refreshing mapping {mapping.map.Protocol}({mapping.map.PublicPort})");
                    try {
                        await mapping.dev.DeletePortMapAsync (mapping.map).ConfigureAwait (false);
                    } catch (MappingException e) {
                        this.logger.Error ($"{Display (mapping.dev)} failed to delete mapping {mapping.map.Protocol}({mapping.map.PublicPort}): {e}");
                    }
                    var newMapping = new Mono.Nat.Mapping (
                            mapping.map.Protocol,
                            privatePort: mapping.map.PrivatePort,
                            publicPort: mapping.map.PublicPort);
                    try {
                        await mapping.dev.CreatePortMapAsync (mapping.map).ConfigureAwait (false);
                        replace.Add ((mapping.map, newMapping));
                    } catch (MappingException e) {
                        this.logger.Error ($"{Display (mapping.dev)} failed to recreate mapping {mapping.map.Protocol}({mapping.map.PublicPort}): {e}");
                        replace.Add ((mapping.map, null));
                    }
                }

                lock (active) {
                    int i = 0;
                    while (i < active.Count && replace.Count > 0) {
                        var current = active[i];
                        var (map, newMap) = replace.FirstOrDefault (r => r.Item1.Equals (current.map));
                        if (map is null) {
                            i++;
                            continue;
                        }
                        if (newMap is null)
                            active.RemoveAt (i);
                        else
                            active[i] = (current.dev, newMap);
                    }
                }

                await Task.Delay (TimeSpan.FromMinutes (1), token).ConfigureAwait (false);
            }
        }

        static string Display(INatDevice device) => $"{Display(device.NatProtocol)}({device.DeviceEndpoint})";
        static string Display(NatProtocol protocol) => protocol == NatProtocol.Pmp ? "PMP" : "UPnP";

        async void RaiseMappingsChangedAsync ()
        {
            if (MappingsChanged != null) {
                await new ThreadSwitcher ();
                MappingsChanged.Invoke (this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            NatUtility.DeviceFound -= this.OnDeviceFound;
        }
    }
}
