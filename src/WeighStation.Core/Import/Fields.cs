using System.Text;

namespace WeighStation.Core.Import;

public enum ImportKind { Vehicles, Companies, Cargos, Drivers, Weighings }

public sealed record ImportField(string Key, string Title, bool Required, string Hint, params string[] Synonyms);

public static class ImportSchema
{
    public static string Title(ImportKind kind) => kind switch
    {
        ImportKind.Vehicles => "Транспорт",
        ImportKind.Companies => "Контрагенты",
        ImportKind.Cargos => "Грузы",
        ImportKind.Drivers => "Водители",
        ImportKind.Weighings => "Журнал взвешиваний (история)",
        _ => kind.ToString(),
    };

    public static IReadOnlyList<ImportField> Fields(ImportKind kind) => kind switch
    {
        ImportKind.Vehicles =>
        [
            new("plate", "Госномер", true, "А123ВС197", "госномер", "номер", "номер тс", "номер тягача", "гос номер", "регистрационный номер", "plate", "regnumber"),
            new("trailer", "Прицеп", false, "АВ456777", "прицеп", "номер прицепа", "госномер прицепа", "trailer"),
            new("driver", "Водитель", false, "Петров С.И.", "водитель", "фио водителя", "driver"),
            new("company", "Контрагент", false, "ООО «Север»", "контрагент", "организация", "компания", "владелец", "перевозчик", "company"),
            new("cargo", "Груз", false, "Щебень", "груз", "материал", "продукция", "cargo"),
            new("tare", "Тара, кг", false, "14320", "тара", "масса тары", "вес тары", "tare"),
            new("max", "Допустимая масса, кг", false, "44000", "допустимая масса", "разрешенная масса", "максимальная масса", "макс масса", "полная масса", "maxmass"),
            new("note", "Заметка", false, "", "заметка", "примечание", "комментарий", "note"),
        ],
        ImportKind.Companies =>
        [
            new("name", "Название", true, "ООО «Север»", "название", "наименование", "контрагент", "организация", "компания", "name"),
            new("inn", "ИНН", false, "7700000000", "инн", "inn"),
            new("note", "Заметка", false, "", "заметка", "примечание", "комментарий", "note"),
        ],
        ImportKind.Cargos =>
        [
            new("name", "Название", true, "Щебень", "название", "наименование", "груз", "материал", "name"),
        ],
        ImportKind.Drivers =>
        [
            new("name", "ФИО", true, "Петров Сергей Иванович", "фио", "водитель", "имя", "фамилия имя отчество", "name"),
            new("phone", "Телефон", false, "+7 900 000-00-00", "телефон", "тел", "мобильный", "phone"),
        ],
        ImportKind.Weighings =>
        [
            new("date", "Дата (и время)", true, "28.10.2026 14:32", "дата", "дата время", "дата и время", "дата взвешивания", "время взвешивания", "date", "datetime"),
            new("time", "Время (если в отдельной колонке)", false, "14:32", "время", "time"),
            new("plate", "Госномер", true, "А123ВС197", "госномер", "номер", "номер тс", "номер тягача", "гос номер", "plate"),
            new("trailer", "Прицеп", false, "АВ456777", "прицеп", "номер прицепа", "trailer"),
            new("driver", "Водитель", false, "Петров С.И.", "водитель", "driver"),
            new("company", "Контрагент", false, "ООО «Север»", "контрагент", "организация", "компания", "поставщик", "покупатель", "company"),
            new("cargo", "Груз", false, "Щебень", "груз", "материал", "продукция", "cargo"),
            new("kind", "Операция (брутто/тара)", false, "Брутто", "операция", "тип взвешивания", "вид взвешивания", "тип", "kind"),
            new("gross", "Брутто, кг", false, "42780", "брутто", "масса брутто", "вес брутто", "gross"),
            new("tare", "Тара, кг", false, "14320", "тара", "масса тары", "вес тары", "tare"),
            new("net", "Нетто, кг", false, "28460", "нетто", "масса нетто", "вес нетто", "net"),
            new("operator", "Оператор", false, "Иванов А.П.", "оператор", "весовщик", "operator"),
        ],
        _ => [],
    };
}

/// <summary>Which source column feeds which field. -1 = not mapped.</summary>
public sealed class ColumnMapping
{
    private readonly Dictionary<string, int> _columns = [];

    public int this[string field]
    {
        get => _columns.GetValueOrDefault(field, -1);
        set => _columns[field] = value;
    }

    public bool IsMapped(string field) => this[field] >= 0;

    /// <summary>Matches columns to fields by header text: exact (after normalising) first, then "header contains synonym".</summary>
    public static ColumnMapping Auto(ImportKind kind, IReadOnlyList<string> headers)
    {
        var mapping = new ColumnMapping();
        var normalised = headers.Select(Normalize).ToList();
        var taken = new HashSet<int>();

        // Two passes so an exact match always beats a fuzzy one ("Тара" must not be taken by "Допустимая масса тары").
        foreach (var exact in new[] { true, false })
        {
            foreach (var field in ImportSchema.Fields(kind).Where(f => !mapping.IsMapped(f.Key)))
            {
                var synonyms = field.Synonyms.Select(Normalize).Append(Normalize(field.Title)).Where(s => s.Length > 0).ToList();
                for (var i = 0; i < normalised.Count; i++)
                {
                    if (taken.Contains(i) || normalised[i].Length == 0)
                    {
                        continue;
                    }

                    var hit = exact
                        ? synonyms.Contains(normalised[i])
                        : synonyms.Any(s => s.Length >= 5 && normalised[i].Contains(s));
                    if (hit)
                    {
                        mapping[field.Key] = i;
                        taken.Add(i);
                        break;
                    }
                }
            }
        }

        return mapping;
    }

    /// <summary>"Гос. номер ТС" → "госномертс": lower case, ё=е, only letters and digits.</summary>
    public static string Normalize(string header)
    {
        var sb = new StringBuilder(header.Length);
        foreach (var ch in header.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch == 'ё' ? 'е' : ch);
            }
        }

        return sb.ToString();
    }
}
