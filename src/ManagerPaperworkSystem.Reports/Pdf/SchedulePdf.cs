using ManagerPaperworkSystem.Core.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ManagerPaperworkSystem.Reports.Pdf;

public static class SchedulePdf
{
    private const string Ink = "#111111";
    private const string Grid = "#444444";
    private const string Header = "#F2F2F2";

    public static void Generate(
        string storeName,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<ScheduleShift> shifts,
        IReadOnlyDictionary<int, Employee> employees,
        string outputPath)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var weeks = WeekRanges(from, to).ToList();

        Document.Create(document =>
        {
            foreach (var week in weeks)
            {
                var dates = Enumerable.Range(0, week.Days)
                    .Select(offset => week.From.AddDays(offset))
                    .ToArray();
                var weekShifts = shifts
                    .Where(x => x.ShiftDate >= week.From && x.ShiftDate <= week.To)
                    .ToList();
                var weekEmployees = employees.Values
                    .OrderBy(x => x.FirstName)
                    .ThenBy(x => x.LastName)
                    .ToList();

                document.Page(page =>
                {
                    page.Size(PageSizes.Letter.Landscape());
                    page.Margin(16);
                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(8).FontColor(Ink));
                    page.Content().Column(column =>
                    {
                        column.Item().Border(1.5f).BorderColor(Grid).PaddingVertical(5)
                            .AlignCenter().Text(storeName.ToUpperInvariant()).Bold().FontSize(18);
                        column.Item().BorderHorizontal(1.5f).BorderColor(Grid).PaddingVertical(4)
                            .AlignCenter().Text($"{week.From:M/d/yy} - {week.To:M/d/yy}").Bold().FontSize(22);
                        column.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.ConstantColumn(92);
                                foreach (var _ in dates)
                                    columns.RelativeColumn();
                            });

                            HeaderCell(table, "");
                            foreach (var date in dates)
                                HeaderCell(table, date.ToString("dddd").ToUpperInvariant());

                            HeaderCell(table, "");
                            foreach (var date in dates)
                                HeaderCell(table, date.ToString("M/d/yyyy"));

                            foreach (var employee in weekEmployees)
                            {
                                NameCell(table, DisplayName(employee));
                                foreach (var date in dates)
                                {
                                    var daily = weekShifts
                                        .Where(x => x.EmployeeId == employee.Id && x.ShiftDate == date)
                                        .OrderBy(x => x.StartTime)
                                        .Select(ShiftText)
                                        .ToList();
                                    ShiftCell(table, daily.Count == 0 ? "OFF" : string.Join("\n", daily), daily.Count == 0);
                                }
                            }

                            if (weekEmployees.Count == 0)
                            {
                                NameCell(table, "NO SHIFTS");
                                foreach (var _ in dates)
                                    ShiftCell(table, "OFF", true);
                            }
                        });
                    });
                    page.Footer().DefaultTextStyle(x => x.FontSize(7).FontColor(Colors.Grey.Darken1))
                        .AlignRight().Text(text =>
                    {
                        text.Span("Published schedule  |  ");
                        text.Span(DateTime.Now.ToString("M/d/yyyy h:mm tt"));
                    });
                });
            }
        }).GeneratePdf(outputPath);
    }

    private static IEnumerable<(DateOnly From, DateOnly To, int Days)> WeekRanges(DateOnly from, DateOnly to)
    {
        var cursor = from;
        while (cursor <= to)
        {
            var end = cursor.AddDays(6);
            if (end > to) end = to;
            yield return (cursor, end, end.DayNumber - cursor.DayNumber + 1);
            cursor = end.AddDays(1);
        }
    }

    private static string DisplayName(Employee employee)
    {
        var first = employee.FirstName.Trim();
        return string.IsNullOrWhiteSpace(first) ? employee.FullName.ToUpperInvariant() : first.ToUpperInvariant();
    }

    private static string ShiftText(ScheduleShift shift)
    {
        var start = DateTime.Today.Add(shift.StartTime).ToString("h:mm tt");
        var end = DateTime.Today.Add(shift.EndTime).ToString("h:mm tt");
        return $"{start} - {end}";
    }

    private static void HeaderCell(TableDescriptor table, string value) =>
        table.Cell().Background(Header).Border(1).BorderColor(Grid).PaddingVertical(5).PaddingHorizontal(2)
            .AlignCenter().AlignMiddle().Text(value).Bold().FontSize(8);

    private static void NameCell(TableDescriptor table, string value) =>
        table.Cell().Border(1).BorderColor(Grid).MinHeight(42).PaddingHorizontal(3)
            .AlignCenter().AlignMiddle().Text(value).Bold().FontSize(9);

    private static void ShiftCell(TableDescriptor table, string value, bool off = false)
    {
        var text = table.Cell().Border(1).BorderColor(Grid).MinHeight(42).PaddingHorizontal(2)
            .AlignCenter().AlignMiddle().Text(value).SemiBold().FontSize(8);
        if (off) text.FontColor(Colors.Grey.Darken1);
    }
}
