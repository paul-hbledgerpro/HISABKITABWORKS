# DEVELOPER HANDOFF DOCUMENTATION
## HB Store Ledger Pro - Major Feature Implementation

This document provides complete specifications for implementing the requested changes to the HB Store Ledger Pro application.

---

## PROJECT OVERVIEW

**Application**: HB Store Ledger Pro (WPF .NET 8 Application)
**Current State**: Working application with complex invoice import system
**Required Changes**: Simplify purchases, add intelligent multi-vendor invoice parser

---

## COMPLETED WORK

### ✅ 1. Login Window (DONE)
- **File**: `src/ManagerPaperworkSystem.UI/Views/LoginWindow.xaml`
- **Status**: Complete and working
- Size: 650x420 pixels
- Layout: Two-panel (branding left, form right)
- Buttons: Login, Cancel (regular), Create Account, Forgot Password (link-style)

### ✅ 2. Calendar Controls (DONE)
- All custom DatePicker styling removed
- Uses Windows native calendar throughout app

### ⚠️ 3. Home Button (SIMPLE FIX NEEDED)
**File**: `src/ManagerPaperworkSystem.UI/Views/MainWindow.xaml`
**Line**: ~104-120
**Current**: Large button with text
**Required**: Small icon-only button (35x35px) matching logo size

**Code to replace**:
```xml
<Button x:Name="btnHomeTop" 
        Grid.Column="1"
        Width="35" Height="35"
        Margin="0,0,15,0"
        Background="Transparent"
        BorderBrush="Transparent"
        Cursor="Hand"
        ToolTip="Home"
        Click="Home_Click">
  <Path Data="M10,20V14H14V20H19V12H22L12,3L2,12H5V20H10Z" 
        Fill="White" 
        Stretch="Uniform"
        Width="20" Height="20"/>
</Button>
```

---

## FEATURE 1: SIMPLIFIED PURCHASES SECTION

### Current Implementation
- Complex invoice import with PDF parsing
- Line items grid
- Vendor name, invoice file, notes fields
- Multiple import buttons

### Required New Implementation

#### A. Database Schema

**New Table**: `PurchaseRecords`
```sql
CREATE TABLE IF NOT EXISTS PurchaseRecords (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Date TEXT NOT NULL,
    VendorId INTEGER NOT NULL,
    InvoiceNumber TEXT NOT NULL,
    TotalAmount REAL NOT NULL,
    CreatedDate TEXT NOT NULL,
    FOREIGN KEY (VendorId) REFERENCES Vendors(Id)
);
```

#### B. User Interface (MainWindow.xaml)

**Location**: Purchases tab section (currently around lines 850-940)

**Replace entire section with**:
```xml
<!-- Purchases Section - Simplified -->
<TabItem Header="Purchases">
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
    </Grid.RowDefinitions>

    <Border Grid.Row="0" Style="{StaticResource CardStyle}" Padding="20" Margin="10">
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="100"/>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="100"/>
          <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
          <RowDefinition Height="Auto"/>
          <RowDefinition Height="4"/>
          <RowDefinition Height="Auto"/>
          <RowDefinition Height="15"/>
          <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Date -->
        <TextBlock Grid.Row="0" Grid.Column="0" Text="Date" VerticalAlignment="Center" Margin="0,0,4,0"/>
        <DatePicker Grid.Row="0" Grid.Column="1" x:Name="purchDate" Height="34" VerticalAlignment="Center"/>

        <!-- Vendor -->
        <TextBlock Grid.Row="0" Grid.Column="2" Text="Vendor" VerticalAlignment="Center" Margin="8,0,4,0"/>
        <ComboBox Grid.Row="0" Grid.Column="3" x:Name="purchVendor" Height="34" VerticalAlignment="Center" DisplayMemberPath="Name" SelectedValuePath="Id"/>

        <!-- Invoice Number -->
        <TextBlock Grid.Row="2" Grid.Column="0" Text="Invoice #" VerticalAlignment="Center" Margin="0,0,4,0"/>
        <TextBox Grid.Row="2" Grid.Column="1" x:Name="purchInvoiceNum" Height="34" VerticalAlignment="Center"/>

        <!-- Total Amount -->
        <TextBlock Grid.Row="2" Grid.Column="2" Text="Total Amount" VerticalAlignment="Center" Margin="8,0,4,0"/>
        <TextBox Grid.Row="2" Grid.Column="3" x:Name="purchAmount" Height="34" VerticalAlignment="Center"/>

        <!-- Buttons -->
        <StackPanel Grid.Row="4" Grid.Column="0" Grid.ColumnSpan="4" Orientation="Horizontal" HorizontalAlignment="Right">
          <Button Content="Add Invoice" Width="120" Height="40" Margin="0,0,10,0" Click="Purchase_Add_Click" Style="{StaticResource PrimaryButton}"/>
          <Button Content="Update Selected" Width="120" Height="40" Margin="0,0,10,0" Click="Purchase_Update_Click" Style="{StaticResource SecondaryButton}"/>
          <Button Content="Delete Selected" Width="120" Height="40" Click="Purchase_Delete_Click" Style="{StaticResource SecondaryButton}"/>
        </StackPanel>
      </Grid>
    </Border>

    <!-- Purchases Grid -->
    <DataGrid Grid.Row="1" x:Name="gridPurchases" Margin="10" IsReadOnly="True" SelectionMode="Single" AutoGenerateColumns="False" SelectionChanged="Purchases_SelectionChanged">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Date" Binding="{Binding Date, StringFormat=\{0:MM/dd/yyyy\}}" Width="100"/>
        <DataGridTextColumn Header="Vendor" Binding="{Binding VendorName}" Width="*"/>
        <DataGridTextColumn Header="Invoice #" Binding="{Binding InvoiceNumber}" Width="120"/>
        <DataGridTextColumn Header="Amount" Binding="{Binding TotalAmount, StringFormat=C2}" Width="100"/>
      </DataGrid.Columns>
    </DataGrid>
  </Grid>
</TabItem>
```

#### C. Code-Behind (MainWindow.xaml.cs)

**Add these methods**:
```csharp
private async void Purchase_Add_Click(object sender, RoutedEventArgs e)
{
    if (!purchDate.SelectedDate.HasValue)
    {
        MessageBox.Show("Please select a date", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    if (purchVendor.SelectedValue == null)
    {
        MessageBox.Show("Please select a vendor", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    if (string.IsNullOrWhiteSpace(purchInvoiceNum.Text))
    {
        MessageBox.Show("Please enter an invoice number", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    if (!decimal.TryParse(purchAmount.Text, out decimal amount) || amount <= 0)
    {
        MessageBox.Show("Please enter a valid amount", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    var purchase = new PurchaseRecord
    {
        Date = purchDate.SelectedDate.Value.ToString("yyyy-MM-dd"),
        VendorId = (int)purchVendor.SelectedValue,
        InvoiceNumber = purchInvoiceNum.Text.Trim(),
        TotalAmount = amount,
        CreatedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
    };

    _db.PurchaseRecords.Add(purchase);
    await _db.SaveChangesAsync();

    MessageBox.Show("Purchase record added successfully", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    
    await LoadPurchasesAsync();
    ClearPurchaseForm();
}

private async Task LoadPurchasesAsync()
{
    var purchases = await _db.PurchaseRecords
        .Include(p => p.Vendor)
        .OrderByDescending(p => p.Date)
        .Select(p => new
        {
            p.Id,
            Date = DateTime.Parse(p.Date),
            VendorName = p.Vendor.Name,
            p.InvoiceNumber,
            p.TotalAmount
        })
        .ToListAsync();

    gridPurchases.ItemsSource = purchases;
}

private void ClearPurchaseForm()
{
    purchDate.SelectedDate = DateTime.Now;
    purchVendor.SelectedIndex = -1;
    purchInvoiceNum.Text = "";
    purchAmount.Text = "";
}
```

#### D. Entity Model

**File**: `src/ManagerPaperworkSystem.Core/Models/Entities.cs`

**Add**:
```csharp
public sealed class PurchaseRecord : Entity
{
    public string Date { get; set; } = "";
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public string CreatedDate { get; set; } = "";
}
```

#### E. DbContext Update

**File**: `src/ManagerPaperworkSystem.Data/Db/AppDbContext.cs`

**Add**:
```csharp
public DbSet<PurchaseRecord> PurchaseRecords => Set<PurchaseRecord>();
```

---

## FEATURE 2: INTELLIGENT MULTI-VENDOR INVOICE PARSER

### Overview
This is the most complex feature. It requires parsing PDF invoices from 7 different vendors and extracting product cost information.

### Vendor Formats Analysis

Based on provided samples, here are the extraction patterns:

#### Vendor 1: TRI STATE DISTRO
**File Pattern**: Sales Order format
**Invoice Number**: 4-digit (e.g., 1466)
**Vendor Detection**: Header contains "TRI STATE DISTRO"
**Line Items Table**:
- Columns: SKU | Product Name / Description | SO | IO | Out | Price | Sold Price | Tax | Amount
- Extract: SKU, Product Name, **Sold Price** (this is unit cost)

**Pattern**:
```
Regex for line: 
^(\d+)\s+(\d+)\s+(.+?)\s+(\d+)\s+(\d+)\s+(\d+)\s+\$(\d+\.\d{2})\s+\$(\d+\.\d{2})
Group 2 = SKU
Group 3 = Product Name
Group 8 = Sold Price (unit cost)
```

#### Vendor 2: SAFA GOODS
**Invoice Number**: 5-digit (40155, 40149)
**Vendor Detection**: "SAFA GOODS" in header
**Line Items**:
- Columns: UPC | Product Name / Description | SO | IO | Out | Sold Price | Tax | Amount
- Extract: UPC, Product Name, **Sold Price**

**Pattern**:
```
Regex:
^(\d+)\s+(\d{13})\s+(.+?)\s+(\d+)\s+(\d+)\s+(\d+)\s+\$(\d+\.\d{2})
Group 2 = UPC
Group 3 = Product Name
Group 7 = Sold Price
```

#### Vendor 3: SKYGATE WHOLESALE
**Invoice Number**: S + 6-digit (S036382)
**Vendor Detection**: "SKYGATE WHOLESALE" in header
**Line Items**:
- Columns: ITEM# | ORD | SHIP | UNIT | DESCRIPTION | PRICE | TAX | AMOUNT
- Extract: ITEM#, DESCRIPTION, **PRICE**

**Pattern**:
```
Regex:
^(\d+)\s+(\w+)\s+(\d+)\s+(\d+)\s+(\w+)\s+(.+?)\s+\$\d+\.\d{2}\s+\$(\d+\.\d{2})
Group 2 = ITEM#
Group 6 = DESCRIPTION
Group 7 = PRICE
```

#### Vendor 4: 1OAK WHOLESALE
**Invoice Number**: #000105366
**Vendor Detection**: "1OAK WHOLESALE" in header
**Line Items**:
- Format: Product Name (SKU: xxx) | Qty | Price | Subtotal
- Extract: SKU, Product Name, **Price**

**Pattern**:
```
Look for lines with:
SKU: \w+
Then extract price from same line
```

#### Vendor 5: HS WHOLESALE
**Order Number**: #486044
**Vendor Detection**: "HS WHOLESALE" in header
**Line Items**:
- Format: Qty | Code/SKU | Product Name | Price | Total
- Extract: Code/SKU, Product Name, **Price**

#### Vendor 6: AMERICAN DISTRIBUTORS
**Transaction Number**: 10603626
**Vendor Detection**: "AMERICAN DISTRIBUTORS" in header
**Line Items**:
- Columns: SKU | QTY | DESCRIPTION | UNIT PRICE | UNIT TAX | EXTENDED PRICE
- Extract: SKU, DESCRIPTION, **UNIT PRICE**

#### Vendor 7: AK WHOLESALE
**Invoice**: S + 6-digit
**Vendor Detection**: "AK WHOLESALE" in header
**Line Items**:
- Columns: UPC | ORD | SHIP | UNIT | DESCRIPTION | VOL(ML) | TAX | PRICE | AMOUNT
- Extract: UPC, DESCRIPTION, **PRICE**

### Implementation Structure

#### A. Create InvoiceParserService.cs

**File**: `src/ManagerPaperworkSystem.Core/Services/InvoiceParserService.cs`

```csharp
using UglyToad.PdfPig;
using System.Text.RegularExpressions;

public class InvoiceParserService
{
    public class ParsedInvoice
    {
        public string VendorName { get; set; }
        public string InvoiceNumber { get; set; }
        public DateTime InvoiceDate { get; set; }
        public List<InvoiceLineItem> LineItems { get; set; } = new();
    }

    public class InvoiceLineItem
    {
        public string SKU { get; set; }
        public string UPC { get; set; }
        public string ProductName { get; set; }
        public decimal UnitCost { get; set; }
    }

    public ParsedInvoice ParseInvoice(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        var text = ExtractAllText(document);
        
        var vendor = DetectVendor(text);
        
        return vendor switch
        {
            "TRI STATE DISTRO" => ParseTriStateDistro(text),
            "SAFA GOODS" => ParseSafaGoods(text),
            "SKYGATE WHOLESALE" => ParseSkygateWholesale(text),
            "1OAK WHOLESALE" => ParseOneOakWholesale(text),
            "HS WHOLESALE" => ParseHSWholesale(text),
            "AMERICAN DISTRIBUTORS" => ParseAmericanDistributors(text),
            "AK WHOLESALE" => ParseAKWholesale(text),
            _ => throw new Exception("Unknown vendor format")
        };
    }

    private string DetectVendor(string text)
    {
        if (text.Contains("TRI STATE DISTRO")) return "TRI STATE DISTRO";
        if (text.Contains("SAFA GOODS")) return "SAFA GOODS";
        if (text.Contains("SKYGATE WHOLESALE")) return "SKYGATE WHOLESALE";
        if (text.Contains("1OAK WHOLESALE") || text.Contains("1oakwholesale")) return "1OAK WHOLESALE";
        if (text.Contains("HS WHOLESALE")) return "HS WHOLESALE";
        if (text.Contains("AMERICAN DISTRIBUTORS")) return "AMERICAN DISTRIBUTORS";
        if (text.Contains("AK WHOLESALE") || text.Contains("AK Wholesale")) return "AK WHOLESALE";
        
        return "UNKNOWN";
    }

    // Implement parsing methods for each vendor
    // See vendor-specific patterns above
}
```

#### B. Create ProductCostService.cs

```csharp
public class ProductCostService
{
    private readonly AppDbContext _db;

    public async Task<List<PriceChange>> ProcessInvoiceAsync(ParsedInvoice invoice)
    {
        var priceChanges = new List<PriceChange>();

        foreach (var item in invoice.LineItems)
        {
            var existing = await _db.ProductCosts
                .Where(pc => pc.SKU == item.SKU || pc.UPC == item.UPC)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                if (existing.CurrentCost != item.UnitCost)
                {
                    // Price changed!
                    var change = new PriceChange
                    {
                        ProductName = item.ProductName,
                        OldPrice = existing.CurrentCost,
                        NewPrice = item.UnitCost,
                        ChangePercent = ((item.UnitCost - existing.CurrentCost) / existing.CurrentCost) * 100,
                        DetectedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        VendorName = invoice.VendorName
                    };

                    priceChanges.Add(change);

                    existing.PreviousCost = existing.CurrentCost;
                    existing.CurrentCost = item.UnitCost;
                    existing.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
            else
            {
                // New product
                _db.ProductCosts.Add(new ProductCost
                {
                    SKU = item.SKU,
                    UPC = item.UPC,
                    ProductName = item.ProductName,
                    CurrentCost = item.UnitCost,
                    PreviousCost = 0,
                    VendorName = invoice.VendorName,
                    LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
        }

        await _db.SaveChangesAsync();
        return priceChanges;
    }
}
```

#### C. Database Schema

**Add to AppDbContext**:

```sql
CREATE TABLE IF NOT EXISTS ProductCosts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UPC TEXT,
    SKU TEXT,
    ProductName TEXT NOT NULL,
    CurrentCost REAL NOT NULL,
    PreviousCost REAL NOT NULL DEFAULT 0,
    VendorName TEXT NOT NULL,
    LastUpdated TEXT NOT NULL,
    InvoiceNumber TEXT
);

CREATE TABLE IF NOT EXISTS PriceAlerts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProductName TEXT NOT NULL,
    OldPrice REAL NOT NULL,
    NewPrice REAL NOT NULL,
    ChangePercent REAL NOT NULL,
    DetectedDate TEXT NOT NULL,
    VendorName TEXT NOT NULL,
    IsRead INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_productcosts_sku ON ProductCosts(SKU);
CREATE INDEX idx_productcosts_upc ON ProductCosts(UPC);
```

#### D. UI for Product Costs

**In MainWindow.xaml, Product Costs tab**:

```xml
<TabItem Header="Product Costs">
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
    </Grid.RowDefinitions>

    <!-- Upload Section -->
    <Border Grid.Row="0" Style="{StaticResource CardStyle}" Padding="20" Margin="10">
      <StackPanel>
        <TextBlock Text="Upload Vendor Invoice" FontSize="16" FontWeight="SemiBold" Margin="0,0,0,10"/>
        <Button Content="Upload Invoice PDF" Width="200" Height="40" Click="UploadInvoice_Click" Style="{StaticResource PrimaryButton}"/>
        <TextBlock x:Name="txtUploadStatus" Margin="0,10,0,0" Foreground="{StaticResource MutedTextBrush}"/>
      </StackPanel>
    </Border>

    <!-- Product Costs Grid -->
    <DataGrid Grid.Row="1" x:Name="gridProductCosts" Margin="10" IsReadOnly="True" AutoGenerateColumns="False">
      <DataGrid.Columns>
        <DataGridTextColumn Header="SKU/UPC" Binding="{Binding Identifier}" Width="120"/>
        <DataGridTextColumn Header="Product Name" Binding="{Binding ProductName}" Width="*"/>
        <DataGridTextColumn Header="Current Cost" Binding="{Binding CurrentCost, StringFormat=C2}" Width="100"/>
        <DataGridTextColumn Header="Previous Cost" Binding="{Binding PreviousCost, StringFormat=C2}" Width="100"/>
        <DataGridTextColumn Header="Change %" Binding="{Binding ChangePercent, StringFormat=F2}" Width="80"/>
        <DataGridTextColumn Header="Vendor" Binding="{Binding VendorName}" Width="150"/>
        <DataGridTextColumn Header="Last Updated" Binding="{Binding LastUpdated}" Width="120"/>
      </DataGrid.Columns>
    </DataGrid>
  </Grid>
</TabItem>
```

---

## TESTING REQUIREMENTS

### Test Cases for Purchases
1. Add purchase record with all fields
2. Update existing purchase record
3. Delete purchase record
4. Verify data persists across app restart

### Test Cases for Invoice Parser
1. Upload TRI STATE DISTRO invoice → Extract all line items correctly
2. Upload SAFA GOODS invoice → Extract all line items correctly
3. Upload SKYGATE invoice → Extract all line items correctly
4. Upload 1OAK invoice → Extract all line items correctly
5. Upload HS WHOLESALE invoice → Extract all line items correctly
6. Upload AMERICAN DISTRIBUTORS invoice → Extract all line items correctly
7. Upload AK WHOLESALE invoice → Extract all line items correctly
8. Verify price changes detected when uploading second invoice with different prices
9. Verify alerts created for price changes
10. Verify product costs grid displays correctly

---

## DEPENDENCIES

**NuGet Packages Required**:
- PdfPig (for PDF parsing)
```xml
<PackageReference Include="PdfPig" Version="0.1.8" />
```

---

## ESTIMATED EFFORT

- **Purchases Simplification**: 2-3 hours
- **Invoice Parser Core**: 4-5 hours
- **Price Change Detection**: 1-2 hours
- **UI Integration**: 1-2 hours
- **Testing & Refinement**: 2-3 hours

**Total**: 10-15 hours

---

## DELIVERABLES CHECKLIST

- [ ] Purchases section simplified with new UI
- [ ] PurchaseRecord database table created
- [ ] Purchase CRUD operations working
- [ ] InvoiceParserService implemented for all 7 vendors
- [ ] ProductCostService implemented
- [ ] ProductCosts database table created
- [ ] PriceAlerts database table created
- [ ] Product Costs UI implemented
- [ ] Price change detection working
- [ ] All test cases passing
- [ ] Home button resized to match logo

---

## CONTACT & SUPPORT

For questions or clarifications, refer to:
- Sample invoices in `/samples` folder
- Current working code in the application
- This specification document

Good luck with the implementation!
