using System.Reflection;
using ManagerPaperworkSystem.WinForms;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Reports.Pdf;
using System.Globalization;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        var output = Path.GetFullPath(args.Single()); Directory.CreateDirectory(output);
        void Theme(string mode) => typeof(ThemePreferences).GetProperty("Choice")!.SetValue(null, mode);
        void Capture(Form form, string name)
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.Opacity=0;form.Show();form.PerformLayout();
            using var bitmap = new Bitmap(form.Width,form.Height);
            form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,name+".png"));form.Hide();
        }
        foreach(var mode in new[]{"Light","Dark"})
        {
            Theme(mode);
            var notes=ReleaseHighlights.Load();
            if(notes.Version!="1.0.176" || notes.Features.Length<7)throw new Exception("Incomplete release highlights.");
            using var whatsNew = new WhatsNewForm(notes);
            if(whatsNew.AcceptButton is not Button { Text: "OK", DialogResult: DialogResult.OK })throw new Exception("Notice OK button is missing.");
            Capture(whatsNew,"whats-new-"+mode);
            using var entry=new ShiftDropEntryForm("GALAXY ELGIN");entry.Batch.Text="1551";entry.Drop.Value=95;entry.Payout.Value=10;entry.Reason.Text="Register supplies";
            if(entry.AcceptButton!=entry.SaveButton)throw new Exception("Enter does not save.");
            Capture(entry,"entry-"+mode);
            foreach(var variance in new decimal?[]{5,-5,0,null})
            {
                using var result=new ShiftDropResultForm("Batch 1551",variance,"Entry saved — waiting for batch to sync.");
                var label=result.Controls.Find("ShiftResult",false).Single();
                if(variance>0 && (!label.Text.StartsWith("OVER") || label.ForeColor.G<=label.ForeColor.R))throw new Exception("Over result not green.");
                if(variance<0 && (!label.Text.StartsWith("SHORT") || label.ForeColor.R<=label.ForeColor.G))throw new Exception("Short result not red.");
                Capture(result,"result-"+mode+"-"+(variance?.ToString()??"pending"));
            }
            Theme("Dark");ThemePreferences.ApplyTree(entry);Theme("Light");ThemePreferences.ApplyTree(entry);
            if(entry.BackColor!=Color.White || entry.Batch.ForeColor.GetBrightness()>.6f)throw new Exception("Theme did not restore.");
        }
        var month=new DateOnly(2026,10,1);
        var cash=new[]{new CashOnHandEntry{Date=month.AddMonths(-1),CashAdded=900,Reference="CARRY_FORWARD"},new CashOnHandEntry{Date=month,CashAdded=500,Reference="CARRY_FORWARD",Description="Opening cash"},new CashOnHandEntry{Date=month.AddDays(1),CashAdded=90,Reference="SHIFTLOG:1"},new CashOnHandEntry{Date=month.AddDays(2),IsPayout=true,PayoutAmount=20}};
        QuestPDF.Settings.License=QuestPDF.Infrastructure.LicenseType.Community;
        var pdf=Path.Combine(output,"monthly-cash.pdf");
        SelectedOptionReportPdf.GenerateCashOnHand("Fixture Store","Test",month,month.AddMonths(1).AddDays(-1),cash.Where(x=>x.Date>=month).ToList(),pdf,cash);
        using var parsed=UglyToad.PdfPig.PdfDocument.Open(pdf);
        var text=string.Join(" ",parsed.GetPages().Select(x=>x.Text));
        if(!text.Contains("500.00") || !text.Contains("570.00") || text.Contains("1,470.00"))throw new Exception("PDF opening or closing balance is wrong.");
        Console.WriteLine("PASS: entry form, Enter binding, light/dark over/short/balanced/pending results, theme restoration, release highlights in both themes and PDF monthly balances. Forms rendered offscreen; application was not launched.");
    }
}
