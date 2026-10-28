using System.Text;

namespace WeighStation.Core.Import;

/// <summary>A spreadsheet reduced to text: a header row and data rows. Every cell is a string, whatever it was in Excel.</summary>
public sealed record TableData(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, string SourceName);

public interface ITableReader
{
    /// <summary>Reads .xlsx, .xls or .csv. Throws <see cref="Abstractions.ValidationException"/> with a readable reason when it can't.</summary>
    Task<TableData> ReadAsync(string path, CancellationToken ct = default);
}

/// <summary>RFC 4180 CSV with the quirks of files saved by Russian Excel: semicolons, quoted line breaks, a BOM.</summary>
public static class CsvParser
{
    /// <summary>Guesses the separator from the first non-empty line (ignoring anything inside quotes).</summary>
    public static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0, ['|'] = 0 };
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && (ch is '\n' or '\r') && counts.Values.Sum() > 0)
            {
                break;
            }
            else if (!quoted && counts.ContainsKey(ch))
            {
                counts[ch]++;
            }
        }

        // Semicolon wins ties: Russian Excel uses it because the comma is the decimal separator.
        return counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key == ';' ? 0 : 1).First().Key;
    }

    public static List<List<string>> Parse(string text, char? delimiter = null)
    {
        text = text.TrimStart('﻿');
        var sep = delimiter ?? DetectDelimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var cellStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            if (ch == '"' && !cellStarted)
            {
                quoted = true;
                cellStarted = true;
            }
            else if (ch == sep)
            {
                row.Add(cell.ToString());
                cell.Clear();
                cellStarted = false;
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                cellStarted = false;
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(ch);
                cellStarted = true;
            }
        }

        if (cell.Length > 0 || row.Count > 0 || cellStarted)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Splits parsed rows into headers and data; trims cells and drops completely empty rows.</summary>
    public static TableData ToTable(IReadOnlyList<IReadOnlyList<string>> raw, string sourceName)
    {
        var rows = raw
            .Select(r => (IReadOnlyList<string>)r.Select(c => c.Trim()).ToList())
            .Where(r => r.Any(c => c.Length > 0))
            .ToList();
        if (rows.Count == 0)
        {
            throw new Abstractions.ValidationException("Файл пуст: в нём нет ни заголовков, ни данных.");
        }

        var width = rows.Max(r => r.Count);
        var headers = Enumerable.Range(0, width)
            .Select(i => i < rows[0].Count && rows[0][i].Length > 0 ? rows[0][i] : $"Колонка {i + 1}")
            .ToList();
        var data = rows.Skip(1)
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(i => i < r.Count ? r[i] : "").ToList())
            .ToList();
        return new TableData(headers, data, sourceName);
    }
}
