using System.Net;
using System.Net.Sockets;

namespace Patterns.App.Tests;

/// <summary>
/// The suite's free TCP ports, probed the way the desk binds them: on the any address (<see cref="IPAddress.Any"/>), where
/// the wire, the web remote and the audience listener open by default — a port another socket holds at any address of the
/// machine fails that probe as it would fail the desk. The ports one desk is given come from one <see cref="Distinct"/>
/// call, which holds every probe open until all of them are picked, so no two come back equal. A port is free only until
/// its probe lets it go; a desk that still cannot bind says so through <see cref="TestListeners"/>, never by a timeout.
/// </summary>
internal static class TestPorts
{
    /// <summary>One free port, by the same probe as <see cref="Distinct"/>.</summary>
    public static int Free() => Distinct(1)[0];

    /// <summary>Two distinct free ports for one desk (the web remote's and the wire's, say).</summary>
    public static (int, int) Two()
    {
        var p = Distinct(2);
        return (p[0], p[1]);
    }

    /// <summary>Three distinct free ports for one desk (the web remote's, the wire's and the audience's).</summary>
    public static (int, int, int) Three()
    {
        var p = Distinct(3);
        return (p[0], p[1], p[2]);
    }

    /// <summary>
    /// <paramref name="count"/> free UDP ports (OSC's, the beacon's), distinct by construction as <see cref="Distinct"/>'s are: each
    /// probe is a datagram socket bound on the any address, held until the last is picked.
    /// </summary>
    public static int[] DistinctUdp(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var probes = new List<UdpClient>(count);
        try
        {
            for (var i = 0; i < count; i++) probes.Add(new UdpClient(new IPEndPoint(IPAddress.Any, 0)));
            return probes.Select(p => ((IPEndPoint)p.Client.LocalEndPoint!).Port).ToArray();
        }
        finally
        {
            foreach (var probe in probes) probe.Dispose();
        }
    }

    /// <summary>
    /// <paramref name="count"/> free ports, distinct by construction: each probe is opened on the any address and stays open
    /// until the last is picked, so the operating system cannot hand one number out twice; then every probe is closed.
    /// </summary>
    public static int[] Distinct(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var probes = new List<TcpListener>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var probe = new TcpListener(IPAddress.Any, 0);
                probes.Add(probe);
                probe.Start();
            }
            return probes.Select(p => ((IPEndPoint)p.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var probe in probes) probe.Dispose();
        }
    }
}
