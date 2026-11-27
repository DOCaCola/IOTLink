using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IOTLinkAPI.Platform
{
    public sealed class NetworkAdapter
    {
        private readonly NetworkInterface _network;
        private readonly string _ipv4Address;
        private readonly string _ipv6Address;

        internal NetworkAdapter(NetworkInterface network, string ipv4Address, string ipv6Address)
        {
            _network = network;
            _ipv4Address = ipv4Address;
            _ipv6Address = ipv6Address;
        }

        public string Id => _network.Id;

        public static IEnumerable<NetworkAdapter> Discover()
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network == null || network.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (network.NetworkInterfaceType != NetworkInterfaceType.Ethernet && network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                    continue;

                var description = network.Description;
                if (description != null && (description.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            description.IndexOf("pseudo", StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                var properties = network.GetIPProperties();
                if (properties == null)
                    continue;

                string ipv4 = null;
                string ipv6 = null;
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address == null || IPAddress.IsLoopback(address))
                        continue;

                    if (address.AddressFamily == AddressFamily.InterNetwork)
                        ipv4 = address.ToString();
                    else if (ipv6 == null && address.AddressFamily == AddressFamily.InterNetworkV6)
                        ipv6 = address.ToString();
                }

                if (ipv4 != null || ipv6 != null)
                    yield return new NetworkAdapter(network, ipv4, ipv6);
            }
        }

        public NetworkInfo Read()
        {
            var counters = _network.GetIPStatistics();
            return new NetworkInfo
            {
                IPv4Address = _ipv4Address,
                IPv6Address = _ipv6Address,
                Wired = _network.NetworkInterfaceType == NetworkInterfaceType.Ethernet,
                Speed = _network.Speed / 1000000,
                BytesReceived = counters?.BytesReceived ?? 0L,
                BytesSent = counters?.BytesSent ?? 0L
            };
        }
    }
}
