using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using Microsoft.Win32;

namespace ManagerPaperworkSystem.UI.Views;

public partial class ReportsWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly IReportService _reports;

    public ReportsWindow(ISettingsService settings, IReportService reports)
    {
        InitializeComponent();
        _settings = settings;
        _reports = reports;

        cmbReportType.ItemsSource = Enum.GetValues(typeof(ReportType)).Cast<ReportType>().ToList();

        Loaded += async (_, _) =>
        {
            try
            {
                var s = await _settings.GetSettingsAsync();
                cmbReportType.SelectedItem = s.DefaultReportType;
            }
            catch
            {
                cmbReportType.SelectedItem = ReportType.All;
            }

            var today = DateOnly.FromDateTime(DateTime.Today);
            var from = new DateOnly(today.Year, today.Month, 1);
            var to = from.AddMonths(1).AddDays(-1);

            dpFrom.SelectedDate = from.ToDateTime(TimeOnly.MinValue);
            dpTo.SelectedDate = to.ToDateTime(TimeOnly.MinValue);
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";

        if (dpFrom.SelectedDate is null || dpTo.SelectedDate is null)
        {
            lblError.Text = "Please select a date range.";
            return;
        }

        var from = DateOnly.FromDateTime(dpFrom.SelectedDate.Value);
        var to = DateOnly.FromDateTime(dpTo.SelectedDate.Value);
        if (to < from)
        {
            lblError.Text = "End date must be after start date.";
            return;
        }

        var type = (ReportType)(cmbReportType.SelectedItem ?? ReportType.ShiftLog);

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "HisabKitab_Reports");
            Directory.CreateDirectory(tempDir);

            var pdfs = new List<string>();

            if (type == ReportType.All || type == ReportType.ShiftLog)
            {
                var p = Path.Combine(tempDir, $"ShiftLog_Report_{from:yyyyMMdd}_{to:yyyyMMdd}_{DateTime.Now:HHmmss}.pdf");
                await _reports.GenerateShiftLogPdfAsync(from, to, p);
                pdfs.Add(p);
            }
            if (type == ReportType.All || type == ReportType.CashOnHand)
            {
                var p = Path.Combine(tempDir, $"CashOnHand_Report_{from:yyyyMMdd}_{to:yyyyMMdd}_{DateTime.Now:HHmmss}.pdf");
                await _reports.GenerateCashOnHandPdfAsync(from, to, p);
                pdfs.Add(p);
            }
            if (type == ReportType.All || type == ReportType.CheckPayouts)
            {
                var p = Path.Combine(tempDir, $"CheckPayouts_Report_{from:yyyyMMdd}_{to:yyyyMMdd}_{DateTime.Now:HHmmss}.pdf");
                await _reports.GenerateCheckPayoutsPdfAsync(from, to, p);
                pdfs.Add(p);
            }
            if (type == ReportType.SalesSummaryByDate)
            {
                var p = Path.Combine(tempDir, $"SalesSummaryByDate_Report_{from:yyyyMMdd}_{to:yyyyMMdd}_{DateTime.Now:HHmmss}.pdf");
                await _reports.GenerateSalesSummaryByDatePdfAsync(from, to, p);
                pdfs.Add(p);
            }

            if (pdfs.Count == 0)
            {
                lblError.Text = "No report selected.";
                return;
            }

            var preview = new PdfPreviewWindow(pdfs) { Owner = this };
            preview.ShowDialog();
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        lblError.Text = "";

        if (dpFrom.SelectedDate is null || dpTo.SelectedDate is null)
        {
            lblError.Text = "Please select a date range.";
            return;
        }

        var from = DateOnly.FromDateTime(dpFrom.SelectedDate.Value);
        var to = DateOnly.FromDateTime(dpTo.SelectedDate.Value);
        if (to < from)
        {
            lblError.Text = "End date must be after start date.";
            return;
        }

        var type = (ReportType)(cmbReportType.SelectedItem ?? ReportType.All);

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "HisabKitab_Reports");
            Directory.CreateDirectory(tempDir);

            var pdfs = new List<string>();

            if (type == ReportType.All || type == ReportType.ShiftLog)
            {
                var p = Path.Combine(tempDir, $"ShiftLog_Report_{from:yyyyMMdd}_{to:yyyyMMdd}_{DateTime.Now:HHmmss}.pdf");
                await _reports.GenerateShiftLogPdfAsync(from, to, p);
                pdfs.Add(p);
            }
            if (type == ReportType.All || type == ReportType.CashOnHand)
            {
                var p = Path.Combine(tempDir, $"CashOnHand_Report_{from:yyyyMMdd}_{to:yyyyMMdd}_{DateTime.Now:HHmmss}.pdf");
                await _reports.GenerateCashOnHandPdfAsync(from, to, p);
                pdfs.Add(p);
            }
            if (type == ReportType.All || type == ReportType.CheckPayouts)
            {
                var p = Path.Combine(tempDir, $"CheckPayouts_Report_{from:yyyyMMdd}_{to:yyyyMMdd}_{DateTime.Now:HHmmss}.pdf");
                await _reports.GenerateCheckPayoutsPdfAsync(from, to, p);
                pdfs.Add(p);
            }
            if (type == ReportType.SalesSummaryByDate)
            {
                var p = Path.Combine(tempDir, $"SalesSummaryByDate_Report_{from:yyyyMMdd}_{to:yyyyMMdd}_{DateTime.Now:HHmmss}.pdf");
                await _reports.GenerateSalesSummaryByDatePdfAsync(from, to, p);
                pdfs.Add(p);
            }
            if (pdfs.Count == 0)
            {
                lblError.Text = "No report selected.";
                return;
            }

            var preview = new PdfPreviewWindow(pdfs, 0, autoPrint: true) { Owner = this };
            preview.ShowDialog();
        }
        catch (Exception ex)
        {
            lblError.Text = ex.Message;
        }
    }

    private static string GetMonthlyReportsFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var desired = Path.Combine(docs, "HISAB KITAB", "Reports", "Monthly Reports");
        try
        {
            Directory.CreateDirectory(desired);
            return desired;
        }
        catch
        {
            return docs;
        }
    }
}
