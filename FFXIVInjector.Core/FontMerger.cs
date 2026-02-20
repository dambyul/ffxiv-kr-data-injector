using System;
using System.Collections.Generic;
using System.Linq;
using System.Buffers.Binary;

namespace FFXIVInjector.Core;

public class FontMerger
{
    private const uint Scale1K = 0x04000400;
    private const uint Scale4K = 0x10001000;

    /// <summary> Merges overlay FDT into base FDT, redirecting glyphs if needed. </summary>
    public static byte[] Merge(byte[] baseFdt, byte[] overlayFdt, int forceBaseTexId = -1)
    {
        uint baseGlyphCount = BinaryPrimitives.ReadUInt32LittleEndian(baseFdt.AsSpan(0x24, 4));
        uint overlayGlyphCount = BinaryPrimitives.ReadUInt32LittleEndian(overlayFdt.AsSpan(0x24, 4));
        
        uint baseScale = BinaryPrimitives.ReadUInt32LittleEndian(baseFdt.AsSpan(0x30, 4));
        uint overlayScale = BinaryPrimitives.ReadUInt32LittleEndian(overlayFdt.AsSpan(0x30, 4));

        bool promoteBase = (baseScale == Scale1K && overlayScale == Scale4K);

        var baseMap = ParseRawGlyphs(baseFdt, baseGlyphCount);
        var overMap = ParseRawGlyphs(overlayFdt, overlayGlyphCount);

        var mergedMap = new Dictionary<uint, byte[]>();

        // 1. Initial State (Base/Native)
        foreach (var kv in baseMap)
        {
            byte[] data = (byte[])kv.Value.Clone();
            if (promoteBase)
            {
                ushort adv = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
                ushort u = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2));
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10, 2));
                
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), (ushort)(adv * 4));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), (ushort)(u * 4));
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10, 2), (ushort)(v * 4));
                
                data[12] = (byte)Math.Min(255, data[12] * 4);
                data[13] = (byte)Math.Min(255, data[13] * 4);
                data[15] = (byte)Math.Clamp((sbyte)data[15] * 4, -128, 127);
            }
            mergedMap[kv.Key] = data;
        }

        // Calculate Min Slot in Overlay to normalize redirection
        ushort minOverlaySlot = 0;
        if (overMap.Count > 0)
            minOverlaySlot = overMap.Values.Min(d => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6, 2)));

        // 2. Additive Injection (Overlay)
        int maxTargetSlotUsed = -1;
        // ONLY add glyphs that are missing in Global to avoid breaking existing Latin/Jap characters.
        foreach (var kv in overMap)
        {
            uint key = kv.Key;
            if (!mergedMap.ContainsKey(key))
            {
                byte[] data = (byte[])kv.Value.Clone();
                if (forceBaseTexId != -1)
                {
                    ushort originalSlot = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2));
                    ushort targetSlot = (ushort)(forceBaseTexId + (originalSlot - minOverlaySlot));
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6, 2), targetSlot);
                    maxTargetSlotUsed = Math.Max(maxTargetSlotUsed, targetSlot);
                }
                mergedMap[key] = data;
            }
        }

        // 3. Rebuild
        var sortedKeys = mergedMap.Keys.ToList();
        sortedKeys.Sort();

        byte[] header = new byte[0x40];
        Buffer.BlockCopy(baseFdt, 0, header, 0, 0x40);

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24, 4), (uint)sortedKeys.Count);
        if (promoteBase || overlayScale == Scale4K)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30, 4), Scale4K);

        if (maxTargetSlotUsed != -1)
        {
            uint currentTexCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38, 4), Math.Max(currentTexCount, (uint)maxTargetSlotUsed + 1));
        }

        byte[] glyphTable = new byte[sortedKeys.Count * 16];
        for (int i = 0; i < sortedKeys.Count; i++)
        {
            Buffer.BlockCopy(mergedMap[sortedKeys[i]], 0, glyphTable, i * 16, 16);
        }

        byte[] result = new byte[0x40 + glyphTable.Length];
        Buffer.BlockCopy(header, 0, result, 0, 0x40);
        Buffer.BlockCopy(glyphTable, 0, result, 0x40, glyphTable.Length);

        // Disable KNHD tail/search-tree pointer
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x0C, 4), 0);

        return result;
    }

    private static Dictionary<uint, byte[]> ParseRawGlyphs(byte[] data, uint count)
    {
        var map = new Dictionary<uint, byte[]>();
        for (int i = 0; i < count; i++)
        {
            int off = 0x40 + i * 16;
            if (off + 16 > data.Length) break;

            uint key = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off, 4));
            byte[] entry = new byte[16];
            Buffer.BlockCopy(data, off, entry, 0, 16);
            map[key] = entry;
        }
        return map;
    }
}
