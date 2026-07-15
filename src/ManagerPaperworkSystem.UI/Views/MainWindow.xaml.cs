using System.Globalization;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using Microsoft.Win32;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Core.Services;
using ManagerPaperworkSystem.Data.Db;
using ManagerPaperworkSystem.UI.Services;
using Microsoft.EntityFrameworkCore;
using ManagerPaperworkSystem.UI.Utils;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfButton = System.Windows.Controls.Button;
using WpfControl = System.Windows.Controls.Control;
using WpfImage = System.Windows.Controls.Image;
using WpfPanel = System.Windows.Controls.Panel;

namespace ManagerPaperworkSystem.UI.Views;

public partial class MainWindow : Window
{
    private const string AppDisplayName = "HISAB KITAB";
    private const string AppTitleName = "Hisab Kitab";
    private const string AppLogoAsset = "pack://application:,,,/Assets/HisabKitab_Logo.png";
    private const string AppIconAsset = "pack://application:,,,/Assets/HisabKitab_Icon.png";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISettingsService _settingsService;
    private readonly IReportService _reportService;
    private readonly IAppPaths _paths;
    private readonly SessionState _session;
    private readonly PosReportImportService _posImporter;
    private readonly UiPreferencesService _uiPrefs;

    private readonly PurchaseService _purchaseService;
    private readonly InvoiceImportService _invoiceImportService;
    private readonly CheckPrintService _checkPrintService = new();
    private StoreConnectionService? _storeConnService;
    private readonly Dictionary<int, WpfButton> _sidebarTabButtons = new();
    private readonly List<UIElement> _adminOnlySidebarElements = new();
    private readonly HashSet<MenuItem> _readableMenuHookedItems = new();
    private readonly List<TextBlock> _operationalStoreLabels = new();
    private readonly List<System.Windows.Controls.ComboBox> _operationalStoreCombos = new();
    private WpfButton? _activeSidebarButton;
    private System.Windows.Controls.TextBox? _slPayoutReason;
    private TextBlock? _cohCarryForwardSummary;
    private TextBlock? _cohCurrentBalanceSummary;
    private TextBlock? _cohTodayAddedSummary;
    private TextBlock? _cohPendingPayoutsSummary;
    private TextBlock? _chkUnclearedTotalSummary;
    private TextBlock? _chkClearedThisMonthSummary;
    private TextBlock? _chkNextCheckSummary;
    private int _operationsHubTabIndex = -1;

    private int _currentStoreId = 1;
    private bool _loadingStores;
    private bool _syncingOperationalStoreSelection;

    // Guards Shift Log -> Cash On Hand sync so we don't re-enter from multiple UI refresh points.
    private bool _syncingShiftDrops;

    private readonly ObservableCollection<PurchaseInvoiceLine> _invoiceLines = new();
    private int? _selectedInvoiceId;
    private InvoiceImportResult? _lastInvoiceImport;
    private ImportedInvoice? _pendingImportedInvoice;
    private string? _pendingInvoiceSourceFile;
    
    // Helper to create a new DbContext - use 'using var db = CreateDb();' in each method
    private AppDbContext CreateDb() => _storeConnService?.CreateDbContext() ?? _dbFactory.CreateDbContext();

    // Helper for Add + Save operations
    private async Task AddAndSaveAsync<T>(T entity) where T : class
    {
        using var db = CreateDb();
        db.Set<T>().Add(entity);
        await db.SaveChangesAsync();
    }

    // Helper for Remove + Save operations
    private async Task RemoveAndSaveAsync<T>(T entity) where T : class
    {
        using var db = CreateDb();
        db.Set<T>().Attach(entity);
        db.Set<T>().Remove(entity);
        await db.SaveChangesAsync();
    }

    // Helper to execute action with a context
    private async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var db = CreateDb();
        await action(db);
    }

    public MainWindow(IDbContextFactory<AppDbContext> dbFactory, ISettingsService settingsService, IReportService reportService, IAppPaths paths, SessionState session, PosReportImportService posImporter, UiPreferencesService uiPrefs, PurchaseService purchaseService, InvoiceImportService invoiceImportService)
    {
        InitializeComponent();
        ApplyAppBranding();
        _dbFactory = dbFactory;
        _settingsService = settingsService;
        _reportService = reportService;
        _paths = paths;
        _session = session;
        _posImporter = posImporter;
        _uiPrefs = uiPrefs;
        _purchaseService = purchaseService;
        _invoiceImportService = invoiceImportService;
        EnsureOperationsHubTab();
        InstallSidebarNavigation();
        ApplySelectedShiftCashDropShell();
        InstallShiftPayoutReasonField();
        InstallSelectedShiftCashDropLayout();
        InstallCashOnHandMockLayout();
        InstallCheckPayoutMockLayout();
        InstallOperationsModuleShells();

        // On some systems the window frame (caption buttons) + top Menu may not paint until a resize.
        // Force a non-client refresh when the HWND is ready and again after first render.
        SourceInitialized += (_, _) => WindowFrameRefresh.Refresh(this);
        ContentRendered += (_, _) =>
        {
            try
            {
                InvalidateVisual();
                UpdateLayout();
                WindowFrameRefresh.Refresh(this);
            }
            catch { }
        };

        Loaded += MainWindow_Loaded;
        Loaded += (_, _) => RefreshLegacyThemeBrushes();

        // Initialize store connection service for multi-database support
        try
        {
            using var tempDb = _dbFactory.CreateDbContext();
            var defaultConnStr = tempDb.Database.GetConnectionString() ?? "";
            var useSqlServer = defaultConnStr.Contains("Server=", StringComparison.OrdinalIgnoreCase);
            _storeConnService = new StoreConnectionService(_dbFactory, defaultConnStr, useSqlServer);

            // Load saved store connections from JSON file
            var savedConnections = StoreManagerWindow.LoadAllStoreConnections();
            foreach (var kvp in savedConnections)
            {
                if (int.TryParse(kvp.Key, out var storeId))
                    _storeConnService.RegisterStore(storeId, kvp.Value);
            }

            // Wire into ReportService for multi-database report generation
            if (_reportService is ReportService rs)
                rs.SetStoreConnectionService(_storeConnService);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StoreConnectionService init: {ex.Message}");
        }
    }

    private void ApplyAppBranding()
    {
        try
        {
            Title = AppDisplayName;
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(AppIconAsset, UriKind.Absolute));
        }
        catch { }
    }

    private void InstallShiftPayoutReasonField()
    {
        if (_slPayoutReason is not null)
            return;

        if (slRegPayout.Parent is not Grid shiftInputGrid)
            return;

        if (shiftInputGrid.RowDefinitions.Count < 6)
            shiftInputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        foreach (UIElement child in shiftInputGrid.Children)
        {
            if (child is TextBlock text && string.Equals(text.Text, "POS Report", StringComparison.OrdinalIgnoreCase))
                Grid.SetRow(text, 5);

            if (child is System.Windows.Controls.TextBox box &&
                box.IsReadOnly &&
                string.Equals(box.Text, "Upload using buttons below", StringComparison.OrdinalIgnoreCase))
            {
                Grid.SetRow(box, 5);
            }
        }

        var label = new TextBlock
        {
            Text = "Payout Reason",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 10, 6)
        };
        Grid.SetRow(label, 4);
        Grid.SetColumn(label, 3);
        shiftInputGrid.Children.Add(label);

        _slPayoutReason = new System.Windows.Controls.TextBox
        {
            Height = 40,
            FontSize = 14,
            MaxLength = 300,
            Margin = new Thickness(0, 6, 0, 6),
            ToolTip = "Reason for register payout"
        };
        Grid.SetRow(_slPayoutReason, 4);
        Grid.SetColumn(_slPayoutReason, 4);
        shiftInputGrid.Children.Add(_slPayoutReason);

        var hasReasonColumn = gridShift.Columns.Any(c =>
            string.Equals(c.Header?.ToString(), "Payout Reason", StringComparison.OrdinalIgnoreCase));
        if (!hasReasonColumn)
        {
            var payoutIndex = gridShift.Columns
                .Select((column, index) => new { column, index })
                .FirstOrDefault(x => string.Equals(x.column.Header?.ToString(), "Payout", StringComparison.OrdinalIgnoreCase))
                ?.index;

            var reasonColumn = new DataGridTextColumn
            {
                Header = "Payout Reason",
                Binding = new System.Windows.Data.Binding(nameof(ShiftLogEntry.PayoutReason)),
                Width = new DataGridLength(220),
                MinWidth = 180
            };

            gridShift.Columns.Insert(payoutIndex.HasValue ? payoutIndex.Value + 1 : gridShift.Columns.Count, reasonColumn);
        }
    }

    private void InstallSelectedShiftCashDropLayout()
    {
        if (slRegPayout.Parent is not Grid shiftInputGrid)
            return;

        RestyleShiftCashDropSurface(shiftInputGrid);
        AddShiftCashDropHeader(shiftInputGrid);
        ArrangeShiftCashDropForm(shiftInputGrid);
        ArrangeShiftCashDropActions();
        ArrangeShiftCashDropGrid();
    }

    private void InstallCashOnHandMockLayout()
    {
        if (gridCoh.Parent is not Grid rootGrid)
            return;

        rootGrid.Children.Clear();
        rootGrid.RowDefinitions.Clear();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildOperationalHeader("\uE8D7", "Cash On Hand", "Track cash added, payouts, and carry forward balance.");
        Grid.SetRow(header, 0);
        rootGrid.Children.Add(header);

        var formBorder = CreateOperationalPanel();
        Grid.SetRow(formBorder, 1);
        rootGrid.Children.Add(formBorder);

        var form = new Grid { Margin = new Thickness(0) };
        formBorder.Child = form;
        for (var i = 0; i < 3; i++)
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star), MinWidth = 320 });

        AddOperationalField(form, "Date *", cohDate, 0, 0);
        AddOperationalField(form, "Payout Amount", cohPayoutAmount, 0, 3, rightAlign: true);
        AddOperationalField(form, "Description", cohDesc, 0, 6);
        AddOperationalField(form, "Cash Added *", cohCashAdded, 1, 0, rightAlign: true);
        AddOperationalField(form, "Vendor", cohVendor, 1, 3);
        AddCarryForwardField(form, 1, 6);
        AddOperationalField(form, "Is Payout *", cohIsPayout, 2, 0);
        AddOperationalField(form, "Purpose", cohPurpose, 2, 3);

        var actionSummaryRow = BuildCashOnHandActionSummaryRow();
        Grid.SetRow(actionSummaryRow, 2);
        rootGrid.Children.Add(actionSummaryRow);

        cohError.Margin = new Thickness(0, 8, 0, 8);
        ReparentTo(cohError, rootGrid);
        Grid.SetRow(cohError, 3);

        StyleOperationalGrid(gridCoh);
        ConfigureCashOnHandGridColumns();
        Grid.SetRow(gridCoh, 4);
        gridCoh.Margin = new Thickness(0, 14, 0, 0);
        rootGrid.Children.Add(gridCoh);

        var footer = BuildOperationalGridFooter("CashOnHandGridFooter", "Showing 1 to 8 of 15 entries");
        Grid.SetRow(footer, 5);
        rootGrid.Children.Add(footer);
    }

    private void InstallCheckPayoutMockLayout()
    {
        if (gridChk.Parent is not Grid rootGrid)
            return;

        rootGrid.Children.Clear();
        rootGrid.RowDefinitions.Clear();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildOperationalHeader("\uE8A1", "Check Payout", "Record, print, and clear vendor check payouts.");
        Grid.SetRow(header, 0);
        rootGrid.Children.Add(header);

        var formBorder = CreateOperationalPanel();
        Grid.SetRow(formBorder, 1);
        rootGrid.Children.Add(formBorder);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star), MinWidth = 540 });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 520 });
        formBorder.Child = layout;

        var form = new Grid();
        Grid.SetColumn(form, 0);
        layout.Children.Add(form);
        for (var i = 0; i < 4; i++)
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });

        AddOperationalField(form, "Date", chkDate, 0, 0);
        AddOperationalField(form, "Vendor", chkVendor, 0, 3);
        AddOperationalField(form, "Description", chkDesc, 1, 0, columnSpan: 4);
        AddOperationalField(form, "Amount", chkAmount, 2, 0, rightAlign: true);
        AddOperationalField(form, "Check #", chkNumber, 2, 3);
        AddOperationalField(form, "Cleared", chkCleared, 3, 0);

        var statGrid = new Grid { Margin = new Thickness(0) };
        Grid.SetColumn(statGrid, 2);
        layout.Children.Add(statGrid);
        statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var uncleared = BuildOperationalStatCard("Uncleared Total", "#FFFF4136");
        _chkUnclearedTotalSummary = GetStatValueText(uncleared);
        Grid.SetColumn(uncleared, 0);
        statGrid.Children.Add(uncleared);

        var cleared = BuildOperationalStatCard("Cleared This Month", "#FF39D06E");
        _chkClearedThisMonthSummary = GetStatValueText(cleared);
        Grid.SetColumn(cleared, 2);
        statGrid.Children.Add(cleared);

        var nextCheck = BuildOperationalStatCard("Next Check #", "#FFF1C18C", money: false);
        _chkNextCheckSummary = GetStatValueText(nextCheck);
        Grid.SetColumn(nextCheck, 4);
        statGrid.Children.Add(nextCheck);

        var actionBar = BuildCheckPayoutActions();
        Grid.SetRow(actionBar, 2);
        rootGrid.Children.Add(actionBar);

        chkError.Margin = new Thickness(0, 8, 0, 8);
        Grid.SetRow(chkError, 3);
        ReparentTo(chkError, rootGrid);

        StyleOperationalGrid(gridChk);
        ConfigureCheckPayoutGridColumns();
        Grid.SetRow(gridChk, 4);
        gridChk.Margin = new Thickness(0, 14, 0, 0);
        ReparentTo(gridChk, rootGrid);

        var footer = BuildOperationalGridFooter("CheckPayoutGridFooter", "Showing 1 to 8 of 15 entries");
        Grid.SetRow(footer, 5);
        rootGrid.Children.Add(footer);
    }

    private void InstallOperationsModuleShells()
    {
        InstallVendorsPurposesMockLayout();
        InstallPurchasesMockLayout();
        WrapOperationsModuleTab(6, "\uE825", "Bank Statement", "Import statements, categorize transactions, and reconcile checks.");
        InstallProductCostsMockLayout();
        InstallPriceAlertsMockLayout();
        InstallProfitLossMockLayout();
    }

    private Grid CreateOperationsMockRoot(int tabIndex, string glyph, string title, string subtitle)
    {
        if (tabsMain.Items.Count <= tabIndex || tabsMain.Items[tabIndex] is not TabItem tab)
            throw new InvalidOperationException($"Tab {tabIndex} was not found.");

        tab.Content = null;

        var frame = new Border
        {
            Tag = "OperationsModuleShell",
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.Child = root;

        var header = BuildOperationalHeader(glyph, title, subtitle);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var content = new Grid { MinWidth = 1180 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        scroll.Content = content;

        tab.Content = frame;
        return content;
    }

    private Border CreateMockPanel(double marginBottom = 14)
    {
        var border = CreateOperationalPanel();
        border.CornerRadius = new CornerRadius(6);
        border.Margin = new Thickness(0, 0, 0, marginBottom);
        return border;
    }

    private TextBlock CreateMockLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private void AddMockField(Grid grid, string label, System.Windows.Controls.Control control, int row, int column, int columnSpan = 1, bool rightAlign = false)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
        stack.Children.Add(CreateMockLabel(label));
        StyleShiftEntryControl(control, rightAlign);
        ReparentTo(control, stack);
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, column);
        Grid.SetColumnSpan(stack, columnSpan);
        grid.Children.Add(stack);
    }

    private Border CreateMockSummaryCard(string title, string subtitle, string glyph, string accent = "#FFD49A5B")
    {
        var border = new Border
        {
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        border.Child = grid;
        grid.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 28,
            Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(accent)),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        copy.Children.Add(new TextBlock { Text = title, Foreground = GetBrush("Accent3Brush", "#FFF1C18C"), FontWeight = FontWeights.Bold, FontSize = 14 });
        copy.Children.Add(new TextBlock { Text = subtitle, Foreground = GetBrush("MutedTextBrush", "#FF9BA8B5"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
        return border;
    }

    private UIElement CreateMockActionBar(int columns, params UIElement[] actions)
    {
        var bar = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = columns,
            Margin = new Thickness(0, 0, 0, 14),
            MinHeight = 46
        };
        foreach (var action in actions)
        {
            if (action is WpfButton button)
            {
                button.Height = 46;
                button.Margin = new Thickness(5, 0, 5, 0);
                button.Style = CreateShiftActionButtonStyle(4);
                button.Foreground = GetBrush("Accent2Brush", "#FFD49A5B");
            }
            ReparentTo(action, bar);
        }
        return bar;
    }

    private void InstallVendorsPurposesMockLayout()
    {
        try
        {
            var root = CreateOperationsMockRoot(4, "\uE716", "Vendors & Purposes", "Manage vendors, suppliers, and expense purposes.");

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 430 });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 430 });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            Grid.SetRow(top, 0);
            root.Children.Add(top);

            var vendorPanel = CreateMockPanel();
            vendorPanel.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Vendor", Foreground = GetBrush("Accent3Brush", "#FFF1C18C"), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,8) },
                    CreateMockLabel("Vendor Name")
                }
            };
            var vendorStack = (StackPanel)vendorPanel.Child;
            StyleShiftEntryControl(vendorName);
            ReparentTo(vendorName, vendorStack);
            Grid.SetColumn(vendorPanel, 0);
            top.Children.Add(vendorPanel);

            var purposePanel = CreateMockPanel();
            purposePanel.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Purpose", Foreground = GetBrush("Accent3Brush", "#FFF1C18C"), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,8) },
                    CreateMockLabel("Purpose Name")
                }
            };
            var purposeStack = (StackPanel)purposePanel.Child;
            StyleShiftEntryControl(purposeName);
            ReparentTo(purposeName, purposeStack);
            Grid.SetColumn(purposePanel, 2);
            top.Children.Add(purposePanel);

            var summary = new StackPanel();
            summary.Children.Add(CreateMockSummaryCard("Active Vendors", "Pulled from saved vendor list", "\uE716"));
            summary.Children.Add(CreateMockSummaryCard("Active Purposes", "Pulled from saved purpose list", "\uE8A5"));
            summary.Children.Add(CreateMockSummaryCard("Admin Managed", "Managers cannot edit lists", "\uE72E"));
            Grid.SetColumn(summary, 4);
            top.Children.Add(summary);

            var actions = CreateMockActionBar(5,
                CreateOperationalActionButton("\uE80F", "Home", Nav_SelectTab_Click, "0"),
                btnVendorAdd,
                btnPurposeAdd,
                btnVendorDelete,
                btnPurposeDelete);
            Grid.SetRow(actions, 1);
            root.Children.Add(actions);

            var grids = new Grid();
            grids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(grids, 2);
            root.Children.Add(grids);

            StyleOperationalGrid(gridVendors);
            StyleOperationalGrid(gridPurposes);
            ReparentTo(gridVendors, grids);
            Grid.SetColumn(gridVendors, 0);
            ReparentTo(gridPurposes, grids);
            Grid.SetColumn(gridPurposes, 2);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Vendor layout failed: {ex.Message}");
            WrapOperationsModuleTab(4, "\uE716", "Vendors & Purposes", "Maintain vendor names and payout purpose lists.");
        }
    }

    private void InstallPurchasesMockLayout()
    {
        try
        {
            var root = CreateOperationsMockRoot(5, "\uE7BF", "Purchases", "Record vendor invoices, imports, and purchase totals.");
            var formPanel = CreateMockPanel();
            var form = new Grid();
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            formPanel.Child = form;
            AddMockField(form, "Date *", invDate, 0, 0);
            AddMockField(form, "Vendor *", invVendor, 0, 1);
            AddMockField(form, "Invoice #", invNumber, 0, 2);
            AddMockField(form, "Total Amount *", invTotal, 0, 3, rightAlign: true);
            invImportStatus.Visibility = Visibility.Visible;
            ReparentTo(invImportStatus, form);
            Grid.SetRow(invImportStatus, 1);
            Grid.SetColumn(invImportStatus, 0);
            Grid.SetColumnSpan(invImportStatus, 4);
            invError.Margin = new Thickness(0, 4, 0, 0);
            ReparentTo(invError, form);
            Grid.SetRow(invError, 2);
            Grid.SetColumn(invError, 0);
            Grid.SetColumnSpan(invError, 4);
            Grid.SetRow(formPanel, 0);
            root.Children.Add(formPanel);

            var actions = CreateMockActionBar(7,
                CreateOperationalActionButton("\uE80F", "Home", Nav_SelectTab_Click, "0"),
                btnInvClear,
                btnInvImportPdf,
                btnInvAdd,
                btnInvUpdate,
                btnInvDelete,
                btnInvRefresh);
            Grid.SetRow(actions, 1);
            root.Children.Add(actions);

            StyleOperationalGrid(gridInvoices);
            Grid.SetRow(gridInvoices, 2);
            ReparentTo(gridInvoices, root);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Purchases layout failed: {ex.Message}");
            WrapOperationsModuleTab(5, "\uE7BF", "Purchases", "Import, enter, and review purchase invoices.");
        }
    }

    private void InstallProductCostsMockLayout()
    {
        try
        {
            var root = CreateOperationsMockRoot(7, "\uE8B7", "Product Costs", "Track item costs, vendors, and price history.");
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
            Grid.SetRow(top, 0);
            root.Children.Add(top);

            var status = CreateMockPanel();
            status.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Invoice Cost Import", Foreground = GetBrush("Accent3Brush", "#FFF1C18C"), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,8) }
                }
            };
            var statusStack = (StackPanel)status.Child;
            txtCostsImportStatus.Visibility = Visibility.Visible;
            ReparentTo(txtCostsImportStatus, statusStack);
            Grid.SetColumn(status, 0);
            top.Children.Add(status);

            var summary = new StackPanel();
            summary.Children.Add(CreateMockSummaryCard("Products Tracked", "Based on saved product costs", "\uE8B7"));
            summary.Children.Add(CreateMockSummaryCard("Cost Changes", "Created from invoice imports", "\uE9D9", "#FF39D06E"));
            Grid.SetColumn(summary, 2);
            top.Children.Add(summary);

            var addProduct = CreateOperationalActionButton("\uE710", "Add Product", HubAddProduct_Click);
            var actions = CreateMockActionBar(6,
                CreateOperationalActionButton("\uE80F", "Home", Nav_SelectTab_Click, "0"),
                addProduct,
                btnCostsUploadInvoice,
                btnCostsRefresh,
                btnCostsDeleteSelected,
                CreateOperationalActionButton("\uE895", "Refresh Hub", (_, _) => RefreshOperationsHubPage()));
            Grid.SetRow(actions, 1);
            root.Children.Add(actions);

            StyleOperationalGrid(gridProductCosts);
            Grid.SetRow(gridProductCosts, 2);
            ReparentTo(gridProductCosts, root);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Product costs layout failed: {ex.Message}");
            WrapOperationsModuleTab(7, "\uE8B7", "Product Costs", "Track product costs, invoices, and cost changes.");
        }
    }

    private void InstallPriceAlertsMockLayout()
    {
        try
        {
            var root = CreateOperationsMockRoot(8, "\uE7BA", "Price Alerts", "Review supplier cost changes and alert thresholds.");
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            Grid.SetRow(top, 0);
            root.Children.Add(top);

            var info = CreateMockPanel();
            info.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Alert Review", Foreground = GetBrush("Accent3Brush", "#FFF1C18C"), FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,8) },
                    new TextBlock { Text = "Alerts are generated from real invoice and product-cost changes.", Foreground = GetBrush("TextBrush", "#FFF4F1EA"), FontSize = 13 }
                }
            };
            Grid.SetColumn(info, 0);
            top.Children.Add(info);
            var unread = CreateMockSummaryCard("Unread Alerts", "Filtered from saved alerts", "\uE7BA", "#FFFF4136");
            Grid.SetColumn(unread, 2);
            top.Children.Add(unread);
            var resolved = CreateMockSummaryCard("Resolved / Read", "Marked read by users", "\uE73E", "#FF39D06E");
            Grid.SetColumn(resolved, 4);
            top.Children.Add(resolved);

            var actions = CreateMockActionBar(6,
                CreateOperationalActionButton("\uE80F", "Home", Nav_SelectTab_Click, "0"),
                btnAlertsRefresh,
                btnAlertsMarkRead,
                btnAlertsMarkAllRead,
                btnAlertsDelete,
                CreateOperationalActionButton("\uE713", "Manage Alerts", HubManageAlerts_Click));
            Grid.SetRow(actions, 1);
            root.Children.Add(actions);

            StyleOperationalGrid(gridPriceAlerts);
            Grid.SetRow(gridPriceAlerts, 2);
            ReparentTo(gridPriceAlerts, root);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Price alerts layout failed: {ex.Message}");
            WrapOperationsModuleTab(8, "\uE7BA", "Price Alerts", "Review cost changes and supplier price warnings.");
        }
    }

    private void InstallProfitLossMockLayout()
    {
        try
        {
            var root = CreateOperationsMockRoot(9, "\uE9D9", "Profit & Loss", "Analyze sales, expenses, and net profit by period.");
            var filters = CreateMockPanel();
            var filterGrid = new Grid();
            for (var i = 0; i < 5; i++)
                filterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 4 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            filterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filters.Child = filterGrid;
            AddMockField(filterGrid, "Month", cmbPlMonth, 0, 0);
            AddMockField(filterGrid, "Year", cmbPlYear, 0, 1);
            ReparentTo(chkPlYearly, filterGrid);
            Grid.SetColumn(chkPlYearly, 2);
            chkPlYearly.VerticalAlignment = VerticalAlignment.Bottom;
            chkPlYearly.Margin = new Thickness(0, 0, 14, 12);
            ReparentTo(btnPlRefresh, filterGrid);
            SetOperationButtonGlyph(btnPlRefresh, "Calculate");
            Grid.SetColumn(btnPlRefresh, 3);
            ReparentTo(btnPlReport, filterGrid);
            SetOperationButtonGlyph(btnPlReport, "Export Report");
            btnPlReport.Height = 42;
            Grid.SetColumn(btnPlReport, 4);
            Grid.SetRow(filters, 0);
            root.Children.Add(filters);

            var kpis = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            for (var i = 0; i < 5; i++)
                kpis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddProfitKpi(kpis, 0, "Net Sales", plGrossSales, "\uE7BF", "#FF39D06E");
            AddProfitKpi(kpis, 1, "Sales Tax", plSalesTax, "\uE8C7", "#FFF1C18C");
            AddProfitKpi(kpis, 2, "Purchases", plPurchases, "\uE8B7", "#FFFF4136");
            AddProfitKpi(kpis, 3, "Expenses", plTotalExpenses, "\uE8A1", "#FFFF4136");
            AddProfitKpi(kpis, 4, "Net Profit", plNetResult, "\uE9D9", "#FF39D06E");
            Grid.SetRow(kpis, 1);
            root.Children.Add(kpis);

            var details = CreateMockPanel(0);
            var detailGrid = new Grid();
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            details.Child = detailGrid;
            AddProfitDetailPanel(detailGrid, 0, "Revenue", plTotalRevenue, plBankDeposits);
            AddProfitDetailPanel(detailGrid, 2, "Bank Expenses", plUtilities, plRent, plPayroll, plInsurance, plBankFees, plTaxes, plLoanDebt, plOtherBank);
            Grid.SetRow(details, 2);
            root.Children.Add(details);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"P&L layout failed: {ex.Message}");
            WrapOperationsModuleTab(9, "\uE9D9", "Profit & Loss", "Calculate monthly revenue, expenses, and profit.");
        }
    }

    private void AddProfitKpi(Grid grid, int column, string title, TextBlock value, string glyph, string color)
    {
        var card = CreateMockSummaryCard(title, "", glyph, color);
        var stack = ((StackPanel)((Grid)card.Child).Children[1]);
        ReparentTo(value, stack);
        value.FontSize = 20;
        value.FontWeight = FontWeights.Bold;
        value.Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color));
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private void AddProfitDetailPanel(Grid grid, int column, string title, params TextBlock[] values)
    {
        var panel = CreateMockPanel(0);
        var stack = new StackPanel();
        panel.Child = stack;
        stack.Children.Add(new TextBlock { Text = title, Foreground = GetBrush("Accent3Brush", "#FFF1C18C"), FontWeight = FontWeights.Bold, FontSize = 16, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var value in values)
        {
            value.Margin = new Thickness(0, 4, 0, 4);
            value.Foreground = GetBrush("TextBrush", "#FFF4F1EA");
            ReparentTo(value, stack);
        }
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private void WrapOperationsModuleTab(int tabIndex, string glyph, string title, string subtitle)
    {
        if (tabsMain.Items.Count <= tabIndex || tabsMain.Items[tabIndex] is not TabItem tab || tab.Content is not UIElement original)
            return;
        if (original is FrameworkElement { Tag: string tag } && tag == "OperationsModuleShell")
            return;

        tab.Content = null;

        var frame = new Border
        {
            Tag = "OperationsModuleShell",
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.Child = root;

        var header = BuildOperationalHeader(glyph, title, subtitle);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var host = new Border
        {
            Background = GetBrush("TileShimmerBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),
            Child = original
        };
        Grid.SetRow(host, 1);
        root.Children.Add(host);

        tab.Content = frame;
        StyleOperationModuleTree(original);
    }

    private void StyleOperationModuleTree(DependencyObject root)
    {
        foreach (var child in EnumerateVisualChildren(root))
        {
            switch (child)
            {
                case DataGrid grid:
                    StyleOperationalGrid(grid);
                    grid.Margin = grid.Margin == default ? new Thickness(0, 12, 0, 0) : grid.Margin;
                    break;
                case WpfButton button:
                    if (button.Tag as string == "SidebarNav")
                        break;
                    button.Height = double.IsNaN(button.Height) ? 38 : Math.Max(button.Height, 38);
                    button.Margin = button.Margin == default ? new Thickness(4, 2, 4, 2) : button.Margin;
                    button.Style = CreateShiftActionButtonStyle(5);
                    button.Foreground = GetBrush("Accent2Brush", "#FFD49A5B");
                    if (button.Content is string content && !string.IsNullOrWhiteSpace(content))
                        SetOperationButtonGlyph(button, content);
                    break;
                case System.Windows.Controls.TextBox textBox:
                    StyleShiftEntryControl(textBox);
                    break;
                case System.Windows.Controls.ComboBox comboBox:
                    StyleShiftEntryControl(comboBox);
                    ApplyReadableComboBox(comboBox);
                    break;
                case DatePicker datePicker:
                    StyleShiftEntryControl(datePicker);
                    break;
                case Border border when border.Tag as string != "OperationsModuleShell":
                    if (border.BorderThickness.Left > 0 || border.BorderThickness.Top > 0 || border.BorderThickness.Right > 0 || border.BorderThickness.Bottom > 0)
                    {
                        border.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
                        border.Background = GetBrush("TileShimmerBrush", "#FF10243A");
                        if (border.CornerRadius.TopLeft < 6)
                            border.CornerRadius = new CornerRadius(6);
                    }
                    break;
            }
        }
    }

    private IEnumerable<DependencyObject> EnumerateVisualChildren(DependencyObject root)
    {
        if (root is null)
            yield break;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in EnumerateVisualChildren(child))
                yield return descendant;
        }
    }

    private void SetOperationButtonGlyph(WpfButton button, string label)
    {
        var clean = label
            .Replace("🏠", "", StringComparison.Ordinal)
            .Replace("📄", "", StringComparison.Ordinal)
            .Replace("🔄", "", StringComparison.Ordinal)
            .Replace("🏷", "", StringComparison.Ordinal)
            .Replace("💾", "", StringComparison.Ordinal)
            .Replace("🗑", "", StringComparison.Ordinal)
            .Replace("📊", "", StringComparison.Ordinal)
            .Trim();

        var lower = clean.ToLowerInvariant();
        var glyph = lower.Contains("dashboard") || lower == "home" ? "\uE80F" :
            lower.Contains("upload") || lower.Contains("import") ? "\uE898" :
            lower.Contains("refresh") || lower.Contains("calculate") ? "\uE895" :
            lower.Contains("delete") ? "\uE74D" :
            lower.Contains("save") ? "\uE74E" :
            lower.Contains("print") ? "\uE749" :
            lower.Contains("correction") || lower.Contains("categorize") ? "\uE70F" :
            lower.Contains("clear") ? "\uE711" :
            lower.Contains("mark") || lower.Contains("toggle") ? "\uE73E" :
            "\uE710";
        SetIconButtonContent(button, glyph, clean);
    }

    private void ApplyReadableComboBox(System.Windows.Controls.ComboBox comboBox)
    {
        comboBox.Foreground = GetBrush("TextBrush", "#FFF4F1EA");
        comboBox.Background = GetBrush("ControlBrush", "#FF0E263D");
        comboBox.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
        comboBox.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, GetBrush("TextBrush", "#FFF4F1EA"));

        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118")));
        itemStyle.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("FieldBrush", "#FFF4F1EA")));
        itemStyle.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(10, 6, 10, 6)));
        itemStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118")));

        var selectedTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("Accent3Brush", "#FFF1C18C")));
        selectedTrigger.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118")));
        itemStyle.Triggers.Add(selectedTrigger);

        comboBox.ItemContainerStyle = itemStyle;
    }

    private void EnsureOperationsHubTab()
    {
        if (_operationsHubTabIndex >= 0)
            return;

        var hubTab = new TabItem
        {
            Header = "Operations Hub",
            Content = BuildOperationsHubPage()
        };
        tabsMain.Items.Add(hubTab);
        _operationsHubTabIndex = tabsMain.Items.Count - 1;
    }

    private void RefreshOperationsHubPage()
    {
        try
        {
            if (_operationsHubTabIndex < 0 || tabsMain.Items.Count <= _operationsHubTabIndex)
                return;

            if (tabsMain.Items[_operationsHubTabIndex] is TabItem hubTab)
                hubTab.Content = BuildOperationsHubPage();
        }
        catch
        {
        }
    }

    private UIElement BuildOperationsHubPage()
    {
        var frame = new Border
        {
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(12)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frame.Child = root;

        var header = BuildOperationalHeader("\uECA5", "Operations Hub", "Access and manage all core operational modules.");
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 0)
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scroll.Content = content;

        if (_session.IsAdmin)
        {
            AddHubCardToGrid(content, BuildVendorsHubCard(), 0, 0);
            AddHubCardToGrid(content, BuildPurchasesHubCard(), 0, 1);
            AddHubCardToGrid(content, BuildBankStatementHubCard(), 0, 2);
            AddHubCardToGrid(content, BuildProductCostsHubCard(), 1, 0);
            AddHubCardToGrid(content, BuildPriceAlertsHubCard(), 1, 1);
            AddHubCardToGrid(content, BuildProfitLossHubCard(), 1, 2);

            var reports = BuildReportsHubPanel();
            Grid.SetRow(reports, 2);
            Grid.SetColumnSpan(reports, 3);
            content.Children.Add(reports);
        }
        else
        {
            AddHubCardToGrid(content, BuildProductCostsHubCard(), 0, 0);
            AddHubCardToGrid(content, BuildPriceAlertsHubCard(), 0, 1);
        }

        return frame;
    }

    private void AddHubCardToGrid(Grid parent, UIElement card, int row, int column)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        parent.Children.Add(card);
    }

    private UIElement BuildVendorsHubCard() => CreateHubCard(
        "\uE716", "Vendors & Purposes",
        new[] { CreateHubField("\uE721", "Search vendors...", 1), CreateHubButton("\uE710", "Add Vendor", Nav_SelectTab_Click, "4"), CreateHubButton("\uE710", "Add Purpose", Nav_SelectTab_Click, "4") },
        new[] { "Vendor / Purpose", "Type", "Status", "Actions" },
        GetHubVendorPurposeRows(),
        "View All Vendors & Purposes", Nav_SelectTab_Click, "4");

    private UIElement BuildPurchasesHubCard() => CreateHubCard(
        "\uE7BF", "Purchases",
        new[] { CreateHubField("\uE721", "Search purchases...", 1), CreateHubField("", "This Month", .55), CreateHubButton("\uE710", "New Purchase", HubNewPurchase_Click, null) },
        new[] { "Date", "Vendor", "Amount", "Status" },
        GetHubPurchaseRows(),
        "View All Purchases", Nav_SelectTab_Click, "5", "\uE898", "Import Purchases", HubImportPurchases_Click, null);

    private UIElement BuildBankStatementHubCard() => CreateHubCard(
        "\uE825", "Bank Statement",
        new[] { CreateHubField("\uE721", "Search transactions...", 1), CreateHubField("", "All Accounts", .85), CreateHubField("", "This Month", .7) },
        new[] { "Date", "Category", "Description", "Debit", "Credit", "Check #" },
        GetHubBankStatementRows(),
        "View Statement", Nav_SelectTab_Click, "6", "\uE898", "Import Statement", HubImportStatement_Click, null);

    private UIElement BuildProductCostsHubCard() => CreateHubCard(
        "\uE8B7", "Product Costs",
        new[] { CreateHubField("\uE721", "Search products...", 1), CreateHubField("", "All Categories", .7), CreateHubButton("\uE710", "Add Product", HubAddProduct_Click, null) },
        new[] { "Product", "SKU/UPC", "Unit Cost", "Vendor", "Last Date" },
        GetHubProductCostRows(),
        "View All Products", Nav_SelectTab_Click, "7", "\uE895", "Update Costs", HubUpdateCosts_Click, null);

    private UIElement BuildPriceAlertsHubCard() => CreateHubCard(
        "\uE7BA", "Price Alerts",
        new[] { CreateHubField("", "All Categories", 1), CreateHubField("", "All Suppliers", 1) },
        new[] { "Product", "Supplier", "Old Cost", "New Cost", "Status" },
        GetHubPriceAlertRows(),
        "View All Alerts", Nav_SelectTab_Click, "8", "\uE713", "Manage Alerts", HubManageAlerts_Click, null);

    private UIElement BuildProfitLossHubCard()
    {
        var card = CreateHubShell("\uE9D9", "Profit & Loss", minHeight: 274);
        var layout = (Grid)((Border)card).Child;

        var top = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(CreateHubField("", "This Month", 1));
        var refresh = CreateHubButton("\uE895", "Refresh", HubRefreshProfitLoss_Click, null);
        Grid.SetColumn(refresh, 1);
        top.Children.Add(refresh);
        Grid.SetRow(top, 1);
        layout.Children.Add(top);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        Grid.SetRow(body, 2);
        layout.Children.Add(body);

        var metrics = new StackPanel();
        foreach (var row in GetHubProfitLossRows())
            metrics.Children.Add(CreateProfitMetricRow(row.Item1, row.Item2, row.Item3));
        body.Children.Add(metrics);

        var chart = BuildMiniProfitChart();
        Grid.SetColumn(chart, 1);
        body.Children.Add(chart);

        var footer = CreateHubFooter("View Full Report", Nav_SelectTab_Click, "9", "\uE898", "Export Report", HubExportProfitLoss_Click, null);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        return card;
    }

    private UIElement BuildReportsHubPanel()
    {
        var card = CreateHubShell("\uE8A5", "Reports", minHeight: 250);
        card.Margin = new Thickness(0, 0, 0, 10);
        var layout = (Grid)((Border)card).Child;

        var filters = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.55, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.65, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.55, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.55, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(.55, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddHubFilter(filters, 0, "Report Type", "Sales Summary by Date");
        var (from, to) = GetCurrentMonthDateRange();
        AddHubFilter(filters, 1, "From", from.ToString("M/d/yyyy", CultureInfo.InvariantCulture));
        AddHubFilter(filters, 2, "To", to.ToString("M/d/yyyy", CultureInfo.InvariantCulture));
        AddHubFilter(filters, 3, "Group By", "Day");
        var generate = CreateHubButton("\uE8A5", "Generate Report", HubGenerateReport_Click, null);
        Grid.SetColumn(generate, 5);
        filters.Children.Add(generate);
        Grid.SetRow(filters, 1);
        layout.Children.Add(filters);

        var table = CreateHubTable(new[] { "Report Name", "Description", "Last Generated", "Generated By", "Format", "Actions" },
            GetHubReportRows(),
            compact: false);
        Grid.SetRow(table, 2);
        layout.Children.Add(table);

        var footer = CreateHubFooter("View All Reports", Reports_Click, null, "\uE898", "Export All Reports", HubExportAllReports_Click, null);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        return card;
    }

    private string[][] GetHubVendorPurposeRows()
    {
        try
        {
            using var db = CreateDb();
            var rows = new List<string[]>();
            rows.AddRange(db.Vendors.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderBy(x => x.Name)
                .Take(3)
                .ToList()
                .Select(x => new[] { x.Name, "Vendor", "Active", "\uE70F   \uE74D" })
                .ToList());
            rows.AddRange(db.Purposes.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderBy(x => x.Name)
                .Take(Math.Max(0, 5 - rows.Count))
                .ToList()
                .Select(x => new[] { x.Name, "Purpose", "Active", "\uE70F   \uE74D" })
                .ToList());
            return rows.ToArray();
        }
        catch
        {
            return Array.Empty<string[]>();
        }
    }

    private string[][] GetHubPurchaseRows()
    {
        try
        {
            var (from, to) = GetCurrentMonthDateRange();
            using var db = CreateDb();
            return db.PurchaseInvoices.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.InvoiceDate >= from && x.InvoiceDate <= to)
                .OrderByDescending(x => x.InvoiceDate)
                .ThenByDescending(x => x.Id)
                .Take(5)
                .ToList()
                .Select(x => new[]
                {
                    x.InvoiceDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(x.VendorName) ? "-" : x.VendorName,
                    x.Total.ToString("C2", CultureInfo.CurrentCulture),
                    "Entered"
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<string[]>();
        }
    }

    private string[][] GetHubProductCostRows()
    {
        try
        {
            using var db = CreateDb();
            return db.ProductCosts.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.UpdatedUtc)
                .ThenBy(x => x.ProductName)
                .Take(5)
                .ToList()
                .Select(x => new[]
                {
                    x.ProductName,
                    string.IsNullOrWhiteSpace(x.Sku) ? "-" : x.Sku,
                    x.LastUnitCost.ToString("C4", CultureInfo.CurrentCulture),
                    string.IsNullOrWhiteSpace(x.LastVendorName) ? "-" : x.LastVendorName,
                    x.LastInvoiceDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture)
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<string[]>();
        }
    }

    private string[][] GetHubPriceAlertRows()
    {
        try
        {
            using var db = CreateDb();
            return db.PriceAlerts.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.CreatedUtc)
                .Take(5)
                .ToList()
                .Select(x => new[]
                {
                    x.ProductName,
                    string.IsNullOrWhiteSpace(x.VendorName) ? "-" : x.VendorName,
                    x.OldUnitCost.ToString("C4", CultureInfo.CurrentCulture),
                    x.NewUnitCost.ToString("C4", CultureInfo.CurrentCulture),
                    x.IsRead ? "Read" : "Unread"
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<string[]>();
        }
    }

    private string[][] GetHubBankStatementRows()
    {
        try
        {
            var today = DateTime.Today;
            using var db = CreateDb();
            var provider = db.Database.ProviderName ?? "";
            var sql = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                ? @"SELECT Date, COALESCE(Category, '') AS Category, COALESCE(Description, '') AS Description, Debit, Credit, COALESCE(CheckNumber, '') AS CheckNumber
                    FROM BankStatementTransactions
                    WHERE StoreId = {0} AND StatementMonth = {1} AND StatementYear = {2}
                    ORDER BY Date DESC
                    LIMIT 5"
                : @"SELECT TOP (5) [Date], ISNULL(Category, '') AS Category, ISNULL([Description], '') AS Description, Debit, Credit, ISNULL(CheckNumber, '') AS CheckNumber
                    FROM BankStatementTransactions
                    WHERE StoreId = {0} AND StatementMonth = {1} AND StatementYear = {2}
                    ORDER BY [Date] DESC";

            return db.Database.SqlQueryRaw<HubBankTxnRow>(sql, _currentStoreId, today.Month, today.Year)
                .ToList()
                .Select(x => new[]
                {
                    x.Date.ToString("M/d/yyyy", CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(x.Category) ? "-" : x.Category,
                    string.IsNullOrWhiteSpace(x.Description) ? "-" : x.Description,
                    x.Debit == 0 ? "-" : x.Debit.ToString("C2", CultureInfo.CurrentCulture),
                    x.Credit == 0 ? "-" : x.Credit.ToString("C2", CultureInfo.CurrentCulture),
                    string.IsNullOrWhiteSpace(x.CheckNumber) ? "-" : x.CheckNumber
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<string[]>();
        }
    }

    private (string, string, string)[] GetHubProfitLossRows()
    {
        try
        {
            var (from, to) = GetCurrentMonthDateRange();
            using var db = CreateDb();

            var shiftLogs = db.ShiftLogs.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.Date >= from && x.Date <= to)
                .ToList();
            var cashEntries = db.CashOnHand.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.Date >= from && x.Date <= to)
                .ToList();
            var checkEntries = db.CheckPayouts.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.Date >= from && x.Date <= to)
                .ToList();
            var purchases = db.PurchaseInvoices.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.InvoiceDate >= from && x.InvoiceDate <= to)
                .ToList();

            var effShifts = EffectiveRows(shiftLogs, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var effCash = EffectiveRows(cashEntries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var effChecks = EffectiveRows(checkEntries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

            var netSales = effShifts.Sum(x => x.NetSales);
            var costOfGoods = purchases.Sum(x => x.Total);
            var grossProfit = netSales - costOfGoods;
            var expenses = effCash.Where(x => x.IsPayout).Sum(x => x.PayoutAmount) + effChecks.Sum(x => x.CheckAmount);
            var netProfit = grossProfit - expenses;

            return new[]
            {
                ("Net Sales", netSales.ToString("C2", CultureInfo.CurrentCulture), "#FF39D06E"),
                ("Cost of Goods Sold", costOfGoods.ToString("C2", CultureInfo.CurrentCulture), "#FFFF4136"),
                ("Gross Profit", grossProfit.ToString("C2", CultureInfo.CurrentCulture), grossProfit < 0 ? "#FFFF4136" : "#FF39D06E"),
                ("Expenses", expenses.ToString("C2", CultureInfo.CurrentCulture), "#FFFF4136"),
                ("Net Profit", netProfit.ToString("C2", CultureInfo.CurrentCulture), netProfit < 0 ? "#FFFF4136" : "#FF39D06E")
            };
        }
        catch
        {
            return new[]
            {
                ("Net Sales", 0m.ToString("C2", CultureInfo.CurrentCulture), "#FF39D06E"),
                ("Cost of Goods Sold", 0m.ToString("C2", CultureInfo.CurrentCulture), "#FFFF4136"),
                ("Gross Profit", 0m.ToString("C2", CultureInfo.CurrentCulture), "#FF39D06E"),
                ("Expenses", 0m.ToString("C2", CultureInfo.CurrentCulture), "#FFFF4136"),
                ("Net Profit", 0m.ToString("C2", CultureInfo.CurrentCulture), "#FF39D06E")
            };
        }
    }

    private static string[][] GetHubReportRows() => Array.Empty<string[]>();

    private static (DateOnly From, DateOnly To) GetCurrentMonthDateRange()
    {
        var today = DateTime.Today;
        var from = new DateOnly(today.Year, today.Month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    private sealed class HubBankTxnRow
    {
        public DateTime Date { get; set; }
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public string CheckNumber { get; set; } = "";
    }

    private void HubNewPurchase_Click(object sender, RoutedEventArgs e)
    {
        ClearInvoiceForm();
        if (invDate is not null)
            invDate.SelectedDate = DateTime.Today;
        System.Windows.MessageBox.Show(this, "Purchase form cleared and ready for a new purchase.", "Purchases", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void HubImportPurchases_Click(object sender, RoutedEventArgs e) => Inv_ImportPdf_Click(sender, e);

    private void HubImportStatement_Click(object sender, RoutedEventArgs e) => bankStatementTab?.StartStatementImportFromHub();

    private async void HubAddProduct_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("add product costs");
            var dialog = new Window
            {
                Title = "Add Product Cost",
                Owner = this,
                Width = 520,
                Height = 390,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = GetBrush("PanelBrush", "#FF10243A"),
                Foreground = GetBrush("TextBrush", "#FFF4F1EA")
            };

            var root = new Grid { Margin = new Thickness(18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dialog.Content = root;

            var nameBox = new System.Windows.Controls.TextBox();
            var skuBox = new System.Windows.Controls.TextBox();
            var costBox = new System.Windows.Controls.TextBox();
            var vendorBox = new System.Windows.Controls.TextBox();
            var invoiceBox = new System.Windows.Controls.TextBox { Text = $"MANUAL-{DateTime.Now:yyyyMMdd-HHmm}" };
            var datePicker = new DatePicker { SelectedDate = DateTime.Today, Style = TryFindResource("GoldDatePicker") as Style };

            AddDialogField(root, "Product Name *", nameBox, 0);
            AddDialogField(root, "SKU / UPC", skuBox, 1);
            AddDialogField(root, "Unit Cost *", costBox, 2, rightAlign: true);
            AddDialogField(root, "Vendor", vendorBox, 3);
            AddDialogField(root, "Invoice #", invoiceBox, 4);
            AddDialogField(root, "Invoice Date", datePicker, 5);

            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = CreateOperationalActionButton("\uE711", "Cancel", (_, _) => dialog.DialogResult = false);
            var save = CreateOperationalActionButton("\uE74E", "Save Product", (_, _) => dialog.DialogResult = true);
            cancel.Width = 130;
            save.Width = 160;
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            Grid.SetRow(buttons, 6);
            Grid.SetColumnSpan(buttons, 2);
            root.Children.Add(buttons);

            if (dialog.ShowDialog() != true)
                return;

            var productName = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(productName))
                throw new Exception("Product name is required.");
            if (!decimal.TryParse(costBox.Text.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var unitCost) || unitCost < 0)
                throw new Exception("Enter a valid unit cost.");

            var sku = skuBox.Text.Trim();
            var vendor = string.IsNullOrWhiteSpace(vendorBox.Text) ? "Manual Entry" : vendorBox.Text.Trim();
            var invoice = string.IsNullOrWhiteSpace(invoiceBox.Text) ? $"MANUAL-{DateTime.Now:yyyyMMdd-HHmm}" : invoiceBox.Text.Trim();
            var invoiceDate = DateOnly.FromDateTime(datePicker.SelectedDate ?? DateTime.Today);
            var productKey = NormalizeProductKey(string.IsNullOrWhiteSpace(sku) ? productName : sku);

            using var db = CreateDb();
            var existing = await db.ProductCosts.FirstOrDefaultAsync(x => x.StoreId == _currentStoreId && x.ProductKey == productKey);
            if (existing is null)
            {
                db.ProductCosts.Add(new ProductCost
                {
                    StoreId = _currentStoreId,
                    ProductKey = productKey,
                    ProductName = productName,
                    Sku = sku,
                    LastUnitCost = unitCost,
                    LastVendorName = vendor,
                    LastInvoiceNumber = invoice,
                    LastInvoiceDate = invoiceDate,
                    UpdatedUtc = DateTime.UtcNow
                });
            }
            else
            {
                var oldCost = existing.LastUnitCost;
                if (oldCost != unitCost)
                {
                    db.PriceAlerts.Add(new PriceAlert
                    {
                        StoreId = _currentStoreId,
                        ProductKey = productKey,
                        ProductName = productName,
                        Sku = sku,
                        OldUnitCost = oldCost,
                        NewUnitCost = unitCost,
                        Direction = unitCost > oldCost ? PriceChangeDirection.Up : PriceChangeDirection.Down,
                        AlertType = PriceAlertType.PriceChange,
                        VendorName = vendor,
                        InvoiceNumber = invoice,
                        InvoiceDate = invoiceDate,
                        CreatedUtc = DateTime.UtcNow
                    });
                }
                existing.ProductName = productName;
                existing.Sku = sku;
                existing.LastUnitCost = unitCost;
                existing.LastVendorName = vendor;
                existing.LastInvoiceNumber = invoice;
                existing.LastInvoiceDate = invoiceDate;
                existing.UpdatedUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
            await LoadPurchasesModuleAsync();
            System.Windows.MessageBox.Show(this, "Product cost saved from Operations Hub.", "Product Costs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Product Costs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string NormalizeProductKey(string value) => Regex.Replace(value.Trim().ToUpperInvariant(), @"\s+", " ");

    private void AddDialogField(Grid root, string label, WpfControl control, int row, bool rightAlign = false)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5, 12, 5)
        };
        Grid.SetRow(labelBlock, row);
        root.Children.Add(labelBlock);

        StyleShiftEntryControl(control, rightAlign);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        root.Children.Add(control);
    }

    private void HubUpdateCosts_Click(object sender, RoutedEventArgs e) => Costs_UploadInvoice_Click(sender, e);

    private async void HubManageAlerts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var unread = await CreateDb().PriceAlerts.CountAsync(x => x.StoreId == _currentStoreId && !x.IsRead);
            if (unread == 0)
            {
                await LoadPurchasesModuleAsync();
                System.Windows.MessageBox.Show(this, "No unread price alerts found. Alerts were refreshed.", "Price Alerts", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show(this, $"Mark all {unread} unread price alert(s) as read?", "Manage Alerts", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            await _purchaseService.MarkAllAlertsReadAsync(_currentStoreId);
            await LoadPurchasesModuleAsync();
            System.Windows.MessageBox.Show(this, "All price alerts were marked as read.", "Price Alerts", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Price Alerts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HubRefreshProfitLoss_Click(object sender, RoutedEventArgs e) => PL_Refresh_Click(sender, e);

    private void HubExportProfitLoss_Click(object sender, RoutedEventArgs e) => PL_GenerateReport_Click(sender, e);

    private async void HubGenerateReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var today = DateTime.Today;
            var from = new DateOnly(today.Year, today.Month, 1);
            var to = from.AddMonths(1).AddDays(-1);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Sales Summary Report",
                Filter = "PDF Files (*.pdf)|*.pdf",
                FileName = $"SalesSummary_{from:yyyyMMdd}_{to:yyyyMMdd}.pdf"
            };
            if (dlg.ShowDialog() != true)
                return;

            await _reportService.GenerateSalesSummaryByDatePdfAsync(from, to, dlg.FileName);
            OpenGeneratedFile(dlg.FileName);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Generate Report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void HubExportAllReports_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var today = DateTime.Today;
            var from = new DateOnly(today.Year, today.Month, 1);
            var to = from.AddMonths(1).AddDays(-1);
            using var folder = new WinForms.FolderBrowserDialog
            {
                Description = "Select folder for Operations Hub reports",
                UseDescriptionForTitle = true
            };
            if (folder.ShowDialog() != WinForms.DialogResult.OK || string.IsNullOrWhiteSpace(folder.SelectedPath))
                return;

            var stamp = $"{from:yyyyMMdd}_{to:yyyyMMdd}";
            var shift = Path.Combine(folder.SelectedPath, $"ShiftLog_{stamp}.pdf");
            var cash = Path.Combine(folder.SelectedPath, $"CashOnHand_{stamp}.pdf");
            var checks = Path.Combine(folder.SelectedPath, $"CheckPayouts_{stamp}.pdf");
            var sales = Path.Combine(folder.SelectedPath, $"SalesSummary_{stamp}.pdf");
            var profit = Path.Combine(folder.SelectedPath, $"ProfitLoss_{stamp}.pdf");

            await _reportService.GenerateShiftLogPdfAsync(from, to, shift);
            await _reportService.GenerateCashOnHandPdfAsync(from, to, cash);
            await _reportService.GenerateCheckPayoutsPdfAsync(from, to, checks);
            await _reportService.GenerateSalesSummaryByDatePdfAsync(from, to, sales);
            await _reportService.GenerateProfitLossPdfAsync(from, to, profit);

            System.Windows.MessageBox.Show(this, $"Reports exported to:\n{folder.SelectedPath}", "Export All Reports", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export All Reports", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenGeneratedFile(string path)
    {
        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private Border CreateHubCard(string glyph, string title, UIElement[] toolbar, string[] headers, string[][] rows, string footerLink, RoutedEventHandler linkHandler, string? commandParameter, string? buttonGlyph = null, string? buttonText = null, RoutedEventHandler? buttonHandler = null, string? buttonCommandParameter = null)
    {
        var card = CreateHubShell(glyph, title, minHeight: 274);
        var layout = (Grid)card.Child;
        var tools = CreateHubToolbar(toolbar);
        Grid.SetRow(tools, 1);
        layout.Children.Add(tools);
        var table = CreateHubTable(headers, rows, compact: true);
        Grid.SetRow(table, 2);
        layout.Children.Add(table);
        var footer = CreateHubFooter(footerLink, linkHandler, commandParameter, buttonGlyph, buttonText, buttonHandler, buttonCommandParameter);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        return card;
    }

    private Border CreateHubShell(string glyph, string title, double minHeight)
    {
        var card = new Border
        {
            Background = GetBrush("TileShimmerBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 12, 12),
            MinHeight = minHeight
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = layout;

        var heading = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 28,
            Foreground = GetBrush("Accent2Brush", "#FFD49A5B"),
            Width = 38,
            VerticalAlignment = VerticalAlignment.Center
        });
        heading.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = GetBrush("Accent3Brush", "#FFF1C18C"),
            FontWeight = FontWeights.Bold,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);
        return card;
    }

    private Grid CreateHubToolbar(IReadOnlyList<UIElement> controls)
    {
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        for (var i = 0; i < controls.Count; i++)
        {
            if (i > 0)
                toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(controls[i] is WpfButton ? .62 : 1, GridUnitType.Star) });
            Grid.SetColumn(controls[i], i * 2);
            toolbar.Children.Add(controls[i]);
        }
        return toolbar;
    }

    private UIElement CreateHubField(string glyph, string text, double weight)
    {
        var border = new Border
        {
            Height = 32,
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            MinWidth = 90
        };
        var stack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(glyph))
            stack.Children.Add(new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), Foreground = GetBrush("MutedTextBrush", "#FF9BA8B5"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = text, Foreground = GetBrush("TextBrush", "#FFF4F1EA"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        border.Child = stack;
        return border;
    }

    private WpfButton CreateHubButton(string glyph, string label, RoutedEventHandler handler, string? commandParameter)
    {
        var button = CreateOperationalActionButton(glyph, label, handler, commandParameter);
        button.Height = 32;
        button.Margin = new Thickness(0);
        button.MinWidth = 110;
        button.FontSize = 12;
        return button;
    }

    private Border CreateHubTable(string[] headers, string[][] rows, bool compact)
    {
        var grid = new Grid { ClipToBounds = true };
        for (var i = 0; i < headers.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 0 ? 1.35 : 1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(compact ? 30 : 34) });
        for (var i = 0; i < rows.Length; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(compact ? 26 : 34) });

        for (var c = 0; c < headers.Length; c++)
            AddHubCell(grid, headers[c], 0, c, true);

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < headers.Length; c++)
                AddHubCell(grid, rows[r][c], r + 1, c, false);
        }

        return new Border
        {
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("BorderBrush", "#FF24445F"),
            BorderThickness = new Thickness(1),
            Child = grid
        };
    }

    private void AddHubCell(Grid grid, string text, int row, int column, bool header)
    {
        var border = new Border
        {
            Background = header ? GetBrush("ControlBrush", "#FF132C45") : GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("BorderBrush", "#FF24445F"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 0, 8, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = GetHubCellBrush(text, header),
                FontFamily = ContainsIconGlyph(text) ? new System.Windows.Media.FontFamily("Segoe MDL2 Assets") : new System.Windows.Media.FontFamily("Segoe UI"),
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                FontSize = header ? 12 : 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private MediaBrush GetHubCellBrush(string text, bool header)
    {
        if (header) return GetBrush("TextBrush", "#FFF4F1EA");
        if (text.Contains("Active") || text.Contains("Read"))
            return GetBrush("SuccessBrush", "#FF2FBF9B");
        if (text.Contains("High") || text.Contains("Unread") || text.StartsWith("($", StringComparison.Ordinal))
            return GetBrush("Accent5Brush", "#FFD65A5A");
        if (ContainsIconGlyph(text))
            return GetBrush("Accent2Brush", "#FFD49A5B");
        return GetBrush("TextBrush", "#FFF4F1EA");
    }

    private static bool ContainsIconGlyph(string text) => text.Any(ch => ch >= '\uE000' && ch <= '\uF8FF');

    private Grid CreateHubFooter(string linkText, RoutedEventHandler linkHandler, string? commandParameter, string? buttonGlyph, string? buttonText, RoutedEventHandler? buttonHandler = null, string? buttonCommandParameter = null)
    {
        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var link = new WpfButton
        {
            Content = $"{linkText}  \uE72A",
            Background = MediaBrushes.Transparent,
            BorderBrush = MediaBrushes.Transparent,
            Foreground = GetBrush("Accent2Brush", "#FFD49A5B"),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(0)
        };
        link.Click += linkHandler;
        if (commandParameter is not null) link.CommandParameter = commandParameter;
        footer.Children.Add(link);
        if (!string.IsNullOrWhiteSpace(buttonGlyph) && !string.IsNullOrWhiteSpace(buttonText))
        {
            var button = CreateHubButton(buttonGlyph, buttonText, buttonHandler ?? linkHandler, buttonCommandParameter ?? commandParameter);
            Grid.SetColumn(button, 1);
            footer.Children.Add(button);
        }
        return footer;
    }

    private void AddHubFilter(Grid parent, int column, string label, string value)
    {
        var wrap = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wrap.Children.Add(new TextBlock { Text = label, Foreground = GetBrush("TextBrush", "#FFF4F1EA"), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
        var field = CreateHubField("", value, 1);
        Grid.SetColumn(field, 1);
        wrap.Children.Add(field);
        Grid.SetColumn(wrap, column);
        parent.Children.Add(wrap);
    }

    private Border CreateProfitMetricRow(string label, string value, string color)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 12, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "\uE91F  " + label, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), Foreground = GetBrush("TextBrush", "#FFF4F1EA"), FontSize = 12 });
        var valueText = new TextBlock { Text = value, Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color)), FontWeight = FontWeights.Bold, FontSize = 12 };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return new Border { Child = grid };
    }

    private Border BuildMiniProfitChart()
    {
        const double width = 210;
        const double height = 150;
        var canvas = new Canvas { Height = height, MinWidth = 190 };
        for (var i = 0; i < 4; i++)
        {
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0,
                X2 = width,
                Y1 = 20 + (i * 34),
                Y2 = 20 + (i * 34),
                Stroke = GetBrush("BorderBrush", "#FF24445F"),
                StrokeThickness = 1
            });
        }

        var points = GetHubProfitChartPoints(width, height);
        if (points.Count > 1)
        {
            var line = new System.Windows.Shapes.Polyline
            {
                Points = points,
                Stroke = GetBrush("Accent2Brush", "#FFD49A5B"),
                StrokeThickness = 2
            };
            canvas.Children.Add(line);
        }
        foreach (var point in points)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = GetBrush("Accent2Brush", "#FFD49A5B")
            };
            Canvas.SetLeft(dot, point.X - 2.5);
            Canvas.SetTop(dot, point.Y - 2.5);
            canvas.Children.Add(dot);
        }
        return new Border
        {
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("BorderBrush", "#FF24445F"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Child = canvas
        };
    }

    private PointCollection GetHubProfitChartPoints(double width, double height)
    {
        try
        {
            var (from, to) = GetCurrentMonthDateRange();
            using var db = CreateDb();
            var shifts = db.ShiftLogs.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.Date >= from && x.Date <= to)
                .ToList();
            var effective = EffectiveRows(shifts, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc)
                .GroupBy(x => x.Date)
                .OrderBy(x => x.Key)
                .Select(x => x.Sum(row => row.NetSales))
                .ToList();

            var points = new PointCollection();
            if (effective.Count == 0)
                return points;

            var min = effective.Min();
            var max = effective.Max();
            var range = max - min;
            var usableHeight = height - 24;
            for (var i = 0; i < effective.Count; i++)
            {
                var x = effective.Count == 1 ? width / 2 : i * (width / Math.Max(1, effective.Count - 1));
                var y = range == 0 ? height / 2 : 12 + ((double)(max - effective[i]) / (double)range * usableHeight);
                points.Add(new System.Windows.Point(x, y));
            }
            return points;
        }
        catch
        {
            return new PointCollection();
        }
    }

    private UIElement BuildOperationalHeader(string glyph, string title, string subtitle)
    {
        var header = new Grid { Margin = new Thickness(0, 4, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 38,
            Foreground = GetBrush("Accent2Brush", "#FFD49A5B"),
            Margin = new Thickness(0, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        header.Children.Add(icon);

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("Accent3Brush", "#FFF1C18C")
        });
        copy.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = GetBrush("MutedTextBrush", "#FF9BA8B5"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var storeTools = BuildOperationalHeaderStoreTools();
        Grid.SetColumn(storeTools, 2);
        header.Children.Add(storeTools);
        return header;
    }

    private UIElement BuildOperationalHeaderStoreTools()
    {
        var tools = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 0, 0)
        };

        tools.Children.Add(new TextBlock
        {
            Text = "Store:",
            Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        var storeCombo = new System.Windows.Controls.ComboBox
        {
            Width = 260,
            Height = 46,
            ItemsSource = cmbStore?.ItemsSource,
            DisplayMemberPath = "Name",
            SelectedValuePath = "Id",
            SelectedValue = cmbStore?.SelectedValue,
            Background = GetBrush("PanelBrush", "#FF0A1C2C"),
            Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        storeCombo.SelectionChanged += OperationalStore_SelectionChanged;
        _operationalStoreCombos.Add(storeCombo);
        tools.Children.Add(storeCombo);

        var storesButton = new WpfButton
        {
            Content = "Stores...",
            Width = 116,
            Height = 46,
            Margin = new Thickness(14, 0, 0, 0),
            Style = CreateFilledCopperButtonStyle(8),
            Foreground = GetBrush("ButtonTextBrush", "#FF0C1118"),
            FontWeight = FontWeights.SemiBold
        };
        storesButton.Click += ManageStores_Click;
        tools.Children.Add(storesButton);

        return tools;
    }

    private string GetCurrentStoreDisplayName()
    {
        if (cmbStore?.SelectedItem is Store store && !string.IsNullOrWhiteSpace(store.Name))
            return store.Name;
        if (!string.IsNullOrWhiteSpace(cmbStore?.Text))
            return cmbStore.Text;
        if (!string.IsNullOrWhiteSpace(dashStoreName?.Text) && !string.Equals(dashStoreName.Text, "Dashboard", StringComparison.OrdinalIgnoreCase))
            return dashStoreName.Text;
        return "Select Store";
    }

    private void UpdateOperationalStoreLabels()
    {
        var storeName = GetCurrentStoreDisplayName();
        foreach (var label in _operationalStoreLabels)
            label.Text = storeName;

        _syncingOperationalStoreSelection = true;
        try
        {
            foreach (var combo in _operationalStoreCombos)
            {
                combo.ItemsSource = cmbStore?.ItemsSource;
                combo.DisplayMemberPath = "Name";
                combo.SelectedValuePath = "Id";
                combo.SelectedValue = cmbStore?.SelectedValue;
                combo.Text = storeName;
            }
        }
        finally
        {
            _syncingOperationalStoreSelection = false;
        }
    }

    private void OperationalStore_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingOperationalStoreSelection || sender is not System.Windows.Controls.ComboBox combo)
            return;
        if (combo.SelectedValue is int storeId && !Equals(cmbStore.SelectedValue, storeId))
            cmbStore.SelectedValue = storeId;
    }

    private Border CreateOperationalPanel()
    {
        return new Border
        {
            Background = GetBrush("TileShimmerBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 12, 18, 12),
            Margin = new Thickness(0, 0, 0, 14)
        };
    }

    private void AddOperationalField(Grid grid, string labelText, System.Windows.Controls.Control control, int row, int labelColumn, int columnSpan = 1, bool rightAlign = false)
    {
        var label = new TextBlock
        {
            Text = labelText,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 10, 6)
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, labelColumn);
        grid.Children.Add(label);

        StyleShiftEntryControl(control, rightAlign);
        ReparentTo(control, grid);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, labelColumn + 1);
        Grid.SetColumnSpan(control, columnSpan);
    }

    private void AddCarryForwardField(Grid grid, int row, int labelColumn)
    {
        var label = new TextBlock
        {
            Text = "Carry Forward",
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 10, 6)
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, labelColumn);
        grid.Children.Add(label);

        StyleShiftEntryControl(cohCarryForward, true);
        ReparentTo(cohCarryForward, grid);
        Grid.SetRow(cohCarryForward, row);
        Grid.SetColumn(cohCarryForward, labelColumn + 1);

        _cohCarryForwardSummary = new TextBlock
        {
            Text = "$0.00",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("Accent3Brush", "#FFF1C18C"),
            Margin = new Thickness(0, 4, 0, 0)
        };
    }

    private UIElement BuildCashOnHandActions()
    {
        var bar = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6, Margin = new Thickness(0, 2, 0, 14) };
        var home = CreateOperationalActionButton("\uE80F", "Go to Dashboard", Nav_SelectTab_Click, "0");
        bar.Children.Add(home);

        AddActionButton(bar, btnCohAdd, "\uE710", "Add");
        AddActionButton(bar, btnCohCorrect, "\uE70F", "Add Correction");
        AddActionButton(bar, btnCohUpdate, "\uE895", "Update Selected");
        AddActionButton(bar, btnCohDelete, "\uE74D", "Delete Selected");
        AddActionButton(bar, btnCohSetCarryForward, "\uE895", "Set Carry Forward");
        return bar;
    }

    private UIElement BuildCheckPayoutActions()
    {
        var bar = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6, Margin = new Thickness(0, 0, 0, 14) };
        bar.Children.Add(CreateOperationalActionButton("\uE80F", "Home", Nav_SelectTab_Click, "0"));
        AddActionButton(bar, btnChkAdd, "\uE710", "Add");
        AddActionButton(bar, btnChkPrint, "\uE749", "Print Check");
        AddActionButton(bar, btnChkCorrect, "\uE70F", "Add Correction");
        AddActionButton(bar, btnChkToggle, "\uE73E", "Toggle Cleared");
        AddActionButton(bar, btnChkDelete, "\uE74D", "Delete Selected");
        return bar;
    }

    private UIElement BuildCashOnHandActionSummaryRow()
    {
        var layout = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star), MinWidth = 720 });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 520 });

        var actions = BuildCashOnHandActions();
        if (actions is FrameworkElement actionElement)
            actionElement.Margin = new Thickness(0);
        Grid.SetColumn(actions, 0);
        layout.Children.Add(actions);

        var summaries = BuildCashOnHandSummaryStrip();
        if (summaries is FrameworkElement summaryElement)
            summaryElement.Margin = new Thickness(0);
        Grid.SetColumn(summaries, 2);
        layout.Children.Add(summaries);

        return layout;
    }

    private WpfButton CreateOperationalActionButton(string glyph, string label, RoutedEventHandler handler, string? commandParameter = null)
    {
        var button = new WpfButton
        {
            Height = 46,
            Margin = new Thickness(5, 0, 5, 0),
            Style = CreateShiftActionButtonStyle(4),
            Foreground = GetBrush("Accent2Brush", "#FFD49A5B")
        };
        button.Click += handler;
        if (commandParameter is not null)
            button.CommandParameter = commandParameter;
        SetIconButtonContent(button, glyph, label);
        return button;
    }

    private void AddActionButton(WpfPanel panel, WpfButton button, string glyph, string label)
    {
        ReparentTo(button, panel);
        button.Height = 46;
        button.Margin = new Thickness(5, 0, 5, 0);
        button.Style = CreateShiftActionButtonStyle(4);
        button.Foreground = GetBrush("Accent2Brush", "#FFD49A5B");
        SetIconButtonContent(button, glyph, label);
    }

    private UIElement BuildCashOnHandSummaryStrip()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var balance = BuildOperationalStatCard("Current Balance", "#FFF1C18C");
        _cohCurrentBalanceSummary = GetStatValueText(balance);
        Grid.SetColumn(balance, 0);
        grid.Children.Add(balance);

        var today = BuildOperationalStatCard("Today Added", "#FF39D06E");
        _cohTodayAddedSummary = GetStatValueText(today);
        Grid.SetColumn(today, 2);
        grid.Children.Add(today);

        var pending = BuildOperationalStatCard("Pending Payouts", "#FFFF4136");
        _cohPendingPayoutsSummary = GetStatValueText(pending);
        Grid.SetColumn(pending, 4);
        grid.Children.Add(pending);
        return grid;
    }

    private UIElement BuildOperationalGridFooter(string tag, string statusText)
    {
        var footer = new Border
        {
            Tag = tag,
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1, 0, 1, 1),
            Padding = new Thickness(14, 8, 14, 8),
            MinHeight = 52
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pager = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var pagerLabel in new[] { "|<", "<", "1", "2", ">", ">|" })
        {
            var button = new WpfButton
            {
                Content = pagerLabel,
                Width = 36,
                Height = 34,
                Margin = new Thickness(0, 0, 5, 0),
                Style = pagerLabel == "1" ? CreateFilledCopperButtonStyle(4) : CreateShiftActionButtonStyle(4),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 12,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center
            };
            if (pagerLabel == "1")
                button.Foreground = GetBrush("ButtonTextBrush", "#FF0C1118");
            pager.Children.Add(button);
        }
        pager.Children.Add(new TextBlock
        {
            Text = statusText,
            Foreground = GetBrush("MutedTextBrush", "#FF9BA8B5"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 13
        });
        Grid.SetColumn(pager, 0);
        layout.Children.Add(pager);

        var exportTools = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var refreshButton = new WpfButton
        {
            Width = 42,
            Height = 36,
            Margin = new Thickness(0, 0, 10, 0),
            Style = CreateShiftActionButtonStyle(4),
            Foreground = GetBrush("Accent2Brush", "#FFD49A5B"),
            Padding = new Thickness(0)
        };
        SetIconButtonContent(refreshButton, "\uE895", "");
        exportTools.Children.Add(refreshButton);

        var exportButton = CreateOperationalActionButton("\uE8A5", "Export to Excel", (_, _) => { });
        exportButton.Width = 150;
        exportButton.Height = 36;
        exportButton.Margin = new Thickness(0);
        exportTools.Children.Add(exportButton);
        Grid.SetColumn(exportTools, 2);
        layout.Children.Add(exportTools);

        footer.Child = layout;
        return footer;
    }

    private Border BuildOperationalStatCard(string title, string valueColor, bool money = true)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = money ? "$0.00" : "0",
            FontSize = 23,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(valueColor)),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });

        return new Border
        {
            Background = GetBrush("TileShimmerBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            MinHeight = 76,
            Child = stack
        };
    }

    private static TextBlock? GetStatValueText(Border card)
    {
        return (card.Child as StackPanel)?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault();
    }

    private void StyleOperationalGrid(DataGrid grid)
    {
        grid.RowHeight = 42;
        grid.ColumnHeaderHeight = 42;
        grid.BorderThickness = new Thickness(1);
        grid.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
        grid.Background = GetBrush("TileShimmerBrush", "#FF10243A");
        grid.RowBackground = GetBrush("PanelBrush", "#FF10243A");
        grid.AlternatingRowBackground = GetBrush("ControlBrush", "#FF132C45");
        grid.GridLinesVisibility = DataGridGridLinesVisibility.All;
        grid.HorizontalGridLinesBrush = GetBrush("BorderBrush", "#FF24445F");
        grid.VerticalGridLinesBrush = GetBrush("BorderBrush", "#FF24445F");
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        grid.CellStyle = CreateShiftGridCellStyle();
        grid.RowStyle = CreateShiftGridRowStyle();
    }

    private void ConfigureCashOnHandGridColumns()
    {
        gridCoh.Columns.Clear();
        gridCoh.Columns.Add(CreateTextColumn("Date", nameof(CashOnHandEntry.Date), new DataGridLength(165), date: true));
        gridCoh.Columns.Add(CreateTextColumn("Cash Added", nameof(CashOnHandEntry.CashAdded), new DataGridLength(165), money: true));
        gridCoh.Columns.Add(CreateTextColumn("Is Payout", nameof(CashOnHandEntry.IsPayout), new DataGridLength(130)));
        gridCoh.Columns.Add(CreateTextColumn("Payout", nameof(CashOnHandEntry.PayoutAmount), new DataGridLength(165), money: true));
        gridCoh.Columns.Add(CreateTextColumn("Vendor", "Vendor.Name", new DataGridLength(1.2, DataGridLengthUnitType.Star), minWidth: 180));
        gridCoh.Columns.Add(CreateTextColumn("Purpose", "Purpose.Name", new DataGridLength(1.2, DataGridLengthUnitType.Star), minWidth: 170));
        gridCoh.Columns.Add(CreateTextColumn("Description", nameof(CashOnHandEntry.Description), new DataGridLength(2.4, DataGridLengthUnitType.Star), minWidth: 300));
        gridCoh.Columns.Add(CreateTextColumn("Check #", nameof(CashOnHandEntry.Reference), new DataGridLength(130), minWidth: 110));
    }

    private void ConfigureCheckPayoutGridColumns()
    {
        gridChk.Columns.Clear();
        gridChk.Columns.Add(CreateTextColumn("Date", nameof(CheckPayout.Date), new DataGridLength(150), date: true));
        gridChk.Columns.Add(CreateTextColumn("Vendor", nameof(CheckPayout.VendorName), new DataGridLength(1.7, DataGridLengthUnitType.Star), minWidth: 230));
        gridChk.Columns.Add(CreateTextColumn("Description", nameof(CheckPayout.Description), new DataGridLength(2.3, DataGridLengthUnitType.Star), minWidth: 320));
        gridChk.Columns.Add(CreateTextColumn("Amount", nameof(CheckPayout.CheckAmount), new DataGridLength(150), money: true));
        gridChk.Columns.Add(CreateTextColumn("Check #", nameof(CheckPayout.CheckNumber), new DataGridLength(130), minWidth: 110));
        gridChk.Columns.Add(CreateTextColumn("Cleared", nameof(CheckPayout.Cleared), new DataGridLength(150), minWidth: 125));
    }

    private DataGridTextColumn CreateTextColumn(string header, string path, DataGridLength width, bool money = false, bool date = false, double minWidth = 100)
    {
        var binding = new System.Windows.Data.Binding(path);
        if (money)
            binding.StringFormat = "{0:C}";
        if (date)
            binding.StringFormat = "{0:MM/dd/yyyy}";

        return new DataGridTextColumn
        {
            Header = header,
            Binding = binding,
            Width = width,
            MinWidth = minWidth,
            ElementStyle = CreateShiftGridTextBlockStyle(money)
        };
    }

    private void ApplySelectedShiftCashDropShell()
    {
        try
        {
            if (RootPanel is null) return;

            foreach (var topBorder in RootPanel.Children.OfType<Border>()
                         .Where(x => DockPanel.GetDock(x) == Dock.Top)
                         .ToList())
            {
                topBorder.Visibility = Visibility.Collapsed;
            }

            if (RootPanel.Children.OfType<Grid>().FirstOrDefault() is { } contentGrid)
                contentGrid.Margin = new Thickness(14, 12, 14, 10);

            foreach (var status in RootPanel.Children.OfType<System.Windows.Controls.Primitives.StatusBar>())
                status.Background = GetBrush("PanelBrush", "#FF10243A");
        }
        catch { }
    }

    private void RestyleShiftCashDropSurface(Grid shiftInputGrid)
    {
        if (shiftInputGrid.Parent is not Border inputBorder || inputBorder.Parent is not Grid rootGrid)
            return;

        if (rootGrid.Parent is Border outerBorder)
        {
            outerBorder.Background = GetBrush("BgBrush", "#FF071827");
            outerBorder.BorderThickness = new Thickness(0);
            outerBorder.Padding = new Thickness(0);
            outerBorder.CornerRadius = new CornerRadius(0);
        }

        rootGrid.Margin = new Thickness(12, 10, 12, 8);

        inputBorder.Style = null;
        inputBorder.Background = GetBrush("TileShimmerBrush", "#FF10243A");
        inputBorder.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
        inputBorder.BorderThickness = new Thickness(1);
        inputBorder.CornerRadius = new CornerRadius(8);
        inputBorder.Padding = new Thickness(20, 14, 20, 14);
        inputBorder.Margin = new Thickness(0, 0, 0, 10);
    }

    private void AddShiftCashDropHeader(Grid shiftInputGrid)
    {
        if (shiftInputGrid.Parent is not Border inputBorder || inputBorder.Parent is not Grid rootGrid)
            return;

        if (rootGrid.Children.OfType<FrameworkElement>().Any(x => Equals(x.Tag, "ShiftCashDropSelectedHeader")))
            return;

        rootGrid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement child in rootGrid.Children)
            Grid.SetRow(child, Grid.GetRow(child) + 1);

        var header = new Grid
        {
            Tag = "ShiftCashDropSelectedHeader",
            Margin = new Thickness(0, 0, 0, 12)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = "\uE8C7",
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 30,
            Foreground = GetBrush("Accent2Brush", "#FFD49A5B"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(icon, 0);
        header.Children.Add(icon);

        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(copy, 1);
        copy.Children.Add(new TextBlock
        {
            Text = "Shift Cash Drop",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("Accent3Brush", "#FFF1C18C")
        });
        copy.Children.Add(new TextBlock
        {
            Text = "Record and track shift cash drops and register payouts.",
            FontSize = 13,
            Foreground = GetBrush("MutedTextBrush", "#FF9BA8B5"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        header.Children.Add(copy);

        var rightTools = BuildShiftHeaderStoreTools();
        Grid.SetColumn(rightTools, 2);
        header.Children.Add(rightTools);

        Grid.SetRow(header, 0);
        rootGrid.Children.Add(header);
    }

    private UIElement BuildShiftHeaderStoreTools()
    {
        var tools = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0)
        };

        tools.Children.Add(new TextBlock
        {
            Text = "Store:",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
            Margin = new Thickness(0, 0, 8, 0)
        });

        ReparentTo(cmbStore, tools);
        ApplyShiftHeaderStoreSelectorStyle();

        ReparentTo(btnStores, tools);
        btnStores.ClearValue(WpfControl.BackgroundProperty);
        btnStores.ClearValue(WpfControl.ForegroundProperty);
        btnStores.ClearValue(WpfControl.BorderBrushProperty);
        btnStores.ClearValue(WpfControl.BorderThicknessProperty);
        btnStores.ClearValue(WpfControl.TemplateProperty);
        btnStores.Width = 112;
        btnStores.MinWidth = 112;
        btnStores.Height = 42;
        btnStores.Margin = new Thickness(0);
        btnStores.Style = CreateFilledCopperButtonStyle(8);
        btnStores.Background = TryFindResource("AccentGradient") as MediaBrush ?? GetBrush("Accent2Brush", "#FFD49A5B");
        btnStores.Foreground = GetBrush("ButtonTextBrush", "#FF0C1118");
        btnStores.BorderBrush = GetBrush("Accent2Brush", "#FFD49A5B");
        btnStores.BorderThickness = new Thickness(1);
        btnStores.FontWeight = FontWeights.Bold;
        btnStores.Opacity = 1;
        btnStores.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118"));
        btnStores.IsEnabled = true;

        return tools;
    }

    private void ApplyShiftHeaderStoreSelectorStyle()
    {
        cmbStore.ClearValue(WpfControl.BackgroundProperty);
        cmbStore.ClearValue(WpfControl.ForegroundProperty);
        cmbStore.ClearValue(WpfControl.BorderBrushProperty);
        cmbStore.ClearValue(WpfControl.BorderThicknessProperty);
        cmbStore.ClearValue(WpfControl.TemplateProperty);
        cmbStore.ClearValue(WpfControl.PaddingProperty);

        cmbStore.Style = null;
        cmbStore.Width = 250;
        cmbStore.MinWidth = 250;
        cmbStore.Height = 42;
        cmbStore.Margin = new Thickness(0, 0, 12, 0);
        cmbStore.Background = GetBrush("FieldBrush", "#FFF4F1EA");
        cmbStore.Foreground = GetBrush("ButtonTextBrush", "#FF0C1118");
        cmbStore.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
        cmbStore.BorderThickness = new Thickness(1);
        cmbStore.FontWeight = FontWeights.SemiBold;
        cmbStore.Padding = new Thickness(10, 0, 10, 0);
        cmbStore.DisplayMemberPath = null;
        cmbStore.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118"));

        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        textFactory.SetValue(TextBlock.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118"));
        textFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        textFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        cmbStore.ItemTemplate = new DataTemplate { VisualTree = textFactory };

        var storeItemStyle = new Style(typeof(ComboBoxItem));
        storeItemStyle.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118")));
        storeItemStyle.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("FieldBrush", "#FFF4F1EA")));
        storeItemStyle.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(10, 6, 10, 6)));
        storeItemStyle.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118")));

        var selectedTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("Accent3Brush", "#FFF1C18C")));
        selectedTrigger.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118")));
        storeItemStyle.Triggers.Add(selectedTrigger);

        cmbStore.ItemContainerStyle = storeItemStyle;
    }

    private static void ReparentTo(UIElement element, WpfPanel newParent)
    {
        switch (element)
        {
            case null:
                return;
            case FrameworkElement { Parent: WpfPanel oldPanel }:
                oldPanel.Children.Remove(element);
                break;
            case FrameworkElement { Parent: Decorator oldDecorator }:
                oldDecorator.Child = null;
                break;
            case FrameworkElement { Parent: ContentControl oldContent } when Equals(oldContent.Content, element):
                oldContent.Content = null;
                break;
        }

        if (!newParent.Children.Contains(element))
            newParent.Children.Add(element);
    }

    private void StyleShiftEntryControl(System.Windows.Controls.Control control, bool rightAlign = false)
    {
        control.Height = 42;
        control.FontSize = 15;
        control.Margin = new Thickness(0, 5, 0, 5);
        control.Background = GetBrush("ControlBrush", "#FF0E263D");
        control.Foreground = GetBrush("TextBrush", "#FFF4F1EA");
        control.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
        control.BorderThickness = new Thickness(1);
        control.Padding = new Thickness(12, 0, 12, 0);

        if (control is System.Windows.Controls.TextBox textBox)
        {
            textBox.CaretBrush = GetBrush("Accent3Brush", "#FFF1C18C");
            textBox.SelectionBrush = GetBrush("AccentBrush", "#FFB87333");
            textBox.TextAlignment = rightAlign ? TextAlignment.Right : TextAlignment.Left;
            textBox.VerticalContentAlignment = VerticalAlignment.Center;
        }
        else if (control is System.Windows.Controls.ComboBox comboBox)
        {
            comboBox.VerticalContentAlignment = VerticalAlignment.Center;
        }
    }

    private Style CreateShiftActionButtonStyle(double radius = 6)
    {
        var style = new Style(typeof(WpfButton));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("PanelBrush", "#FF10243A")));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("Accent2Brush", "#FFD49A5B")));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, GetBrush("AccentBrush", "#FFB87333")));
        style.Setters.Add(new Setter(WpfControl.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(14, 0, 14, 0)));
        style.Setters.Add(new Setter(WpfControl.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(WpfControl.CursorProperty, System.Windows.Input.Cursors.Hand));

        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "ButtonBorder";
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfControl.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(WpfControl.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(WpfControl.BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentFactory.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(WpfControl.PaddingProperty));
        borderFactory.AppendChild(contentFactory);

        var template = new ControlTemplate(typeof(WpfButton)) { VisualTree = borderFactory };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, GetBrush("ControlBrush", "#FF16324D"), "ButtonBorder"));
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, GetBrush("Accent3Brush", "#FFF1C18C"), "ButtonBorder"));
        template.Triggers.Add(hoverTrigger);
        style.Setters.Add(new Setter(WpfControl.TemplateProperty, template));
        return style;
    }

    private Style CreateFilledCopperButtonStyle(double radius = 8)
    {
        var style = CreateShiftActionButtonStyle(radius);
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, TryFindResource("AccentGradient") as MediaBrush ?? GetBrush("Accent2Brush", "#FFD49A5B")));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("ButtonTextBrush", "#FF0C1118")));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, GetBrush("Accent2Brush", "#FFD49A5B")));
        return style;
    }

    private void SetIconButtonContent(WpfButton button, string glyph, string label)
    {
        var stack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 17,
            Foreground = GetBrush("Accent2Brush", "#FFD49A5B"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, string.IsNullOrWhiteSpace(label) ? 0 : 8, 0)
        });
        if (!string.IsNullOrWhiteSpace(label))
        {
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("Accent2Brush", "#FFD49A5B"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        button.Content = stack;
    }

    private void ArrangeShiftCashDropForm(Grid shiftInputGrid)
    {
        shiftInputGrid.RowDefinitions.Clear();
        for (var i = 0; i < 4; i++)
            shiftInputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        shiftInputGrid.ColumnDefinitions.Clear();
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star), MinWidth = 300 });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star), MinWidth = 270 });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        shiftInputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star), MinWidth = 360 });

        void MoveLabel(string text, int row, int column)
        {
            var label = shiftInputGrid.Children.OfType<TextBlock>()
                .FirstOrDefault(x => string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase));
            if (label is null) return;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.FontSize = 13;
            label.FontWeight = FontWeights.SemiBold;
            label.Foreground = GetBrush("TextBrush", "#FFF4F1EA");
            label.Margin = new Thickness(0, 6, 10, 6);
            Grid.SetRow(label, row);
            Grid.SetColumn(label, column);
            Grid.SetColumnSpan(label, 1);
        }

        void MoveControl(System.Windows.Controls.Control control, int row, int column, int columnSpan = 1, bool rightAlign = false)
        {
            StyleShiftEntryControl(control, rightAlign);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            Grid.SetColumnSpan(control, columnSpan);
        }

        var posReportLabel = shiftInputGrid.Children.OfType<TextBlock>()
            .FirstOrDefault(x => string.Equals(x.Text, "POS Report", StringComparison.OrdinalIgnoreCase));
        var posReportBox = shiftInputGrid.Children.OfType<System.Windows.Controls.TextBox>()
            .FirstOrDefault(box => box.IsReadOnly &&
                                   string.Equals(box.Text, "Upload using buttons below", StringComparison.OrdinalIgnoreCase));
        if (posReportLabel is not null) posReportLabel.Visibility = Visibility.Collapsed;
        if (posReportBox is not null) posReportBox.Visibility = Visibility.Collapsed;

        MoveLabel("Date", 0, 0);
        MoveControl(slDate, 0, 1);
        MoveLabel("Employee", 0, 3);
        MoveControl(slEmployee, 0, 4);
        MoveLabel("Cash Drop", 0, 6);
        MoveControl(slDrop, 0, 7, 3, true);

        MoveLabel("Shift #", 1, 0);
        MoveControl(slShiftNo, 1, 1);

        MoveLabel("Cash Total", 2, 0);
        MoveControl(slCash, 2, 1, rightAlign: true);
        MoveLabel("Net Sales", 2, 3);
        MoveControl(slNetSales, 2, 4, rightAlign: true);
        MoveLabel("Register Payout", 2, 6);
        MoveControl(slRegPayout, 2, 7, rightAlign: true);
        var reasonLabel = shiftInputGrid.Children.OfType<TextBlock>()
            .FirstOrDefault(x => string.Equals(x.Text, "Payout Reason", StringComparison.OrdinalIgnoreCase));
        if (reasonLabel is not null)
        {
            Grid.SetColumn(reasonLabel, 9);
            Grid.SetRow(reasonLabel, 2);
        }
        if (_slPayoutReason is not null)
        {
            StyleShiftEntryControl(_slPayoutReason);
            Grid.SetRow(_slPayoutReason, 3);
            Grid.SetColumn(_slPayoutReason, 9);
            Grid.SetColumnSpan(_slPayoutReason, 1);
        }

        MoveLabel("Card Total", 3, 0);
        MoveControl(slCard, 3, 1, rightAlign: true);
        MoveLabel("Tax Total", 3, 3);
        MoveControl(slTax, 3, 4, rightAlign: true);
    }

    private void ArrangeShiftCashDropActions()
    {
        if (btnImportHint.Parent is not System.Windows.Controls.Primitives.UniformGrid actionsGrid)
            return;

        if (actionsGrid.Parent is StackPanel actionPanel && actionPanel.Parent is Border actionBorder)
        {
            var dashboardButton = actionPanel.Children.OfType<WpfButton>().FirstOrDefault();
            if (dashboardButton is null)
                return;

            var strip = new Grid
            {
                Tag = "ShiftCashDropActionStrip",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.75, GridUnitType.Star) });
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });

            actionPanel.Children.Clear();
            actionsGrid.Children.Clear();

            var orderedButtons = new[]
            {
                dashboardButton,
                btnImportHint,
                btnShiftClearImport,
                btnShiftCorrect,
                btnShiftAdd,
                btnShiftUpdate,
                btnShiftDelete
            };

            var labels = new[]
            {
                ("\uE80F", "Go to Dashboard"),
                ("\uE898", "Upload POS Report"),
                ("\uE74D", "Clear Imported"),
                ("\uE70F", "Add Correction"),
                ("\uE710", "Add"),
                ("\uE72C", "Update Selected"),
                ("\uE74D", "Delete Selected")
            };

            for (var i = 0; i < orderedButtons.Length; i++)
            {
                var button = orderedButtons[i];
                button.Style = CreateShiftActionButtonStyle(6);
                button.Height = 42;
                button.MinWidth = 0;
                button.Margin = new Thickness(i == orderedButtons.Length - 1 ? 0 : 0, 0, i == orderedButtons.Length - 1 ? 0 : 10, 0);
                button.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                button.VerticalAlignment = VerticalAlignment.Center;
                SetIconButtonContent(button, labels[i].Item1, labels[i].Item2);
                Grid.SetColumn(button, i);
                strip.Children.Add(button);
            }

            actionBorder.Child = strip;
            actionBorder.Background = MediaBrushes.Transparent;
            actionBorder.BorderThickness = new Thickness(0);
            actionBorder.Padding = new Thickness(0);
            actionBorder.Margin = new Thickness(0, 0, 0, 16);
            actionBorder.CornerRadius = new CornerRadius(0);
        }
    }

    private void ArrangeShiftCashDropGrid()
    {
        if (gridShift.Parent is Grid rootGrid &&
            !rootGrid.Children.OfType<FrameworkElement>().Any(x => Equals(x.Tag, "ShiftCashDropGridFooter")))
        {
            var gridRow = Grid.GetRow(gridShift);
            if (rootGrid.RowDefinitions.Count <= gridRow + 1)
                rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var footer = BuildShiftCashDropGridFooter();
            Grid.SetRow(footer, gridRow + 1);
            rootGrid.Children.Add(footer);
        }

        if (!gridShift.Columns.Any(c => string.Equals(c.Header?.ToString(), "Gross Sales", StringComparison.OrdinalIgnoreCase)))
        {
            var taxIndex = gridShift.Columns
                .Select((column, index) => new { column, index })
                .FirstOrDefault(x => string.Equals(x.column.Header?.ToString(), "Tax", StringComparison.OrdinalIgnoreCase))
                ?.index;

            var grossSalesColumn = new DataGridTextColumn
            {
                Header = "Gross Sales",
                Binding = new System.Windows.Data.Binding(nameof(ShiftLogEntry.GrossSales))
                {
                    StringFormat = "{0:C}"
                },
                Width = new DataGridLength(130),
                MinWidth = 120
            };
            gridShift.Columns.Insert(taxIndex.HasValue ? taxIndex.Value + 1 : gridShift.Columns.Count, grossSalesColumn);
        }

        gridShift.RowHeight = 40;
        gridShift.ColumnHeaderHeight = 40;
        gridShift.CanUserResizeColumns = true;
        gridShift.BorderThickness = new Thickness(1);
        gridShift.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
        gridShift.Background = GetBrush("TileShimmerBrush", "#FF10243A");
        gridShift.RowBackground = GetBrush("PanelBrush", "#FF10243A");
        gridShift.AlternatingRowBackground = GetBrush("ControlBrush", "#FF132C45");
        gridShift.AlternationCount = 2;
        gridShift.SelectionUnit = DataGridSelectionUnit.FullRow;
        gridShift.GridLinesVisibility = DataGridGridLinesVisibility.All;
        gridShift.HorizontalGridLinesBrush = GetBrush("BorderBrush", "#FF24445F");
        gridShift.VerticalGridLinesBrush = GetBrush("BorderBrush", "#FF24445F");
        gridShift.CellStyle = CreateShiftGridCellStyle();
        gridShift.RowStyle = CreateShiftGridRowStyle();

        foreach (var column in gridShift.Columns)
        {
            if (column is DataGridTextColumn textColumn)
            {
                var header = column.Header?.ToString() ?? string.Empty;
                textColumn.ElementStyle = CreateShiftGridTextBlockStyle(IsShiftGridMoneyColumn(header));
                textColumn.EditingElementStyle = CreateShiftGridEditingTextBoxStyle(IsShiftGridMoneyColumn(header));
            }

            if (string.Equals(column.Header?.ToString(), "Employee", StringComparison.OrdinalIgnoreCase))
                column.Width = new DataGridLength(180);
            else if (string.Equals(column.Header?.ToString(), "Date", StringComparison.OrdinalIgnoreCase))
                column.Width = new DataGridLength(120);
            else if (string.Equals(column.Header?.ToString(), "Shift #", StringComparison.OrdinalIgnoreCase))
                column.Width = new DataGridLength(90);
            else if (string.Equals(column.Header?.ToString(), "Cash", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(column.Header?.ToString(), "Card", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(column.Header?.ToString(), "Net", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(column.Header?.ToString(), "Tax", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(column.Header?.ToString(), "Gross Sales", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(column.Header?.ToString(), "Drop", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(column.Header?.ToString(), "Payout", StringComparison.OrdinalIgnoreCase))
                column.Width = new DataGridLength(120);
            else if (string.Equals(column.Header?.ToString(), "Payout Reason", StringComparison.OrdinalIgnoreCase))
                column.Width = new DataGridLength(230);
            else if (string.Equals(column.Header?.ToString(), "Variance", StringComparison.OrdinalIgnoreCase))
                column.Width = new DataGridLength(120);
        }
    }

    private Style CreateShiftGridCellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, MediaBrushes.Transparent));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("TextBrush", "#FFF4F1EA")));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, GetBrush("BorderBrush", "#FF24445F")));
        style.Setters.Add(new Setter(WpfControl.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        style.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(WpfControl.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(WpfControl.VerticalContentAlignmentProperty, VerticalAlignment.Center));

        var selectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("ControlBrush", "#FF132C45")));
        selectedTrigger.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("TextBrush", "#FFF4F1EA")));
        selectedTrigger.Setters.Add(new Setter(WpfControl.BorderBrushProperty, GetBrush("AccentBrush", "#FFB87333")));
        style.Triggers.Add(selectedTrigger);

        return style;
    }

    private Style CreateShiftGridRowStyle()
    {
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("PanelBrush", "#FF10243A")));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("TextBrush", "#FFF4F1EA")));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, GetBrush("BorderBrush", "#FF24445F")));
        style.Setters.Add(new Setter(WpfControl.BorderThicknessProperty, new Thickness(0)));

        var selectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("ControlBrush", "#FF132C45")));
        selectedTrigger.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("TextBrush", "#FFF4F1EA")));
        style.Triggers.Add(selectedTrigger);

        return style;
    }

    private Style CreateShiftGridTextBlockStyle(bool rightAlign)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, GetBrush("TextBrush", "#FFF4F1EA")));
        style.Setters.Add(new Setter(TextBlock.BackgroundProperty, MediaBrushes.Transparent));
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, rightAlign ? TextAlignment.Right : TextAlignment.Left));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        return style;
    }

    private Style CreateShiftGridEditingTextBoxStyle(bool rightAlign)
    {
        var style = new Style(typeof(System.Windows.Controls.TextBox));
        style.Setters.Add(new Setter(WpfControl.ForegroundProperty, GetBrush("TextBrush", "#FFF4F1EA")));
        style.Setters.Add(new Setter(WpfControl.BackgroundProperty, GetBrush("ControlBrush", "#FF0E263D")));
        style.Setters.Add(new Setter(WpfControl.BorderBrushProperty, GetBrush("AccentBrush", "#FFB87333")));
        style.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(10, 0, 10, 0)));
        style.Setters.Add(new Setter(System.Windows.Controls.TextBox.TextAlignmentProperty, rightAlign ? TextAlignment.Right : TextAlignment.Left));
        return style;
    }

    private static bool IsShiftGridMoneyColumn(string header)
    {
        return string.Equals(header, "Cash", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(header, "Card", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(header, "Net", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(header, "Tax", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(header, "Gross Sales", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(header, "Drop", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(header, "Payout", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(header, "Variance", StringComparison.OrdinalIgnoreCase);
    }

    private UIElement BuildShiftCashDropGridFooter()
    {
        var footer = new Border
        {
            Tag = "ShiftCashDropGridFooter",
            Background = GetBrush("PanelBrush", "#FF10243A"),
            BorderBrush = GetBrush("AccentBrush", "#FFB87333"),
            BorderThickness = new Thickness(1, 0, 1, 1),
            Padding = new Thickness(14, 8, 14, 8),
            MinHeight = 52
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pager = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var pagerLabels = new[] { "<<", "<", "1", "2", ">", ">>" };
        foreach (var pagerLabel in pagerLabels)
        {
            var button = new WpfButton
            {
                Content = pagerLabel,
                Width = 36,
                Height = 34,
                Margin = new Thickness(0, 0, 5, 0),
                Style = CreateShiftActionButtonStyle(4),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                FontSize = 12,
                Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center
            };
            if (pagerLabel == "1")
            {
                button.Style = CreateFilledCopperButtonStyle(4);
                button.Foreground = GetBrush("ButtonTextBrush", "#FF0C1118");
            }
            pager.Children.Add(button);
        }
        pager.Children.Add(new TextBlock
        {
            Text = "Showing 1 to 8 of 14 entries",
            Foreground = GetBrush("MutedTextBrush", "#FF9BA8B5"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 13
        });
        Grid.SetColumn(pager, 0);
        layout.Children.Add(pager);

        var exportTools = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var refreshButton = new WpfButton
        {
            Width = 42,
            Height = 36,
            Margin = new Thickness(0, 0, 10, 0),
            Style = CreateShiftActionButtonStyle(4)
        };
        SetIconButtonContent(refreshButton, "\uE72C", "");
        var exportButton = new WpfButton
        {
            Height = 36,
            MinWidth = 150,
            Style = CreateShiftActionButtonStyle(4)
        };
        SetIconButtonContent(exportButton, "\uE8A5", "Export to Excel");
        exportTools.Children.Add(refreshButton);
        exportTools.Children.Add(exportButton);
        Grid.SetColumn(exportTools, 2);
        layout.Children.Add(exportTools);

        footer.Child = layout;
        return footer;
    }

    private void InstallSidebarNavigation()
    {
        try
        {
            if (RootPanel is null) return;
            if (RootPanel.Children.OfType<FrameworkElement>().Any(x => Equals(x.Tag, "PrimarySidebarNavigation"))) return;

            var contentIndex = -1;
            for (var i = 0; i < RootPanel.Children.Count; i++)
            {
                if (RootPanel.Children[i] is Grid)
                {
                    contentIndex = i;
                    break;
                }
            }

            if (contentIndex < 0) return;

            var sidebar = new Border
            {
                Tag = "PrimarySidebarNavigation",
                Width = 250,
                MinWidth = 232,
                Padding = new Thickness(14, 16, 14, 12),
                Background = GetBrush("SidebarBrush", "#FF081A2A"),
                BorderBrush = GetBrush("BorderBrush", "#FF24445F"),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };

            DockPanel.SetDock(sidebar, Dock.Left);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var sidebarLayout = new DockPanel { LastChildFill = true };
            var panel = new StackPanel();
            panel.Children.Add(new WpfImage
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(AppLogoAsset, UriKind.Absolute)),
                Height = 82,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(new TextBlock
            {
                Text = AppDisplayName,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = GetBrush("Accent3Brush", "#FFF1C18C"),
                Margin = new Thickness(4, 0, 0, 3)
            });
            panel.Children.Add(new TextBlock
            {
                Text = _session.IsAdmin ? "ADMIN VIEW" : "MANAGER VIEW",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("MutedTextBrush", "#FF9BA8B5"),
                Margin = new Thickness(4, 0, 0, 16)
            });

            AddSidebarSection(panel, "Main");
            panel.Children.Add(CreateSidebarNavButton("Dashboard", 0));
            panel.Children.Add(CreateSidebarNavButton("Shift Cash Drop", 1));
            panel.Children.Add(CreateSidebarNavButton("Cash On Hand", 2));
            panel.Children.Add(CreateSidebarNavButton("Check Payout", 3));

            AddSidebarSection(panel, "Operations");
            panel.Children.Add(CreateSidebarNavButton("Operations Hub", _operationsHubTabIndex));
            panel.Children.Add(CreateSidebarNavButton("Vendors & Purposes", 4, adminOnly: true, child: true));
            panel.Children.Add(CreateSidebarNavButton("Purchases", 5, adminOnly: true, child: true));
            panel.Children.Add(CreateSidebarNavButton("Bank Statement", 6, adminOnly: true, child: true));
            panel.Children.Add(CreateSidebarNavButton("Product Costs", 7, child: true));
            panel.Children.Add(CreateSidebarNavButton("Price Alerts", 8, child: true));
            panel.Children.Add(CreateSidebarNavButton("Profit & Loss", 9, adminOnly: true, child: true));
            panel.Children.Add(CreateSidebarActionButton("Reports", Reports_Click, adminOnly: true, child: true));

            AddSidebarSection(panel, "Admin", adminOnly: true);
            panel.Children.Add(CreateSidebarActionButton("Stores", ManageStores_Click, adminOnly: true));
            panel.Children.Add(CreateSidebarActionButton("User Accounts", Users_Click, adminOnly: true));
            panel.Children.Add(CreateSidebarActionButton("Database Settings", DatabaseSettings_Click, adminOnly: true));

            var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            footer.Children.Add(new Border
            {
                Height = 1,
                Background = GetBrush("BorderBrush", "#FF24445F"),
                Margin = new Thickness(-14, 0, -14, 10)
            });
            footer.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_session.DisplayName) ? _session.Username : _session.DisplayName,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
                Margin = new Thickness(4, 0, 0, 2)
            });
            footer.Children.Add(new TextBlock
            {
                Text = _session.IsAdmin ? "System Administrator" : "Manager",
                FontSize = 11,
                Foreground = GetBrush("MutedTextBrush", "#FF9BA8B5"),
                Margin = new Thickness(4, 0, 0, 12)
            });
            footer.Children.Add(new TextBlock
            {
                Text = "Version: 2.0.0.0",
                FontSize = 13,
                Foreground = GetBrush("TextBrush", "#FFF4F1EA"),
                Margin = new Thickness(4, 0, 0, 0)
            });

            scroll.Content = panel;
            sidebarLayout.Children.Add(footer);
            sidebarLayout.Children.Add(scroll);
            sidebar.Child = sidebarLayout;
            RootPanel.Children.Insert(contentIndex, sidebar);

            ApplyNavyCopperShell();
            CollapseDashboardShortcutPanel();
            ApplySidebarRoleVisibility();
            UpdateSidebarSelection();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Sidebar navigation init: {ex.Message}");
        }
    }

    private void ApplyNavyCopperShell()
    {
        try
        {
            foreach (var menu in RootPanel.Children.OfType<Menu>())
            {
                menu.Background = GetBrush("SidebarBrush", "#FF081A2A");
                menu.Foreground = GetBrush("Accent3Brush", "#FFF1C18C");
                ApplyReadableMenuColors(menu);
            }

            foreach (var border in RootPanel.Children.OfType<Border>())
            {
                if (DockPanel.GetDock(border) == Dock.Top)
                {
                    border.Background = TryFindResource("AccentGradient") as MediaBrush ?? GetBrush("PanelBrush", "#FF10243A");
                    border.BorderBrush = GetBrush("BorderBrush", "#FF24445F");
                }
            }
        }
        catch { }
    }

    private void RefreshLegacyThemeBrushes()
    {
        try
        {
            ReplaceLegacyBrushes(this);
            foreach (var menu in RootPanel.Children.OfType<Menu>())
                ApplyReadableMenuColors(menu);
        }
        catch { }
    }

    private void ApplyReadableMenuColors(Menu menu)
    {
        menu.Background = GetBrush("SidebarBrush", "#FF081A2A");
        menu.Foreground = GetBrush("Accent3Brush", "#FFF1C18C");

        foreach (var item in menu.Items.OfType<MenuItem>())
            ApplyReadableMenuItemColors(item, topLevel: true);
    }

    private void ApplyReadableMenuItemColors(MenuItem item, bool topLevel)
    {
        var foreground = topLevel ? GetBrush("Accent3Brush", "#FFF1C18C") : MediaBrushes.Black;
        item.Foreground = foreground;
        item.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, foreground);
        item.Background = topLevel ? MediaBrushes.Transparent : MediaBrushes.White;
        item.BorderBrush = topLevel ? GetBrush("AccentBrush", "#FFB87333") : new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString("#FFE2E8F0"));
        item.FontWeight = topLevel ? FontWeights.Normal : FontWeights.SemiBold;
        item.Opacity = 1;
        ForceMenuHeaderForeground(item, foreground);

        if (!topLevel)
        {
            item.Resources[System.Windows.SystemColors.HighlightBrushKey] = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString("#FFC7EAF8"));
            item.Resources[System.Windows.SystemColors.HighlightTextBrushKey] = MediaBrushes.Black;
            item.Resources[System.Windows.SystemColors.MenuTextBrushKey] = MediaBrushes.Black;
        }

        if (_readableMenuHookedItems.Add(item))
        {
            item.SubmenuOpened += (_, _) =>
            {
                foreach (var child in item.Items.OfType<MenuItem>())
                    ApplyReadableMenuItemColors(child, topLevel: false);
            };
        }

        foreach (var child in item.Items.OfType<MenuItem>())
            ApplyReadableMenuItemColors(child, topLevel: false);
    }

    private static void ForceMenuHeaderForeground(MenuItem item, MediaBrush foreground)
    {
        switch (item.Header)
        {
            case string text:
                item.Header = new TextBlock
                {
                    Text = text,
                    Foreground = foreground,
                    FontWeight = item.FontWeight,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = true
                };
                break;
            case TextBlock block:
                block.Foreground = foreground;
                block.FontWeight = item.FontWeight;
                block.IsEnabled = true;
                break;
        }
    }

    private void ReplaceLegacyBrushes(DependencyObject root)
    {
        if (root is Border border)
        {
            if (IsColor(border.Background, "#FF0B0B0F") || IsColor(border.Background, "#FF121218") || IsColor(border.Background, "#FF13131A"))
                border.Background = GetBrush("PanelBrush", "#FF10243A");

            if (IsColor(border.BorderBrush, "#FFD4AF37"))
                border.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
        }
        else if (root is TextBlock textBlock)
        {
            if (IsColor(textBlock.Foreground, "#FFD4AF37"))
                textBlock.Foreground = GetBrush("Accent3Brush", "#FFF1C18C");
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            ReplaceLegacyBrushes(VisualTreeHelper.GetChild(root, i));
    }

    private static bool IsColor(MediaBrush? brush, string color)
    {
        if (brush is not SolidColorBrush solid) return false;
        return solid.Color == (MediaColor)MediaColorConverter.ConvertFromString(color);
    }

    private void AddSidebarSection(WpfPanel panel, string title, bool adminOnly = false)
    {
        var label = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = GetBrush("HintTextBrush", "#FF748495"),
            Margin = new Thickness(4, 14, 0, 6)
        };
        if (adminOnly) _adminOnlySidebarElements.Add(label);
        panel.Children.Add(label);
    }

    private WpfButton CreateSidebarNavButton(string text, int tabIndex, bool adminOnly = false, bool child = false)
    {
        var button = CreateSidebarButton(text, adminOnly, child);
        button.CommandParameter = tabIndex;
        button.Click += Nav_SelectTab_Click;
        _sidebarTabButtons[tabIndex] = button;
        return button;
    }

    private WpfButton CreateSidebarActionButton(string text, RoutedEventHandler handler, bool adminOnly = false, bool child = false)
    {
        var button = CreateSidebarButton(text, adminOnly, child);
        button.Click += handler;
        return button;
    }

    private WpfButton CreateSidebarButton(string text, bool adminOnly, bool child = false)
    {
        var button = new WpfButton
        {
            Style = TryFindResource("SidebarNavButton") as Style,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Content = CreateSidebarButtonContent(text, child)
        };

        if (child)
        {
            button.MinHeight = 34;
            button.Margin = new Thickness(18, 1, 0, 1);
            button.Padding = new Thickness(10, 6, 10, 6);
        }

        if (button.Style is null)
        {
            button.MinHeight = child ? 34 : 38;
            button.Padding = child ? new Thickness(10, 6, 10, 6) : new Thickness(12, 8, 12, 8);
            button.Margin = child ? new Thickness(18, 1, 0, 1) : new Thickness(0, 2, 0, 2);
            button.Background = MediaBrushes.Transparent;
            button.Foreground = GetBrush("TextBrush", "#FFF4F1EA");
            button.BorderBrush = MediaBrushes.Transparent;
        }

        if (adminOnly) _adminOnlySidebarElements.Add(button);
        return button;
    }

    private UIElement CreateSidebarButtonContent(string text, bool child = false)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var icon = new TextBlock
        {
            Text = GetSidebarGlyph(text),
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = child ? 15 : 18,
            Width = child ? 22 : 26,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        icon.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(WpfButton), 1)
        });

        var label = new TextBlock
        {
            Text = text,
            FontSize = child ? 11 : 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(WpfButton), 1)
        });

        row.Children.Add(icon);
        row.Children.Add(label);
        return row;
    }

    private static string GetSidebarGlyph(string text) => text switch
    {
        "Dashboard" => "\uE80F",
        "Shift Cash Drop" => "\uE8C7",
        "Cash On Hand" => "\uE8D7",
        "Check Payout" => "\uE8A1",
        "Vendors & Purposes" => "\uE716",
        "Purchases" => "\uE7BF",
        "Bank Statement" => "\uE825",
        "Product Costs" => "\uE8B7",
        "Price Alerts" => "\uE7BA",
        "Profit & Loss" => "\uE9D9",
        "Reports" => "\uE9F9",
        "Stores" => "\uE719",
        "User Accounts" => "\uE716",
        "Database Settings" => "\uE1D2",
        _ => "\uE10F"
    };

    private MediaBrush GetBrush(string resourceKey, string fallback)
    {
        if (TryFindResource(resourceKey) is MediaBrush brush) return brush;
        return new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(fallback));
    }

    private void ApplySidebarRoleVisibility()
    {
        var visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in _adminOnlySidebarElements)
            element.Visibility = visibility;
    }

    private void UpdateSidebarSelection()
    {
        try
        {
            if (tabsMain is null) return;

            if (_activeSidebarButton is not null)
            {
                _activeSidebarButton.Background = GetBrush("SidebarNavBrush", "#0010243A");
                _activeSidebarButton.BorderBrush = MediaBrushes.Transparent;
                _activeSidebarButton.Foreground = GetBrush("TextBrush", "#FFF4F1EA");
            }

            if (_sidebarTabButtons.TryGetValue(tabsMain.SelectedIndex, out var selected))
            {
                selected.Background = GetBrush("SidebarNavActiveBrush", "#22D49A5B");
                selected.BorderBrush = GetBrush("AccentBrush", "#FFB87333");
                selected.Foreground = GetBrush("Accent3Brush", "#FFF1C18C");
                _activeSidebarButton = selected;
            }
        }
        catch { }
    }

    private void CollapseDashboardShortcutPanel()
    {
        try
        {
            if (tabsMain?.Items.Count <= 0) return;
            if (tabsMain.Items[0] is not TabItem dashboardTab) return;
            if (dashboardTab.Content is not Border dashboardBorder) return;
            if (dashboardBorder.Child is not Grid dashboardGrid) return;

            foreach (var child in dashboardGrid.Children.OfType<Border>())
            {
                if (Grid.GetRow(child) == 1)
                {
                    child.Visibility = Visibility.Collapsed;
                    break;
                }
            }
        }
        catch { }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        // Theme is locked permanently to the HB navy/copper brand.
        // (Menu is hidden, but keep handler to avoid breaking older XAML.)
        System.Windows.MessageBox.Show(this,
            "Theme is locked to the HB navy/copper brand in this build.",
            "Theme",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private bool _refreshingDashboard;
    
    private async void DashPeriod_Changed(object sender, SelectionChangedEventArgs e)
    {
        // When period changes, refresh dashboard tiles/charts.
        if (!IsLoaded) return;
        if (_refreshingDashboard) return;
        _refreshingDashboard = true;
        try
        {
            await RefreshDashboardAsync();
        }
        finally
        {
            _refreshingDashboard = false;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        lblDbPath.Text = $"DB: {_paths.DatabasePath}";

        // Theme is locked; do not apply persisted accents.

        // Dashboard defaults - removed from header, now shown on dashboard only
        // lblCashBalance.Text = "Cash On Hand Balance: $0.00";
        // lblUnclearedChecks.Text = "Uncleared Checks Total: $0.00";

        cohIsPayout.ItemsSource = new List<string> { "No", "Yes" };
        cohIsPayout.SelectedIndex = 0;

        // Defaults
        var today = DateTime.Today;
        slDate.SelectedDate = today;
        cohDate.SelectedDate = today;
        chkDate.SelectedDate = today;
        invDate.SelectedDate = today;

        // Dashboard period selector (Month + Year)
        try
        {
            var years = Enumerable.Range(today.Year - 5, 6).Reverse().ToList();
            cmbDashYear.ItemsSource = years;
            cmbDashYear.SelectedItem = today.Year;

            var months = Enumerable.Range(1, 12)
                .Select(m => new KeyValuePair<int, string>(m, new DateTime(2000, m, 1).ToString("MMMM")))
                .ToList();
            cmbDashMonth.ItemsSource = months;
            cmbDashMonth.DisplayMemberPath = "Value";
            cmbDashMonth.SelectedValuePath = "Key";
            cmbDashMonth.SelectedValue = today.Month;
        }
        catch { }

        gridInvoiceLines.ItemsSource = _invoiceLines;
        ClearInvoiceForm();

        // P&L month/year selectors
        try
        {
            var plYears = Enumerable.Range(today.Year - 5, 6).Reverse().ToList();
            cmbPlYear.ItemsSource = plYears;
            cmbPlYear.SelectedItem = today.Year;

            var plMonths = Enumerable.Range(1, 12)
                .Select(m => new KeyValuePair<int, string>(m, new DateTime(2000, m, 1).ToString("MMMM")))
                .ToList();
            cmbPlMonth.ItemsSource = plMonths;
            cmbPlMonth.DisplayMemberPath = "Value";
            cmbPlMonth.SelectedValuePath = "Key";
            cmbPlMonth.SelectedValue = today.Month;
        }
        catch { }

        // Auto-create any missing tables in the connected database.
        // This ensures new/empty store databases get the full schema on first connect.
        try
        {
            using var schemaDb = CreateDb();
            var connStr = schemaDb.Database.GetConnectionString();
            if (!string.IsNullOrEmpty(connStr))
                await DatabaseSchemaService.EnsureSchemaAsync(connStr);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Schema check on startup: {ex.Message}");
        }

        await LoadSettingsAsync();
        await LoadStoresAsync();
        await LoadLookupsAsync();
        await LoadDataAsync();
        await LoadPurchasesModuleAsync();

        ApplyRolePermissions();

        // Default cursor: Cash Drop
        _ = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                slDrop.Focus();
                slDrop.SelectAll();
            }
            catch { }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void ApplyRolePermissions()
    {
        // Owner/Admin has full access. Managers can add entries + create corrections, but cannot update/delete.
        var isAdmin = _session.IsAdmin;

        miUsers.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        miReports.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        miBackup.IsEnabled = isAdmin;

        // Shift
        btnShiftUpdate.IsEnabled = isAdmin;
        btnShiftDelete.IsEnabled = isAdmin;

        // Cash
        btnCohUpdate.IsEnabled = isAdmin;
        btnCohDelete.IsEnabled = isAdmin;

        // Check
        btnChkToggle.IsEnabled = isAdmin;
        btnChkDelete.IsEnabled = isAdmin;

        // Lists
        btnVendorAdd.IsEnabled = isAdmin;
        btnVendorDelete.IsEnabled = isAdmin;
        btnPurposeAdd.IsEnabled = isAdmin;
        btnPurposeDelete.IsEnabled = isAdmin;

        // Stores
        btnStores.IsEnabled = isAdmin;

        // Purchases - Admin/Owner only (hide from managers)
        btnInvUpdate.IsEnabled = isAdmin;
        btnInvDelete.IsEnabled = isAdmin;
        
        // Only hide Purchases and P&L sections from managers
        if (btnNavPurchases is not null) btnNavPurchases.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        // Bank Statement — admin only (nav button only, tab header always hidden)
        if (btnNavBankStatement is not null) btnNavBankStatement.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
        // Tab header stays collapsed — navigation is via dashboard button only
        if (btnNavProfitLoss is not null) btnNavProfitLoss.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

        // Alerts
        btnAlertsDelete.IsEnabled = isAdmin;

        ApplySidebarRoleVisibility();
    }

    private async Task LoadSettingsAsync()
    {
        var s = await _settingsService.GetSettingsAsync();
        // Prefer the store selected at login, then fall back to settings
        if (_session.LastStoreId > 0)
            _currentStoreId = _session.LastStoreId;
        else
            _currentStoreId = s.LastStoreId > 0 ? s.LastStoreId : s.DefaultStoreId;
        lblStore.Text = "";
    }

    private async Task LoadStoresAsync()
    {
        _loadingStores = true;
        try
        {
            List<Store> stores;
            try
            {
                stores = await CreateDb().Stores.AsNoTracking()
                    .Where(x => x.IsActive)
                    .OrderBy(x => x.Name)
                    .ToListAsync();

                if (stores.Count == 0)
                    stores = await CreateDb().Stores.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
            {
                System.Diagnostics.Debug.WriteLine($"Stores error {ex.Number} in LoadStoresAsync: {ex.Message}");
                var fs = await _settingsService.GetSettingsAsync();
                stores = new List<Store>
                {
                    new Store
                    {
                        Id = fs.DefaultStoreId > 0 ? fs.DefaultStoreId : 1,
                        Name = string.IsNullOrWhiteSpace(fs.StoreName) ? "Store 1" : fs.StoreName,
                        Address = fs.StoreAddress ?? "",
                        IsActive = true
                    }
                };
            }

            // Also load stores from store_connections.json (multi-database stores)
            if (_storeConnService != null)
            {
                var savedConnections = StoreManagerWindow.LoadAllStoreConnections();
                foreach (var kvp in savedConnections)
                {
                    if (!int.TryParse(kvp.Key, out var remoteStoreId)) continue;
                    if (stores.Any(s => s.Id == remoteStoreId)) continue;

                    try
                    {
                        using var remoteDb = _storeConnService.CreateDbContext(remoteStoreId);
                        var remoteStore = await remoteDb.Stores.AsNoTracking().FirstOrDefaultAsync();
                        if (remoteStore != null)
                        {
                            remoteStore.Id = remoteStoreId; // Use the main DB registry Id
                            stores.Add(remoteStore);
                        }
                    }
                    catch { }
                }
                stores = stores.OrderBy(s => s.Name).ToList();
            }

            // Filter stores: non-admin users only see stores where they have an account
            if (!_session.IsAdmin)
            {
                var username = (_session.Username ?? "").ToLowerInvariant();
                var accessibleStoreIds = new List<int>();

                foreach (var store in stores)
                {
                    try
                    {
                        var savedConns = StoreManagerWindow.LoadAllStoreConnections();
                        var sid = store.Id.ToString();

                        if (savedConns.TryGetValue(sid, out var connStr))
                        {
                            // Remote store — check directly
                            var ob = new DbContextOptionsBuilder<AppDbContext>();
                            ob.UseSqlServer(connStr);
                            using var checkDb = new AppDbContext(ob.Options);
                            var exists = await checkDb.Users.AsNoTracking()
                                .AnyAsync(u => u.IsActive && u.Username.ToLower() == username);
                            if (exists) accessibleStoreIds.Add(store.Id);
                        }
                        else
                        {
                            // Default DB store — check via CreateDb
                            using var checkDb = CreateDb();
                            var exists = await checkDb.Users.AsNoTracking()
                                .AnyAsync(u => u.IsActive && u.Username.ToLower() == username);
                            if (exists) accessibleStoreIds.Add(store.Id);
                        }
                    }
                    catch
                    {
                        // If we can't check, include it (fail open for owner stores)
                        accessibleStoreIds.Add(store.Id);
                    }
                }

                stores = stores.Where(s => accessibleStoreIds.Contains(s.Id)).ToList();
            }

            cmbStore.ItemsSource = stores;
            cmbStore.SelectedValuePath = "Id";
            ApplyShiftHeaderStoreSelectorStyle();

            // Determine which store to select — prefer session (login selection)
            var targetId = _currentStoreId;
            if (_session.LastStoreId > 0 && stores.Any(s => s.Id == _session.LastStoreId))
                targetId = _session.LastStoreId;
            if (targetId <= 0 || !stores.Any(s => s.Id == targetId))
                targetId = stores.FirstOrDefault()?.Id ?? 1;

            cmbStore.SelectedValue = targetId;
            _currentStoreId = targetId;
            UpdateOperationalStoreLabels();

            // Switch StoreConnectionService to the selected store's database
            if (_storeConnService != null)
            {
                _storeConnService.CurrentStoreId = targetId;

                // If the selected store has a custom connection, resolve its actual StoreId
                if (_storeConnService.HasCustomConnection(targetId))
                {
                    try
                    {
                        using var remoteDb = _storeConnService.CreateDbContext(targetId);
                        var remoteStore = await remoteDb.Stores.AsNoTracking().FirstOrDefaultAsync();
                        if (remoteStore != null)
                            _currentStoreId = remoteStore.Id;
                    }
                    catch { }
                }
            }

            // Update the DB path label to reflect actual database in use
            if (_storeConnService != null && _storeConnService.HasCustomConnection(targetId))
            {
                try
                {
                    using var checkDb = _storeConnService.CreateDbContext(targetId);
                    var connStr = checkDb.Database.GetConnectionString() ?? "";
                    lblDbPath.Text = $"DB: {connStr}";
                }
                catch { }
            }
        }
        finally
        {
            _loadingStores = false;
        }

        // Wire the store-specific database into AuthService so User Accounts window uses correct store
        try
        {
            var authSvc = ((App)System.Windows.Application.Current).Services.GetRequiredService<IAuthService>();
            if (authSvc is ManagerPaperworkSystem.Data.Services.AuthService concreteAuth)
                concreteAuth.SetStoreDbCreator(() => CreateDb());
        }
        catch { /* DI not ready yet */ }

        await UpdateStoreHeaderAsync();
    }

    private async Task UpdateStoreHeaderAsync()
    {
        try
        {
            // Try current DB (which may be the store-specific one via CreateDb)
        var store = await CreateDb().Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _currentStoreId);

        // If not found, try the first store in the current DB (for remote databases where StoreId=1)
        if (store is null)
            store = await CreateDb().Stores.AsNoTracking().FirstOrDefaultAsync();

        if (store is not null)
        {
            lblStore.Text = $"{store.Name}  •  {store.Address}";
            dashStoreName.Text = store.Name;
            UpdateOperationalStoreLabels();
        }
        else
        {
            var s = await _settingsService.GetSettingsAsync();
            lblStore.Text = $"{s.StoreName}  •  {s.StoreAddress}";
            dashStoreName.Text = string.IsNullOrWhiteSpace(s.StoreName) ? "Dashboard" : s.StoreName;
            UpdateOperationalStoreLabels();
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        {
            System.Diagnostics.Debug.WriteLine($"Stores error {ex.Number}: {ex.Message}");
            var s2 = await _settingsService.GetSettingsAsync();
            lblStore.Text = $"{s2.StoreName}  \u2022  {s2.StoreAddress}";
            dashStoreName.Text = string.IsNullOrWhiteSpace(s2.StoreName) ? "Dashboard" : s2.StoreName;
            UpdateOperationalStoreLabels();

            _ = Task.Run(async () =>
            {
                try
                {
                    using var db = CreateDb();
                    await db.Database.ExecuteSqlRawAsync(@"
                        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Stores')
                        BEGIN
                            CREATE TABLE [Stores] ([Id] INT IDENTITY(1,1) PRIMARY KEY,[Name] NVARCHAR(200) NOT NULL DEFAULT '',[Address] NVARCHAR(500) NOT NULL DEFAULT '',[IsActive] BIT NOT NULL DEFAULT 1,[CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());
                            IF EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings')
                                INSERT INTO [Stores] ([Name],[Address],[IsActive]) SELECT TOP 1 ISNULL(StoreName,'Store 1'),ISNULL(StoreAddress,''),1 FROM [AppSettings];
                            ELSE INSERT INTO [Stores] ([Name],[Address],[IsActive]) VALUES ('Store 1','',1);
                        END
                        ELSE
                        BEGIN
                            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id=OBJECT_ID('Stores') AND name='Name')
                            BEGIN
                                IF EXISTS (SELECT * FROM sys.columns WHERE object_id=OBJECT_ID('Stores') AND name='StoreName') EXEC sp_rename 'Stores.StoreName','Name','COLUMN';
                                ELSE ALTER TABLE [Stores] ADD [Name] NVARCHAR(200) NOT NULL DEFAULT '';
                            END
                            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id=OBJECT_ID('Stores') AND name='Address')
                            BEGIN
                                IF EXISTS (SELECT * FROM sys.columns WHERE object_id=OBJECT_ID('Stores') AND name='StoreAddress') EXEC sp_rename 'Stores.StoreAddress','Address','COLUMN';
                                ELSE ALTER TABLE [Stores] ADD [Address] NVARCHAR(500) NOT NULL DEFAULT '';
                            END
                            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id=OBJECT_ID('Stores') AND name='IsActive') ALTER TABLE [Stores] ADD [IsActive] BIT NOT NULL DEFAULT 1;
                            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id=OBJECT_ID('Stores') AND name='CreatedUtc') ALTER TABLE [Stores] ADD [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME();
                        END");
                }
                catch (Exception autoEx) { System.Diagnostics.Debug.WriteLine($"Auto-migrate Stores failed: {autoEx.Message}"); }
            });
        }
    }

    private async void Store_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStores) return;
        if (cmbStore.SelectedValue is not int storeId) return;

        _currentStoreId = storeId;
        UpdateOperationalStoreLabels();

        // Update StoreConnectionService to use this store's database
        if (_storeConnService != null)
        {
            _storeConnService.CurrentStoreId = storeId;

            // If this store has its own database, resolve the actual StoreId from THAT database
            // (the store's data uses StoreId=1 in its own DB, not the Id from the main DB)
            if (_storeConnService.HasCustomConnection(storeId))
            {
                try
                {
                    using var remoteDb = _storeConnService.CreateDbContext(storeId);
                    // Ensure remote database has full schema
                    var rConnStr = remoteDb.Database.GetConnectionString();
                    if (!string.IsNullOrEmpty(rConnStr))
                        await DatabaseSchemaService.EnsureSchemaAsync(rConnStr);

                    var remoteStore = await remoteDb.Stores.AsNoTracking().FirstOrDefaultAsync();
                    if (remoteStore != null)
                        _currentStoreId = remoteStore.Id; // Use the StoreId from the remote database
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Remote store lookup: {ex.Message}");
                }
            }
        }

        var s = await _settingsService.GetSettingsAsync();
        s.LastStoreId = storeId;
        await _settingsService.SaveSettingsAsync(s);

        // Wire the store-specific database into AuthService so User Accounts window uses correct store
        try
        {
            var authSvc = ((App)System.Windows.Application.Current).Services.GetRequiredService<IAuthService>();
            if (authSvc is ManagerPaperworkSystem.Data.Services.AuthService concreteAuth)
                concreteAuth.SetStoreDbCreator(() => CreateDb());
        }
        catch { /* DI not ready yet */ }

        await UpdateStoreHeaderAsync();
        await LoadLookupsAsync();
        await LoadDataAsync();
        await LoadPurchasesModuleAsync();
        RefreshOperationsHubPage();
    }

    private async void ManageStores_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = ((App)System.Windows.Application.Current).Services.GetRequiredService<StoreManagerWindow>();
            win.Owner = this;
            if (_storeConnService != null)
                win.SetStoreConnectionService(_storeConnService);
            win.ShowDialog();

            // Reload store connections in case new stores were added
            if (_storeConnService != null)
            {
                var savedConnections = StoreManagerWindow.LoadAllStoreConnections();
                foreach (var kvp in savedConnections)
                {
                    if (int.TryParse(kvp.Key, out var storeId))
                        _storeConnService.RegisterStore(storeId, kvp.Value);
                }
            }

            await LoadStoresAsync();
            await LoadLookupsAsync();
            await LoadDataAsync();
            await LoadPurchasesModuleAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Stores", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Left navigation shortcuts (ultra-modern sidebar)
    private void Nav_SelectTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe)
            {
                // Dashboard tiles use CommandParameter for the destination index (Tag is used for the icon).
                var raw = (fe as System.Windows.Controls.Button)?.CommandParameter ?? fe.Tag;
                if (raw is null || !int.TryParse(raw.ToString(), out var idx)) return;

                if (tabsMain is null) return;
                var max = tabsMain.Items.Count - 1;
                if (max < 0) return;
                idx = Math.Clamp(idx, 0, max);
                if (!_session.IsAdmin && IsAdminOnlyTabIndex(idx))
                {
                    System.Windows.MessageBox.Show(this, "Only Owner/Admin accounts can open this section.", "Access Restricted", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                tabsMain.SelectedIndex = idx;
            }
        }
        catch { }
    }

    private static bool IsAdminOnlyTabIndex(int idx) => idx is 4 or 5 or 6 or 9;

    private void TabsMain_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            // Home button is permanently available in the top toolbar (btnHomeTop).
            // No per-tab overlay toggle needed.
            if (!_session.IsAdmin && IsAdminOnlyTabIndex(tabsMain.SelectedIndex))
            {
                tabsMain.SelectedIndex = 0;
                System.Windows.MessageBox.Show(this, "Only Owner/Admin accounts can open this section.", "Access Restricted", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            UpdateSidebarSelection();
            if (tabsMain.SelectedIndex == _operationsHubTabIndex)
                RefreshOperationsHubPage();
            Dispatcher.BeginInvoke(new Action(RefreshLegacyThemeBrushes), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch { }
    }


    private void PriceAlerts_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            foreach (var item in tabsMain.Items)
            {
                if (item is TabItem ti && (ti.Header?.ToString() ?? "") == "Price Alerts")
                {
                    tabsMain.SelectedItem = ti;
                    break;
                }
            }
        }
        catch { }
    }

    private async Task LoadLookupsAsync()
    {
        try
        {
        // Ensure a helpful default vendor list exists for the current store so the Purchases
        // vendor dropdown is immediately usable (and can route to vendor-specific parsers).
        var defaultVendorNames = new[]
        {
            "AK WHOLESALE",
            "AMERICAN DISTRIBUTORS",
            "HS WHOLESALE",
            "SKYGATE WHOLESALE",
            "1 OAK WHOLESALE",
            "SAFA GOODS",
            "TRI STATE DISTRO"
        };

        var existing = await CreateDb().Vendors
            .Where(x => x.StoreId == _currentStoreId)
            .ToListAsync();

        var existingNames = new HashSet<string>(existing.Select(v => (v.Name ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        var toAdd = new List<Vendor>();
        foreach (var name in defaultVendorNames)
        {
            if (!existingNames.Contains(name))
            {
                toAdd.Add(new Vendor { StoreId = _currentStoreId, Name = name });
            }
        }

        if (toAdd.Count > 0)
        {
            using var db = CreateDb();
            db.Vendors.AddRange(toAdd);
            await db.SaveChangesAsync();
        }

        var vendors = await CreateDb().Vendors.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId)
            .OrderBy(x => x.Name)
            .ToListAsync();
        var purposes = await CreateDb().Purposes.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId)
            .OrderBy(x => x.Name)
            .ToListAsync();

        cohVendor.ItemsSource = vendors;
        cohVendor.DisplayMemberPath = "Name";
        cohVendor.SelectedValuePath = "Id";

        cohPurpose.ItemsSource = purposes;
        cohPurpose.DisplayMemberPath = "Name";
        cohPurpose.SelectedValuePath = "Id";

        invVendor.ItemsSource = vendors;
        invVendor.DisplayMemberPath = "Name";
        invVendor.SelectedValuePath = "Id";

        gridVendors.ItemsSource = vendors;
        gridPurposes.ItemsSource = purposes;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        {
            System.Diagnostics.Debug.WriteLine($"LoadLookupsAsync skipped (error {ex.Number}): {ex.Message}");
        }
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var shift = await CreateDb().ShiftLogs.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
                .Take(2000)
                .ToListAsync();
            gridShift.ItemsSource = shift;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        { System.Diagnostics.Debug.WriteLine($"ShiftLogs skipped: {ex.Message}"); }

        // Keep Cash On Hand in sync even if Shift Log rows were inserted via import or other code paths.
        try { await SyncShiftLogCashDropsToCashOnHandRecentAsync(daysBack: 120); }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        { System.Diagnostics.Debug.WriteLine($"SyncCashDrops skipped: {ex.Message}"); }

        try
        {
        var coh = await CreateDb().CashOnHand.AsNoTracking()
            .Include(x => x.Vendor)
            .Include(x => x.Purpose)
            .Where(x => x.StoreId == _currentStoreId)
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
            .Take(5000)
            .ToListAsync();
        gridCoh.ItemsSource = coh;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        {
            System.Diagnostics.Debug.WriteLine($"CashOnHand with Include skipped: {ex.Message}");
            // Retry without Vendor/Purpose includes (tables may not have correct schema)
            try
            {
                var coh = await CreateDb().CashOnHand.AsNoTracking()
                    .Where(x => x.StoreId == _currentStoreId)
                    .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
                    .Take(5000)
                    .ToListAsync();
                gridCoh.ItemsSource = coh;
            }
            catch (Exception ex2) { System.Diagnostics.Debug.WriteLine($"CashOnHand fallback also failed: {ex2.Message}"); }
        }
        catch (Exception ex) 
        {
            System.Diagnostics.Debug.WriteLine($"CashOnHand load error: {ex.Message}");
            // Retry without includes
            try
            {
                var coh = await CreateDb().CashOnHand.AsNoTracking()
                    .Where(x => x.StoreId == _currentStoreId)
                    .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
                    .Take(5000)
                    .ToListAsync();
                gridCoh.ItemsSource = coh;
            }
            catch (Exception ex2) { System.Diagnostics.Debug.WriteLine($"CashOnHand fallback also failed: {ex2.Message}"); }
        }

        try
        {
        var chk = await CreateDb().CheckPayouts.AsNoTracking()
            .Where(x => x.StoreId == _currentStoreId)
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
            .Take(2000)
            .ToListAsync();
        gridChk.ItemsSource = chk;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        { System.Diagnostics.Debug.WriteLine($"CheckPayouts skipped: {ex.Message}"); }

        // Bank Statement tab — initialize with current DB + store
        try { if (bankStatementTab is not null) bankStatementTab.Initialize(_dbFactory, _currentStoreId, _session); }
        catch (Exception bex) { System.Diagnostics.Debug.WriteLine($"BankStatement skipped: {bex.Message}"); }

        try { await RefreshDashboardAsync(); }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        { System.Diagnostics.Debug.WriteLine($"Dashboard skipped: {ex.Message}"); }
    }

    private async Task RefreshDashboardAsync()
    {
        try
        {
        using var db = CreateDb();
        
        // Compute totals using "effective" rows (original overridden by latest correction).
        var cashAll = await db.CashOnHand.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToListAsync();
        var cashEff = EffectiveRows(cashAll,
            x => x.IsCorrection,
            x => x.CorrectsId,
            x => x.Id,
            x => x.CreatedUtc);

        var cashAdded = cashEff.Where(x => !x.IsPayout).Sum(x => x.CashAdded);
        var cashPayoutTotal = cashEff.Where(x => x.IsPayout).Sum(x => x.PayoutAmount);
        var balance = cashAdded - cashPayoutTotal;
        var todayOnly = DateOnly.FromDateTime(DateTime.Today);
        var cashAddedToday = cashEff.Where(x => !x.IsPayout && x.Date == todayOnly).Sum(x => x.CashAdded);

        var chkAll = await db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToListAsync();
        var chkEff = EffectiveRows(chkAll,
            x => x.IsCorrection,
            x => x.CorrectsId,
            x => x.Id,
            x => x.CreatedUtc);
        var unclearedTotal = chkEff.Where(x => !x.Cleared).Sum(x => x.CheckAmount);
        var checkPayoutTotal = chkEff.Sum(x => x.CheckAmount);
        var monthStart = new DateOnly(todayOnly.Year, todayOnly.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var clearedThisMonth = chkEff.Where(x => x.Cleared && x.Date >= monthStart && x.Date <= monthEnd).Sum(x => x.CheckAmount);
        var nextCheckNo = GetNextCheckNumber(chkEff);

        if (dashCash is not null) dashCash.Text = $"{balance:C2}";
        if (dashChecks is not null) dashChecks.Text = $"{unclearedTotal:C2}";
        
        // Update Payouts tile
        if (dashCashPayout is not null) dashCashPayout.Text = $"{cashPayoutTotal:C2}";
        if (dashCheckPayout is not null) dashCheckPayout.Text = $"{checkPayoutTotal:C2}";
        UpdateOperationalSummaries(balance, cashAddedToday, cashPayoutTotal, unclearedTotal, clearedThisMonth, nextCheckNo);
        
        // Populate Invoice Analytics on dashboard
        await RefreshInvoiceAnalyticsAsync();
        
        // Populate Recent Price Changes grid on dashboard
        if (gridDashPriceAlerts is not null)
        {
            try
            {
                var recentAlerts = await db.PriceAlerts.AsNoTracking()
                    .Where(x => x.StoreId == _currentStoreId)
                    .OrderByDescending(x => x.CreatedUtc)
                    .Take(10)
                    .ToListAsync();
                gridDashPriceAlerts.ItemsSource = recentAlerts;
            }
            catch (Microsoft.Data.SqlClient.SqlException pex) when (pex.Number == 207 || pex.Number == 208)
            { System.Diagnostics.Debug.WriteLine($"PriceAlerts skipped: {pex.Message}"); }
        }
        
        // Recent Entries removed from dashboard
        
        await RefreshDashboardChartsAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207 || ex.Number == 208)
        {
            System.Diagnostics.Debug.WriteLine($"RefreshDashboard skipped: {ex.Message}");
            if (dashCash is not null) dashCash.Text = "$0.00";
            if (dashChecks is not null) dashChecks.Text = "$0.00";
            if (dashCashPayout is not null) dashCashPayout.Text = "$0.00";
            if (dashCheckPayout is not null) dashCheckPayout.Text = "$0.00";
            UpdateOperationalSummaries(0m, 0m, 0m, 0m, 0m, "0");
        }
    }

    private void UpdateOperationalSummaries(decimal cashBalance, decimal todayAdded, decimal pendingCashPayouts, decimal unclearedChecks, decimal clearedThisMonth, string nextCheckNo)
    {
        if (_cohCurrentBalanceSummary is not null) _cohCurrentBalanceSummary.Text = $"{cashBalance:C2}";
        if (_cohTodayAddedSummary is not null) _cohTodayAddedSummary.Text = $"{todayAdded:C2}";
        if (_cohPendingPayoutsSummary is not null) _cohPendingPayoutsSummary.Text = $"{pendingCashPayouts:C2}";
        if (_cohCarryForwardSummary is not null) _cohCarryForwardSummary.Text = $"{ParseMoney(cohCarryForward.Text):C2}";
        if (_chkUnclearedTotalSummary is not null) _chkUnclearedTotalSummary.Text = $"{unclearedChecks:C2}";
        if (_chkClearedThisMonthSummary is not null) _chkClearedThisMonthSummary.Text = $"{clearedThisMonth:C2}";
        if (_chkNextCheckSummary is not null) _chkNextCheckSummary.Text = nextCheckNo;
    }

    private static string GetNextCheckNumber(IEnumerable<CheckPayout> checks)
    {
        var max = 0;
        foreach (var check in checks)
        {
            if (int.TryParse(check.CheckNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > max)
                max = value;
        }
        return (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    private async Task RefreshInvoiceAnalyticsAsync()
    {
        try
        {
            using var db = CreateDb();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);

            // Get all invoices for this store
            var allInvoices = await db.PurchaseInvoices.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .ToListAsync();

            var totalInvoiceCount = allInvoices.Count;
            var totalInvoiceAmount = allInvoices.Sum(x => x.Total);
            
            // This month invoices
            var thisMonthInvoices = allInvoices
                .Where(x => x.InvoiceDate >= monthStart && x.InvoiceDate <= monthEnd)
                .ToList();
            var thisMonthTotal = thisMonthInvoices.Sum(x => x.Total);
            
            // Unique vendors
            var uniqueVendors = allInvoices.Select(x => x.VendorName).Distinct().Count();

            // Update UI
            if (dashInvoiceCount is not null) dashInvoiceCount.Text = totalInvoiceCount.ToString();
            if (dashInvoiceTotal is not null) dashInvoiceTotal.Text = $"Total: {totalInvoiceAmount:C2}";
            if (dashInvoiceThisMonth is not null) dashInvoiceThisMonth.Text = $"This Month: {thisMonthTotal:C2}";
            if (dashInvoiceVendors is not null) dashInvoiceVendors.Text = $"Vendors: {uniqueVendors}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error refreshing invoice analytics: {ex.Message}");
        }
    }

    private async Task RefreshDashboardChartsAsync()
    {
        try
        {
            using var db = CreateDb();
            
            // Update Sales Analytics bars for the selected Month/Year (12 day window).
            var today = DateOnly.FromDateTime(DateTime.Today);
            var year = cmbDashYear?.SelectedItem is int y ? y : today.Year;
            var month = cmbDashMonth?.SelectedValue is int m ? m : today.Month;

            var monthStart = new DateOnly(year, month, 1);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);

            // If current month selected, don't include future dates.
            var effectiveEnd = (year == today.Year && month == today.Month) ? today : monthEnd;

            // Choose a 12-day window. For past months: last 12 days of the month.
            // For early current month: show monthStart..(monthStart+11) (future days will show as 0).
            DateOnly start;
            DateOnly end;
            if (year != today.Year || month != today.Month)
            {
                end = monthEnd;
                start = end.AddDays(-11);
            }
            else
            {
                if (effectiveEnd.DayNumber - monthStart.DayNumber >= 11)
                {
                    end = effectiveEnd;
                    start = end.AddDays(-11);
                }
                else
                {
                    start = monthStart;
                    end = monthStart.AddDays(11);
                }
            }
            // Pull raw shift rows (including corrections), then collapse to effective rows before aggregating.
            var rawShiftAll = await db.ShiftLogs.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.CreatedUtc)
                .Take(20000)
                .ToListAsync();

            var rawShift = rawShiftAll
                .Where(x => x.Date >= start && x.Date <= end)
                .ToList();

            var effShift = EffectiveRows(rawShift,
                x => x.IsCorrection,
                x => x.CorrectsId,
                x => x.Id,
                x => x.CreatedUtc);

            var byDay = effShift
                .GroupBy(x => x.Date)
                .Select(g => new { Date = g.Key, TotalSales = g.Sum(x => x.NetSales + x.Tax) })
                .ToList();


            var map = byDay.ToDictionary(x => x.Date, x => x.TotalSales);
            var values = new double[12];
            double max = 0;
            for (int i = 0; i < 12; i++)
            {
                var d = start.AddDays(i);
                var v = map.TryGetValue(d, out var total) ? (double)total : 0d;
                values[i] = v;
                if (v > max) max = v;
            }

            if (max <= 0.01) max = 1; // avoid divide-by-zero

            const double minH = 18;
            const double maxH = 150;

            void SetBar(System.Windows.Shapes.Rectangle? bar, double val)
            {
                if (bar is null) return;
                var h = minH + (val / max) * (maxH - minH);
                if (h < minH) h = minH;
                bar.Height = h;
            }

            SetBar(salesBar01, values[0]);
            SetBar(salesBar02, values[1]);
            SetBar(salesBar03, values[2]);
            SetBar(salesBar04, values[3]);
            SetBar(salesBar05, values[4]);
            SetBar(salesBar06, values[5]);
            SetBar(salesBar07, values[6]);
            SetBar(salesBar08, values[7]);
            SetBar(salesBar09, values[8]);
            SetBar(salesBar10, values[9]);
            SetBar(salesBar11, values[10]);
            SetBar(salesBar12, values[11]);
        }
        catch
        {
            // Never block the app if charts fail.
        }
    }

    private static List<T> EffectiveRows<T>(IEnumerable<T> all,
        Func<T, bool> isCorrection,
        Func<T, int?> correctsId,
        Func<T, int> id,
        Func<T, DateTime> createdUtc)
    {
        var originals = all.Where(x => !isCorrection(x)).ToList();
        var latestCorr = all.Where(isCorrection)
            .Select(x => (row: x, target: correctsId(x)))
            .Where(x => x.target.HasValue)
            .GroupBy(x => x.target!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => createdUtc(x.row)).First().row);

        var result = new List<T>(originals.Count);
        foreach (var o in originals)
        {
            if (latestCorr.TryGetValue(id(o), out var corr)) result.Add(corr);
            else result.Add(o);
        }
        return result;
    }

    // Recent Entry class kept for compatibility
    public class RecentEntry
    {
        public string Type { get; set; } = "";
        public DateTime Date { get; set; }
        public string Description { get; set; } = "";
        public decimal Amount { get; set; }
    }

    private Task RefreshRecentEntriesAsync()
    {
        // Recent Entries section removed from dashboard
        return Task.CompletedTask;
    }

    // Dashboard tile refresh icon handlers (using MouseLeftButtonDown for TextBlock icons)
    private async void RefreshCash_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            using var db = CreateDb();
            var cashAll = await db.CashOnHand.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToListAsync();
            var cashEff = EffectiveRows(cashAll, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var cashAdded = cashEff.Where(x => !x.IsPayout).Sum(x => x.CashAdded);
            var cashPayoutTotal = cashEff.Where(x => x.IsPayout).Sum(x => x.PayoutAmount);
            var balance = cashAdded - cashPayoutTotal;
            if (dashCash is not null) dashCash.Text = $"{balance:C2}";
        }
        catch { }
    }

    private async void RefreshChecks_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            using var db = CreateDb();
            var chkAll = await db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToListAsync();
            var chkEff = EffectiveRows(chkAll, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var unclearedTotal = chkEff.Where(x => !x.Cleared).Sum(x => x.CheckAmount);
            if (dashChecks is not null) dashChecks.Text = $"{unclearedTotal:C2}";
        }
        catch { }
    }

    private async void RefreshPayouts_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            using var db = CreateDb();
            var cashAll = await db.CashOnHand.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToListAsync();
            var cashEff = EffectiveRows(cashAll, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var cashPayoutTotal = cashEff.Where(x => x.IsPayout).Sum(x => x.PayoutAmount);
            
            var chkAll = await db.CheckPayouts.AsNoTracking().Where(x => x.StoreId == _currentStoreId).ToListAsync();
            var chkEff = EffectiveRows(chkAll, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);
            var checkPayoutTotal = chkEff.Sum(x => x.CheckAmount);
            
            if (dashCashPayout is not null) dashCashPayout.Text = $"{cashPayoutTotal:C2}";
            if (dashCheckPayout is not null) dashCheckPayout.Text = $"{checkPayoutTotal:C2}";
        }
        catch { }
    }

    private async void RefreshInvoice_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await RefreshInvoiceAnalyticsAsync();
    }

    private async void RefreshPriceChanges_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (gridDashPriceAlerts is null) return;
            using var db = CreateDb();
            var recentAlerts = await db.PriceAlerts.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.CreatedUtc)
                .Take(10)
                .ToListAsync();
            gridDashPriceAlerts.ItemsSource = recentAlerts;
        }
        catch { }
    }

    private async void RefreshRecent_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await RefreshRecentEntriesAsync();
    }

    // Screen mode scaling removed. The app now runs maximized and adapts to the current display.

    private static decimal ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        text = text.Trim();
        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.CurrentCulture, out var d))
            return d;
        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out d))
            return d;
        throw new FormatException($"Invalid amount: {text}");
    }

    private void EnsureAdmin(string action)
    {
        if (_session.IsAdmin) return;
        throw new Exception($"Only Owner/Admin can {action}. Managers must use Add Correction if a mistake was made.");
    }

    // -------------------- MENU --------------------

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.MessageBox.Show(this, "Logout and return to the login screen?", "Logout", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // Clear current session
            _session.Clear();

            // Prevent app from shutting down just because we hide/close a window
            System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Hide();

            var login = ((App)System.Windows.Application.Current).Services.GetRequiredService<LoginWindow>();
            var ok = login.ShowDialog();
            if (ok == true)
            {
                // Logged back in (SessionState was updated by LoginWindow)
                // Reset store ID from session
                if (_session.LastStoreId > 0)
                    _currentStoreId = _session.LastStoreId;
                else
                {
                    var s = await _settingsService.GetSettingsAsync();
                    _currentStoreId = s.LastStoreId > 0 ? s.LastStoreId : s.DefaultStoreId;
                }

                // Re-wire AuthService to current store
                try
                {
                    var authSvc = ((App)System.Windows.Application.Current).Services.GetRequiredService<IAuthService>();
                    if (authSvc is ManagerPaperworkSystem.Data.Services.AuthService concreteAuth)
                        concreteAuth.SetStoreDbCreator(() => CreateDb());
                }
                catch { }

                ApplyRolePermissions();
                await LoadStoresAsync();
                await UpdateStoreHeaderAsync();
                await LoadLookupsAsync();
                await LoadDataAsync();

                Show();
                Activate();
                System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
                return;
            }

            // If login cancelled, exit app
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Logout Error", MessageBoxButton.OK, MessageBoxImage.Error);
            try
            {
                // Best effort: show window again if something failed
                Show();
                Activate();
                System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
            catch { }
        }
    }

    private void OpenDbFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(_paths.DatabasePath) ?? _paths.AppDataDirectory;
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Open Folder Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyDbPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_paths.DatabasePath);
            System.Windows.MessageBox.Show(this, _paths.DatabasePath, "Database Path (Copied)", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Copy Path Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var v = Services.UpdateService.CurrentVersion;

        // Detect which database is in use
        var dbInfo = "Data is stored locally in SQLite.";
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var connStr = db.Database.GetConnectionString() ?? "";
            if (connStr.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                (connStr.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) &&
                 connStr.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase)))
                dbInfo = "Connected to SQL Server database.";
        }
        catch { }

        System.Windows.MessageBox.Show(this,
            $"Hisab Kitab\nVersion {v}\n\n{dbInfo}",
            "About Hisab Kitab",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Reports_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("open reports");
            var win = ((App)System.Windows.Application.Current).Services.GetRequiredService<ReportsWindow>();
            win.Owner = this;
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Reports", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void CloseMonth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Close month for the CURRENT STORE ONLY.
            Store? store = null;
            try { store = await CreateDb().Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _currentStoreId); }
            catch (Microsoft.Data.SqlClient.SqlException sqlex) when (sqlex.Number == 207 || sqlex.Number == 208)
            { System.Diagnostics.Debug.WriteLine($"Stores error {sqlex.Number} in CloseMonth: {sqlex.Message}"); }

            var settings = await _settingsService.GetSettingsAsync();

            // Ensure reports use the current store (ReportService reads Settings.LastStoreId).
            if (settings.LastStoreId != _currentStoreId)
            {
                settings.LastStoreId = _currentStoreId;
                await _settingsService.SaveSettingsAsync(settings);
            }

            var storeName = store?.Name ?? (settings.StoreName?.Trim() ?? $"Store {_currentStoreId}");
            var storeAddress = store?.Address ?? (settings.StoreAddress?.Trim() ?? "");

            static string SafeFolder(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return "Store";
                foreach (var c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                return name.Trim().Trim('.').Trim();
            }

            // Determine the most recent month that has any data for THIS store.
            DateOnly? latest = null;

            var maxShift = await CreateDb().ShiftLogs.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.Date)
                .Select(x => (DateOnly?)x.Date)
                .FirstOrDefaultAsync();

            var maxCash = await CreateDb().CashOnHand.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.Date)
                .Select(x => (DateOnly?)x.Date)
                .FirstOrDefaultAsync();

            var maxChk = await CreateDb().CheckPayouts.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.Date)
                .Select(x => (DateOnly?)x.Date)
                .FirstOrDefaultAsync();

            var maxInv = await CreateDb().PurchaseInvoices.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId)
                .OrderByDescending(x => x.InvoiceDate)
                .Select(x => (DateOnly?)x.InvoiceDate)
                .FirstOrDefaultAsync();

            latest = new[] { maxShift, maxCash, maxChk, maxInv }
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .OrderByDescending(d => d)
                .FirstOrDefault();

            var baseDate = latest ?? DateOnly.FromDateTime(DateTime.Today);
            var from = new DateOnly(baseDate.Year, baseDate.Month, 1);
            var to = from.AddMonths(1).AddDays(-1);

            var msg = $"Close month {from:MMMM yyyy} for:\n\n{storeName}\n{storeAddress}\n\nThis will export separate PDF reports for each tab (Shift Log, Cash On Hand, Check Payouts).";
            if (System.Windows.MessageBox.Show(this, msg, "Close Month", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // Ask whether to clear everything (store-scoped) or only transactional paperwork entries.
            var clearChoice = System.Windows.MessageBox.Show(this,
                "After exporting reports, do you want to CLEAR EVERYTHING for this store?\n\n" +
                "Yes = Clear everything (paperwork + vendors/purposes + purchases/costs/alerts)\n" +
                "No  = Clear paperwork entries only (Shift/Cash/Checks)\n" +
                "Cancel = Do not clear",
                "Clear Data (This Store Only)",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            // Export PDFs (store-scoped via Settings.LastStoreId).
            var installDir = AppContext.BaseDirectory;
            var monthFolder = Path.Combine(installDir, "Reports", "Monthly Reports", SafeFolder(storeName), $"{from:yyyy-MM}");
            Directory.CreateDirectory(monthFolder);

            var shiftPdf = Path.Combine(monthFolder, $"ShiftLog_{from:yyyy-MM}.pdf");
            var cashPdf = Path.Combine(monthFolder, $"CashOnHand_{from:yyyy-MM}.pdf");
            var chkPdf = Path.Combine(monthFolder, $"CheckPayouts_{from:yyyy-MM}.pdf");

            await _reportService.GenerateShiftLogPdfAsync(from, to, shiftPdf);
            await _reportService.GenerateCashOnHandPdfAsync(from, to, cashPdf);
            await _reportService.GenerateCheckPayoutsPdfAsync(from, to, chkPdf);

            // Backup DB before clearing
            try
            {
                var backupName = $"backup_before_close_{SafeFolder(storeName)}_{from:yyyy-MM}_{DateTime.Now:yyyyMMdd_HHmmss}.db";
                var backupPath = Path.Combine(_paths.BackupsDirectory, backupName);
                File.Copy(_paths.DatabasePath, backupPath, true);
            }
            catch { }

            if (clearChoice == MessageBoxResult.Cancel)
            {
                System.Windows.MessageBox.Show(this, "Reports exported successfully. Data was NOT cleared.", "Close Month", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Clear month data FOR THIS STORE ONLY. This app is designed for monthly paperwork; we reset for the next month.
            using (var db = CreateDb())
            {
                db.ShiftLogs.RemoveRange(db.ShiftLogs.Where(x => x.StoreId == _currentStoreId));
                db.CashOnHand.RemoveRange(db.CashOnHand.Where(x => x.StoreId == _currentStoreId));
                db.CheckPayouts.RemoveRange(db.CheckPayouts.Where(x => x.StoreId == _currentStoreId));

                if (clearChoice == MessageBoxResult.Yes)
                {
                    // Store-scoped "Clear everything"
                    db.PriceAlerts.RemoveRange(db.PriceAlerts.Where(x => x.StoreId == _currentStoreId));
                    db.ProductCosts.RemoveRange(db.ProductCosts.Where(x => x.StoreId == _currentStoreId));
                    db.PurchaseInvoices.RemoveRange(db.PurchaseInvoices.Where(x => x.StoreId == _currentStoreId));

                    db.Vendors.RemoveRange(db.Vendors.Where(x => x.StoreId == _currentStoreId));
                    db.Purposes.RemoveRange(db.Purposes.Where(x => x.StoreId == _currentStoreId));
                }

                await db.SaveChangesAsync();
            }

            await LoadLookupsAsync();
            await LoadDataAsync();
            await LoadPurchasesModuleAsync();

            System.Windows.MessageBox.Show(this,
                $"Reports exported to:\n{monthFolder}\n\nData cleared successfully for this store.",
                "Close Month", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Close Month", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var win = ((App)System.Windows.Application.Current).Services.GetRequiredService<ChangePasswordWindow>();
        win.Owner = this;
        win.ShowDialog();
    }



    private void Users_Click(object sender, RoutedEventArgs e)
    {
        var win = ((App)System.Windows.Application.Current).Services.GetRequiredService<UserAccountsWindow>();
        win.Owner = this;
        win.ShowDialog();
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("create backups");
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(_paths.BackupsDirectory, $"managerpaperwork_{stamp}.db");
            File.Copy(_paths.DatabasePath, dest, overwrite: true);
            System.Windows.MessageBox.Show(this, $"Backup created:\n{dest}", "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -------------------- SHIFT LOG CRUD --------------------

    private async void Shift_Add_Click(object sender, RoutedEventArgs e)
    {
        slError.Text = "";
        try
        {
            if (slDate.SelectedDate is null) throw new Exception("Date is required.");

            var entry = new ShiftLogEntry
            {
                StoreId = _currentStoreId,
                Date = DateOnly.FromDateTime(slDate.SelectedDate.Value),
                Employee = slEmployee.Text?.Trim() ?? "",
                ShiftNo = slShiftNo.Text?.Trim() ?? "",
                CashTotal = ParseMoney(slCash.Text),
                CardTotal = ParseMoney(slCard.Text),
                CashDropReceived = ParseMoney(slDrop.Text),
                NetSales = ParseMoney(slNetSales.Text),
                Tax = ParseMoney(slTax.Text),
                RegisterPayout = ParseMoney(slRegPayout.Text),
                PayoutReason = _slPayoutReason?.Text?.Trim() ?? "",
                CreatedByUserId = _session.UserId,
                CreatedByName = _session.DisplayName
            };

            { using var db = CreateDb(); db.ShiftLogs.Add(entry); await db.SaveChangesAsync(); }

            // Auto-update Cash On Hand daily cash-drop entry (one line per date).
            try { await SyncShiftLogCashDropsToCashOnHandAsync(entry.Date); }
            catch (Microsoft.Data.SqlClient.SqlException syncEx) when (syncEx.Number == 207 || syncEx.Number == 208)
            { System.Diagnostics.Debug.WriteLine($"SyncShiftLog skipped after add: {syncEx.Message}"); }

            // Clear all shift log input fields after successful add
            slEmployee.Text = "";
            slShiftNo.Text = "";
            slCash.Text = "";
            slCard.Text = "";
            slDrop.Text = "";
            slNetSales.Text = "";
            slTax.Text = "";
            slRegPayout.Text = "";
            if (_slPayoutReason is not null) _slPayoutReason.Text = "";
            slError.Text = "Shift entry added successfully.";
            slError.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF059669"));

            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            slError.Text = msg;
        }
    }

    private async void Shift_Update_Click(object sender, RoutedEventArgs e)
    {
        slError.Text = "";
        try
        {
            EnsureAdmin("update shift entries");
            if (gridShift.SelectedItem is not ShiftLogEntry selected)
                throw new Exception("Select a row to update.");

            using var db = CreateDb();
            var entity = await db.ShiftLogs.FirstOrDefaultAsync(x => x.Id == selected.Id && x.StoreId == _currentStoreId);
            if (entity is null) throw new Exception("Row not found.");

            if (slDate.SelectedDate is null) throw new Exception("Date is required.");
            var oldDate = entity.Date;
            entity.Date = DateOnly.FromDateTime(slDate.SelectedDate.Value);
            entity.Employee = slEmployee.Text?.Trim() ?? "";
            entity.ShiftNo = slShiftNo.Text?.Trim() ?? "";
            entity.CashTotal = ParseMoney(slCash.Text);
            entity.CardTotal = ParseMoney(slCard.Text);
            entity.CashDropReceived = ParseMoney(slDrop.Text);
            entity.NetSales = ParseMoney(slNetSales.Text);
            entity.Tax = ParseMoney(slTax.Text);
            entity.RegisterPayout = ParseMoney(slRegPayout.Text);
            entity.PayoutReason = _slPayoutReason?.Text?.Trim() ?? "";

            await db.SaveChangesAsync();

            try
            {
                await SyncShiftLogCashDropsToCashOnHandAsync(oldDate);
                if (entity.Date != oldDate) await SyncShiftLogCashDropsToCashOnHandAsync(entity.Date);
            }
            catch (Microsoft.Data.SqlClient.SqlException syncEx) when (syncEx.Number == 207 || syncEx.Number == 208)
            { System.Diagnostics.Debug.WriteLine($"SyncShiftLog skipped after update: {syncEx.Message}"); }

            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            slError.Text = msg;
        }
    }

    private async void Shift_Delete_Click(object sender, RoutedEventArgs e)
    {
        slError.Text = "";
        try
        {
            EnsureAdmin("delete shift entries");
            if (gridShift.SelectedItem is not ShiftLogEntry selected)
                throw new Exception("Select a row to delete.");

            var entity = await CreateDb().ShiftLogs.FirstOrDefaultAsync(x => x.Id == selected.Id && x.StoreId == _currentStoreId);
            if (entity is null) return;

            if (System.Windows.MessageBox.Show(this, "Delete selected shift entry?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            { using var db = CreateDb(); db.ShiftLogs.Attach(entity); db.ShiftLogs.Remove(entity); await db.SaveChangesAsync(); }

            try { await SyncShiftLogCashDropsToCashOnHandAsync(entity.Date); }
            catch (Microsoft.Data.SqlClient.SqlException syncEx) when (syncEx.Number == 207 || syncEx.Number == 208)
            { System.Diagnostics.Debug.WriteLine($"SyncShiftLog skipped after delete: {syncEx.Message}"); }

            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            slError.Text = msg;
        }
    }

    // -------------------- POS REPORT IMPORT (XLSX/PDF) --------------------

    private void UploadPos_Click(object sender, RoutedEventArgs e)
    {
        slError.Text = "";
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select POS Report",
                Filter = "POS Reports (*.xlsx;*.pdf)|*.xlsx;*.pdf|Excel (*.xlsx)|*.xlsx|PDF (*.pdf)|*.pdf",
                Multiselect = false
            };

            if (dlg.ShowDialog(this) != true) return;

            var data = _posImporter.Import(dlg.FileName);

            if (data.ReportDate.HasValue)
                slDate.SelectedDate = data.ReportDate.Value.ToDateTime(TimeOnly.MinValue);
            slEmployee.Text = data.Employee ?? slEmployee.Text;
            slShiftNo.Text = data.ShiftOrBatch ?? slShiftNo.Text;

            slCash.Text = data.CashTotal.ToString("0.00");
            slCard.Text = data.CardTotal.ToString("0.00");
            slNetSales.Text = data.NetSales.ToString("0.00");
            slTax.Text = data.TaxTotal.ToString("0.00");

            slError.Text = $"Imported: {data.DetectedType}";

            // Workflow: jump straight to Cash Drop after import
            slDrop.Focus();
            slDrop.SelectAll();
        }
        catch (Exception ex)
        {
            slError.Text = ex.Message;
        }
    }

    private void Shift_ClearImport_Click(object sender, RoutedEventArgs e)
    {
        slError.Text = "";

        // Clear only imported fields (keep date + shift # + payout/drop)
        slEmployee.Text = "";
        slCash.Text = "";
        slCard.Text = "";
        slNetSales.Text = "";
        slTax.Text = "";

        slDrop.Focus();
        slDrop.SelectAll();
    }

    private void SlDrop_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // TAB from Cash Drop should go to Add
        if (e.Key == System.Windows.Input.Key.Tab && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            btnShiftAdd.Focus();
            e.Handled = true;
        }
    }
    private async Task SyncShiftLogCashDropsToCashOnHandAsync(DateOnly date)
    {
        if (_currentStoreId <= 0) return;

        using var db = CreateDb();
        
        // Pull all shift log rows for this day (including corrections), then collapse to the effective rows.
        var rows = await db.ShiftLogs
            .Where(x => x.StoreId == _currentStoreId && x.Date == date)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync();

        var eff = EffectiveRows(
            rows,
            isCorrection: r => r.IsCorrection,
            correctsId:   r => r.CorrectsId,
            id:           r => r.Id,
            createdUtc:   r => r.CreatedUtc
        );

        // Ensure lookup values exist so the grid columns stay readable (Vendor/Purpose columns).
        Vendor? autoVendor = null;
        Purpose? autoPurpose = null;
        try
        {
        autoVendor = await db.Vendors.FirstOrDefaultAsync(v => v.StoreId == _currentStoreId && v.Name == "Shift Log");
        if (autoVendor is null)
        {
            autoVendor = new Vendor { StoreId = _currentStoreId, Name = "Shift Log" };
            db.Vendors.Add(autoVendor);
            await db.SaveChangesAsync();
        }

        autoPurpose = await db.Purposes.FirstOrDefaultAsync(p => p.StoreId == _currentStoreId && p.Name == "Cash Drop");
        if (autoPurpose is null)
        {
            autoPurpose = new Purpose { StoreId = _currentStoreId, Name = "Cash Drop" };
            db.Purposes.Add(autoPurpose);
            await db.SaveChangesAsync();
        }
        }
        catch (Microsoft.Data.SqlClient.SqlException vex) when (vex.Number == 207 || vex.Number == 208)
        {
            System.Diagnostics.Debug.WriteLine($"Vendor/Purpose skipped (error {vex.Number}): {vex.Message}");
        }

        // We create ONE Cash On Hand row per effective shift log row that has a cash drop.
        // Use Reference = SHIFTLOG:<logicalId> so we can upsert and keep it stable even when a correction is created.
        var desired = new List<CashOnHandEntry>();
        foreach (var s in eff)
        {
            if (s is null) continue;
            if (s.CashDropReceived == 0m) continue;

            var logicalId = (s.IsCorrection && s.CorrectsId.HasValue) ? s.CorrectsId.Value : s.Id;
            var reference = $"SHIFTLOG:{logicalId}";

            var desc = $"Auto: Cash Drop from Shift Log (Shift {s.ShiftNo})";
            if (!string.IsNullOrWhiteSpace(s.Employee)) desc += $" - {s.Employee}";

            desired.Add(new CashOnHandEntry
            {
                StoreId = _currentStoreId,
                Date = date,
                IsPayout = false,
                CashAdded = s.CashDropReceived,
                PayoutAmount = 0m,
                VendorId = autoVendor?.Id,
                PurposeId = autoPurpose?.Id,
                Description = desc,
                Reference = reference,
                CreatedUtc = DateTime.UtcNow
            });
        }

        // Load existing auto shift-log cash-drop entries for the day.
        var existing = await db.CashOnHand
            .Where(x => x.StoreId == _currentStoreId && x.Date == date && x.Reference != null && x.Reference.StartsWith("SHIFTLOG:"))
            .ToListAsync();

        // Remove legacy aggregated row if it exists.
        var legacy = await db.CashOnHand
            .Where(x => x.StoreId == _currentStoreId && x.Date == date && x.Reference == "AUTO_SHIFT_DROPS")
            .ToListAsync();
        if (legacy.Count > 0)
        {
            db.CashOnHand.RemoveRange(legacy);
        }

        var desiredRefs = desired.Select(d => d.Reference!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingByRef = existing
            .Where(e => !string.IsNullOrWhiteSpace(e.Reference))
            .ToDictionary(e => e.Reference!, StringComparer.OrdinalIgnoreCase);

        foreach (var d in desired)
        {
            if (existingByRef.TryGetValue(d.Reference!, out var ex))
            {
                ex.CashAdded = d.CashAdded;
                ex.IsPayout = false;
                ex.PayoutAmount = 0m;
                ex.VendorId = d.VendorId;
                ex.PurposeId = d.PurposeId;
                ex.Description = d.Description;
            }
            else
            {
                db.CashOnHand.Add(d);
            }
        }

        // Delete any auto rows for this day that no longer correspond to an effective shift log row.
        foreach (var ex in existing)
        {
            if (string.IsNullOrWhiteSpace(ex.Reference)) continue;
            if (!desiredRefs.Contains(ex.Reference))
            {
                db.CashOnHand.Remove(ex);
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Ensures that recent Shift Log cash drops are mirrored into Cash On Hand.
    /// This makes the behavior consistent even if shift rows were created by imports
    /// or if a prior run crashed before sync ran.
    /// </summary>
    private async Task SyncShiftLogCashDropsToCashOnHandRecentAsync(int daysBack)
    {
        if (_currentStoreId <= 0) return;
        if (_syncingShiftDrops) return;

        _syncingShiftDrops = true;
        try
        {
            var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-Math.Max(1, daysBack)));

            var dates = await CreateDb().ShiftLogs.AsNoTracking()
                .Where(x => x.StoreId == _currentStoreId && x.Date >= from)
                .Select(x => x.Date)
                .Distinct()
                .ToListAsync();

            // Sync oldest -> newest so IDs are stable and the user sees the newest entries at the top.
            foreach (var d in dates.OrderBy(x => x))
            {
                await SyncShiftLogCashDropsToCashOnHandAsync(d);
            }
        }
        finally
        {
            _syncingShiftDrops = false;
        }
    }

    // -------------------- CASH ON HAND CRUD --------------------

    private async void Coh_SetCarryForward_Click(object sender, RoutedEventArgs e)
    {
        cohError.Text = "";
        try
        {
            var amount = ParseMoney(cohCarryForward.Text);
            var today = DateTime.Today;
            var firstDay = new DateOnly(today.Year, today.Month, 1);

            // Upsert a non-correction carry forward row for the first day of the current month.
            using var db = CreateDb();
            var existing = await db.CashOnHand
                .Where(x => x.StoreId == _currentStoreId && x.Date == firstDay && x.Reference == "CARRY_FORWARD" && !x.IsCorrection)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();

            if (existing is null)
            {
                existing = new CashOnHandEntry
                {
                    StoreId = _currentStoreId,
                    Date = firstDay,
                    CashAdded = amount,
                    IsPayout = false,
                    PayoutAmount = 0,
                    VendorId = null,
                    PurposeId = null,
                    Description = "Carry Forward (Start of Month)",
                    Reference = "CARRY_FORWARD",
                    CreatedByUserId = _session.UserId,
                    CreatedByName = _session.DisplayName
                };
                db.CashOnHand.Add(existing);
            }
            else
            {
                existing.CashAdded = amount;
                existing.IsPayout = false;
                existing.PayoutAmount = 0;
                existing.Description = "Carry Forward (Start of Month)";
            }

            await db.SaveChangesAsync();
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            cohError.Text = ex.Message;
        }
    }

    private async void Coh_Add_Click(object sender, RoutedEventArgs e)
    {
        cohError.Text = "";
        try
        {
            if (cohDate.SelectedDate is null) throw new Exception("Date is required.");

            var isPayout = cohIsPayout.SelectedIndex == 1;
            var entry = new CashOnHandEntry
            {
                StoreId = _currentStoreId,
                Date = DateOnly.FromDateTime(cohDate.SelectedDate.Value),
                CashAdded = ParseMoney(cohCashAdded.Text),
                IsPayout = isPayout,
                PayoutAmount = ParseMoney(cohPayoutAmount.Text),
                VendorId = cohVendor.SelectedValue as int?,
                PurposeId = cohPurpose.SelectedValue as int?,
                Description = cohDesc.Text?.Trim() ?? "",
                CreatedByUserId = _session.UserId,
                CreatedByName = _session.DisplayName
            };

            { using var db = CreateDb(); db.CashOnHand.Add(entry); await db.SaveChangesAsync(); }

            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            cohError.Text = ex.Message;
        }
    }

    private async void Coh_Update_Click(object sender, RoutedEventArgs e)
    {
        cohError.Text = "";
        try
        {
            EnsureAdmin("update cash entries");
            if (gridCoh.SelectedItem is not CashOnHandEntry selected)
                throw new Exception("Select a row to update.");

            using var db = CreateDb();
            var entity = await db.CashOnHand.FirstOrDefaultAsync(x => x.Id == selected.Id && x.StoreId == _currentStoreId);
            if (entity is null) throw new Exception("Row not found.");

            if (cohDate.SelectedDate is null) throw new Exception("Date is required.");

            entity.Date = DateOnly.FromDateTime(cohDate.SelectedDate.Value);
            entity.CashAdded = ParseMoney(cohCashAdded.Text);
            entity.IsPayout = cohIsPayout.SelectedIndex == 1;
            entity.PayoutAmount = ParseMoney(cohPayoutAmount.Text);
            entity.VendorId = cohVendor.SelectedValue as int?;
            entity.PurposeId = cohPurpose.SelectedValue as int?;
            entity.Description = cohDesc.Text?.Trim() ?? "";

            await db.SaveChangesAsync();
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            cohError.Text = ex.Message;
        }
    }

    private async void Coh_Delete_Click(object sender, RoutedEventArgs e)
    {
        cohError.Text = "";
        try
        {
            EnsureAdmin("delete cash entries");
            if (gridCoh.SelectedItem is not CashOnHandEntry selected)
                throw new Exception("Select a row to delete.");

            var entity = await CreateDb().CashOnHand.FirstOrDefaultAsync(x => x.Id == selected.Id && x.StoreId == _currentStoreId);
            if (entity is null) return;

            if (System.Windows.MessageBox.Show(this, "Delete selected cash entry?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            { using var db = CreateDb(); db.CashOnHand.Attach(entity); db.CashOnHand.Remove(entity); await db.SaveChangesAsync(); }
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            cohError.Text = ex.Message;
        }
    }

    // -------------------- CHECK PAYOUT CRUD --------------------

    private async void Chk_Add_Click(object sender, RoutedEventArgs e)
    {
        chkError.Text = "";
        try
        {
            if (chkDate.SelectedDate is null) throw new Exception("Date is required.");

            bool cleared = false;
            if (chkCleared.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag is string tag && bool.TryParse(tag, out var b))
                cleared = b;

            var entry = new CheckPayout
            {
                StoreId = _currentStoreId,
                Date = DateOnly.FromDateTime(chkDate.SelectedDate.Value),
                VendorName = chkVendor.Text?.Trim() ?? "",
                Description = chkDesc.Text?.Trim() ?? "",
                CheckNumber = chkNumber.Text?.Trim() ?? "",
                CheckAmount = ParseMoney(chkAmount.Text),
                Cleared = cleared,
                CreatedByUserId = _session.UserId,
                CreatedByName = _session.DisplayName
            };

            { using var db = CreateDb(); db.CheckPayouts.Add(entry); await db.SaveChangesAsync(); }

            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            chkError.Text = ex.Message;
        }
    }

    private async void Chk_ToggleCleared_Click(object sender, RoutedEventArgs e)
    {
        chkError.Text = "";
        try
        {
            EnsureAdmin("toggle cleared status");
            if (gridChk.SelectedItem is not CheckPayout selected)
                throw new Exception("Select a row to update.");

            var entity = await CreateDb().CheckPayouts.FirstOrDefaultAsync(x => x.Id == selected.Id && x.StoreId == _currentStoreId);
            if (entity is null) return;

            entity.Cleared = !entity.Cleared;
            await CreateDb().SaveChangesAsync();

            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            chkError.Text = ex.Message;
        }
    }

    private async void Chk_Delete_Click(object sender, RoutedEventArgs e)
    {
        chkError.Text = "";
        try
        {
            EnsureAdmin("delete check payouts");
            if (gridChk.SelectedItem is not CheckPayout selected)
                throw new Exception("Select a row to delete.");

            var entity = await CreateDb().CheckPayouts.FirstOrDefaultAsync(x => x.Id == selected.Id && x.StoreId == _currentStoreId);
            if (entity is null) return;

            if (System.Windows.MessageBox.Show(this, "Delete selected check payout?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            { using var db = CreateDb(); db.CheckPayouts.Attach(entity); db.CheckPayouts.Remove(entity); await db.SaveChangesAsync(); }
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            chkError.Text = ex.Message;
        }
    }

    private async void Chk_Print_Click(object sender, RoutedEventArgs e)
    {
        chkError.Text = "";
        try
        {
            EnsureAdmin("print checks");
            if (chkDate.SelectedDate is null) throw new Exception("Select a date.");
            var payee = chkVendor.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(payee)) throw new Exception("Enter a payee/vendor name.");
            var amount = ParseMoney(chkAmount.Text);
            if (amount <= 0) throw new Exception("Enter a valid amount.");

            var req = new CheckPrintRequest
            {
                Date = chkDate.SelectedDate.Value,
                PayeeName = payee,
                Amount = (decimal)amount,
                Memo = chkDesc.Text?.Trim(),
                Reference = chkNumber.Text?.Trim(),
                Address = null
            };

            // Print first
            _checkPrintService.PrintCheck(req);

            // Then record it in the Check Payouts table (so it shows up immediately)
            await Task.Delay(50);
            Chk_Add_Click(sender, e);
        }
        catch (Exception ex)
        {
            chkError.Text = ex.Message;
        }
    }



    // -------------------- CORRECTIONS (NO EDITING HISTORY) --------------------

    private async void Shift_Correction_Click(object sender, RoutedEventArgs e)
    {
        slError.Text = "";
        try
        {
            if (gridShift.SelectedItem is not ShiftLogEntry selected)
                throw new Exception("Select a shift row to correct.");
            if (selected.IsCorrection)
                throw new Exception("You selected a correction row. Please select the original row to correct.");

            var win = new ShiftCorrectionWindow(selected, _session.UserId, _session.DisplayName) { Owner = this };
            var ok = win.ShowDialog();
            if (ok != true || win.ResultEntry is null)
                return;

            { using var db = CreateDb(); db.ShiftLogs.Add(win.ResultEntry); await db.SaveChangesAsync(); }

            // Auto cash-drop daily row update (original date + possibly new date).
            try
            {
                await SyncShiftLogCashDropsToCashOnHandAsync(selected.Date);
                if (win.ResultEntry.Date != selected.Date)
                    await SyncShiftLogCashDropsToCashOnHandAsync(win.ResultEntry.Date);
            }
            catch (Microsoft.Data.SqlClient.SqlException syncEx) when (syncEx.Number == 207 || syncEx.Number == 208)
            { System.Diagnostics.Debug.WriteLine($"SyncShiftLog skipped after correction: {syncEx.Message}"); }
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            slError.Text = ex.Message;
        }
    }

    private async void Coh_Correction_Click(object sender, RoutedEventArgs e)
    {
        cohError.Text = "";
        try
        {
            if (gridCoh.SelectedItem is not CashOnHandEntry selected)
                throw new Exception("Select a cash row to correct.");
            if (selected.IsCorrection)
                throw new Exception("You selected a correction row. Please select the original row to correct.");

            var vendors = await CreateDb().Vendors.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
            var purposes = await CreateDb().Purposes.AsNoTracking().OrderBy(x => x.Name).ToListAsync();

            var win = new CashCorrectionWindow(selected, _session.UserId, _session.DisplayName, vendors, purposes) { Owner = this };
            var ok = win.ShowDialog();
            if (ok != true || win.ResultEntry is null)
                return;

            { using var db = CreateDb(); db.CashOnHand.Add(win.ResultEntry); await db.SaveChangesAsync(); }
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            cohError.Text = ex.Message;
        }
    }

    private async void Chk_Correction_Click(object sender, RoutedEventArgs e)
    {
        chkError.Text = "";
        try
        {
            if (gridChk.SelectedItem is not CheckPayout selected)
                throw new Exception("Select a check payout row to correct.");
            if (selected.IsCorrection)
                throw new Exception("You selected a correction row. Please select the original row to correct.");

            var win = new CheckCorrectionWindow(selected, _session.UserId, _session.DisplayName) { Owner = this };
            var ok = win.ShowDialog();
            if (ok != true || win.ResultEntry is null)
                return;

            { using var db = CreateDb(); db.CheckPayouts.Add(win.ResultEntry); await db.SaveChangesAsync(); }
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            chkError.Text = ex.Message;
        }
    }

    // -------------------- LISTS CRUD --------------------

    private async void Vendor_Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("edit vendor list");
            var name = vendorName.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return;

            if (await CreateDb().Vendors.AnyAsync(x => x.StoreId == _currentStoreId && x.Name == name))
                throw new Exception("Vendor already exists.");

            { using var db = CreateDb(); db.Vendors.Add(new Vendor { StoreId = _currentStoreId, Name = name }); await db.SaveChangesAsync(); }
            vendorName.Text = "";
            await LoadLookupsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Vendor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Vendor_Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("edit vendor list");
            if (gridVendors.SelectedItem is not Vendor v) return;
            var entity = await CreateDb().Vendors.FirstOrDefaultAsync(x => x.Id == v.Id && x.StoreId == _currentStoreId);
            if (entity is null) return;

            { using var db = CreateDb(); db.Vendors.Attach(entity); db.Vendors.Remove(entity); await db.SaveChangesAsync(); }
            await LoadLookupsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Vendor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Purpose_Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("edit purpose list");
            var name = purposeName.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return;

            if (await CreateDb().Purposes.AnyAsync(x => x.StoreId == _currentStoreId && x.Name == name))
                throw new Exception("Purpose already exists.");

            { using var db = CreateDb(); db.Purposes.Add(new Purpose { StoreId = _currentStoreId, Name = name }); await db.SaveChangesAsync(); }
            purposeName.Text = "";
            await LoadLookupsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Purpose", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Purpose_Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("edit purpose list");
            if (gridPurposes.SelectedItem is not Purpose p) return;
            var entity = await CreateDb().Purposes.FirstOrDefaultAsync(x => x.Id == p.Id && x.StoreId == _currentStoreId);
            if (entity is null) return;

            { using var db = CreateDb(); db.Purposes.Attach(entity); db.Purposes.Remove(entity); await db.SaveChangesAsync(); }
            await LoadLookupsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Purpose", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    


    // -------------------- PURCHASES / COSTS / ALERTS --------------------

    private void ClearInvoiceForm()
    {
        _selectedInvoiceId = null;
        _lastInvoiceImport = null;
        _pendingImportedInvoice = null;
        _pendingInvoiceSourceFile = null;
                invImportStatus.Text = "";
        invError.Text = "";
        try { btnInvImportAll.Visibility = Visibility.Collapsed; } catch { }

        // Reset to read-only import mode by default.
        try { chkInvAllowEdits.IsChecked = false; } catch { }
        SetInvoiceEditMode(false);

        try { invDate.SelectedDate = DateTime.Today; } catch { }

        try
        {
            invVendor.SelectedItem = null;
        }
        catch { }

        invVendorName.Text = "";
        invNumber.Text = "";
        invTotal.Text = "";
        invNotes.Text = "";
        invFilePath.Text = "";

        _invoiceLines.Clear();

        try { gridInvoices.SelectedItem = null; } catch { }
    }

    private void SelectInvoiceInGrid(int invoiceId)
    {
        try
        {
            if (gridInvoices.ItemsSource is not System.Collections.IEnumerable items) return;
            foreach (var it in items)
            {
                if (it is PurchaseInvoice pi && pi.Id == invoiceId)
                {
                    gridInvoices.SelectedItem = pi;
                    gridInvoices.ScrollIntoView(pi);
                    break;
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private void Inv_AllowEdits_Changed(object sender, RoutedEventArgs e)
    {
        SetInvoiceEditMode(chkInvAllowEdits.IsChecked == true);
        if (chkInvAllowEdits.IsChecked == true && _invoiceLines.Count == 0)
            _invoiceLines.Add(new PurchaseInvoiceLine());
    }

    private void SetInvoiceEditMode(bool allow)
    {
        try
        {
            // Purchases tab is now manual-entry focused.
            // Keep the main fields editable at all times.
            invDate.IsEnabled = true;
            invVendorName.IsReadOnly = false;
            invNumber.IsReadOnly = false;
            invTotal.IsReadOnly = false;
            invNotes.IsReadOnly = false;

            // Invoice lines are not used in manual mode.
            gridInvoiceLines.IsReadOnly = true;
            gridInvoiceLines.CanUserAddRows = false;
            gridInvoiceLines.CanUserDeleteRows = false;
        }
        catch { }
    }

    private PurchaseInvoiceLine[] GetInvoiceLinesFromGrid()
    {
        return _invoiceLines
            .Where(l => !string.IsNullOrWhiteSpace(l.ProductName))
            .Select(CloneInvoiceLine)
            .ToArray();
    }

    private static PurchaseInvoiceLine CloneInvoiceLine(PurchaseInvoiceLine l)
    {
        // Preserve structured fields (UPC/ORD/SHIP/etc.) when importing/saving.
        var qty = l.Quantity;
        if (qty <= 0m && l.ShipQuantity > 0m) qty = l.ShipQuantity;

        return new PurchaseInvoiceLine
        {
            ItemCode = (l.ItemCode ?? "").Trim(),
            ProductName = (l.ProductName ?? "").Trim(),
            OrdQuantity = l.OrdQuantity,
            ShipQuantity = l.ShipQuantity,
            VolumeMl = l.VolumeMl,
            Tax = l.Tax,
            Price = l.Price,
            Amount = l.Amount,
            Quantity = qty,
            UnitCost = l.UnitCost
        };
    }

    private async Task LoadPurchasesModuleAsync()
    {
        try
        {
            invError.Text = "";

            gridInvoices.ItemsSource = await _purchaseService.GetInvoicesAsync(_currentStoreId);
            gridProductCosts.ItemsSource = await _purchaseService.GetProductCostsAsync(_currentStoreId);
            gridPriceAlerts.ItemsSource = await _purchaseService.GetAlertsAsync(_currentStoreId);

            await UpdateStoreHeaderAsync();
        }
        catch (Exception ex)
        {
            invError.Text = ex.Message;
        }
    }

    private async void Inv_Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select invoice file",
                Filter = "All Files|*.*|PDF|*.pdf|Excel|*.xlsx;*.xls|Images|*.png;*.jpg;*.jpeg",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() == true)
            {
                invFilePath.Text = dlg.FileName;
                await ImportInvoiceFromFileAsync(dlg.FileName);
            }
        }
        catch (Exception ex)
        {
            invError.Text = ex.Message;
        }
    }

    private async void Inv_ImportAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            invError.Text = "";

            var batch = _lastInvoiceImport?.Invoices;
            if (batch is null || batch.Count <= 1)
            {
                invImportStatus.Text = "No batch invoices loaded. Use 'Import Invoice (Auto)' first.";
                return;
            }

            var filePath = invFilePath.Text;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                invImportStatus.Text = "Invoice file not found.";
                return;
            }

            invImportStatus.Text = $"Importing {batch.Count} invoices...";
            var importedIds = await ImportBatchInvoicesAsync(filePath, batch);
            await LoadPurchasesModuleAsync();
            SelectLatestImportedInvoice(importedIds);
            invImportStatus.Text = $"Imported {batch.Count} invoices from the uploaded PDF.";
        }
        catch (Exception ex)
        {
            invImportStatus.Text = "Batch import failed.";
            invError.Text = ex.Message;
        }
    }

    private async Task ImportInvoiceFromFileAsync(string filePath)
    {
        try
        {
            invError.Text = "";
            invImportStatus.Text = "Reading invoice...";

            var selectedVendorName = "";
            if (invVendor.SelectedItem is Vendor v)
                selectedVendorName = v.Name ?? "";

            var res = await _invoiceImportService.ImportAsync(filePath, selectedVendorName);
            _lastInvoiceImport = res;
            _pendingInvoiceSourceFile = filePath;

            if (res.Invoices is null || res.Invoices.Count == 0)
            {
                invImportStatus.Text = "No invoice data detected in this PDF.";
                _pendingImportedInvoice = null;
                _invoiceLines.Clear();
                return;
            }

            // If the PDF contains multiple invoice pages, allow batch import.
            btnInvImportAll.Visibility = res.Invoices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

            _pendingImportedInvoice = res.Invoices[0];
            PopulateInvoiceFormFromImported(_pendingImportedInvoice, filePath);

            invImportStatus.Text = res.Invoices.Count > 1
                ? $"Batch detected ({res.Invoices.Count} invoices). Preview loaded. Click Add Invoice to save this one or Import All to save all." 
                : "Invoice loaded. Review and click Add Invoice to save.";
        }
        catch (Exception ex)
        {
            invError.Text = ex.Message;
            invImportStatus.Text = "";
            _pendingImportedInvoice = null;
            _pendingInvoiceSourceFile = null;
            _invoiceLines.Clear();
        }
    }

    private void PopulateInvoiceFormFromImported(ImportedInvoice inv, string sourceFilePath)
    {
        // File path
        invFilePath.Text = sourceFilePath;

        // Prefer dropdown vendor (since that's how you choose vendor-specific parsing)
        if (invVendor.SelectedItem is Vendor selected)
        {
            invVendorName.Text = selected.Name;
        }
        else
        {
            invVendorName.Text = (inv.VendorName ?? "").Trim();
        }

        // Invoice date
        var date = inv.InvoiceDate ?? DateOnly.FromDateTime(DateTime.Today);
        invDate.SelectedDate = date.ToDateTime(TimeOnly.MinValue);

        // Invoice number
        invNumber.Text = (inv.InvoiceNumber ?? "").Trim();

        // Total
        if (inv.Total is decimal t)
            invTotal.Text = t.ToString("0.00", CultureInfo.InvariantCulture);
        else
            invTotal.Text = "";

        // Notes (keep whatever user typed)

        // Lines -> grid
        _invoiceLines.Clear();
        var rawLines = inv.Lines ?? new List<PurchaseInvoiceLine>();

        foreach (var src in rawLines)
        {
            if (string.IsNullOrWhiteSpace(src.ProductName)) continue;

            var line = CloneInvoiceLine(src);

            // Normalize quantity for vendor formats that use Ship/Ord columns.
            if (line.Quantity <= 0)
            {
                if (line.ShipQuantity > 0) line.Quantity = line.ShipQuantity;
                else if (line.OrdQuantity > 0) line.Quantity = line.OrdQuantity;
            }

            // Normalize unit cost
            if (line.UnitCost <= 0)
            {
                decimal price = line.Price is decimal p ? p : 0m;
                decimal amount = line.Amount is decimal a ? a : 0m;

                if (price > 0m) line.UnitCost = price;
                else if (amount > 0m && line.Quantity > 0m)
                    line.UnitCost = Math.Round(amount / line.Quantity, 2);
            }

            // If amount not set, compute
            if (!(line.Amount is decimal amtCur && amtCur > 0m) && line.Quantity > 0m && line.UnitCost > 0m)
                line.Amount = Math.Round(line.Quantity * line.UnitCost, 2);

            // Skip lines that still have no usable quantity.
            if (line.Quantity <= 0 && line.ShipQuantity <= 0 && line.OrdQuantity <= 0)
                continue;

            _invoiceLines.Add(line);
        }

        // If total was missing, compute from lines as a fallback.
        if (string.IsNullOrWhiteSpace(invTotal.Text) && _invoiceLines.Count > 0)
        {
            decimal computed = _invoiceLines.Sum(x =>
            {
                var amt = x.Amount;
                if (amt.HasValue && amt.Value > 0m) return amt.Value;
                if (x.Quantity > 0m && x.UnitCost > 0m) return x.Quantity * x.UnitCost;
                return 0m;
            });
            if (computed > 0m)
                invTotal.Text = computed.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Select invoice in list after it's saved (Add Invoice). For now this is just a preview.
    }

    private async Task<List<int>> ImportBatchInvoicesAsync(string filePath, List<ImportedInvoice> batch)
    {
        var addedIds = new List<int>();

        // Load vendors once for matching.
        var vendors = await CreateDb().Vendors
            .Where(v => v.StoreId == _currentStoreId)
            .AsNoTracking()
            .ToListAsync();

        int added = 0;
        var usedNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inv in batch)
        {
            var vendorName = (inv.VendorName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(vendorName)) vendorName = (invVendorName.Text ?? "").Trim();

            int? vendorId = null;
            if (!string.IsNullOrWhiteSpace(vendorName))
            {
                var norm = NormalizeName(vendorName);
                var match = vendors.FirstOrDefault(v => NormalizeName(v.Name) == norm)
                            ?? vendors.FirstOrDefault(v => NormalizeName(v.Name).Contains(norm) || norm.Contains(NormalizeName(v.Name)));
                vendorId = match?.Id;
            }

            var date = inv.InvoiceDate ?? DateOnly.FromDateTime(DateTime.Today);
            var invNum = (inv.InvoiceNumber ?? "").Trim();
            if (string.IsNullOrWhiteSpace(invNum))
                invNum = $"INV-{date:yyyyMMdd}-{added + 1:00}";

            if (inv.PageNumber is int p && p > 0)
            {
                // Ensure uniqueness inside the batch.
                var candidate = invNum;
                if (!usedNums.Add(candidate))
                    candidate = $"{invNum}-p{p}";
                invNum = candidate;
            }

            var lines = (inv.Lines ?? new List<PurchaseInvoiceLine>())
                .Where(l => !string.IsNullOrWhiteSpace(l.ProductName))
                .Select(CloneInvoiceLine)
                .Select(l =>
                {
                    // Normalize quantity (some vendors use Ship/Ord columns)
                    if (l.Quantity <= 0)
                    {
                        if (l.ShipQuantity > 0) l.Quantity = l.ShipQuantity;
                        else if (l.OrdQuantity > 0) l.Quantity = l.OrdQuantity;
                    }

                    // Normalize unit cost
                    if (l.UnitCost <= 0)
                    {
                        decimal price = l.Price is decimal p ? p : 0m;
                        decimal amount = l.Amount is decimal a ? a : 0m;

                        if (price > 0m) l.UnitCost = price;
                        else if (amount > 0m && l.Quantity > 0m) l.UnitCost = Math.Round(amount / l.Quantity, 2);
                    }

                    if (!(l.Amount is decimal amtCur && amtCur > 0m) && l.Quantity > 0m && l.UnitCost > 0m)
                        l.Amount = Math.Round(l.Quantity * l.UnitCost, 2);

                    return l;
                })
                .Where(l => l.Quantity > 0 || l.ShipQuantity > 0 || l.OrdQuantity > 0)
                .ToArray();

            // Skip empty invoices (some PDFs include a cover page or a terms-only page).
            if (lines.Length == 0 && inv.Total is null && string.IsNullOrWhiteSpace(invNum))
                continue;

            var total = inv.Total;
            if (total is null && lines.Length > 0)
                total = lines.Sum(x => x.Quantity * x.UnitCost);

            var created = await _purchaseService.AddInvoiceAsync(
                storeId: _currentStoreId,
                vendorId: vendorId,
                vendorName: vendorName,
                invoiceNumber: invNum,
                invoiceDate: date,
                total: total ?? 0m,
                notes: "",
                sourceFilePath: (inv.PageNumber is int pg && pg > 0) ? $"{filePath}#page{pg}" : filePath,
                lines: lines,
                userId: _session.UserId,
	                userName: string.IsNullOrWhiteSpace(_session.DisplayName) ? _session.Username : _session.DisplayName);

            addedIds.Add(created.Id);
            added++;
        }

        // Price alerts removed from header
        // try
        // {
        //     var unread = await _purchaseService.GetUnreadAlertCountAsync(_currentStoreId);
        //     lblPriceAlerts.Text = unread > 0 ? $"Price Alerts: {unread} unread" : "Price Alerts: none";
        // }
        // catch { }

        invImportStatus.Text = $"Imported {added} invoices.";
        return addedIds;
    }



    private void SelectLatestImportedInvoice(List<int> importedIds)
    {
        try
        {
            if (importedIds is null || importedIds.Count == 0) return;
            var targetId = importedIds[^1];

            // gridInvoices.ItemsSource is populated in LoadPurchasesModuleAsync.
            if (gridInvoices.ItemsSource is System.Collections.IEnumerable enumerable)
            {
                object? found = null;
                foreach (var it in enumerable)
                {
                    if (it is PurchaseInvoice pi && pi.Id == targetId)
                    {
                        found = pi;
                        break;
                    }
                }
                if (found is not null)
                {
                    gridInvoices.SelectedItem = found;
                    gridInvoices.ScrollIntoView(found);
                }
            }
        }
        catch
        {
            // no-op
        }
    }
    private static string NormalizeName(string? s)
    {
        s = (s ?? "").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return s.ToUpperInvariant();
    }

    private void Inv_OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = (invFilePath.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                System.Windows.MessageBox.Show(this, "Invoice file not found.", "Invoice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Invoice", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Inv_ImportPdf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Invoice PDF to Import",
                Filter = "PDF Files (*.pdf)|*.pdf",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() != true)
                return;

            // Extract text from PDF
            var rawText = await Task.Run(() => ExtractPdfText(dlg.FileName));
            
            if (string.IsNullOrWhiteSpace(rawText))
            {
                System.Windows.MessageBox.Show(this, 
                    "Could not extract text from the PDF.\n\nThis may be a scanned image PDF which requires OCR.", 
                    "Import Invoice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Extract Invoice Number
            var invoiceNumber = ExtractInvoiceNumber(rawText);
            
            // Extract Date
            var invoiceDate = ExtractInvoiceDate(rawText);
            
            // Extract Total/Grand Total
            var invoiceTotal = ExtractInvoiceTotal(rawText);
            
            // Extract Vendor Name
            var vendorName = ExtractVendorName(rawText);

            // Populate the form
            if (invDate is not null && invoiceDate.HasValue)
                invDate.SelectedDate = invoiceDate.Value.ToDateTime(TimeOnly.MinValue);
            
            if (invNumber is not null && !string.IsNullOrWhiteSpace(invoiceNumber))
                invNumber.Text = invoiceNumber;
            
            if (invTotal is not null && invoiceTotal > 0)
                invTotal.Text = invoiceTotal.ToString("F2");
            
            // Try to find and select vendor
            if (invVendor is not null && !string.IsNullOrWhiteSpace(vendorName))
            {
                var vendors = invVendor.ItemsSource as System.Collections.IEnumerable;
                bool found = false;
                if (vendors is not null)
                {
                    foreach (var item in vendors)
                    {
                        if (item is Vendor v && v.Name.IndexOf(vendorName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            invVendor.SelectedItem = item;
                            found = true;
                            break;
                        }
                    }
                }
                
                // If not found, add new vendor
                if (!found)
                {
                    var existingVendor = await CreateDb().Vendors
                        .Where(v => v.StoreId == _currentStoreId && v.Name.ToLower().Contains(vendorName.ToLower()))
                        .FirstOrDefaultAsync();

                    if (existingVendor is not null)
                    {
                        invVendor.SelectedValue = existingVendor.Id;
                    }
                    else
                    {
                        var newVendor = new Vendor { StoreId = _currentStoreId, Name = vendorName };
                        { using var db = CreateDb(); db.Vendors.Add(newVendor); await db.SaveChangesAsync(); }
                        
                        invVendor.ItemsSource = await CreateDb().Vendors
                            .Where(v => v.StoreId == _currentStoreId)
                            .OrderBy(v => v.Name)
                            .ToListAsync();
                        invVendor.SelectedValue = newVendor.Id;
                    }
                }
            }

            // Show what was extracted
            var msg = "Invoice data extracted:\n\n";
            msg += $"• Invoice #: {(string.IsNullOrWhiteSpace(invoiceNumber) ? "(not found)" : invoiceNumber)}\n";
            msg += $"• Date: {(invoiceDate.HasValue ? invoiceDate.Value.ToString("MM/dd/yyyy") : "(not found)")}\n";
            msg += $"• Total: {(invoiceTotal > 0 ? invoiceTotal.ToString("C") : "(not found)")}\n";
            msg += $"• Vendor: {(string.IsNullOrWhiteSpace(vendorName) ? "(not found)" : vendorName)}\n\n";
            msg += "Click 'Add Invoice' to save this entry.";
            
            System.Windows.MessageBox.Show(this, msg, "Import Invoice", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Error importing invoice: {ex.Message}", "Import Invoice", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string ExtractPdfText(string filePath)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(filePath);
            var sb = new System.Text.StringBuilder();
            foreach (var page in doc.GetPages())
            {
                sb.AppendLine(page.Text);
            }
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private string? ExtractInvoiceNumber(string text)
    {
        // Pattern 1: Skygate - "INVOICE NO." then (possibly date) then invoice number like S035850
        // The PDF structure is: "DATE\n\nINVOICE NO.\n\n12/8/2025\n\nS035850"
        // So we need to look for S followed by 6 digits anywhere after INVOICE NO.
        var match1 = System.Text.RegularExpressions.Regex.Match(text, 
            @"INVOICE\s+NO\.?.*?([S]\d{6})", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (match1.Success) return match1.Groups[1].Value;

        // Pattern 2: AK Wholesale - "Invoice No" then newlines then S followed by digits
        var match2 = System.Text.RegularExpressions.Regex.Match(text, 
            @"Invoice\s+No.*?([S]\d{6})", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (match2.Success) return match2.Groups[1].Value;

        // Pattern 3: American Distributors - number followed by "TRANSACTION NO." (reversed in PDF)
        var match3 = System.Text.RegularExpressions.Regex.Match(text, 
            @"(\d{7,10})TRANSACTION\s+NO", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match3.Success) return match3.Groups[1].Value;

        // Pattern 4: Just look for S followed by 6 digits anywhere (Skygate/AK invoice format)
        var match4 = System.Text.RegularExpressions.Regex.Match(text, 
            @"\b(S\d{6})\b");
        if (match4.Success) return match4.Groups[1].Value;

        // Pattern 5: 8-digit number that could be invoice/transaction number
        var match5 = System.Text.RegularExpressions.Regex.Match(text, 
            @"\b(\d{8})\b");
        if (match5.Success) return match5.Groups[1].Value;

        return null;
    }

    private DateOnly? ExtractInvoiceDate(string text)
    {
        // Pattern 1: American Distributors - "DD MMM YYYYDATE" (value before label)
        var match1 = System.Text.RegularExpressions.Regex.Match(text, 
            @"(\d{1,2}\s+(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)\s+\d{4})DATE", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match1.Success)
        {
            if (DateTime.TryParse(match1.Groups[1].Value, out var dt1))
                return DateOnly.FromDateTime(dt1);
        }

        // Pattern 2: Skygate - Look for date after "INVOICE NO." label
        // Format: "INVOICE NO.\n\n12/8/2025"
        var match2 = System.Text.RegularExpressions.Regex.Match(text, 
            @"INVOICE\s+NO\.?\s*\n+\s*(\d{1,2}/\d{1,2}/\d{4})", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match2.Success)
        {
            if (DateTime.TryParse(match2.Groups[1].Value, out var dt2))
                return DateOnly.FromDateTime(dt2);
        }

        // Pattern 3: AK Wholesale - "DATE\n\n01/13/26" with 2-digit year
        var match3 = System.Text.RegularExpressions.Regex.Match(text, 
            @"DATE\s*\n+\s*(\d{1,2}/\d{1,2}/\d{2})\b", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match3.Success)
        {
            var dateStr = match3.Groups[1].Value;
            var parts = dateStr.Split('/');
            if (parts.Length == 3 && parts[2].Length == 2)
            {
                var year = int.Parse(parts[2]);
                year = year < 50 ? 2000 + year : 1900 + year;
                dateStr = $"{parts[0]}/{parts[1]}/{year}";
            }
            if (DateTime.TryParse(dateStr, out var dt3))
                return DateOnly.FromDateTime(dt3);
        }

        // Pattern 4: ORDER DATE or SHIP DATE with full year
        var match4 = System.Text.RegularExpressions.Regex.Match(text, 
            @"(?:ORDER|SHIP)\s+DATE\s*\n+\s*(\d{1,2}/\d{1,2}/\d{4})", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match4.Success)
        {
            if (DateTime.TryParse(match4.Groups[1].Value, out var dt4))
                return DateOnly.FromDateTime(dt4);
        }

        // Pattern 5: Generic - first occurrence of MM/DD/YYYY
        var match5 = System.Text.RegularExpressions.Regex.Match(text, 
            @"(\d{1,2}/\d{1,2}/\d{4})");
        if (match5.Success)
        {
            if (DateTime.TryParse(match5.Groups[1].Value, out var dt5))
            {
                if (dt5.Year >= DateTime.Now.Year - 2 && dt5.Year <= DateTime.Now.Year + 1)
                    return DateOnly.FromDateTime(dt5);
            }
        }

        return DateOnly.FromDateTime(DateTime.Today);
    }

    private decimal ExtractInvoiceTotal(string text)
    {
        // Pattern 1: American Distributors - "1,110.00INVOICE BALANCE" (value before label)
        var match1 = System.Text.RegularExpressions.Regex.Match(text, 
            @"([\d,]+\.\d{2})INVOICE\s+BALANCE", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match1.Success)
        {
            var numStr = match1.Groups[1].Value.Replace(",", "");
            if (decimal.TryParse(numStr, out var total1) && total1 > 50)
                return total1;
        }

        // Pattern 2: Skygate/AK - "Balance $795.94" or "Balance\t$795.94"
        var match2 = System.Text.RegularExpressions.Regex.Match(text, 
            @"Balance\s+\$?([\d,]+\.\d{2})", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match2.Success)
        {
            var numStr = match2.Groups[1].Value.Replace(",", "");
            if (decimal.TryParse(numStr, out var total2) && total2 > 100)
                return total2;
        }

        // Pattern 3: "Total" at beginning of line followed by amount
        var match3 = System.Text.RegularExpressions.Regex.Match(text, 
            @"(?:^|\n)\s*Total\s+\$?([\d,]+\.\d{2})", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
        if (match3.Success)
        {
            var numStr = match3.Groups[1].Value.Replace(",", "");
            if (decimal.TryParse(numStr, out var total3) && total3 > 100)
                return total3;
        }

        // Pattern 4: Sub-Total (AK Wholesale uses this)
        var match4 = System.Text.RegularExpressions.Regex.Match(text, 
            @"Sub-Total\s+\$?([\d,]+\.\d{2})", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match4.Success)
        {
            var numStr = match4.Groups[1].Value.Replace(",", "");
            if (decimal.TryParse(numStr, out var total4) && total4 > 100)
                return total4;
        }

        // Pattern 5: Find the largest dollar amount > $100 that's likely a total
        var allAmounts = System.Text.RegularExpressions.Regex.Matches(text, 
            @"\$?([\d,]+\.\d{2})");
        
        decimal maxReasonableTotal = 0;
        foreach (System.Text.RegularExpressions.Match m in allAmounts)
        {
            var numStr = m.Groups[1].Value.Replace(",", "");
            if (decimal.TryParse(numStr, out var amt) && amt > 100 && amt < 50000 && amt > maxReasonableTotal)
            {
                maxReasonableTotal = amt;
            }
        }
        
        return maxReasonableTotal;
    }

    private string? ExtractVendorName(string text)
    {
        // Check for known vendor patterns
        if (text.IndexOf("AMERICAN DISTRIBUTORS", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("AmericanDistributorsllc", StringComparison.OrdinalIgnoreCase) >= 0)
            return "American Distributors";

        if (text.IndexOf("SKYGATE WHOLESALE", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("skygatewholesale", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Skygate Wholesale";

        if (text.IndexOf("AK Wholesale Inc", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("akwholesale", StringComparison.OrdinalIgnoreCase) >= 0)
            return "AK Wholesale Inc";

        if (text.IndexOf("HS Wholesale", StringComparison.OrdinalIgnoreCase) >= 0)
            return "HS Wholesale";

        if (text.IndexOf("SAFA Goods", StringComparison.OrdinalIgnoreCase) >= 0)
            return "SAFA Goods";

        // Try to find vendor from header
        var lines = text.Split('\n').Take(20).ToArray();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 5 || trimmed.Length > 50) continue;
            
            var lower = trimmed.ToLower();
            if (lower.Contains("invoice") || lower.Contains("date") || 
                lower.Contains("total") || lower.Contains("page") ||
                lower.Contains("license") || lower.Contains("tel:"))
                continue;
            
            if (trimmed.EndsWith("LLC", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith("Inc", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Wholesale", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Distributors", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
    }

    private async void Inv_Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            invError.Text = "";

            var dt = invDate.SelectedDate ?? DateTime.Today;
            var invoiceDate = DateOnly.FromDateTime(dt);

            int? vendorId = null;
            var vendorName = (invVendorName.Text ?? "").Trim();

            if (invVendor.SelectedItem is Vendor v)
                vendorName = v.Name;

            if (string.IsNullOrWhiteSpace(vendorName))
                throw new Exception("Vendor name is required.");

            var invoiceNumber = (invNumber.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(invoiceNumber))
                invoiceNumber = $"INV-{DateTime.Now:yyyyMMdd-HHmm}";

            var total = ParseMoney(invTotal.Text);
            var notes = (invNotes.Text ?? "").Trim();
            var file = (invFilePath.Text ?? "").Trim();

            var lines = GetInvoiceLinesFromGrid();
            if (lines.Length == 0) lines = Array.Empty<PurchaseInvoiceLine>();

            // Use CreateDb() (store-aware) instead of _purchaseService (which uses default DB)
            using var db = CreateDb();

            // Resolve vendor in the TARGET database
            var targetVendor = await db.Vendors
                .FirstOrDefaultAsync(x => x.Name == vendorName && x.StoreId == _currentStoreId);

            if (targetVendor != null)
            {
                vendorId = targetVendor.Id;
            }
            else
            {
                // Also try without StoreId filter (some DBs may not use StoreId on vendors)
                targetVendor = await db.Vendors
                    .FirstOrDefaultAsync(x => x.Name == vendorName);

                if (targetVendor != null)
                {
                    vendorId = targetVendor.Id;
                }
                else
                {
                    // Create vendor in target DB
                    var newVendor = new Vendor { Name = vendorName, StoreId = _currentStoreId };
                    db.Vendors.Add(newVendor);
                    await db.SaveChangesAsync();
                    vendorId = newVendor.Id;
                }
            }

            // Create invoice directly in the store-specific database
            var invoice = new PurchaseInvoice
            {
                StoreId = _currentStoreId,
                InvoiceDate = invoiceDate,
                VendorId = vendorId,
                VendorName = vendorName,
                InvoiceNumber = invoiceNumber,
                Total = total,
                Notes = notes,
                CreatedUtc = DateTime.UtcNow
            };
            db.PurchaseInvoices.Add(invoice);
            await db.SaveChangesAsync();

            // Add line items if any
            if (lines.Length > 0)
            {
                foreach (var line in lines)
                {
                    line.PurchaseInvoiceId = invoice.Id;
                }
                db.PurchaseInvoiceLines.AddRange(lines);
                await db.SaveChangesAsync();
            }

            ClearInvoiceForm();
            await LoadPurchasesModuleAsync();
            await RefreshDashboardAsync();
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (ex.InnerException != null)
                msg += "\n" + ex.InnerException.Message;
            invError.Text = msg;
        }
    }

    private async void Inv_Update_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("update invoices");
            if (_selectedInvoiceId is null) throw new Exception("Select an invoice to update.");

            invError.Text = "";

            var dt = invDate.SelectedDate ?? DateTime.Today;
            var invoiceDate = DateOnly.FromDateTime(dt);

            int? vendorId = null;
            var vendorName = (invVendorName.Text ?? "").Trim();

            if (invVendor.SelectedItem is Vendor v)
            {
                vendorId = v.Id;
                vendorName = v.Name;
            }

            if (string.IsNullOrWhiteSpace(vendorName))
                throw new Exception("Vendor name is required.");

            var invoiceNumber = (invNumber.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(invoiceNumber))
                throw new Exception("Invoice number is required.");

            var total = ParseMoney(invTotal.Text);
            var notes = (invNotes.Text ?? "").Trim();
            var file = (invFilePath.Text ?? "").Trim();

            var lines = GetInvoiceLinesFromGrid();

            // Manual purchase history entry is allowed without line items.
            if (lines.Length == 0) lines = Array.Empty<PurchaseInvoiceLine>();

            await _purchaseService.UpdateInvoiceAsync(
                invoiceId: _selectedInvoiceId.Value,
                invoiceDate: invoiceDate,
                vendorId: vendorId,
                vendorName: vendorName,
                invoiceNumber: invoiceNumber,
                total: total,
                notes: notes,
                sourceFilePath: file,
                lines: lines);

            await LoadPurchasesModuleAsync();
        }
        catch (Exception ex)
        {
            invError.Text = ex.Message;
        }
    }

    private async void Inv_Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("delete invoices");
            if (_selectedInvoiceId is null) throw new Exception("Select an invoice to delete.");

            if (System.Windows.MessageBox.Show(this, "Delete selected invoice?", "Invoices", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await _purchaseService.DeleteInvoiceAsync(_selectedInvoiceId.Value);
            ClearInvoiceForm();
            await LoadPurchasesModuleAsync();
        }
        catch (Exception ex)
        {
            invError.Text = ex.Message;
        }
    }

    private void Inv_Clear_Click(object sender, RoutedEventArgs e) => ClearInvoiceForm();

    private async void Inv_Refresh_Click(object sender, RoutedEventArgs e) => await LoadPurchasesModuleAsync();

    private async void Invoices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (gridInvoices.SelectedItem is not PurchaseInvoice inv)
            {
                _selectedInvoiceId = null;
                return;
            }

            _selectedInvoiceId = inv.Id;

            var full = await _purchaseService.GetInvoiceWithLinesAsync(inv.Id);
            if (full is null) return;

            invDate.SelectedDate = full.InvoiceDate.ToDateTime(new TimeOnly(0, 0));
            invVendorName.Text = full.VendorName;
            invNumber.Text = full.InvoiceNumber;
            invTotal.Text = full.Total is decimal tot ? tot.ToString("0.00") : "";
            invNotes.Text = full.Notes;
            invFilePath.Text = full.FilePath;

            if (full.VendorId.HasValue)
                invVendor.SelectedValue = full.VendorId.Value;
            else
                invVendor.SelectedItem = null;

            _invoiceLines.Clear();
            foreach (var l in full.Lines.OrderBy(x => x.Id))
                _invoiceLines.Add(CloneInvoiceLine(l));

            if (_invoiceLines.Count == 0)
                _invoiceLines.Add(new PurchaseInvoiceLine());
        }
        catch (Exception ex)
        {
            invError.Text = ex.Message;
        }
    }

    private async void Costs_Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            gridProductCosts.ItemsSource = await _purchaseService.GetProductCostsAsync(_currentStoreId);
            await UpdateStoreHeaderAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Product Costs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Costs_UploadInvoice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (txtCostsImportStatus is not null)
                txtCostsImportStatus.Text = "";

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select invoice PDF (SKU | Description | $Cost)",
                Filter = "PDF Files (*.pdf)|*.pdf",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() != true)
                return;

            var res = await _invoiceImportService.ImportCostsOnlyAsync(dlg.FileName);
            if (!res.Success)
            {
                System.Windows.MessageBox.Show(this, string.Join("\n", res.Warnings), "Product Costs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (res.Lines is null || res.Lines.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "No SKU/Description/Cost rows were found in the PDF.\n\nMake sure the invoice table contains columns like: SKU/UPC, Description, Cost/Unit Price.",
                    "Product Costs",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var vendorName = string.IsNullOrWhiteSpace(res.VendorName) ? "Unknown" : res.VendorName.Trim();
            var invoiceNumber = string.IsNullOrWhiteSpace(res.InvoiceNumber) ? $"COST-{DateTime.Now:yyyyMMdd-HHmm}" : res.InvoiceNumber.Trim();
            var invoiceDate = res.InvoiceDate ?? DateOnly.FromDateTime(DateTime.Today);
            var invoiceTotal = res.Total ?? res.Lines?.Sum(l => l.UnitCost * Math.Max(l.ShipQuantity, 1m)) ?? 0m;

            var (upserts, alerts) = await _purchaseService.ImportProductCostsAsync(
                storeId: _currentStoreId,
                vendorName: vendorName,
                invoiceNumber: invoiceNumber,
                invoiceDate: invoiceDate,
                lines: res.Lines ?? new List<PurchaseInvoiceLine>());

            if (txtCostsImportStatus is not null)
                txtCostsImportStatus.Text = $"Imported {upserts} item(s). Alerts created: {alerts}.";

            // Auto-populate the Purchases form with extracted invoice data
            await PopulatePurchaseFormFromInvoice(vendorName, invoiceNumber, invoiceDate, invoiceTotal);

            await LoadPurchasesModuleAsync();
            
            // Refresh dashboard to update Invoice Analytics
            await RefreshDashboardAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Product Costs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task PopulatePurchaseFormFromInvoice(string vendorName, string invoiceNumber, DateOnly invoiceDate, decimal invoiceTotal)
    {
        try
        {
            // Set the invoice date
            if (invDate is not null)
                invDate.SelectedDate = invoiceDate.ToDateTime(TimeOnly.MinValue);

            // Set the invoice number
            if (invNumber is not null)
                invNumber.Text = invoiceNumber;

            // Set the total
            if (invTotal is not null)
                invTotal.Text = invoiceTotal.ToString("F2");

            // Find and select the vendor in the combobox
            if (invVendor is not null)
            {
                // Try to find the vendor in the list
                var vendors = invVendor.ItemsSource as System.Collections.IEnumerable;
                if (vendors is not null)
                {
                    foreach (var item in vendors)
                    {
                        if (item is Vendor v && v.Name.Equals(vendorName, StringComparison.OrdinalIgnoreCase))
                        {
                            invVendor.SelectedItem = item;
                            break;
                        }
                    }
                }

                // If vendor not found, add it
                if (invVendor.SelectedItem is null && !string.IsNullOrWhiteSpace(vendorName))
                {
                    var existingVendor = await CreateDb().Vendors
                        .Where(v => v.StoreId == _currentStoreId && v.Name.ToLower() == vendorName.ToLower())
                        .FirstOrDefaultAsync();

                    if (existingVendor is null)
                    {
                        var newVendor = new Vendor { StoreId = _currentStoreId, Name = vendorName };
                        { using var db = CreateDb(); db.Vendors.Add(newVendor); await db.SaveChangesAsync(); }
                        
                        // Reload vendors dropdown
                        invVendor.ItemsSource = await CreateDb().Vendors
                            .Where(v => v.StoreId == _currentStoreId)
                            .OrderBy(v => v.Name)
                            .ToListAsync();
                        invVendor.SelectedValue = newVendor.Id;
                    }
                    else
                    {
                        invVendor.SelectedValue = existingVendor.Id;
                    }
                }
            }

            // Show a message to the user
            System.Windows.MessageBox.Show(
                this,
                $"Invoice data has been populated in the Purchases form:\n\n" +
                $"• Invoice #: {invoiceNumber}\n" +
                $"• Date: {invoiceDate:MM/dd/yyyy}\n" +
                $"• Vendor: {vendorName}\n" +
                $"• Total: {invoiceTotal:C}\n\n" +
                "Navigate to Purchases and click 'Add Invoice' to save this entry.",
                "Invoice Data Extracted",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error populating purchase form: {ex.Message}");
        }
    }

    private async void Costs_DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("delete product costs");

            var selected = gridProductCosts?.SelectedItems?.OfType<ProductCost>().ToList() ?? new List<ProductCost>();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show(this, "Select one or more rows to delete.", "Product Costs", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show(this, $"Delete {selected.Count} selected product cost entr(ies)?", "Product Costs", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await _purchaseService.DeleteProductCostsAsync(_currentStoreId, selected.Select(x => x.Id));

            if (txtCostsImportStatus is not null)
                txtCostsImportStatus.Text = $"Deleted {selected.Count} item(s).";

            await LoadPurchasesModuleAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Product Costs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Alerts_Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            gridPriceAlerts.ItemsSource = await _purchaseService.GetAlertsAsync(_currentStoreId);
            await UpdateStoreHeaderAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Price Alerts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Alerts_MarkRead_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ids = gridPriceAlerts.SelectedItems.OfType<PriceAlert>().Select(x => x.Id).ToList();
            await _purchaseService.MarkAlertsReadAsync(_currentStoreId, ids);
            await LoadPurchasesModuleAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Price Alerts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Alerts_MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _purchaseService.MarkAllAlertsReadAsync(_currentStoreId);
            await LoadPurchasesModuleAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Price Alerts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Alerts_Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureAdmin("delete alerts");
            var ids = gridPriceAlerts.SelectedItems.OfType<PriceAlert>().Select(x => x.Id).ToList();
            if (ids.Count == 0) return;

            if (System.Windows.MessageBox.Show(this, "Delete selected alerts?", "Price Alerts", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await _purchaseService.DeleteAlertsAsync(_currentStoreId, ids);
            await LoadPurchasesModuleAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Price Alerts", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -------------------- PROFIT & LOSS --------------------

    private async void PL_Refresh_Click(object sender, RoutedEventArgs e)
    {
        plError.Text = "";
        try
        {
            DateOnly from, to;

            // Check if using month/year selectors or date pickers
            if (cmbPlMonth?.SelectedValue is int month && cmbPlYear?.SelectedValue is int year)
            {
                if (chkPlYearly?.IsChecked == true)
                {
                    // Yearly: Jan 1 to Dec 31
                    from = new DateOnly(year, 1, 1);
                    to = new DateOnly(year, 12, 31);
                }
                else
                {
                    // Monthly: first to last day of selected month
                    from = new DateOnly(year, month, 1);
                    to = from.AddMonths(1).AddDays(-1);
                }
            }
            else
            {
                plError.Text = "Please select a month and year.";
                return;
            }

            // Query directly using CreateDb() which respects store-specific connections
            var data = await GetProfitLossDirectAsync(from, to);

            // ── Revenue ──
            plGrossSales.Text = $"{data.GrossSales:C2}";
            plSalesTax.Text = $"{data.SalesTax:C2}";
            plTotalRevenue.Text = $"{data.TotalRevenue:C2}";

            // Bank deposits — only show if data exists
            plBankDepositsRow.Visibility = data.BankDeposits != 0 ? Visibility.Visible : Visibility.Collapsed;
            plBankDeposits.Text = $"{data.BankDeposits:C2}";

            // ── Original Expenses ──
            plPurchases.Text = $"({data.Purchases:C2})";
            plCashPayouts.Text = $"({data.CashPayouts:C2})";
            plCheckPayouts.Text = $"({data.CheckPayouts:C2})";

            // ── Bank Statement Expenses — show only non-zero rows ──
            bool hasBankExpenses = data.HasBankStatementData;
            plBankExpenseHeader.Visibility = hasBankExpenses ? Visibility.Visible : Visibility.Collapsed;

            PL_SetExpenseRow(plUtilitiesRow, plUtilities, data.Utilities);
            PL_SetExpenseRow(plRentRow, plRent, data.Rent);
            PL_SetExpenseRow(plPayrollRow, plPayroll, data.Payroll);
            PL_SetExpenseRow(plInsuranceRow, plInsurance, data.Insurance);
            PL_SetExpenseRow(plBankFeesRow, plBankFees, data.BankFees);
            PL_SetExpenseRow(plTaxesRow, plTaxes, data.Taxes);
            PL_SetExpenseRow(plLoanDebtRow, plLoanDebt, data.LoanDebt);
            PL_SetExpenseRow(plOtherBankRow, plOtherBank, data.OtherBankExpenses);

            plTotalExpenses.Text = $"({data.TotalExpenses:C2})";

            // ── Net result with color coding ──
            if (data.IsProfit)
            {
                plNetResult.Text = $"{data.NetProfitLoss:C2}";
                plNetResult.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF059669"));
                plResultBorder.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFD4AF37"));
            }
            else
            {
                plNetResult.Text = $"({Math.Abs(data.NetProfitLoss):C2})";
                plNetResult.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFDC2626"));
                plResultBorder.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFECACA"));
            }
        }
        catch (Exception ex)
        {
            plError.Text = ex.Message;
        }
    }

    /// <summary>
    /// Queries P&L data directly using CreateDb() so it respects store-specific database connections.
    /// Pulls from: ShiftLogs, CashOnHand, CheckPayouts, PurchaseInvoices, BankStatementTransactions.
    /// </summary>
    private async Task<ProfitLossData> GetProfitLossDirectAsync(DateOnly from, DateOnly to)
    {
        using var db = CreateDb();
        var storeId = _currentStoreId;

        // Shift Logs (revenue)
        var shiftLogs = await db.ShiftLogs.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .ToListAsync();
        var effShifts = EffectiveRows(shiftLogs, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        // Cash On Hand (payouts)
        var cashEntries = await db.CashOnHand.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .ToListAsync();
        var effCash = EffectiveRows(cashEntries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        // Check Payouts
        var checkEntries = await db.CheckPayouts.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.Date >= from && x.Date <= to)
            .ToListAsync();
        var effChecks = EffectiveRows(checkEntries, x => x.IsCorrection, x => x.CorrectsId, x => x.Id, x => x.CreatedUtc);

        // Purchases
        var purchases = await db.PurchaseInvoices.AsNoTracking()
            .Where(x => x.StoreId == storeId && x.InvoiceDate >= from && x.InvoiceDate <= to)
            .ToListAsync();

        var data = new ProfitLossData
        {
            GrossSales = effShifts.Sum(x => x.NetSales),
            SalesTax = effShifts.Sum(x => x.Tax),
            CashPayouts = effCash.Where(x => x.IsPayout).Sum(x => x.PayoutAmount),
            CheckPayouts = effChecks.Sum(x => x.CheckAmount),
            Purchases = purchases.Sum(x => x.Total)
        };

        // Bank Statement Transactions (credits = deposits, debits = expenses by category)
        try
        {
            var fromDate = from.ToDateTime(TimeOnly.MinValue);
            var toDate = to.ToDateTime(TimeOnly.MaxValue);

            var bankTxns = await db.Database.SqlQueryRaw<BankTxnRow>(
                @"SELECT ISNULL(Category, 'Other') AS Category, 
                         ISNULL(SUM(Credit), 0) AS TotalCredit, 
                         ISNULL(SUM(Debit), 0) AS TotalDebit
                  FROM BankStatementTransactions 
                  WHERE StoreId = {0} AND Date >= {1} AND Date <= {2}
                  GROUP BY Category",
                storeId, fromDate, toDate).ToListAsync();

            foreach (var txn in bankTxns)
            {
                var cat = (txn.Category ?? "Other").Trim().ToLower();

                // Credits (deposits/income)
                if (txn.TotalCredit > 0)
                {
                    if (cat.Contains("deposit") || cat.Contains("income") || cat.Contains("transfer in"))
                        data.BankDeposits += txn.TotalCredit;
                }

                // Debits (expenses by category)
                if (txn.TotalDebit > 0)
                {
                    if (cat.Contains("utilit")) data.Utilities += txn.TotalDebit;
                    else if (cat.Contains("rent") || cat.Contains("lease")) data.Rent += txn.TotalDebit;
                    else if (cat.Contains("payroll") || cat.Contains("salary") || cat.Contains("wage")) data.Payroll += txn.TotalDebit;
                    else if (cat.Contains("insurance")) data.Insurance += txn.TotalDebit;
                    else if (cat.Contains("bank") || cat.Contains("fee") || cat.Contains("service charge")) data.BankFees += txn.TotalDebit;
                    else if (cat.Contains("tax")) data.Taxes += txn.TotalDebit;
                    else if (cat.Contains("loan") || cat.Contains("debt") || cat.Contains("mortgage")) data.LoanDebt += txn.TotalDebit;
                    else data.OtherBankExpenses += txn.TotalDebit;
                }
            }
        }
        catch (Exception ex)
        {
            // Bank statement table may not exist in all databases — that's fine
            System.Diagnostics.Debug.WriteLine($"Bank statement P&L query: {ex.Message}");
        }

        return data;
    }

    // Helper class for raw SQL bank statement query
    private class BankTxnRow
    {
        public string Category { get; set; } = "";
        public decimal TotalCredit { get; set; }
        public decimal TotalDebit { get; set; }
    }

    /// <summary>Shows a P&amp;L expense row only when the value is non-zero.</summary>
    private void PL_SetExpenseRow(System.Windows.Controls.Grid rowGrid, TextBlock amountText, decimal value)
    {
        if (value != 0)
        {
            rowGrid.Visibility = Visibility.Visible;
            amountText.Text = $"({value:C2})";
        }
        else
        {
            rowGrid.Visibility = Visibility.Collapsed;
        }
    }

    private async void PL_GenerateReport_Click(object sender, RoutedEventArgs e)
    {
        plError.Text = "";
        try
        {
            DateOnly from, to;

            if (cmbPlMonth?.SelectedValue is int month && cmbPlYear?.SelectedValue is int year)
            {
                if (chkPlYearly?.IsChecked == true)
                {
                    from = new DateOnly(year, 1, 1);
                    to = new DateOnly(year, 12, 31);
                }
                else
                {
                    from = new DateOnly(year, month, 1);
                    to = from.AddMonths(1).AddDays(-1);
                }
            }
            else
            {
                plError.Text = "Please select a month and year.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save P&L Report",
                Filter = "PDF Files (*.pdf)|*.pdf",
                FileName = $"ProfitLoss_{from:yyyyMMdd}_{to:yyyyMMdd}.pdf"
            };

            if (dlg.ShowDialog() != true) return;

            await _reportService.GenerateProfitLossPdfAsync(from, to, dlg.FileName);

            // Open the PDF
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dlg.FileName,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            plError.Text = ex.Message;
        }
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var updateWindow = new UpdateWindow();
            updateWindow.Owner = this;
            updateWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error opening update window: {ex.Message}", "Update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DatabaseSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dbSettingsWindow = new DatabaseSettingsWindow();
            dbSettingsWindow.Owner = this;
            dbSettingsWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error opening database settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
