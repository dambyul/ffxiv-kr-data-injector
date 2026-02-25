using System;
using System.Collections.Generic;
using System.Linq;
using System.Buffers.Binary;
using System.Text;

namespace FFXIVInjector.Core;

/// <summary>
/// Handles merging and repairing of FFXIV FDT (Font Definition Table) files.
/// FDT format: fcsv0100 header → fthd glyph table (sorted by CharUtf8) → knhd kerning pairs.
/// Glyphs must remain sorted by CharUtf8; Dalamud uses binary search to locate characters.
/// </summary>
public class FontMerger
{
    // Verified offsets (based on Dalamud FdtReader.cs analysis):
    private const int OffsetKerningTableHeaderOffset = 0x0C;  // in FdtHeader
    private const int OffsetFontTableEntryCount      = 0x24;  // = FontTableHeaderOffset(0x20) + 0x04
    private const int GlyphTableStart               = 0x40;  // = FontTableHeaderOffset(0x20) + sizeof(FontTableHeader)(0x20)
    private const int GlyphEntrySize                = 16;    // sizeof(FontTableEntry)
    private const int TextureIndexOffsetInEntry      = 0x06;  // offset of TextureIndex within FontTableEntry

    /// <summary>
    /// Repairs an FDT file by verifying its KNHD pointer is consistent with the glyph count.
    /// The KNHD (kerning) block is preserved exactly as-is from original.
    /// </summary>
    public static byte[] Repair(byte[] fdtData)
    {
        if (fdtData.Length < GlyphTableStart) return fdtData;

        // Verify magic
        string magic = Encoding.ASCII.GetString(fdtData, 0, 8);
        if (!magic.StartsWith("fcsv")) return fdtData;

        // Read header fields
        uint knhdPtr    = BinaryPrimitives.ReadUInt32LittleEndian(fdtData.AsSpan(OffsetKerningTableHeaderOffset, 4));
        uint glyphCount = BinaryPrimitives.ReadUInt32LittleEndian(fdtData.AsSpan(OffsetFontTableEntryCount, 4));

        // KNHD should start immediately after all glyph entries
        uint expectedKnhdPtr = (uint)(GlyphTableStart + glyphCount * GlyphEntrySize);

        // Only repair if KNHD pointer is wrong
        if (knhdPtr == expectedKnhdPtr)
        {
            return fdtData;
        }

        if (knhdPtr == 0 || knhdPtr > fdtData.Length)
        {
            // No KNHD to preserve - just fix the pointer
            byte[] fixedData = (byte[])fdtData.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(fixedData.AsSpan(OffsetKerningTableHeaderOffset, 4), expectedKnhdPtr);
            return fixedData;
        }

        // KNHD exists but pointer is stale - preserve KNHD data, update pointer
        int knhdSize = fdtData.Length - (int)knhdPtr;
        if (knhdSize <= 0) return fdtData;

        byte[] result = new byte[(int)expectedKnhdPtr + knhdSize];
        Buffer.BlockCopy(fdtData, 0, result, 0, Math.Min((int)expectedKnhdPtr, fdtData.Length));
        Buffer.BlockCopy(fdtData, (int)knhdPtr, result, (int)expectedKnhdPtr, knhdSize);

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(OffsetKerningTableHeaderOffset, 4), expectedKnhdPtr);
        return result;
    }

    /// <summary>
    /// Merges two FDT files: the base FDT's structure and glyphs combined with the overlay FDT's glyphs.
    /// 
    /// IMPORTANT: The merged glyph table is sorted by CharUtf8 so Dalamud's binary search works correctly.
    /// TextureIndex values from the overlay are remapped by adding overlayBaseSlot offset.
    /// The base FDT's KNHD (kerning table) is always preserved.
    /// </summary>
    public static byte[] Merge(byte[] baseFdt, byte[] overlayFdt, int overlayBaseSlot = -1)
    {
        if (baseFdt.Length < GlyphTableStart || overlayFdt.Length < GlyphTableStart) return baseFdt;

        uint baseGlyphCount    = BinaryPrimitives.ReadUInt32LittleEndian(baseFdt.AsSpan(OffsetFontTableEntryCount, 4));
        uint overlayGlyphCount = BinaryPrimitives.ReadUInt32LittleEndian(overlayFdt.AsSpan(OffsetFontTableEntryCount, 4));
        uint baseKnhdPtr       = BinaryPrimitives.ReadUInt32LittleEndian(baseFdt.AsSpan(OffsetKerningTableHeaderOffset, 4));

        int baseGlyphBytes    = (int)(baseGlyphCount * GlyphEntrySize);
        int overlayGlyphBytes = (int)(overlayGlyphCount * GlyphEntrySize);

        // Collect all glyph entries as raw 16-byte chunks
        var allEntries = new List<(int charUtf8, byte[] data)>();

        // Add base glyphs
        for (int i = 0; i < (int)baseGlyphCount; i++)
        {
            int src = GlyphTableStart + i * GlyphEntrySize;
            if (src + GlyphEntrySize > baseFdt.Length) break;
            byte[] entry = new byte[GlyphEntrySize];
            Buffer.BlockCopy(baseFdt, src, entry, 0, GlyphEntrySize);
            int charUtf8 = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(0, 4));
            allEntries.Add((charUtf8, entry));
        }

        // Add overlay glyphs (remapping TextureIndex if needed)
        int minOverlayTextureIndex = int.MaxValue;
        var overlayEntries = new List<byte[]>();
        for (int i = 0; i < (int)overlayGlyphCount; i++)
        {
            int src = GlyphTableStart + i * GlyphEntrySize;
            if (src + GlyphEntrySize > overlayFdt.Length) break;
            byte[] entry = new byte[GlyphEntrySize];
            Buffer.BlockCopy(overlayFdt, src, entry, 0, GlyphEntrySize);
            int texIdx = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(TextureIndexOffsetInEntry, 2));
            if (texIdx < minOverlayTextureIndex) minOverlayTextureIndex = texIdx;
            overlayEntries.Add(entry);
        }

        foreach (var entry in overlayEntries)
        {
            if (overlayBaseSlot >= 0 && minOverlayTextureIndex != int.MaxValue)
            {
                int texIdx = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(TextureIndexOffsetInEntry, 2));
                int newTexIdx = overlayBaseSlot + (texIdx - minOverlayTextureIndex);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(TextureIndexOffsetInEntry, 2), (ushort)newTexIdx);
            }
            int charUtf8 = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(0, 4));
            // Override base glyph if same character
            int existing = allEntries.FindIndex(e => e.charUtf8 == charUtf8);
            if (existing >= 0)
                allEntries[existing] = (charUtf8, entry);
            else
                allEntries.Add((charUtf8, entry));
        }

        // Sort by CharUtf8 for Dalamud's BinarySearch compatibility
        allEntries.Sort((a, b) => a.charUtf8.CompareTo(b.charUtf8));

        uint totalGlyphs = (uint)allEntries.Count;
        uint newKnhdPtr  = (uint)(GlyphTableStart + totalGlyphs * GlyphEntrySize);

        // Preserve KNHD block from original base FDT
        int knhdSize = 0;
        if (baseKnhdPtr > 0 && baseKnhdPtr < baseFdt.Length)
            knhdSize = baseFdt.Length - (int)baseKnhdPtr;

        // Build output FDT
        byte[] result = new byte[(int)newKnhdPtr + knhdSize];

        // Copy header from base (first GlyphTableStart = 0x40 bytes)
        Buffer.BlockCopy(baseFdt, 0, result, 0, Math.Min(GlyphTableStart, baseFdt.Length));

        // Update header: glyph count and KNHD pointer
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(OffsetFontTableEntryCount, 4), totalGlyphs);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(OffsetKerningTableHeaderOffset, 4), newKnhdPtr);

        // Write sorted glyph table
        for (int i = 0; i < allEntries.Count; i++)
        {
            int dst = GlyphTableStart + i * GlyphEntrySize;
            Buffer.BlockCopy(allEntries[i].data, 0, result, dst, GlyphEntrySize);
        }

        // Preserve original KNHD block
        if (knhdSize > 0)
            Buffer.BlockCopy(baseFdt, (int)baseKnhdPtr, result, (int)newKnhdPtr, knhdSize);

        return result;
    }
}
