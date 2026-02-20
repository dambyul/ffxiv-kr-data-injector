using System;
using System.IO;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace FFXIVInjector.Core;

// Low-level SqPack DAT writer and Index updater.
// Ensures strict 128-byte block alignment and safe index expansion.
public class SqPackWriter : IDisposable
{
    private readonly string _gamePath;
    private readonly Dictionary<string, FileStream> _openStreams = new();
    public Action<string>? OnLog;

    public SqPackWriter(string gamePath, Action<string>? onLog = null)
    {
        _gamePath = gamePath;
        OnLog = onLog;
    }

    private FileStream GetStream(string folderName, string datName)
    {
        string key = $"{folderName}/{datName}";
        if (_openStreams.TryGetValue(key, out var stream)) return stream;
        var path = Path.Combine(_gamePath, $"sqpack/{folderName}/{datName}");
        if (!File.Exists(path)) throw new FileNotFoundException(key);
        var newStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        _openStreams[key] = newStream;
        return newStream;
    }

    public long AppendRawBlock(string folderName, string datName, byte[] blockData)
    {
        var stream = GetStream(folderName, datName);
        // Align append position to 128 bytes
        stream.Position = (stream.Length + 127) & ~127;
        long entryOffset = stream.Position;
        
        stream.Write(blockData);
        
        // Pad the written block to 128 bytes to maintain future alignment
        long paddedSize = (blockData.Length + 127) & ~127;
        int paddingLen = (int)(paddedSize - blockData.Length);
        if (paddingLen > 0) stream.Write(new byte[paddingLen]);
        
        stream.Flush(true);
        // OnLog?.Invoke($"  [SqPackWriter] Appended block ({blockData.Length} bytes) @ 0x{entryOffset:X}");
        return entryOffset;
    }
    
    private static uint CalculateAdler32(byte[] data)
    {
        const uint MOD_ADLER = 65521;
        uint a = 1, b = 0;
        foreach (byte c in data) { a = (a + c) % MOD_ADLER; b = (b + a) % MOD_ADLER; }
        return (b << 16) | a;
    }

    /* 
     * TECHNICAL NOTE: SqPack Index Segment Offsets
     * FFXIV uses a non-contiguous segment description header.
     * Segment 0 (File Hashes) metadata starts at 0x404 (fixed length 76 with padding).
     * Segments 1-10 metadata start at 0x450, spaced exactly 72 bytes apart.
     * Each segment info block: [4:Offset] [4:Size] [4:Adler32] [20:SHA1] [40:Padding]
     */
    private int GetSegOffsetPtr(int i) => i == 0 ? 0x408 : (0x450 + (i - 1) * 72 + 4);
    private int GetSegSizePtr(int i) => GetSegOffsetPtr(i) + 4;
    private int GetSegAdlerPtr(int i) => GetSegOffsetPtr(i) + 8;

    // Insert or update hash entry. Handles segment shifting to prevent index corruption.
    private byte[] ExpandAndInsert(byte[] indexData, uint pathHash, uint fileHash, uint packedValue, bool isIndex2)
    {
        int entrySize = isIndex2 ? 8 : 16;
        ulong fullHash = (ulong)pathHash << 32 | fileHash;
        uint targetHash32 = fileHash;

        int fileSegOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(GetSegOffsetPtr(0), 4));
        int fileSegSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(GetSegSizePtr(0), 4));
        
        // Find binary search insertion point
        int insertIdx = fileSegSize / entrySize;
        if (isIndex2) {
            for (int i = 0; i < fileSegSize; i += 8) {
                if (BinaryPrimitives.ReadUInt32LittleEndian(indexData.AsSpan(fileSegOffset + i, 4)) > targetHash32) { insertIdx = i / 8; break; }
            }
        } else {
            for (int i = 0; i < fileSegSize; i += 16) {
                if (BinaryPrimitives.ReadUInt64LittleEndian(indexData.AsSpan(fileSegOffset + i, 8)) > fullHash) { insertIdx = i / 16; break; }
            }
        }

        // Buffer for expanded index
        byte[] nextIndex = new byte[indexData.Length + entrySize];
        int targetPos = fileSegOffset + (insertIdx * entrySize);
        
        // Copy data before insertion point
        Buffer.BlockCopy(indexData, 0, nextIndex, 0, targetPos);
        
        // Write new entry
        if (isIndex2) {
            BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(targetPos, 4), targetHash32);
            BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(targetPos + 4, 4), packedValue);
        } else {
            BinaryPrimitives.WriteUInt64LittleEndian(nextIndex.AsSpan(targetPos, 8), fullHash);
            BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(targetPos + 8, 4), packedValue);
            BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(targetPos + 12, 4), 0);
        }

        // Copy remaining entries in current segment
        int remainingInSeg = fileSegSize - (insertIdx * entrySize);
        if (remainingInSeg > 0) Buffer.BlockCopy(indexData, targetPos, nextIndex, targetPos + entrySize, remainingInSeg);
        
        // Copy all trailing segments/data
        int trailingOffset = fileSegOffset + fileSegSize;
        int trailingSize = indexData.Length - trailingOffset;
        if (trailingSize > 0) Buffer.BlockCopy(indexData, trailingOffset, nextIndex, trailingOffset + entrySize, trailingSize);

        // Update Segment 0 metadata (Size and Adler32)
        int newFileSegSize = fileSegSize + entrySize;
        BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(GetSegSizePtr(0), 4), (uint)newFileSegSize);
        byte[] newSeg0Data = new byte[newFileSegSize];
        Buffer.BlockCopy(nextIndex, fileSegOffset, newSeg0Data, 0, newFileSegSize);
        BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(GetSegAdlerPtr(0), 4), CalculateAdler32(newSeg0Data));

        // Shift offsets of all subsequent segments that were displaced by insertion
        for (int i = 1; i < 11; i++) {
            int ptr = GetSegOffsetPtr(i);
            if (ptr + 4 > nextIndex.Length) break;
            uint sOff = BinaryPrimitives.ReadUInt32LittleEndian(nextIndex.AsSpan(ptr, 4));
            if (sOff >= (uint)fileSegOffset + (uint)fileSegSize) {
                BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(ptr, 4), sOff + (uint)entrySize);
            }
        }

        // Segment 3 (Folders) Maintenance (Index1 only)
        // If a new file was added, we may need to increase the file count/size in the folder segment
        if (!isIndex2) {
            int folderSegPtr = GetSegOffsetPtr(3);
            if (folderSegPtr + 8 <= nextIndex.Length) {
                int folderSegOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(nextIndex.AsSpan(folderSegPtr, 4));
                int folderSegSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(nextIndex.AsSpan(folderSegPtr + 4, 4));
                if (folderSegOffset > 0 && folderSegSize > 0) {
                    for (int i = 0; i < folderSegSize; i += 16) {
                        int ent = folderSegOffset + i;
                        uint fHash = BinaryPrimitives.ReadUInt32LittleEndian(nextIndex.AsSpan(ent, 4));
                        uint fOffset = BinaryPrimitives.ReadUInt32LittleEndian(nextIndex.AsSpan(ent + 4, 4));
                        uint fSize = BinaryPrimitives.ReadUInt32LittleEndian(nextIndex.AsSpan(ent + 8, 4));
                        // Shift folder's internal file pointer if it was after our insertion
                        if (fOffset >= (uint)targetPos) BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(ent + 4, 4), fOffset + (uint)entrySize);
                        // If this is the parent folder of the inserted file, increment its entry size record
                        if (fHash == pathHash) BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(ent + 8, 4), fSize + 16);
                    }
                    // Re-calculate Adler32 for Folder Segment
                    byte[] newSeg3Data = new byte[folderSegSize];
                    Buffer.BlockCopy(nextIndex, folderSegOffset, newSeg3Data, 0, folderSegSize);
                    BinaryPrimitives.WriteUInt32LittleEndian(nextIndex.AsSpan(GetSegAdlerPtr(3), 4), CalculateAdler32(newSeg3Data));
                }
            }
        }
        return nextIndex;
    }

    // Update index with new offset; expand if missing.
    public void UpdateIndexWithExpansion(Dictionary<string, byte[]> cache, string folderName, string domain, uint pathHash, uint fileHash, long offset, bool isIndex2 = false)
    {
        string cacheKey = $"{folderName}/{domain}";
        if (!cache.TryGetValue(cacheKey, out var data)) {
            var p = Path.Combine(_gamePath, $"sqpack/{folderName}/{domain}.win32.index{(isIndex2 ? "2" : "")}");
            if (File.Exists(p)) data = cache[cacheKey] = File.ReadAllBytes(p); else return;
        }

        bool updated = false;
        ulong fullHash = (ulong)pathHash << 32 | fileHash;
        uint targetHash32 = fileHash;
        // SqPack Index1/Index2 Value Format:
        // Bits 0-2: Dat File Index (0 for .dat0)
        // Bits 3-31: Offset >> 3
        // Match SqPackReader.cs: (packed & ~0x7) << 3
        uint packedValue = (uint)(offset >> 3); 

        int fileSegOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(GetSegOffsetPtr(0), 4));
        int fileSegSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(GetSegSizePtr(0), 4));

        if (isIndex2) {
            for (int i = 0; i < fileSegSize; i += 8) {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fileSegOffset + i, 4)) == targetHash32) {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(fileSegOffset + i + 4, 4), packedValue);
                    updated = true; break;
                }
            }
        } else {
            for (int i = 0; i < fileSegSize; i += 16) {
                if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(fileSegOffset + i, 8)) == fullHash) {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(fileSegOffset + i + 8, 4), packedValue);
                    updated = true; break;
                }
            }
        }

        if (updated) {
            byte[] seg0Data = new byte[fileSegSize];
            Buffer.BlockCopy(data, fileSegOffset, seg0Data, 0, fileSegSize);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(GetSegAdlerPtr(0), 4), CalculateAdler32(seg0Data));
        } else {
            // New asset insertion - expands the file
            cache[cacheKey] = ExpandAndInsert(data, pathHash, fileHash, packedValue, isIndex2);
        }
    }

    public void WriteIndex(string indexPath, byte[] data)
    {
        using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.SetLength(data.Length);
        stream.Write(data);
    }

    public void Dispose()
    {
        foreach (var stream in _openStreams.Values) stream.Dispose();
        _openStreams.Clear();
    }
}
