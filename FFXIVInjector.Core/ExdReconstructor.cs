using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FFXIVInjector.Core;

public class ExdReconstructor
{
    private const int RowHeaderSize = 6;

    public class SheetSchema
    {
        public List<int> Offsets { get; set; } = new();
        public List<string> Types { get; set; } = new();
        public int FixedDataSize { get; set; } = 0;
        public int PageCount { get; set; } = 1;
        // Page Table: Each entry is (StartId, RunCount)
        public List<(uint StartId, uint RowCount)> PageTable { get; set; } = new();
    }

    public static byte[] Merge(byte[] originalData, CsvData patchData, SheetSchema fullSchema, HashSet<int>? offsetExclusions = null)
    {
        if (originalData == null || originalData.Length < 32 || Encoding.ASCII.GetString(originalData, 0, 4) != "EXDF")
        {
            return originalData;
        }

        uint originalIndexSize = BinaryPrimitives.ReadUInt32BigEndian(originalData.AsSpan(0x08, 4));
        // originalDataSize at 0x0C is NOT always trustworthy if we're rebuilding, but we read it
        
        // Extract and map original rows
        var rowEntries = new Dictionary<uint, byte[]>();
        for (int i = 0; i < (int)originalIndexSize; i += 8)
        {
            int entryPos = 32 + i;
            if (entryPos + 8 > originalData.Length) break;

            uint id = BinaryPrimitives.ReadUInt32BigEndian(originalData.AsSpan(entryPos, 4));
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(originalData.AsSpan(entryPos + 4, 4));
            
            if (offset >= 32 + originalIndexSize && offset + 6 <= originalData.Length)
            {
                uint dataSizeInRow = BinaryPrimitives.ReadUInt32BigEndian(originalData.AsSpan((int)offset, 4));
                uint totalLength = dataSizeInRow + 6;

                if (offset + totalLength > (uint)originalData.Length) {
                    totalLength = (uint)originalData.Length - offset;
                }
                
                byte[] rowData = new byte[totalLength];
                Array.Copy(originalData, (int)offset, rowData, 0, (int)totalLength);
                rowEntries[id] = rowData;
            }
        }

        // Perform updates
        var processedRows = new Dictionary<uint, byte[]>();
        foreach (var entry in rowEntries)
        {
            uint id = entry.Key;
            byte[] rowData = entry.Value;

            if (patchData.Rows.TryGetValue((int)id, out var patchRow))
            {
                // Surgically update this row
                processedRows[id] = ApplyPatchToRow(rowData, patchRow, patchData.Offsets, patchData.Types, fullSchema, offsetExclusions);
            }
            else
            {
                processedRows[id] = rowData;
            }
        }

        // Build final EXDF segments
        var sortedIds = processedRows.Keys.OrderBy(id => id).ToList();
        int indexTableSize = sortedIds.Count * 8;
        int dataAreaStart = 32 + indexTableSize;

        using var msIndex = new MemoryStream();
        using var msData = new MemoryStream();
        uint currentDataOffset = (uint)dataAreaStart;

        foreach (var id in sortedIds)
        {
            byte[] rowData = processedRows[id];
            
            // Write Index Entry: [ID (4)] [Offset (4)]
            byte[] idBuf = new byte[4];
            byte[] offBuf = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(idBuf, id);
            BinaryPrimitives.WriteUInt32BigEndian(offBuf, currentDataOffset);
            msIndex.Write(idBuf);
            msIndex.Write(offBuf);

            // Write Data Row: [Header(6)] [Data...]
            msData.Write(rowData);
            currentDataOffset += (uint)rowData.Length;
        }

        // Final Assembly
        byte[] finalExdf = new byte[32 + msIndex.Length + msData.Length];
        Array.Copy(new byte[] { 0x45, 0x58, 0x44, 0x46, 0x00, 0x02, 0x00, 0x00 }, 0, finalExdf, 0, 8);
        BinaryPrimitives.WriteUInt32BigEndian(finalExdf.AsSpan(0x08, 4), (uint)msIndex.Length);
        BinaryPrimitives.WriteUInt32BigEndian(finalExdf.AsSpan(0x0C, 4), (uint)msData.Length);
        
        Buffer.BlockCopy(msIndex.ToArray(), 0, finalExdf, 32, (int)msIndex.Length);
        Buffer.BlockCopy(msData.ToArray(), 0, finalExdf, 32 + (int)msIndex.Length, (int)msData.Length);

        return finalExdf;
    }

    private static byte[] ApplyPatchToRow(byte[] rowData, List<string> patchRow, List<int> csvOffsets, List<string> csvTypes, SheetSchema fullSchema, HashSet<int>? offsetExclusions)
    {
        // Use EXACT FixedDataSize from EXH schema
        int stringBaseOffset = fullSchema.FixedDataSize;
        if (stringBaseOffset == 0) {
            // Fallback (fragile) - legacy behavior
            stringBaseOffset = rowData.Length - RowHeaderSize;
            for (int i = 0; i < fullSchema.Offsets.Count; i++)
            {
                if (fullSchema.Types[i].Equals("str", StringComparison.OrdinalIgnoreCase))
                {
                    int fieldPos = RowHeaderSize + fullSchema.Offsets[i];
                    if (fieldPos + 4 <= rowData.Length)
                    {
                        uint offset = BinaryPrimitives.ReadUInt32BigEndian(rowData.AsSpan(fieldPos, 4));
                        if (offset < stringBaseOffset && offset > 0) stringBaseOffset = (int)offset;
                    }
                }
            }
        }

        // Copy fixed part from original row
        byte[] fixedPart = new byte[RowHeaderSize + fullSchema.FixedDataSize];
        int copySize = Math.Min(rowData.Length, fixedPart.Length);
        Array.Copy(rowData, 0, fixedPart, 0, copySize);

        // Prepare CSV patched values
        var csvPatched = new Dictionary<int, string>();
        
        // Prepare CSV patched values (Strict Offset Mode)
        for (int i = 0; i < patchRow.Count; i++)
        {
            if (i < csvOffsets.Count)
            {
                csvPatched[csvOffsets[i]] = patchRow[i];
            }
        }

        // Update numeric fields in fixedPart
        for (int i = 0; i < csvOffsets.Count; i++)
        {
            int targetOffset = csvOffsets[i];
            
            int schemaIdx = fullSchema.Offsets.IndexOf(targetOffset);
            string realType = "unknown";
            if (schemaIdx != -1 && schemaIdx < fullSchema.Types.Count) realType = fullSchema.Types[schemaIdx];
            else if (i < csvTypes.Count) realType = csvTypes[i];
            
            // Skip strings (handled in Step 5) and unknown types
            if (realType == "str" || realType == "unknown") continue;

            int fieldPos = RowHeaderSize + targetOffset;
            
            // Apply Offset Exclusion check
            if (offsetExclusions != null && offsetExclusions.Contains(targetOffset)) continue;

            // Add bounds checks for string offset reads.
            if (fieldPos + 4 <= fixedPart.Length && i < patchRow.Count) {
                if (decimal.TryParse(patchRow[i], out decimal dVal))
                {
                    if (fieldPos < fixedPart.Length)
                    {
                        WriteValueByType(fixedPart.AsSpan(fieldPos), realType, (double)dVal);
                    }
                }
            }
        }

        // Rebuild String Table
        using var rowStream = new MemoryStream();
        rowStream.Write(fixedPart);
        long poolStartPos = rowStream.Length;

        // We MUST process ALL string columns from the FULL schema to preserve unpatched ones
        for (int i = 0; i < fullSchema.Offsets.Count; i++)
        {
            if (fullSchema.Types[i].Equals("str", StringComparison.OrdinalIgnoreCase))
            {
                int offset = fullSchema.Offsets[i];
                int fieldPos = RowHeaderSize + offset;
                if (fieldPos + 4 > fixedPart.Length) continue;

                byte[] stringBytes = Array.Empty<byte>();
                bool hasPatch = csvPatched.TryGetValue(offset, out var patchValue);

                if (hasPatch)
                {
                    // Patched from CSV (Check Offset Exclusion)
                    if (offsetExclusions != null && offsetExclusions.Contains(offset))
                    {
                        hasPatch = false; // Treat as unpatched to fallback to original
                    }
                    else
                    {
                        stringBytes = ParseGameString(patchValue);
                    }
                }
                
                if (!hasPatch && fieldPos + 4 <= rowData.Length)
                {
                    // Read original string only if NO patch exists
                    uint relOrigOffset = BinaryPrimitives.ReadUInt32BigEndian(rowData.AsSpan(fieldPos, 4));
                    int stringBlockStart = RowHeaderSize + fullSchema.FixedDataSize;
                    int absOrigOffset = stringBlockStart + (int)relOrigOffset;
                    
                    if (absOrigOffset >= 0 && absOrigOffset < rowData.Length && relOrigOffset != 0xFFFFFFFF) 
                    {
                        int sEnd = absOrigOffset;
                        while (sEnd < rowData.Length && rowData[sEnd] != 0) sEnd++;
                        stringBytes = new byte[sEnd - absOrigOffset];
                        Array.Copy(rowData, absOrigOffset, stringBytes, 0, stringBytes.Length);
                    }
                }

                // Calculate new relative offset from start of string block
                uint newRelativeOffset = (uint)(rowStream.Length - poolStartPos);

                rowStream.Position = fieldPos;
                byte[] offBuf = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(offBuf, newRelativeOffset);
                rowStream.Write(offBuf);

                rowStream.Position = rowStream.Length;
                rowStream.Write(stringBytes);
                rowStream.WriteByte(0);
            }
        }

        // 5.5 Update Row Header Length (With Padding)
        // EXDFBuilder (Reference) writes data.length where data includes padding.
        // And ReplaceEXDF aligns the BODY to 4 bytes.
        
        long lengthBeforePadding = rowStream.Length;
        long bodyLength = lengthBeforePadding - RowHeaderSize;
        
        // Calculate padding to align BODY to 4 bytes
        int paddingSize = 4 - (int)(bodyLength % 4);
        if (paddingSize == 0) paddingSize = 4;

        // Header Size = Body + Padding
        uint dataSizeWithPadding = (uint)(bodyLength + paddingSize);
        
        rowStream.Position = 0;
        byte[] lenBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, dataSizeWithPadding);
        rowStream.Write(lenBuf, 0, 4);
        rowStream.Position = lengthBeforePadding;

        // 6. Padding
        for (int p = 0; p < paddingSize; p++) rowStream.WriteByte(0);

        return rowStream.ToArray();
    }

    private static byte[] ParseGameString(string value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();

        using var ms = new MemoryStream();
        int i = 0;
        while (i < value.Length)
        {
            if (value[i] == '<' && value.Substring(i).StartsWith("<hex:"))
            {
                int end = value.IndexOf('>', i);
                if (end != -1)
                {
                    string hex = value.Substring(i + 5, end - (i + 5));
                    byte[] bytes = HexToBytes(hex);
                    ms.Write(bytes);
                    i = end + 1;
                    continue;
                }
            }
            
            // Standard UTF8 character
            byte[] utf8 = Encoding.UTF8.GetBytes(new[] { value[i] });
            ms.Write(utf8);
            i++;
        }
        return ms.ToArray();
    }

    private static void WriteValueByType(Span<byte> span, string type, double val)
    {
        try {
            switch (type.ToLowerInvariant())
            {
                case "bool":
                case "sbyte":
                case "byte":
                    if (span.Length >= 1) span[0] = (byte)val;
                    break;
                case "int16":
                case "uint16":
                    if (span.Length >= 2) BinaryPrimitives.WriteInt16BigEndian(span, (short)val);
                    break;
                case "int32":
                case "uint32":
                    if (span.Length >= 4) BinaryPrimitives.WriteInt32BigEndian(span, (int)val);
                    break;
                case "int64":
                case "uint64":
                    if (span.Length >= 8) BinaryPrimitives.WriteInt64BigEndian(span, (long)val);
                    break;
                case "float":
                    if (span.Length >= 4) BinaryPrimitives.WriteSingleBigEndian(span, (float)val);
                    break;
                case "double":
                    if (span.Length >= 8) BinaryPrimitives.WriteDoubleBigEndian(span, val);
                    break;
            }
        } catch {}
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0) hex = "0" + hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }
}
