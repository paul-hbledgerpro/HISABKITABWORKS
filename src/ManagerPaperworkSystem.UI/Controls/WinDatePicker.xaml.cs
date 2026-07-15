using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;

namespace ManagerPaperworkSystem.UI.Controls;

public partial class WinDatePicker : System.Windows.Controls.UserControl
{
    private readonly WinForms.DateTimePicker _picker;

    // Win32 API to send messages
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_DOWN = 0x28;

    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime?), typeof(WinDatePicker),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    public WinDatePicker()
    {
        InitializeComponent();

        _picker = new WinForms.DateTimePicker
        {
            Format = WinForms.DateTimePickerFormat.Short,
            Width = 200,
            Font = new System.Drawing.Font("Segoe UI", 10f),
            ShowUpDown = false, // Show calendar dropdown, not spin buttons
        };

        _picker.ValueChanged += (_, __) =>
        {
            if (IsEnabled)
                SelectedDate = _picker.Value.Date;
        };

        // Open calendar dropdown when clicking anywhere on the text portion
        _picker.Click += (_, e) =>
        {
            // Simulate Alt+Down to open the calendar dropdown
            try
            {
                SendMessage(_picker.Handle, WM_SYSKEYDOWN, (IntPtr)VK_DOWN, IntPtr.Zero);
            }
            catch { }
        };

        Host.Child = _picker;

        Loaded += (_, __) => SyncEnabled();
        IsEnabledChanged += (_, __) => SyncEnabled();
    }

    private void SyncEnabled()
    {
        _picker.Enabled = IsEnabled;
        // keep Windows default colors for native look
    }

    private static void OnSelectedDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctl = (WinDatePicker)d;
        if (e.NewValue is DateTime dt)
        {
            ctl._picker.Value = dt;
        }
        else
        {
            ctl._picker.Value = DateTime.Today;
        }
    }
}
