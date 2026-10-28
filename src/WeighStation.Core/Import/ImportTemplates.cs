namespace WeighStation.Core.Import;

/// <summary>A ready-to-fill file for each kind of import: the headers the program recognises and one example row.</summary>
public static class ImportTemplates
{
    public static string Csv(ImportKind kind)
    {
        var fields = ImportSchema.Fields(kind);
        return WeighStation.Core.Csv.Build(
            fields.Select(f => f.Title).ToList(),
            [fields.Select(f => f.Hint).ToList()]);
    }

    public static string FileName(ImportKind kind) => $"шаблон-{kind.ToString().ToLowerInvariant()}.csv";
}
