using System.Diagnostics;

namespace ThermalDesk.Core;

// SET speed, RGB and initialization acknowledgements share the telemetry pipe.
// Match the whole GET header, not a fixed four-packet allowance. Both time and
// packet count are bounded so a noisy or disconnected device cannot spin forever.
public static class FanResponseReader
{
    public static FanReading Read(Func<int, byte[]> read, int port, Action<int>? skipped = null)
    {
        if (port is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(port));
        var clock = Stopwatch.StartNew();
        var headers = new Queue<string>();
        int count = 0;
        try
        {
            while (count < 128 && clock.ElapsedMilliseconds < 1200)
            {
                var packet = read(Math.Max(1, 1200 - (int)clock.ElapsedMilliseconds));
                try { var result = Protocols.ParseFan(packet, port); skipped?.Invoke(count); return result; }
                catch (ArgumentException)
                {
                    count++;
                    headers.Enqueue(Convert.ToHexString(packet.AsSpan(0, Math.Min(8, packet.Length))));
                    if (headers.Count > 6) headers.Dequeue();
                }
            }
        }
        catch (TimeoutException error)
        { throw new IOException($"Telemetry timeout on port {port} after {count} unrelated reports; recent headers: {string.Join(",", headers)}", error); }
        throw new IOException($"No matching telemetry for port {port} after {count} reports within 1200 ms; recent headers: {string.Join(",", headers)}");
    }
}
