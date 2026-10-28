using System.Text;
using System.Text.RegularExpressions;

namespace WeighStation.Core;

/// <summary>
/// Plates arrive in many spellings: cameras return Latin letters for Cyrillic plates, operators add spaces
/// and use lower case. Everything is stored and compared in one canonical form; spaces are for display only.
/// </summary>
public static partial class PlateNumber
{
    // The 12 Cyrillic letters allowed on Russian plates that look exactly like Latin ones.
    private static readonly Dictionary<char, char> LatinToCyrillic = new()
    {
        ['A'] = 'А', ['B'] = 'В', ['E'] = 'Е', ['K'] = 'К', ['M'] = 'М', ['H'] = 'Н',
        ['O'] = 'О', ['P'] = 'Р', ['C'] = 'С', ['T'] = 'Т', ['Y'] = 'У', ['X'] = 'Х',
    };

    public static string Normalize(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
        {
            return "";
        }

        var sb = new StringBuilder(plate.Length);
        foreach (var ch in plate)
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '_')
            {
                continue;
            }

            var upper = char.ToUpperInvariant(ch);
            sb.Append(LatinToCyrillic.GetValueOrDefault(upper, upper));
        }

        return sb.ToString();
    }

    /// <summary>"А123ВС197" → "А123ВС 197", trailer "АВ456777" → "АВ4567 77". Unknown formats are returned as they are.</summary>
    public static string Format(string? plate)
    {
        var normalized = Normalize(plate);
        var m = RussianCar().Match(normalized);
        if (!m.Success)
        {
            m = RussianTrailer().Match(normalized);
        }

        return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}" : normalized;
    }

    [GeneratedRegex(@"^([А-Я]\d{3}[А-Я]{2})(\d{2,3})$")]
    private static partial Regex RussianCar();

    [GeneratedRegex(@"^([А-Я]{2}\d{4})(\d{2,3})$")]
    private static partial Regex RussianTrailer();
}
