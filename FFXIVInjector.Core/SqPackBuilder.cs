using System;
using System.IO;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace FFXIVInjector.Core;

/// <summary>
/// Packages raw data into the SqPack format (Binary or Texture).
/// Implements required block-wrapping and 128-byte alignment.
/// </summary>
public static class SqPackBuilder
{
    private const int MaxBlockSize = 16384; 

    /// <summary> Builds a binary SqPack entry from raw data. </summary>
    public static byte[] BuildBinary(byte[] data)
    {
        int uncompressedSize = data.Length;
        int partCount = (int)Math.Ceiling(uncompressedSize / 16000f);
        if (partCount == 0) partCount = 1;

        int dataHeaderLength = 24 + partCount * 8;
        if (dataHeaderLength < 128) dataHeaderLength = 128;
        else dataHeaderLength = (dataHeaderLength + 127) & ~127;

        byte[] header = new byte[dataHeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), (uint)dataHeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 2); // Type 2: Binary
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)uncompressedSize);
        // Offsets 12 (Total Body / 128) and 16 (Total Body / 128) will be filled after body is built
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), (uint)partCount);

        using var msBodies = new MemoryStream();
        int currentBodyOffset = 0;

        for (int i = 0; i < partCount; i++)
        {
            int start = i * 16000;
            int length = Math.Min(16000, uncompressedSize - start);

            // Create 128-byte aligned body chunks (using uncompressed marker 32000).
            int blockSize = (length + 16 + 127) & ~127;
            byte[] blockBuffer = new byte[blockSize];
            
            BinaryPrimitives.WriteUInt32LittleEndian(blockBuffer.AsSpan(0, 4), 16); // Header Size
            BinaryPrimitives.WriteUInt32LittleEndian(blockBuffer.AsSpan(4, 4), 0);  // Unused
            BinaryPrimitives.WriteUInt32LittleEndian(blockBuffer.AsSpan(8, 4), 32000); // Uncompressed Marker
            BinaryPrimitives.WriteUInt32LittleEndian(blockBuffer.AsSpan(12, 4), (uint)length); // Decompressed Size
            
            Array.Copy(data, start, blockBuffer, 16, length);
            msBodies.Write(blockBuffer);
            
            // Block Table Entry in Header (starting at offset 24)
            int tableEntryPos = 24 + i * 8;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(tableEntryPos, 4), (uint)currentBodyOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(tableEntryPos + 4, 2), (ushort)blockSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(tableEntryPos + 6, 2), (ushort)length);

            currentBodyOffset += blockSize;
        }

        // Fill in the body size relative fields
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)(currentBodyOffset / 128));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), (uint)(currentBodyOffset / 128));

        byte[] result = new byte[header.Length + msBodies.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(msBodies.ToArray(), 0, result, header.Length, (int)msBodies.Length);
        return result;
    }

    /// <summary> Builds a Texture SqPack entry from a .tex file. </summary>
    public static byte[] BuildTexture(byte[] data)
    {
        // Fallback for non-texture data
        if (data.Length < 0x50) return BuildBinary(data);
        
        int texHeaderSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x1C, 4));
        int mipCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x12, 2));
        if (mipCount == 0 || mipCount > 20) mipCount = 1;

        using var msBodies = new MemoryStream();
        byte[] mipTable = new byte[mipCount * 20];
        List<ushort> allBlockSizes = new List<ushort>();

        int currentBodyOffset = 0;
        int currentBlockIndex = 0;

        for (int m = 0; m < mipCount; m++)
        {
            int mipOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x1C + m * 4, 4));
            int nextMipOffset = (m + 1 < mipCount) 
                ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x1C + (m + 1) * 4, 4))
                : data.Length;
            int mipDataSize = nextMipOffset - mipOffset;

            int mipBlockCount = (int)Math.Ceiling(mipDataSize / (float)MaxBlockSize);
            int mipStartOnDisk = currentBodyOffset;
            int mipStartBlock = currentBlockIndex;

            for (int b = 0; b < mipBlockCount; b++)
            {
                int bStart = mipOffset + (b * MaxBlockSize);
                int bLen = Math.Min(MaxBlockSize, nextMipOffset - bStart);
                int bOnDisk = (bLen + 16 + 127) & ~127;

                byte[] block = new byte[bOnDisk];
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0, 4), 16);
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8, 4), 32000); // Forced Uncompressed Signal
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12, 4), (uint)bLen);
                Array.Copy(data, bStart, block, 16, bLen);

                msBodies.Write(block);
                allBlockSizes.Add((ushort)bOnDisk);
                currentBodyOffset += bOnDisk;
            }

            // Mip Entry (20 bytes): [Offset] [Size] [DecompressedSize] [FirstBlockIndex] [BlockCount]
            BinaryPrimitives.WriteUInt32LittleEndian(mipTable.AsSpan(m * 20 + 0, 4), (uint)mipStartOnDisk);
            BinaryPrimitives.WriteUInt32LittleEndian(mipTable.AsSpan(m * 20 + 4, 4), (uint)(currentBodyOffset - mipStartOnDisk));
            BinaryPrimitives.WriteUInt32LittleEndian(mipTable.AsSpan(m * 20 + 8, 4), (uint)mipDataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(mipTable.AsSpan(m * 20 + 12, 4), (uint)mipStartBlock);
            BinaryPrimitives.WriteUInt32LittleEndian(mipTable.AsSpan(m * 20 + 16, 4), (uint)mipBlockCount);
            
            currentBlockIndex += mipBlockCount;
        }

        int headerAreaSize = 24 + (mipCount * 20) + (allBlockSizes.Count * 2);
        int paddedHeaderAreaSize = (headerAreaSize + 127) & ~127;
        
        // Align data blocks to 128-byte boundary relative to dat0.
        int currentPosBeforeBlocks = paddedHeaderAreaSize + texHeaderSize;
        int alignedBlockStart = (currentPosBeforeBlocks + 127) & ~127;
        int paddedTexHeaderSize = alignedBlockStart - paddedHeaderAreaSize;

        // Adjust mipmap offsets to be relative to the end of Entry Header.
        for (int m = 0; m < mipCount; m++)
        {
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(mipTable.AsSpan(m * 20, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(mipTable.AsSpan(m * 20, 4), off + (uint)paddedTexHeaderSize);
        }

        // RawFileSize = paddedTexHeaderSize + total decompressed size (Lumina uses this as decode buffer size)
        uint totalDecompressedSize = 0;
        for (int m = 0; m < mipCount; m++)
            totalDecompressedSize += BinaryPrimitives.ReadUInt32LittleEndian(mipTable.AsSpan(m * 20 + 8, 4));
        uint rawFileSize = (uint)paddedTexHeaderSize + totalDecompressedSize;

        byte[] finalHeader = new byte[paddedHeaderAreaSize];
        BinaryPrimitives.WriteUInt32LittleEndian(finalHeader.AsSpan(0, 4), (uint)paddedHeaderAreaSize);
        BinaryPrimitives.WriteUInt32LittleEndian(finalHeader.AsSpan(4, 4), 4); // Type 4: Texture
        BinaryPrimitives.WriteUInt32LittleEndian(finalHeader.AsSpan(8, 4), rawFileSize); // Raw Size
        
        int totalEntrySize = (alignedBlockStart + (int)msBodies.Length + 127) & ~127;
        BinaryPrimitives.WriteUInt32LittleEndian(finalHeader.AsSpan(12, 4), (uint)(totalEntrySize / 128));
        BinaryPrimitives.WriteUInt32LittleEndian(finalHeader.AsSpan(16, 4), (uint)((paddedTexHeaderSize + (int)msBodies.Length) / 128));
        BinaryPrimitives.WriteUInt32LittleEndian(finalHeader.AsSpan(20, 4), (uint)mipCount);

        // Copy Table Data
        Array.Copy(mipTable, 0, finalHeader, 24, mipTable.Length);
        int bTablePos = 24 + mipCount * 20;
        for (int i = 0; i < allBlockSizes.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(finalHeader.AsSpan(bTablePos + i * 2, 2), allBlockSizes[i]);

        // Assemble Final Data
        byte[] result = new byte[finalHeader.Length + paddedTexHeaderSize + (int)msBodies.Length];
        Array.Copy(finalHeader, 0, result, 0, finalHeader.Length);
        Array.Copy(data, 0, result, finalHeader.Length, texHeaderSize); // Original raw .tex header

        // OffsetToSurface[0] must reflect paddedTexHeaderSize; Lumina seeks to this offset before reading pixels.
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(finalHeader.Length + 28, 4), (uint)paddedTexHeaderSize);

        Array.Copy(msBodies.ToArray(), 0, result, finalHeader.Length + paddedTexHeaderSize, (int)msBodies.Length);

        return result;
    }
}
