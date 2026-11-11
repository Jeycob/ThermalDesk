using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace ThermalDesk.Core;

public static partial class Protocols
{
    public const int Vendor = 0x264A, PanelPid = 0x233D, AioPid = 0x2328, FanPid = 0x232B;
    public static byte[] Command(byte opcode)
    {
        var p = new byte[441]; p[1] = opcode; p[2] = 1; p[4] = 0x80; return p;
    }
    public static IReadOnlyList<byte[]> PanelJpeg(ReadOnlySpan<byte> jpeg)
    {
        ValidateJpeg(jpeg);
        int count = (jpeg.Length + 1019) / 1020;
        if (count > 255) throw new ArgumentException("Panel JPEG exceeds 255 packets.");
        var packets = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            var p = new byte[1025]; p[1] = 8; p[2] = (byte)(i == 0 ? count : i); p[4] = i == 0 ? (byte)0x80 : (byte)0;
            jpeg.Slice(i * 1020, Math.Min(1020, jpeg.Length - i * 1020)).CopyTo(p.AsSpan(5)); packets.Add(p);
        }
        return packets;
    }
    public static IReadOnlyList<byte[]> AioJpeg(ReadOnlySpan<byte> jpeg)
    {
        ValidateJpeg(jpeg);
        if (jpeg.Length > 480 * 480 * 3) throw new ArgumentException("AIO JPEG is too large.");
        int count = (jpeg.Length + 1015) / 1016;
        var packets = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            var p = new byte[1024]; p[0] = 2; p[1] = 9; p[3] = (byte)(i == count - 1 ? 1 : 0);
            int length = Math.Min(1016, jpeg.Length - i * 1016);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), (ushort)length);
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)i);
            jpeg.Slice(i * 1016, length).CopyTo(p.AsSpan(8)); packets.Add(p);
        }
        return packets;
    }
    private static void ValidateJpeg(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xff || b[1] != 0xd8 || b[^2] != 0xff || b[^1] != 0xd9) throw new ArgumentException("A complete JPEG is required.");
    }
    public static bool IsAxFirmware(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 16 || reply[0] != 0x0a) return false;
        string v = Encoding.ASCII.GetString(reply.Slice(6, 10)).Split('\0')[0];
        return FirmwareVersion().IsMatch(v);
    }
    [GeneratedRegex(@"\A\d+(\.\d+){1,3}\z")]
    private static partial Regex FirmwareVersion();
    public static (int Width, int Height) AxSize(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 11 || reply[0] != 7) throw new ArgumentException("Invalid screen reply.");
        int w = BinaryPrimitives.ReadUInt16LittleEndian(reply[7..]), h = BinaryPrimitives.ReadUInt16LittleEndian(reply[9..]);
        if (w != 480 || h != 480) throw new NotSupportedException($"Unverified LCD resolution {w}×{h}.");
        return (w, h);
    }
    public static byte[] FanCommand(byte operation, byte function, int port, params byte[] data)
    {
        if (port is < 0 or > 5 || data.Length > 189) throw new ArgumentOutOfRangeException(nameof(port));
        var p = new byte[193]; p[1] = operation; p[2] = function; p[3] = (byte)port; data.CopyTo(p, 4); return p;
    }
    public static byte[] SetDuty(int port, int duty)
    {
        if (duty is < 30 or > 100) throw new ArgumentOutOfRangeException(nameof(duty));
        return FanCommand(0x32, 0x51, port, 1, (byte)duty);
    }
    public static FanReading ParseFan(ReadOnlySpan<byte> p, int expectedPort)
    {
        if (p.Length < 8 || p[0] != 0 || p[1] != 0x33 || p[2] != 0x51 || p[3] != expectedPort || p[5] > 100)
            throw new ArgumentException("Mismatched fan response.");
        return new(expectedPort, BinaryPrimitives.ReadUInt16LittleEndian(p[6..]), p[5]);
    }
    public static byte[] Rgb(int port, int leds, string hex)
    {
        if (leds is < 1 or > 62 || hex.Length != 6 || !hex.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid RGB data.");
        var p = FanCommand(0x32, 0x52, port, 0x24);
        byte r = Convert.ToByte(hex[..2], 16), g = Convert.ToByte(hex[2..4], 16), b = Convert.ToByte(hex[4..6], 16);
        for (int i = 0; i < leds; i++) { p[5 + i * 3] = g; p[6 + i * 3] = r; p[7 + i * 3] = b; }
        return p;
    }
    public static byte[] RgbFrame(int port, IReadOnlyList<LedColor> colors)
    {
        if (port is < 1 or > 5 || colors.Count is < 1 or > 62) throw new ArgumentException("Invalid RGB port or LED count.");
        var packet = FanCommand(0x32, 0x52, port, 0x24);
        for (int i = 0; i < colors.Count; i++) { packet[5 + i * 3] = colors[i].Green; packet[6 + i * 3] = colors[i].Red; packet[7 + i * 3] = colors[i].Blue; }
        return packet;
    }
}
