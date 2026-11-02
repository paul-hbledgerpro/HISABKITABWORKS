using System.Reflection;
using ManagerPaperworkSystem.WinForms;

internal static class RepeatEntryChecks
{
    private static void Check(bool value, string detail) { if (!value) throw new Exception(detail); }
    private static void Enter(Control control) => typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [new KeyEventArgs(Keys.Enter)]);
    private static void PumpUntil(Func<bool> complete)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!complete() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(1); }
        Check(complete(), "Timed out waiting for entry action"); Application.DoEvents();
    }
    public static void Run()
    {
        foreach (var shift in new[] { true, false })
        foreach (var layout in Enum.GetValues<LedgerLayout>())
        {
            var posted = new List<LedgerEntryDraft>();
            TaskCompletionSource? pending = null; var reject = false;
            using var form = new Form { ClientSize = new Size(1100, 760), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Opacity = 0 };
            var grid = WinTheme.Grid(); grid.DataSource = new[] { new { Batch = "Fixture" } };
            using var entry = new LedgerEntryPanel(shift, async draft =>
            {
                if (pending is not null) await pending.Task;
                if (reject) throw new InvalidOperationException("Fixture save rejected");
                posted.Add(draft); grid.Focus(); // Grid refresh must not retain focus after saving.
            }, () => Task.FromResult((new[] { "Supplier" }, new[] { "Supplies" })));
            var host = new LedgerWorkspaceHost(shift ? "Shift Cash Drop" : "Cash On Hand", new FlowLayoutPanel(), grid, new Label(), entry);
            host.ApplyLayout(layout); form.Controls.Add(host); form.Show(); Application.DoEvents();
            var batch = (ComboBox?)entry.Controls.Find("LedgerBatch", true).SingleOrDefault();
            var amount = (NumericUpDown)entry.Controls.Find("LedgerAmount", true).Single();
            var payout = (NumericUpDown)entry.Controls.Find("LedgerRegisterPayout", true).SingleOrDefault()!;
            var payee = (ComboBox)entry.Controls.Find("LedgerPayee", true).Single();
            var reason = (ComboBox)entry.Controls.Find("LedgerPurpose", true).Single();
            void Fill(int number)
            {
                if (batch is not null) batch.Text = number.ToString(); amount.Value = number; payee.Text = "Supplier"; reason.Text = "Supplies";
                if (shift) payout.Value = 2;
            }
            void Submit()
            {
                Enter(reason); Application.DoEvents();
                if (layout == LedgerLayout.GuidedGrid)
                {
                    var focusedSave = entry.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Single(b => b.Text.StartsWith("Save"));
                    Check(focusedSave.Focused, "Guided review moves keyboard focus to confirmation"); focusedSave.PerformClick();
                }
            }
            host.FocusEntry();
            for (var number = 1; number <= 2; number++)
            {
                Fill(number); Submit(); PumpUntil(() => !entry.IsBusy);
                Check(posted.Count == number && posted[^1].Amount == number, "Each repeated entry posts once");
                Check((shift ? (Control)batch! : amount).ContainsFocus, "Next entry field gets focus without toolbar click");
                Check((batch is null || batch.Text == "") && amount.Value == 0 && payee.Text == "" && reason.Text == "", "Old draft cleared");
                if (shift) Check(payout.Value == 0, "Old register payout cleared");
                else Check(amount.Controls.OfType<TextBox>().Single().SelectionLength > 0, "Default payout amount selected for typing");
            }
            Fill(3); pending = new TaskCompletionSource(); Submit(); Check(entry.IsBusy, "Async save busy guard");
            Enter(reason); Application.DoEvents(); Check(posted.Count == 2, "Repeated Enter cannot post while saving");
            pending.SetResult(); PumpUntil(() => !entry.IsBusy); pending = null;
            Check(posted.Count == 3, "One commit after delayed save");
            Fill(4); reject = true; Submit(); PumpUntil(() => !entry.IsBusy);
            Check(posted.Count == 3 && amount.Value == 4 && reason.Text == "Supplies", "Failed save preserves values and posts nothing");
            form.Hide();
        }
        Console.WriteLine("PASS: both sections in all three layouts; consecutive keyboard saves, next-field focus, cleared drafts, selected payout amount, guided confirmation, duplicate-submit guard and failed-save preservation. Main application not launched.");
    }
}

