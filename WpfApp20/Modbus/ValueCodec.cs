using WpfApp20.Models;

namespace WpfApp20.Modbus;

/// <summary>标签值的编码/解码（含缩放与字序）。</summary>
public static class ValueCodec
{
    /// <summary>从寄存器块解析出工程值。</summary>
    public static double Decode(TagDefinition tag, ushort[] block, int wordIndex)
    {
        double raw = tag.DataType switch
        {
            TagDataType.位 => block[wordIndex] != 0 ? 1 : 0,
            TagDataType.无符号16位 => block[wordIndex],
            TagDataType.有符号16位 => (short)block[wordIndex],
            TagDataType.无符号32位 => Combine32(block, wordIndex, tag.WordSwap),
            TagDataType.有符号32位 => (int)Combine32(block, wordIndex, tag.WordSwap),
            TagDataType.浮点32位 => BitConverter.Int32BitsToSingle((int)Combine32(block, wordIndex, tag.WordSwap)),
            _ => 0,
        };
        return raw * tag.Scale + tag.Offset;
    }

    /// <summary>从位块解析。</summary>
    public static double DecodeBit(bool bit) => bit ? 1 : 0;

    /// <summary>把工程值编码回寄存器字。</summary>
    public static ushort[] Encode(TagDefinition tag, double engineeringValue)
    {
        double raw = tag.Scale == 0 ? engineeringValue : (engineeringValue - tag.Offset) / tag.Scale;
        switch (tag.DataType)
        {
            case TagDataType.位:
                return [(ushort)(raw != 0 ? 1 : 0)];
            case TagDataType.无符号16位:
                return [(ushort)Math.Clamp((int)Math.Round(raw), 0, ushort.MaxValue)];
            case TagDataType.有符号16位:
                return [(ushort)(short)Math.Clamp((int)Math.Round(raw), short.MinValue, short.MaxValue)];
            case TagDataType.无符号32位:
                return Split32((uint)Math.Clamp((long)Math.Round(raw), 0, uint.MaxValue), tag.WordSwap);
            case TagDataType.有符号32位:
                return Split32((uint)(int)Math.Clamp((long)Math.Round(raw), int.MinValue, int.MaxValue), tag.WordSwap);
            case TagDataType.浮点32位:
                return Split32((uint)BitConverter.SingleToInt32Bits((float)raw), tag.WordSwap);
            default:
                return [0];
        }
    }

    public static string FormatValue(double v)
        => Math.Abs(v) >= 100000 || (Math.Abs(v) < 0.001 && v != 0)
            ? v.ToString("E3")
            : Math.Abs(v) == Math.Round(Math.Abs(v))
                ? v.ToString("0")
                : v.ToString("0.###");

    private static uint Combine32(ushort[] block, int i, bool swap)
    {
        uint hi = block[i], lo = block[i + 1];
        return swap ? (lo << 16) | hi : (hi << 16) | lo;
    }

    private static ushort[] Split32(uint v, bool swap)
    {
        ushort hi = (ushort)(v >> 16), lo = (ushort)(v & 0xFFFF);
        return swap ? [lo, hi] : [hi, lo];
    }
}
