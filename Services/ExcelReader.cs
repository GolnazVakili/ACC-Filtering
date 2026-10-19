using AccFiltering.Models;
using ClosedXML.Excel;

namespace AccFiltering.Services;

/// <summary>Reads the <c>Model Name</c> column from the first worksheet.</summary>
public sealed class ExcelReader
{
    /// <summary>
    /// Returns trimmed, unique model names.
    /// Throws <see cref="InvalidOperationException"/> when the column is missing.
    /// </summary>
    public ExcelReadResult Read(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.First();
        var header = FindHeader(sheet);
        if (header is null)
        {
            throw new InvalidOperationException(
                "The Excel file has no column named \"Model Name\". Add that header and run again.");
        }

        var (headerRow, column) = header.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        var duplicates = new List<string>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow;

        for (var row = headerRow + 1; row <= lastRow; row++)
        {
            var value = sheet.Cell(row, column).GetString().Trim();
            if (value.Length == 0)
            {
                continue;
            }

            var key = NameMatcher.Normalize(value);
            if (key.Length == 0)
            {
                continue;
            }

            if (!seen.Add(key))
            {
                duplicates.Add(value);
                continue;
            }

            names.Add(value);
        }

        return new ExcelReadResult(names, duplicates, sheet.Name);
    }

    private static (int Row, int Column)? FindHeader(IXLWorksheet sheet)
    {
        var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 10);
        for (var row = 1; row <= lastRow; row++)
        {
            var lastColumn = sheet.Row(row).LastCellUsed()?.Address.ColumnNumber ?? 0;
            for (var column = 1; column <= lastColumn; column++)
            {
                var text = sheet.Cell(row, column).GetString().Trim();
                if (text.Equals("Model Name", StringComparison.OrdinalIgnoreCase))
                {
                    return (row, column);
                }
            }
        }

        return null;
    }
}
