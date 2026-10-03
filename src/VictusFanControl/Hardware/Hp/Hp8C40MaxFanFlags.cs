namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// 8C40/F.18 DSDT: EC byte ECh has FFFF at bit 1 and FFFS at bit 2.
/// Decoding a byte does not authenticate the direct EC transport. Until more
/// states are qualified, production continues to admit only the raw zero byte.
/// </summary>
internal static class Hp8C40MaxFanFlags
{
    internal const byte FffsMask = 0x04;
    internal static bool DecodeMaxFanBit(byte raw) => (raw & FffsMask) != 0;
    internal static byte OtherBits(byte raw) => (byte)(raw & ~FffsMask);
    internal static string Describe(byte raw) =>
        $"raw=0x{raw:X2};decodedMaxFanBit={(DecodeMaxFanBit(raw) ? 1 : 0)};otherBits=0x{OtherBits(raw):X2}";

    internal static string Refusal(byte raw) => DecodeMaxFanBit(raw)
        ? $"Max Fan flag FFFS is set in the observed EC byte (EC 0xEC=0x{raw:X2}; {Describe(raw)})."
        : $"EC 0xEC is outside the qualified raw 0x00 state ({Describe(raw)}); the observed FFFS bit is clear. Direct EC sample integrity is not established.";
}
