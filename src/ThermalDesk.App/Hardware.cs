using HidSharp;
using ThermalDesk.Core;

namespace ThermalDesk.App;

internal interface IFanLink : IDisposable
{
    void Write(byte[] packet);
    byte[] Read(int length, int timeoutMilliseconds);
}
internal sealed class HidLink : IFanLink
{
    private readonly HidStream stream;
    public HidLink(HidDevice device, int outputLength)
    {
        if (device.VendorID != Protocols.Vendor || device.GetMaxOutputReportLength() != outputLength)
            throw new NotSupportedException("Unexpected HID descriptor. No commands sent.");
        var configuration = new OpenConfiguration();
        configuration.SetOption(OpenOption.Exclusive, true);
        configuration.SetOption(OpenOption.Interruptible, false);
        configuration.SetOption(OpenOption.TimeoutIfTransient, 0);
        configuration.SetOption(OpenOption.TimeoutIfInterruptible, 0);
        if (!device.TryOpen(configuration, out stream!)) throw new IOException("Device busy. Close the vendor software first.");
        stream.ReadTimeout = 700; stream.WriteTimeout = 1000;
    }
    public void Write(byte[] packet) => stream.Write(packet);
    public byte[] Read(int length)
    {
        var b = new byte[length]; int n = stream.Read(b); return b[..n];
    }
    public byte[] Read(int length, int timeoutMilliseconds)
    {
        int previous = stream.ReadTimeout;
        try { stream.ReadTimeout = timeoutMilliseconds; return Read(length); }
        finally { stream.ReadTimeout = previous; }
    }
    public void Feature(byte[] b) => stream.SetFeature(b);
    public byte[] GetFeature(byte id, byte prefix = 0)
    {
        var b = new byte[64]; b[0] = id; b[1] = prefix; stream.GetFeature(b); return b;
    }
    public void Dispose() => stream.Dispose();
    public static HidDevice Single(int pid, string? serial = null)
    {
        var devices = DeviceList.Local.GetHidDevices(Protocols.Vendor, pid)
            .Where(d => string.IsNullOrEmpty(serial) || SafeSerial(d) == serial).ToArray();
        if (devices.Length != 1) throw new IOException($"Expected one {pid:X4} interface, found {devices.Length}. Select a unique controller serial if needed.");
        return devices[0];
    }
    public static string SafeSerial(HidDevice d) { try { return d.GetSerialNumber(); } catch { return ""; } }
    public static object[] Inventory() => DeviceList.Local.GetHidDevices(Protocols.Vendor).Select(d =>
    {
        try { return (object)new { vendor = $"{d.VendorID:X4}", product = $"{d.ProductID:X4}", inputLength = d.GetMaxInputReportLength(), outputLength = d.GetMaxOutputReportLength(), featureLength = d.GetMaxFeatureReportLength(), path = d.DevicePath }; }
        catch (Exception e) { return new { product = $"{d.ProductID:X4}", error = e.Message }; }
    }).ToArray();
}

internal interface ILcd : IDisposable
{
    void Send(byte[] jpeg);
    void Heartbeat();
}

internal sealed class PanelLcd : ILcd
{
    private readonly HidLink command, frame;
    public PanelLcd(int brightness)
    {
        var devices = DeviceList.Local.GetHidDevices(Protocols.Vendor, Protocols.PanelPid).ToArray();
        if (devices.Length == 0) throw new IOException("264A:233D chassis LCD is not present on USB. Check its USB data cable/power; a saved TT profile is not a connected device.");
        if (devices.Length != 2) throw new IOException($"Panel requires two HID interfaces; found {devices.Length}. No commands sent.");
        var first = devices.Single(d => d.DevicePath.Contains("&mi_00", StringComparison.OrdinalIgnoreCase));
        var second = devices.Single(d => d.DevicePath.Contains("&mi_01", StringComparison.OrdinalIgnoreCase));
        command = new(first, 441);
        try { frame = new(second, 1025); }
        catch { command.Dispose(); throw; }
        try
        {
            foreach (byte op in new byte[] { 0x85, 0x87, 0x85, 0x87, 0x84, 0x81 })
            {
                command.Write(Protocols.Command(op));
                if (command.Read(441).Length < 2) throw new IOException("Empty LCD acknowledgement.");
            }
            var b = Protocols.Command(0x12); b[5] = (byte)Math.Clamp(brightness, 0, 100); command.Write(b);
        }
        catch { Dispose(); throw; }
    }
    public void Send(byte[] jpeg)
    {
        foreach (var packet in Protocols.PanelJpeg(jpeg)) frame.Write(packet);
        if (frame.Read(1025).Length < 2) throw new IOException("Missing frame acknowledgement.");
    }
    public void Heartbeat() => command.Write(Protocols.Command(0x82));
    public void Dispose() { frame.Dispose(); command.Dispose(); }
}

// AX detection and protocol adapted from DailyDana/OpenAIO Panel (MIT).
internal sealed class AioLcd : ILcd
{
    private readonly HidLink link;
    public AioLcd(int brightness)
    {
        var device = HidLink.Single(Protocols.AioPid);
        if (device.GetMaxFeatureReportLength() != 64) throw new NotSupportedException("Unexpected AX feature descriptor.");
        link = new(device, 1024);
        try
        {
            if (!Protocols.IsAxFirmware(link.GetFeature(0x0a)))
                throw new NotSupportedException("Not a recognized AX firmware. No configuration sent; HKC panels are unsupported.");
            var prepare = new byte[64]; prepare[0] = 3; prepare[1] = 0x1a; link.Feature(prepare);
            var size = Protocols.AxSize(link.GetFeature(7, 0x1a));
            var mode = new byte[64]; mode[0] = 3; mode[1] = 0x0c; mode[2] = (byte)Math.Clamp(brightness, 0, 100);
            mode[7] = (byte)(size.Width & 255); mode[8] = (byte)(size.Width >> 8);
            mode[9] = (byte)(size.Height & 255); mode[10] = (byte)(size.Height >> 8); mode[11] = 4; mode[12] = 5;
            link.Feature(mode);
            // Match the reference loop: establish its status/keepalive before the first image.
            // This ordering is not by itself proof that a physical LCD rendered the image.
            link.GetFeature(0x0f);
        }
        catch { link.Dispose(); throw; }
    }
    public void Send(byte[] jpeg) { foreach (var packet in Protocols.AioJpeg(jpeg)) link.Write(packet); }
    public void Heartbeat() => link.GetFeature(0x0f);
    public void Dispose() => link.Dispose();
}

internal interface IFanController : IDisposable
{
    void SetDuty(int port, int duty);
    void SetRgbFrame(int port, IReadOnlyList<LedColor> colors);
    FanReading Read(int port);
    void PrepareToClose(bool criticalTemperature);
}

internal sealed class FanController : IFanController
{
    private readonly IFanLink link;
    private readonly int inputLength;
    private readonly Dictionary<int, int> original = [];
    private bool retainCriticalDuty;
    private int disposed;
    public long SkippedReports { get; private set; }
    internal FanController(IFanLink transport, int reportLength)
    {
        if (reportLength is < 8 or > 4096) throw new ArgumentException("Invalid test transport descriptor.");
        link = transport; inputLength = reportLength;
        link.Write(Protocols.FanCommand(0xfe, 0x33, 0));
    }
    public FanController(string serial)
    {
        var device = HidLink.Single(Protocols.FanPid, serial);
        inputLength = device.GetMaxInputReportLength();
        if (inputLength is < 8 or > 4096) throw new NotSupportedException("Unexpected controller input descriptor.");
        link = new HidLink(device, 193);
        try { link.Write(Protocols.FanCommand(0xfe, 0x33, 0)); }
        catch { link.Dispose(); throw; }
    }
    public void SetDuty(int port, int duty)
    {
        if (!original.ContainsKey(port))
        {
            var baseline = Read(port);
            if (baseline.Rpm <= 0 || baseline.Duty is < 30 or > 100) throw new IOException($"Port {port}: cannot confirm the original running duty. No speed command sent.");
            original[port] = baseline.Duty;
        }
        link.Write(Protocols.SetDuty(port, duty));
    }
    public void PrepareToClose(bool criticalTemperature)
    {
        retainCriticalDuty = criticalTemperature;
        if (criticalTemperature)
            foreach (int port in original.Keys) { try { link.Write(Protocols.SetDuty(port, 100)); } catch { } }
    }
    public void SetRgb(PortSettings port, string color) => link.Write(Protocols.Rgb(port.Port, port.LedCount, color));
    public void SetRgbFrame(int port, IReadOnlyList<LedColor> colors) => link.Write(Protocols.RgbFrame(port, colors));
    public FanReading Read(int port)
    {
        link.Write(Protocols.FanCommand(0x33, 0x51, port));
        return FanResponseReader.Read(timeout => link.Read(inputLength, timeout), port, count => SkippedReports += count);
    }
    // Explicit diagnostic restoration also confirms readback before closing.
    public FanReading RestoreAcceptanceDuty(int port, int originalDuty)
    {
        link.Write(Protocols.SetDuty(port, originalDuty));
        Thread.Sleep(1500);
        var reading = Read(port);
        if (reading.Duty != originalDuty) throw new IOException("Original duty was not confirmed; the controller will request the captured baseline on disposal.");
        original.Remove(port);
        return reading;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Stop/test completion is not an over-temperature event. Restore captured
        // duty instead of leaving a synthetic 100% "safety" command behind.
        // A genuinely critical temperature retains the active emergency duty.
        var failed = new List<int>();
        if (!retainCriticalDuty)
            foreach (var (port, duty) in original) { try { link.Write(Protocols.SetDuty(port, duty)); } catch { failed.Add(port); } }
        link.Dispose();
        if (failed.Count > 0) throw new IOException("Could not restore captured duty on port(s) " + string.Join(", ", failed) + ". Restore and verify the vendor controller; no fallback full-speed command was sent.");
    }
}
