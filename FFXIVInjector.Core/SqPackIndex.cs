using System.Runtime.InteropServices;

namespace FFXIVInjector.Core;

public class SqPackIndex
{
    private readonly byte[] _data;

    public SqPackIndex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _data = new byte[stream.Length];
        stream.ReadExactly(_data, 0, (int)stream.Length);
    }

    public long? GetFileOffset(uint pathHash, uint fileHash)
    {
        // Combine as seen in dump: fileHash first, then pathHash
        // 60-31-FA-A8 (file) | 99-79-9B-E3 (path)
        ulong fullHash = (ulong)pathHash << 32 | fileHash;
        
        // Segment Header at 0x400
        // uint size = BitConverter.ToUInt32(_data, 0x400); 
        int segmentOffset = (int)BitConverter.ToUInt32(_data, 0x408);
        int segmentSize = (int)BitConverter.ToUInt32(_data, 0x40C);
        
        for (int i = 0; i < segmentSize; i += 16)
        {
            int current = segmentOffset + i;
            if (current + 16 > _data.Length) break;

            ulong hash = BitConverter.ToUInt64(_data, current);
            if (hash == fullHash)
            {
                uint data = BitConverter.ToUInt32(_data, current + 8);
                
                // packedData: Bits 0-2 = Dat Index, Bits 3-31 = Offset >> 3
                long realOffset = (long)(data & ~0x7) << 3;
                return realOffset;
            }
        }
        
        return null;
    }
}
