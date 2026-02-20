using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FFXIVInjector.Core;

public class CsvData
{
    public List<int> Offsets { get; set; } = new();
    public List<string> Types { get; set; } = new();
    public Dictionary<int, List<string>> Rows { get; set; } = new();
}

public class CsvReader
{
    public static CsvData Read(string path)
    {
        var result = new CsvData();
        if (!File.Exists(path)) return result;

        using var reader = new StreamReader(path);
        
        // Line 1: key,0,1... (Ignored)
        ReadCsvLine(reader);
        
        // Line 2: #,Text... (Ignored)
        ReadCsvLine(reader);

        // Line 3: offset,0,4... (Offsets)
        var line3 = ReadCsvLine(reader);
        if (line3 != null)
        {
            foreach (var part in line3.Skip(1))
            {
                if (int.TryParse(part, out int offset)) result.Offsets.Add(offset);
                else result.Offsets.Add(-1);
            }
        }
        
        // Line 4: int32,str... (Types)
        var line4 = ReadCsvLine(reader);
        if (line4 != null)
        {
            result.Types = line4.Skip(1).ToList();
        }

        // Read remaining lines (Data)
        while (!reader.EndOfStream)
        {
            var line = ReadCsvLine(reader);
            if (line == null || line.Count == 0) continue;

            if (int.TryParse(line[0], out int id))
            {
                result.Rows[id] = line.Skip(1).ToList();
            }
        }

        return result;
    }

    private static List<string> ReadCsvLine(StreamReader reader)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuote = false;
        
        while (true)
        {
            int nextChar = reader.Read();
            if (nextChar == -1) break;

            char c = (char)nextChar;

            if (inQuote)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        current.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        inQuote = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuote = true;
                }
                else if (c == ',')
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else if (c == '\r')
                {
                    if (reader.Peek() == '\n') reader.Read();
                    break;
                }
                else if (c == '\n')
                {
                    break;
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        if (current.Length > 0 || result.Count > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
