using System.Text.Json;

namespace ManagerPaperworkSystem.WinForms;

internal sealed record ReleaseHighlight(string Title, string Description);
internal sealed record ReleaseHighlights(string Version, ReleaseHighlight[] Features)
{
    public static ReleaseHighlights Load()
    {
        using var stream = typeof(ReleaseHighlights).Assembly.GetManifestResourceStream("HisabKitab.ReleaseHighlights.json")
            ?? throw new InvalidOperationException("Release highlights are missing.");
        var notes = JsonSerializer.Deserialize<ReleaseHighlights>(stream)
            ?? throw new InvalidOperationException("Release highlights are empty.");
        if (!System.Version.TryParse(notes.Version, out _) || notes.Features is not { Length: > 0 } ||
            notes.Features.Any(x => string.IsNullOrWhiteSpace(x.Title) || string.IsNullOrWhiteSpace(x.Description)))
            throw new InvalidOperationException("Release highlights are incomplete.");
        return notes;
    }
}

internal sealed class WhatsNewForm : Form
{
    public WhatsNewForm(ReleaseHighlights notes)
    {
        Text = "What's New — HISAB KITAB " + notes.Version;
        ClientSize = new Size(700, 650);
        MinimumSize = new Size(560, 460);
        AutoScaleMode = AutoScaleMode.Dpi;
        MaximizeBox = MinimizeBox = ControlBox = false;
        WinTheme.Apply(this);
        var title = new Label { Dock = DockStyle.Top, Height = 74, Padding = new Padding(20, 14, 20, 8), Text = "What's New in HISAB KITAB\r\nVersion " + notes.Version, Font = WinTheme.BoldFont(17), ForeColor = WinTheme.BlueDark };
        var body = new TextBox { Name = "ReleaseHighlights", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, BackColor = WinTheme.Panel, ForeColor = WinTheme.Text, Font = WinTheme.BodyFont(10.5f), TabStop = false, Text = string.Join("\r\n\r\n", notes.Features.Select(x => x.Title + "\r\n" + x.Description)) };
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8) };
        content.Controls.Add(body);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(20, 8, 20, 12) };
        var ok = WinTheme.Button("OK", true);
        ok.Name = "AcknowledgeRelease";
        ok.Dock = DockStyle.Right;
        ok.Width = 120;
        ok.DialogResult = DialogResult.OK;
        footer.Controls.Add(new Label { Text = "Shown once for each new version.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = WinTheme.Muted });
        footer.Controls.Add(ok);
        AcceptButton = ok;
        Controls.Add(content);
        Controls.Add(title);
        Controls.Add(footer);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing && DialogResult != DialogResult.OK) e.Cancel = true; };
        Shown += (_, _) => { body.SelectionStart = 0; body.SelectionLength = 0; body.ScrollToCaret(); ok.Focus(); };
        ThemePreferences.ApplyTree(this);
    }
}
