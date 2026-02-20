using System;
using System.IO;
using System.Collections.Generic;

namespace FFXIVInjector.Core;

public class SqPackReader : IDisposable
{
    private readonly string _gamePath;
    private readonly Dictionary<string, FileStream> _openStreams = new();

    public SqPackReader(string gamePath)
    {
        _gamePath = gamePath;
    }

    private FileStream GetStream(string domain, uint datIndex)
    {
        string datName = $"{domain}.win32.dat{datIndex}";
        if (_openStreams.TryGetValue(datName, out var stream))
        {
            return stream;
        }

        var path = Path.Combine(_gamePath, $"sqpack/ffxiv/{datName}");
        if (!File.Exists(path)) throw new FileNotFoundException(datName);

        var newStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _openStreams[datName] = newStream;
        return newStream;
    }

    public byte[]? ReadFile(string domain, byte[] indexData, uint pathHash, uint fileHash)
    {
        ulong target = (ulong)pathHash << 32 | fileHash;
        
        // Dynamic search for segments
        int[] handles = { 0x400, 0x404, 0x408, 0x450, 0x454, 0x498, 0x49C };
        foreach (var hOff in handles)
        {
            if (hOff + 12 > indexData.Length) continue;
            uint sOff = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(hOff, 4));
            uint sSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(hOff + 4, 4));

            if (sOff >= 0x800 && sOff + sSize <= indexData.Length && sSize > 0 && sSize % 16 == 0)
            {
                for (int i = 0; i < sSize; i += 16)
                {
                    int curr = (int)sOff + i;
                    ulong h = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(indexData.AsSpan(curr, 8));
                    if (h == target)
                    {
                        uint packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(curr + 8, 4));
                        uint datIndex = packed & 0x7;
                        long offset = (long)(packed & ~0x7) << 3;
                        return ReadFromDat(domain, datIndex, offset);
                    }
                }
            }
        }
        return null;
    }

    public byte[]? ReadFile(string domain, byte[] indexData, string path)
    {
        string p = path.Replace('\\', '/');
        int lastSlash = p.LastIndexOf('/');
        string folder = lastSlash == -1 ? "" : p.Substring(0, lastSlash);
        string file = lastSlash == -1 ? p : p.Substring(lastSlash + 1);

        uint ph = FFXIVHash.Calc(folder);
        uint fh = FFXIVHash.Calc(file);
        return ReadFile(domain, indexData, ph, fh);
    }

    private byte[] ReadFromDat(string domain, uint datIndex, long offset)
    {
        var stream = GetStream(domain, datIndex);
        stream.Position = offset;

        using var reader = new BinaryReader(stream, System.Text.Encoding.Default, leaveOpen: true);
        
        uint commonHeaderSize = reader.ReadUInt32();
        uint commonType = reader.ReadUInt32();
        uint uncompressedSize = reader.ReadUInt32();
        
        Console.WriteLine($"[Debug] CommonHeader: Size={commonHeaderSize:X}, Type={commonType:X}, Uncomp={uncompressedSize:X}");

        if (uncompressedSize == 0 || commonHeaderSize == 0) return Array.Empty<byte>();

        if (commonType == 1) // Uncompressed
        {
            stream.Position = offset + commonHeaderSize;
            return reader.ReadBytes((int)uncompressedSize);
        }
        else if (commonType == 2 || commonType == 3) // Binary/Model
        {
            // Data Header starts right after the Common Header
            long dataHeaderStart = offset + commonHeaderSize;
            stream.Position = dataHeaderStart;
            
            uint dataHeaderSize = reader.ReadUInt32();
            // The uncompressed size is in the Common Header, not the Data Header
            uint totalUncompressedSize = uncompressedSize;
            
            // Block count = (dataHeaderSize - 8) / 8, since each block entry is 8 bytes
            // and the first 8 bytes are the header size + padding
            if (dataHeaderSize < 8 || dataHeaderSize > 1024 * 1024) {
                Console.WriteLine($"[Error] Invalid dataHeaderSize: {dataHeaderSize}");
                return Array.Empty<byte>();
            }
            uint numBlocks = (dataHeaderSize - 8) / 8;

            long blockTableStart = dataHeaderStart + 8; // Block table starts after header size field
            var decompressedData = new byte[totalUncompressedSize];
            int currentPos = 0;

            Console.WriteLine($"[Debug] DataHeaderSize:{dataHeaderSize:X} NumBlocks:{numBlocks} TotalUncompSize:{totalUncompressedSize:X}");

            for (int i = 0; i < numBlocks; i++)
            {
                long blockTablePos = blockTableStart + (i * 8);
                if (blockTablePos + 8 > stream.Length) break;
                
                stream.Position = blockTablePos;
                uint offsetAndFlags = reader.ReadUInt32();
                uint sizes = reader.ReadUInt32();
                
                // FFXIV block table format:
                // Bytes 0-3: Offset (lower 24 bits) + Flags (high 8 bits)
                // Bytes 4-5: Compressed size
                // Bytes 6-7: Unused (always 0)
                // Decompressed size is fixed at 16KB (0x4000) per block, except the last block
                uint blockOffset = offsetAndFlags & 0x00FFFFFF;
                ushort compressedSize = (ushort)(sizes & 0xFFFF);
                
                // Calculate decompressed size: 16KB for all blocks except the last
                int decompressedSize = (i == numBlocks - 1) 
                    ? (int)(totalUncompressedSize - currentPos)
                    : 0x4000;

                long blockStart = offset + commonHeaderSize + blockOffset;
                if (blockStart + 16 > stream.Length) break;
                
                stream.Position = blockStart;
                
                // Read 16-byte block header
                uint blockHeaderSize = reader.ReadUInt32();
                uint blockUnused = reader.ReadUInt32();
                uint blockCompressedSize = reader.ReadUInt32();   // Expected at offset 8
                uint blockUncompressedSize = reader.ReadUInt32(); // Expected at offset 12

                if (blockCompressedSize == 32000) // Uncompressed block marker
                {
                    var b = reader.ReadBytes((int)blockUncompressedSize);
                    int copyLen = Math.Min(b.Length, decompressedData.Length - currentPos);
                    if (copyLen > 0) {
                        Array.Copy(b, 0, decompressedData, currentPos, copyLen);
                        currentPos += copyLen;
                    }
                }
                else
                {
                    var compressedBytes = reader.ReadBytes((int)blockCompressedSize);
                    using var ms = new MemoryStream(compressedBytes);
                    using var zlibStr = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                    var b = new byte[blockUncompressedSize];
                    int read = 0;
                    while (read < b.Length) {
                        int r = zlibStr.Read(b, read, b.Length - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    int copyLen = Math.Min(read, decompressedData.Length - currentPos);
                    if (copyLen > 0) {
                        Array.Copy(b, 0, decompressedData, currentPos, copyLen);
                        currentPos += copyLen;
                    }
                }
            }
            return decompressedData;
        }

        return Array.Empty<byte>();
    }

    public void Dispose()
    {
        foreach (var stream in _openStreams.Values)
        {
            stream.Dispose();
        }
        _openStreams.Clear();
    }
}
