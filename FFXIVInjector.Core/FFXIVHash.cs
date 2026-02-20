using System.Text;

namespace FFXIVInjector.Core;

public static class FFXIVHash
{
    private static readonly uint[] CrcTable = new uint[256];

    static FFXIVHash()
    {
        const uint polynomial = 0xEDB88320;
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 8; j > 0; j--)
            {
                if ((crc & 1) == 1)
                    crc = (crc >> 1) ^ polynomial;
                else
                    crc >>= 1;
            }
            CrcTable[i] = crc;
        }
    }

    /// <summary>
    /// Calculates the dual-table CRC32 used for FFXIV SqPack index paths and filenames.
    /// This matches the logic from FFXIVChnTextPatch (Java).
    /// </summary>
    public static uint Calc(string str)
    {
        if (string.IsNullOrEmpty(str)) return 0;
        return FFXIVCRC.Compute(Encoding.UTF8.GetBytes(str.ToLowerInvariant()));
    }

    /// <summary>
    /// Standard CRC32 implementation for ad-hoc tasks.
    /// </summary>
    public static uint CalcRaw(ReadOnlySpan<byte> bytes, uint initial = 0, bool finalXor = false)
    {
        uint crc = initial;
        foreach (byte b in bytes)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        }
        return finalXor ? ~crc : crc;
    }
}
