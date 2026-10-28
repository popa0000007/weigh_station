using System.Globalization;
using System.Text;
using ExcelDataReader;
using WeighStation.Core.Abstractions;
using WeighStation.Core.Import;

namespace WeighStation.Infrastructure.Import;

/// <summary>Reads .xlsx / .xls (ExcelDataReader) and .csv / .txt / .tsv (own parser) into the same <see cref="TableData"/>.</summary>
public sealed class FileTableReader : ITableReader
{
    private const long MaxBytes = 100L * 1024 * 1024;

    static FileTableReader()
    {
        // Old .xls files and Windows-1251 text need code pages that .NET doesn't load by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Task<TableData> ReadAsync(string path, CancellationToken ct = default) =>
        // File parsing is CPU and disk work: keep it off the UI thread.
        Task.Run(() => Read(path), ct);

    public static TableData Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new ValidationException("Файл не найден.");
            }

            if (info.Length > MaxBytes)
            {
                throw new ValidationException($"Файл слишком большой ({info.Length / 1024 / 1024} МБ). Допустимо до {MaxBytes / 1024 / 1024} МБ.");
            }

            var name = Path.GetFileName(path);
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".xlsx" or ".xls" or ".xlsm" => ReadExcel(path, name),
                ".csv" or ".txt" or ".tsv" => ReadCsv(path, name),
                var other => throw new ValidationException($"Формат «{other}» не поддерживается. Выберите файл .xlsx, .xls или .csv."),
            };
        }
        catch (ValidationException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new ValidationException($"Не удалось открыть файл: {ex.Message} Возможно, он открыт в Excel — закройте его и повторите.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ValidationException($"Не удалось прочитать файл: {ex.Message}");
        }
    }

    private static TableData ReadCsv(string path, string name) =>
        CsvParser.ToTable(CsvParser.Parse(DecodeText(File.ReadAllBytes(path))).Select(r => (IReadOnlyList<string>)r).ToList(), name);

    /// <summary>UTF-8 (with or without BOM) and UTF-16 are recognised; anything that isn't valid UTF-8 is read as Windows-1251.</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private static TableData ReadExcel(string path, string name)
    {
        // FileShare.ReadWrite: Excel keeps files open, and a copy of its current state is still readable.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        do
        {
            var rows = new List<IReadOnlyList<string>>();
            while (reader.Read())
            {
                var cells = new string[reader.FieldCount];
                for (var i = 0; i < cells.Length; i++)
                {
                    cells[i] = CellText(reader.GetValue(i));
                }

                rows.Add(cells);
            }

            // The first sheet that has something in it (people often leave an empty "Sheet1" in front).
            if (rows.Any(r => r.Any(c => c.Trim().Length > 0)))
            {
                return CsvParser.ToTable(rows, name);
            }
        }
        while (reader.NextResult());

        throw new ValidationException("В книге нет ни одного листа с данными.");
    }

    private static string CellText(object? value) => value switch
    {
        null => "",
        // A cell formatted as "time" arrives as a date in 1899 (Excel's zero day): keep only the time.
        DateTime dt when dt.Year < 1900 => dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        double d => d.ToString("0.############", CultureInfo.InvariantCulture),
        float f => f.ToString("0.######", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        TimeSpan ts => ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };
}
