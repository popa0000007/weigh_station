using System.Globalization;
using System.Text.RegularExpressions;

namespace WeighStation.Core.Import;

public enum MassUnit { Kilograms, Tonnes }

public static partial class ImportParsing
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly string[] DateTimeFormats =
    [
        "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm", "dd.MM.yyyy", "d.M.yyyy H:mm:ss", "d.M.yyyy H:mm", "d.M.yyyy",
        "dd.MM.yy HH:mm", "dd.MM.yy", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd",
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy", "dd-MM-yyyy HH:mm", "dd-MM-yyyy",
    ];

    private static readonly string[] TimeFormats = ["HH:mm:ss", "HH:mm", "H:mm:ss", "H:mm"];

    /// <summary>
    /// Mass in kilograms from text like "42780", "42 780", "42 780,5", "42.78" (tonnes) or "42.780".
    /// A dot or comma followed by exactly three digits after 1–3 digits is a thousands separator in kilogram mode.
    /// </summary>
    public static bool TryParseMass(string? text, MassUnit unit, out int kilograms)
    {
        kilograms = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim().Replace(" ", "").Replace(" ", "").Replace(" ", "");
        cleaned = Regex.Replace(cleaned, @"(?i)(кг|kg|т|t|тонн?[ы]?)$", "");
        // Digits with single separators between them: rejects "1..2", ".5", "5.", and negatives.
        if (cleaned.Length == 0 || !Regex.IsMatch(cleaned, @"^\d+([.,]\d+)*$"))
        {
            return false;
        }

        double value;
        if (unit == MassUnit.Kilograms && Regex.IsMatch(cleaned, @"^\d{1,3}([.,]\d{3})+$"))
        {
            value = double.Parse(cleaned.Replace(".", "").Replace(",", ""), Invariant); // 42.780 / 1,042,780
        }
        else
        {
            // One decimal separator at most; "1.042,5" (dot thousands, comma decimal) is resolved by the last separator.
            var lastSep = cleaned.LastIndexOfAny(['.', ',']);
            var normalised = lastSep < 0
                ? cleaned
                : cleaned[..lastSep].Replace(".", "").Replace(",", "") + "." + cleaned[(lastSep + 1)..];
            if (!double.TryParse(normalised, NumberStyles.Float, Invariant, out value))
            {
                return false;
            }
        }

        var kg = unit == MassUnit.Tonnes ? value * 1000 : value;
        if (kg is <= 0 or > 1_000_000 || double.IsNaN(kg))
        {
            return false;
        }

        kilograms = (int)Math.Round(kg, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>Date with optional time. Accepts Russian and ISO forms, and Excel serial numbers ("45592.6").</summary>
    public static bool TryParseDateTime(string? text, string? timeText, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        var parsed = false;
        if (DateTime.TryParseExact(text, DateTimeFormats, Invariant, DateTimeStyles.None, out result))
        {
            parsed = true;
        }
        else if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, Invariant, out var serial) && serial is > 20000 and < 80000)
        {
            result = DateTime.FromOADate(serial);
            parsed = true;
        }

        if (!parsed)
        {
            return false;
        }

        // Date in one column, time in another.
        if (!string.IsNullOrWhiteSpace(timeText) && result.TimeOfDay == TimeSpan.Zero)
        {
            var timeTrimmed = timeText.Trim();
            if (DateTime.TryParseExact(timeTrimmed, TimeFormats, Invariant, DateTimeStyles.None, out var time)
                || DateTime.TryParseExact(timeTrimmed, DateTimeFormats, Invariant, DateTimeStyles.None, out time))
            {
                result = result.Date + time.TimeOfDay;
            }
            else if (double.TryParse(timeText.Trim().Replace(',', '.'), NumberStyles.Float, Invariant, out var fraction) && fraction is >= 0 and < 1)
            {
                result = result.Date + TimeSpan.FromDays(fraction); // Excel stores times as fractions of a day
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    public static bool? ParseKindIsTare(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().ToLowerInvariant();
        if (t.Contains("тар") || t.Contains("tare") || t.Contains("порож") || t.Contains("пуст"))
        {
            return true;
        }

        if (t.Contains("брут") || t.Contains("gross") || t.Contains("груж") || t.Contains("загруж") || t.Contains("полн"))
        {
            return false;
        }

        return null;
    }
}
