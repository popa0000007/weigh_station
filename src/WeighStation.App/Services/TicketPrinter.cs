using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WeighStation.Core;
using WeighStation.Core.Models;
using WeighStation.Presentation.Services;

namespace WeighStation.App.Services;

/// <summary>Builds a one-page weighing ticket as a FlowDocument and sends it to the printer the user picks.</summary>
public sealed class TicketPrinter : ITicketPrinter
{
    public void Print(Weighing weighing, IReadOnlyList<AuditEntry> history)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var document = BuildDocument(weighing, history);
        document.PageWidth = dialog.PrintableAreaWidth;
        document.PageHeight = dialog.PrintableAreaHeight;
        document.PagePadding = new Thickness(48);
        document.ColumnWidth = double.PositiveInfinity;
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, $"Талон взвешивания № {weighing.Id}");
    }

    internal static FlowDocument BuildDocument(Weighing w, IReadOnlyList<AuditEntry> history)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };

        doc.Blocks.Add(new Paragraph(new Run($"Талон взвешивания № {w.Id}")) { FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        doc.Blocks.Add(new Paragraph(new Run(DisplayNames.DateTime(w.Time))) { Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 16) });

        var rows = new (string Label, string Value)[]
        {
            ("Госномер тягача", PlateNumber.Format(w.Plate)),
            ("Прицеп", PlateNumber.Format(w.Trailer)),
            ("Водитель", w.Driver ?? ""),
            ("Контрагент", w.Counterparty ?? ""),
            ("Груз", w.Cargo ?? ""),
            ("Операция", DisplayNames.Of(w.Kind)),
        };
        doc.Blocks.Add(Table(rows));

        doc.Blocks.Add(new Paragraph(new Run("Масса, кг")) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        doc.Blocks.Add(Table(
        [
            ("Брутто", DisplayNames.Kg(w.GrossKg)),
            ("Тара", DisplayNames.Kg(w.TareKg)),
            ("Нетто", DisplayNames.Kg(w.NetKg)),
        ], bold: true));

        if (history.Count > 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("История")) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
            foreach (var entry in history)
            {
                doc.Blocks.Add(new Paragraph(new Run($"{entry.Time.ToLocalTime():HH:mm:ss}  {entry.Text}")) { FontSize = 11, Margin = new Thickness(0, 0, 0, 2), Foreground = Brushes.DimGray });
            }
        }

        doc.Blocks.Add(new Paragraph(new Run($"Оператор: {w.Operator}        Подпись: ______________"))
        {
            Margin = new Thickness(0, 32, 0, 0),
        });
        return doc;
    }

    private static Table Table(IEnumerable<(string Label, string Value)> rows, bool bold = false)
    {
        var table = new Table { CellSpacing = 0 };
        table.Columns.Add(new TableColumn { Width = new GridLength(190) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        foreach (var (label, value) in rows)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(label)) { Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 0, 3) }));
            row.Cells.Add(new TableCell(new Paragraph(new Run(value)) { Margin = new Thickness(0, 3, 0, 3), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal }));
            group.Rows.Add(row);
        }

        table.RowGroups.Add(group);
        return table;
    }
}
