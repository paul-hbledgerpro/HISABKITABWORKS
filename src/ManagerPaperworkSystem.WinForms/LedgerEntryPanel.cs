namespace ManagerPaperworkSystem.WinForms;

internal sealed record LedgerEntryDraft(string Batch, DateOnly Date, decimal Amount, decimal Payout, string Payee, string Reason);

internal sealed class LedgerEntryPanel : UserControl
{
    private readonly bool _shift;
    private readonly ComboBox _batch = Choices("LedgerBatch");
    private readonly DateTimePicker _date = WinTheme.DatePicker();
    private readonly NumericUpDown _amount = new() { DecimalPlaces = 2, Maximum = 99999999, ThousandsSeparator = true, Name = "LedgerAmount" };
    private readonly NumericUpDown _payout = new() { DecimalPlaces = 2, Maximum = 99999999, ThousandsSeparator = true, Name = "LedgerRegisterPayout" };
    private readonly ComboBox _payee = Choices("LedgerPayee");
    private readonly ComboBox _reason = Choices("LedgerPurpose");
    private readonly FlowLayoutPanel _fields = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
    private readonly Label _message = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button _save = WinTheme.Button("Save", true);
    private readonly Button _back = WinTheme.Button("Back");
    private readonly Func<LedgerEntryDraft, Task> _submit;
    private readonly Func<Task<(string[] Payees, string[] Reasons)>> _choices;
    private readonly Func<Task<string[]>>? _batches;
    private bool _loadingBatches;
    private bool _review, _loading, _batchEdited;
    public bool IsBusy { get; private set; }
    public bool HasDraft => _batchEdited || _amount.Value != 0 || _payout.Value != 0 || _payee.Text.Length != 0 || _reason.Text.Length != 0;
    public LedgerLayout LayoutChoice { get; private set; }

    public LedgerEntryPanel(bool shift, Func<LedgerEntryDraft, Task> submit, Func<Task<(string[] Payees, string[] Reasons)>> choices, Func<Task<string[]>>? batches = null)
    {
        _shift = shift; _submit = submit; _choices = choices; _batches = batches; _batch.MaxLength = 20;
        SelectAmountOnFocus(_amount); SelectAmountOnFocus(_payout);
        Dock = DockStyle.Fill; BackColor = WinTheme.Panel; ForeColor = WinTheme.Text; Padding = new Padding(10);
        _save.Width = 145; _back.Width = 85;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.Controls.Add(_fields, 0, 0); root.Controls.Add(_message, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.AddRange([_back, _save]); root.Controls.Add(actions, 0, 2); Controls.Add(root);
        AddField(shift ? "Shift / batch number" : "Date", shift ? _batch : _date, 160);
        AddField(shift ? "Cash drop amount" : "Payout amount", _amount, 160);
        if (shift) AddField("Register payout (if any)", _payout, 220);
        AddField("Paid to / vendor (type or select)", _payee, 240);
        AddField("Purpose / reason (type or select)", _reason, 240);
        _back.Click += (_, _) => { _review = false; UpdateState(); };
        _save.Click += async (_, _) => await SubmitAsync();
        foreach (Control input in new Control[] { _batch, _date, _amount, _payout, _payee, _reason })
            input.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && input is not ComboBox { DroppedDown: true }) { e.SuppressKeyPress = true; await SubmitAsync(); } };
        _payee.DropDown += async (_, _) => await RefreshChoicesAsync();
        _reason.DropDown += async (_, _) => await RefreshChoicesAsync();
        _batch.TextUpdate += (_, _) => _batchEdited = true;
        _batch.SelectionChangeCommitted += (_, _) => _batchEdited = true;
        _batch.DropDown += async (_, _) => await RefreshBatchesAsync();
        HandleCreated += async (_, _) => await RefreshChoicesAsync();
        ApplyLayout(LedgerLayout.SideEntryGrid);
    }
    private static void SelectAmountOnFocus(NumericUpDown amount)
    {
        void Select() { if (!amount.IsDisposed) amount.Select(0, amount.Text.Length); }
        void QueueSelection() { if (!amount.IsDisposed && amount.IsHandleCreated) amount.BeginInvoke((Action)Select); }
        amount.Enter += (_, _) => QueueSelection();
        amount.MouseUp += (_, _) => QueueSelection();
        foreach (var text in amount.Controls.OfType<TextBox>()) text.MouseUp += (_, _) => QueueSelection();
    }
    internal async Task RefreshBatchesAsync()
    {
        if (_batches is null || _loadingBatches || IsDisposed) return;
        _loadingBatches = true;
        try
        {
            var values = await _batches(); if (IsDisposed) return;
            var text = _batch.Text; _batch.BeginUpdate(); _batch.Items.Clear();
            _batch.Items.AddRange(values.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
            _batch.SelectedIndex = -1; _batch.Text = text; _batch.EndUpdate();
        }
        catch (Exception ex) { if (!IsDisposed) _message.Text = "Batch list could not load. You can still type a batch. " + AppBootstrap.RedactSensitiveText(ex.Message); }
        finally { _loadingBatches = false; }
    }
    internal static ComboBox Choices(string name) => new() { Name = name, DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 200, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, FlatStyle = FlatStyle.Standard };
    private void AddField(string label, Control input, int width)
    {
        var field = new Panel { Width = width, Height = 65, Margin = new Padding(2, 4, 8, 0), BackColor = WinTheme.Panel, ForeColor = WinTheme.Text };
        field.Controls.Add(new Label { Text = label, Dock = DockStyle.Top, Height = 25, Font = WinTheme.BoldFont(9), ForeColor = WinTheme.Text, BackColor = WinTheme.Panel });
        input.Top = 27; input.Width = width - 4; input.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right; input.AccessibleName = label;
        field.Controls.Add(input); _fields.Controls.Add(field);
    }
    public void ApplyLayout(LedgerLayout layout)
    {
        LayoutChoice = layout; _review = false;
        _fields.FlowDirection = layout == LedgerLayout.SideEntryGrid ? FlowDirection.TopDown : FlowDirection.LeftToRight;
        _fields.WrapContents = layout != LedgerLayout.SideEntryGrid; UpdateState();
    }
    public void SelectBatch(string batch) { if (!HasDraft && !IsBusy) _batch.Text = batch; }
    public void FocusEntry() { (_shift ? (Control)_batch : _amount).Focus(); }
    private void UpdateState()
    {
        _fields.Enabled = !IsBusy && !_review; _save.Enabled = _back.Enabled = !IsBusy;
        _back.Visible = _review;
        _save.Text = IsBusy ? "Saving…" : LayoutChoice == LedgerLayout.GuidedGrid && !_review ? "Review Entry" : _shift ? "Save Drop" : "Save Payout";
        if (!_review) _message.Text = "New payees and purposes are remembered for this store after saving.";
    }
    public async Task RefreshChoicesAsync()
    {
        if (_loading || IsDisposed) return;
        _loading = true;
        try
        {
            var values = await _choices(); if (IsDisposed) return;
            Fill(_payee, values.Payees); Fill(_reason, values.Reasons);
        }
        catch (Exception ex) { if (!IsDisposed) _message.Text = "Saved choices could not load. You can still type a value. " + AppBootstrap.RedactSensitiveText(ex.GetBaseException().Message); }
        finally { _loading = false; }
    }
    private static void Fill(ComboBox box, IEnumerable<string> values)
    {
        var text = box.Text; box.BeginUpdate(); box.Items.Clear();
        box.Items.AddRange(values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Cast<object>().ToArray());
        box.SelectedIndex = -1; box.Text = text; box.EndUpdate();
    }
    private async Task SubmitAsync()
    {
        if (IsBusy || LicenseRuntime.IsReadOnly) return;
        var draft = new LedgerEntryDraft(_batch.Text.Trim(), DateOnly.FromDateTime(_date.Value), _amount.Value, _payout.Value, _payee.Text.Trim(), _reason.Text.Trim());
        if (!_shift && (draft.Amount <= 0 || draft.Reason.Length == 0)) { _message.Text = "Enter a positive payout amount and its purpose / reason."; return; }
        if (_shift)
        {
            try { ShiftDropWorkflow.Validate(draft.Batch, draft.Amount, draft.Payout, draft.Reason); }
            catch (Exception ex) { _message.Text = ex.Message; return; }
            if (draft.Payout == 0 && draft.Payee.Length > 0) { _message.Text = "Enter a register payout amount or clear the payee."; return; }
        }
        if (LayoutChoice == LedgerLayout.GuidedGrid && !_review)
        {
            _review = true; UpdateState(); _message.Text = $"Confirm {( _shift ? "batch " + draft.Batch : draft.Payee)}: {draft.Amount:C2}" + (_shift ? $"; payout {draft.Payout:C2}." : "."); return;
        }
        IsBusy = true; UpdateState();
        try
        {
            await _submit(draft); if (IsDisposed) return;
            _amount.Value = _payout.Value = 0; _payee.SelectedIndex = _reason.SelectedIndex = _batch.SelectedIndex = -1; _payee.Text = _reason.Text = _batch.Text = ""; _review = false; _batchEdited = false;
            await RefreshChoicesAsync(); _message.Text = "Entry saved.";
        }
        catch (OperationCanceledException) { if (!IsDisposed) _message.Text = "Entry was not saved. Your values are still here."; }
        catch (Exception ex) { if (!IsDisposed) _message.Text = AppBootstrap.RedactSensitiveText(ex.GetBaseException().Message); }
        finally { IsBusy = false; if (!IsDisposed) { var message = _message.Text; UpdateState(); _message.Text = message; } }
    }
}
