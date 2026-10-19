using System.Text;
using AccFiltering.Models;

namespace AccFiltering.Services;

/// <summary>Writes <c>copy_report_yyyyMMdd_HHmm.csv</c> in the working directory.</summary>
public sealed class ReportWriter
{
    /// <summary>Writes every row and returns the full file path.</summary>
    public string Write(IReadOnlyList<CopyReportRow> rows)
    {
        var directory = Directory.GetCurrentDirectory();
        var stamp = DateTime.Now;
        var path = Path.Combine(directory, FormattableString.Invariant($"copy_report_{stamp:yyyyMMdd_HHmm}.csv"));
        if (File.Exists(path))
        {
            path = Path.Combine(directory, FormattableString.Invariant($"copy_report_{stamp:yyyyMMdd_HHmmss}.csv"));
        }

        var csv = ToCsv(rows);
        File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>Builds the report CSV, including a UTF-8 byte order mark so Excel opens it cleanly.</summary>
    public static string ToCsv(IReadOnlyList<CopyReportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ExcelName,MatchedName,SourceItemId,SourceVersionId,Status,DestinationItemId,Error");
        foreach (var row in rows)
        {
            builder.Append(Csv(row.ExcelName)).Append(',')
                .Append(Csv(row.MatchedName)).Append(',')
                .Append(Csv(row.SourceItemId)).Append(',')
                .Append(Csv(row.SourceVersionId)).Append(',')
                .Append(Csv(row.StatusText)).Append(',')
                .Append(Csv(row.DestinationItemId)).Append(',')
                .Append(Csv(row.Error))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }
}
