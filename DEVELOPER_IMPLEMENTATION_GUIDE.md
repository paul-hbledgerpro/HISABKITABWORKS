# DEVELOPER IMPLEMENTATION GUIDE
## HB Store Ledger Pro - Purchases & Product Costs Redesign

---

## PROJECT OVERVIEW

**Client Requirements**: Complete redesign of Purchases and Product Costs sections with intelligent multi-vendor invoice parsing.

**Estimated Time**: 8-12 hours
**Complexity**: Medium-High
**Technologies**: C# .NET 8, WPF, Entity Framework Core, PdfPig (PDF parsing)

---

## PHASE 1: SIMPLIFIED PURCHASES SECTION (2-3 hours)

### Current State
- Complex invoice import with line items
- File upload functionality
- Multiple fields and grids

### New Requirements
Simple manual entry system with 4 fields:
- Date
- Vendor (dropdown)
- Invoice Number
- Total Amount

### Database Changes

**New Table: `SimplePurchases`**
```sql
CREATE TABLE SimplePurchases (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PurchaseDate TEXT NOT NULL,
    VendorId INTEGER NOT NULL,
    InvoiceNumber TEXT NOT NULL,
    TotalAmount REAL NOT NULL,
    CreatedDate TEXT NOT NULL,
    CreatedBy INTEGER,
    FOREIGN KEY (VendorId) REFERENCES Vendors(Id)
);

CREATE INDEX idx_purchases_date ON SimplePurchases(PurchaseDate);
CREATE INDEX idx_purchases_vendor ON SimplePurchases(VendorId);
```

**Entity Class**: `src/ManagerPaperworkSystem.Core/Models/SimplePurchase.cs`
```csharp
using System;
using System.ComponentModel.DataAnnotations;

namespace ManagerPaperworkSystem.Core.Models;

public class SimplePurchase : Entity
{
    [Required]
    public DateTime PurchaseDate { get; set; } = DateTime.Today;
    
    [Required]
    public int VendorId { get; set; }
    
    public Vendor? Vendor { get; set; }
    
    [Required]
    [MaxLength(100)]
    public string InvoiceNumber { get; set; } = "";
    
    [Required]
    public decimal TotalAmount { get; set; }
    
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    
    public int? CreatedBy { get; set; }
}
```

### UI Implementation

**File**: `src/ManagerPaperworkSystem.UI/Views/MainWindow.xaml`

**Find** the Purchases section (around line 850-940) and **REPLACE** with:

```xml
<!-- Purchases Tab Content -->
<TabItem x:Name="tabPurchases" Visibility="Collapsed">
  <Grid Margin="20">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
    </Grid.RowDefinitions>

    <Border Grid.Row="0" Style="{StaticResource CardStyle}" Padding="20" Margin="0,0,0,12">
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="200"/>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="200"/>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="150"/>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="150"/>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>

        <TextBlock Grid.Column="0" Text="Date" VerticalAlignment="Center" Margin="0,0,8,0"/>
        <DatePicker Grid.Column="1" x:Name="purchaseDate" Height="36" VerticalAlignment="Center"/>

        <TextBlock Grid.Column="2" Text="Vendor" VerticalAlignment="Center" Margin="15,0,8,0"/>
        <ComboBox Grid.Column="3" x:Name="purchaseVendor" Height="36" VerticalAlignment="Center"
                  DisplayMemberPath="Name" SelectedValuePath="Id"/>

        <TextBlock Grid.Column="4" Text="Invoice #" VerticalAlignment="Center" Margin="15,0,8,0"/>
        <TextBox Grid.Column="5" x:Name="purchaseInvoiceNumber" Height="36" VerticalAlignment="Center"/>

        <TextBlock Grid.Column="6" Text="Amount" VerticalAlignment="Center" Margin="15,0,8,0"/>
        <TextBox Grid.Column="7" x:Name="purchaseAmount" Height="36" VerticalAlignment="Center"/>

        <Button Grid.Column="9" Content="Add Purchase" Height="40" Width="130"
                Style="{StaticResource PrimaryButton}" Click="Purchase_Add_Click"/>
      </Grid>
    </Border>

    <DataGrid Grid.Row="1" x:Name="gridPurchases" 
              AutoGenerateColumns="False" 
              IsReadOnly="False"
              SelectionMode="Single"
              CanUserAddRows="False">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Date" Binding="{Binding PurchaseDate, StringFormat=d}" Width="100"/>
        <DataGridTextColumn Header="Vendor" Binding="{Binding Vendor.Name}" Width="*" MinWidth="200"/>
        <DataGridTextColumn Header="Invoice #" Binding="{Binding InvoiceNumber}" Width="150"/>
        <DataGridTextColumn Header="Amount" Binding="{Binding TotalAmount, StringFormat=C}" Width="120"/>
        <DataGridTemplateColumn Header="Actions" Width="150">
          <DataGridTemplateColumn.CellTemplate>
            <DataTemplate>
              <StackPanel Orientation="Horizontal">
                <Button Content="Edit" Width="60" Height="30" Margin="0,0,5,0"
                        Style="{StaticResource SecondaryButton}"
                        Click="Purchase_Edit_Click" Tag="{Binding Id}"/>
                <Button Content="Delete" Width="60" Height="30"
                        Style="{StaticResource DangerButton}"
                        Click="Purchase_Delete_Click" Tag="{Binding Id}"/>
              </StackPanel>
            </DataTemplate>
          </DataGridTemplateColumn.CellTemplate>
        </DataGridTemplateColumn>
      </DataGrid.Columns>
    </DataGrid>
  </Grid>
</TabItem>
```

### Code-Behind Implementation

**File**: `src/ManagerPaperworkSystem.UI/Views/MainWindow.xaml.cs`

**Add these methods**:

```csharp
// Purchases Section - Simplified
private int? _editingPurchaseId = null;

private async Task LoadPurchasesAsync()
{
    try
    {
        var purchases = await _db.SimplePurchases
            .Include(p => p.Vendor)
            .OrderByDescending(p => p.PurchaseDate)
            .ToListAsync();
        
        gridPurchases.ItemsSource = purchases;
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error loading purchases: {ex.Message}", "Error", 
                       MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

private async void Purchase_Add_Click(object sender, RoutedEventArgs e)
{
    try
    {
        // Validate
        if (purchaseDate.SelectedDate == null)
        {
            MessageBox.Show("Please select a date.", "Validation", 
                          MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (purchaseVendor.SelectedValue == null)
        {
            MessageBox.Show("Please select a vendor.", "Validation", 
                          MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(purchaseInvoiceNumber.Text))
        {
            MessageBox.Show("Please enter an invoice number.", "Validation", 
                          MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(purchaseAmount.Text, out decimal amount) || amount <= 0)
        {
            MessageBox.Show("Please enter a valid amount.", "Validation", 
                          MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_editingPurchaseId.HasValue)
        {
            // Update existing
            var purchase = await _db.SimplePurchases.FindAsync(_editingPurchaseId.Value);
            if (purchase != null)
            {
                purchase.PurchaseDate = purchaseDate.SelectedDate.Value;
                purchase.VendorId = (int)purchaseVendor.SelectedValue;
                purchase.InvoiceNumber = purchaseInvoiceNumber.Text.Trim();
                purchase.TotalAmount = amount;
                
                await _db.SaveChangesAsync();
                MessageBox.Show("Purchase updated successfully!", "Success", 
                              MessageBoxButton.OK, MessageBoxImage.Information);
            }
            
            _editingPurchaseId = null;
        }
        else
        {
            // Add new
            var purchase = new SimplePurchase
            {
                PurchaseDate = purchaseDate.SelectedDate.Value,
                VendorId = (int)purchaseVendor.SelectedValue,
                InvoiceNumber = purchaseInvoiceNumber.Text.Trim(),
                TotalAmount = amount,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = _session.UserId
            };

            _db.SimplePurchases.Add(purchase);
            await _db.SaveChangesAsync();
            
            MessageBox.Show("Purchase added successfully!", "Success", 
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Clear form
        ClearPurchaseForm();
        await LoadPurchasesAsync();
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error saving purchase: {ex.Message}", "Error", 
                       MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

private void Purchase_Edit_Click(object sender, RoutedEventArgs e)
{
    if (sender is Button btn && btn.Tag is int id)
    {
        var purchase = _db.SimplePurchases
            .Include(p => p.Vendor)
            .FirstOrDefault(p => p.Id == id);
        
        if (purchase != null)
        {
            _editingPurchaseId = id;
            purchaseDate.SelectedDate = purchase.PurchaseDate;
            purchaseVendor.SelectedValue = purchase.VendorId;
            purchaseInvoiceNumber.Text = purchase.InvoiceNumber;
            purchaseAmount.Text = purchase.TotalAmount.ToString("F2");
        }
    }
}

private async void Purchase_Delete_Click(object sender, RoutedEventArgs e)
{
    if (sender is Button btn && btn.Tag is int id)
    {
        var result = MessageBox.Show("Are you sure you want to delete this purchase?", 
                                   "Confirm Delete", 
                                   MessageBoxButton.YesNo, 
                                   MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var purchase = await _db.SimplePurchases.FindAsync(id);
                if (purchase != null)
                {
                    _db.SimplePurchases.Remove(purchase);
                    await _db.SaveChangesAsync();
                    await LoadPurchasesAsync();
                    
                    MessageBox.Show("Purchase deleted successfully!", "Success", 
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting purchase: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

private void ClearPurchaseForm()
{
    _editingPurchaseId = null;
    purchaseDate.SelectedDate = DateTime.Today;
    purchaseVendor.SelectedIndex = -1;
    purchaseInvoiceNumber.Clear();
    purchaseAmount.Clear();
}
```

**Update DbContext**: `src/ManagerPaperworkSystem.Data/Db/AppDbContext.cs`

Add this property:
```csharp
public DbSet<SimplePurchase> SimplePurchases => Set<SimplePurchase>();
```

---

## PHASE 2: PRODUCT COSTS - MULTI-VENDOR INVOICE PARSER (6-8 hours)

### Database Schema

**File**: Create migration or update database

```sql
-- Product cost tracking
CREATE TABLE ProductCosts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UPC TEXT,
    SKU TEXT,
    ProductName TEXT NOT NULL,
    CurrentCost REAL NOT NULL,
    PreviousCost REAL,
    VendorId INTEGER,
    VendorName TEXT,
    LastUpdated TEXT NOT NULL,
    InvoiceNumber TEXT,
    ChangePercent REAL,
    FOREIGN KEY (VendorId) REFERENCES Vendors(Id)
);

CREATE INDEX idx_productcosts_upc ON ProductCosts(UPC);
CREATE INDEX idx_productcosts_sku ON ProductCosts(SKU);
CREATE INDEX idx_productcosts_name ON ProductCosts(ProductName);

-- Price change alerts
CREATE TABLE PriceAlerts (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProductName TEXT NOT NULL,
    UPC TEXT,
    SKU TEXT,
    OldPrice REAL NOT NULL,
    NewPrice REAL NOT NULL,
    ChangePercent REAL NOT NULL,
    VendorName TEXT,
    InvoiceNumber TEXT,
    DetectedDate TEXT NOT NULL,
    IsRead INTEGER DEFAULT 0,
    IsAcknowledged INTEGER DEFAULT 0
);

CREATE INDEX idx_pricealerts_unread ON PriceAlerts(IsRead);
CREATE INDEX idx_pricealerts_date ON PriceAlerts(DetectedDate);
```

### Entity Classes

**File**: `src/ManagerPaperworkSystem.Core/Models/ProductCost.cs`
```csharp
using System;
using System.ComponentModel.DataAnnotations;

namespace ManagerPaperworkSystem.Core.Models;

public class ProductCost : Entity
{
    [MaxLength(50)]
    public string? UPC { get; set; }
    
    [MaxLength(50)]
    public string? SKU { get; set; }
    
    [Required]
    [MaxLength(500)]
    public string ProductName { get; set; } = "";
    
    [Required]
    public decimal CurrentCost { get; set; }
    
    public decimal? PreviousCost { get; set; }
    
    public int? VendorId { get; set; }
    
    [MaxLength(200)]
    public string? VendorName { get; set; }
    
    [Required]
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    
    [MaxLength(100)]
    public string? InvoiceNumber { get; set; }
    
    public decimal? ChangePercent { get; set; }
}
```

**File**: `src/ManagerPaperworkSystem.Core/Models/PriceAlert.cs`
```csharp
using System;
using System.ComponentModel.DataAnnotations;

namespace ManagerPaperworkSystem.Core.Models;

public class PriceAlert : Entity
{
    [Required]
    [MaxLength(500)]
    public string ProductName { get; set; } = "";
    
    [MaxLength(50)]
    public string? UPC { get; set; }
    
    [MaxLength(50)]
    public string? SKU { get; set; }
    
    [Required]
    public decimal OldPrice { get; set; }
    
    [Required]
    public decimal NewPrice { get; set; }
    
    [Required]
    public decimal ChangePercent { get; set; }
    
    [MaxLength(200)]
    public string? VendorName { get; set; }
    
    [MaxLength(100)]
    public string? InvoiceNumber { get; set; }
    
    [Required]
    public DateTime DetectedDate { get; set; } = DateTime.UtcNow;
    
    public bool IsRead { get; set; }
    
    public bool IsAcknowledged { get; set; }
}
```

---

### INVOICE PARSER SERVICE

**File**: `src/ManagerPaperworkSystem.Core/Services/MultiVendorInvoiceParser.cs`

This is the CORE of the system. I'll provide the complete structure:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ManagerPaperworkSystem.Core.Services;

public class InvoiceLineItem
{
    public string? UPC { get; set; }
    public string? SKU { get; set; }
    public string ProductName { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal TotalAmount { get; set; }
}

public class ParsedInvoice
{
    public string VendorName { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public DateTime? InvoiceDate { get; set; }
    public List<InvoiceLineItem> LineItems { get; set; } = new();
    public decimal GrandTotal { get; set; }
}

public class MultiVendorInvoiceParser
{
    public ParsedInvoice ParseInvoice(string pdfFilePath)
    {
        using var document = PdfDocument.Open(pdfFilePath);
        var fullText = ExtractAllText(document);
        
        // Detect vendor
        var vendor = DetectVendor(fullText);
        
        return vendor switch
        {
            "TRI STATE DISTRO" => ParseTriStateDistro(document, fullText),
            "SAFA GOODS" => ParseSafaGoods(document, fullText),
            "SKYGATE WHOLESALE" => ParseSkygateWholesale(document, fullText),
            "1OAK WHOLESALE" => Parse1OakWholesale(document, fullText),
            "HS WHOLESALE" => ParseHSWholesale(document, fullText),
            "AMERICAN DISTRIBUTORS" => ParseAmericanDistributors(document, fullText),
            "AK WHOLESALE" => ParseAKWholesale(document, fullText),
            _ => throw new Exception($"Unknown vendor format. Detected text: {fullText.Substring(0, Math.Min(200, fullText.Length))}")
        };
    }
    
    private string ExtractAllText(PdfDocument document)
    {
        var text = "";
        foreach (var page in document.GetPages())
        {
            text += page.Text + "\n";
        }
        return text;
    }
    
    private string DetectVendor(string text)
    {
        if (text.Contains("TRI STATE DISTRO", StringComparison.OrdinalIgnoreCase))
            return "TRI STATE DISTRO";
        if (text.Contains("SAFA GOODS", StringComparison.OrdinalIgnoreCase))
            return "SAFA GOODS";
        if (text.Contains("SKYGATE WHOLESALE", StringComparison.OrdinalIgnoreCase))
            return "SKYGATE WHOLESALE";
        if (text.Contains("1OAK WHOLESALE", StringComparison.OrdinalIgnoreCase) || 
            text.Contains("1OAKWHOLESALE", StringComparison.OrdinalIgnoreCase))
            return "1OAK WHOLESALE";
        if (text.Contains("HS WHOLESALE", StringComparison.OrdinalIgnoreCase))
            return "HS WHOLESALE";
        if (text.Contains("AMERICAN DISTRIBUTORS", StringComparison.OrdinalIgnoreCase))
            return "AMERICAN DISTRIBUTORS";
        if (text.Contains("AK WHOLESALE", StringComparison.OrdinalIgnoreCase) || 
            text.Contains("AK Wholesale Inc", StringComparison.OrdinalIgnoreCase))
            return "AK WHOLESALE";
        
        return "UNKNOWN";
    }
    
    // VENDOR 1: TRI STATE DISTRO
    private ParsedInvoice ParseTriStateDistro(PdfDocument document, string fullText)
    {
        var invoice = new ParsedInvoice { VendorName = "TRI STATE DISTRO" };
        
        // Extract invoice number (pattern: *001466* or SALES ORDER : 1466)
        var invMatch = Regex.Match(fullText, @"SALES ORDER\s*:\s*(\d+)");
        if (invMatch.Success)
            invoice.InvoiceNumber = invMatch.Groups[1].Value;
        
        // Extract date (pattern: Date: 13 Nov 25)
        var dateMatch = Regex.Match(fullText, @"Date:\s*(\d+)\s+(\w+)\s+(\d+)");
        if (dateMatch.Success)
        {
            try
            {
                var day = int.Parse(dateMatch.Groups[1].Value);
                var month = dateMatch.Groups[2].Value;
                var year = 2000 + int.Parse(dateMatch.Groups[3].Value);
                invoice.InvoiceDate = ParseDate(day, month, year);
            }
            catch { }
        }
        
        // Extract line items - look for pattern: SKU | Product Name | quantities | prices
        var lines = fullText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Pattern: number followed by SKU, product name, and prices
            // Example: 1 1127807 GEEKBAR PULSE 5% DISPO (80ML) 15K PUFFS 5CT/ 2 0 2 $65.00 $65.00 $0.00 $130.00
            var match = Regex.Match(line, @"^\d+\s+(\d+)\s+(.+?)\s+(\d+)\s+\d+\s+\d+\s+\$?([\d.]+)\s+\$?([\d.]+)\s+\$?[\d.]+\s+\$?([\d.]+)$");
            
            if (match.Success)
            {
                invoice.LineItems.Add(new InvoiceLineItem
                {
                    SKU = match.Groups[1].Value,
                    ProductName = match.Groups[2].Value.Trim(),
                    Quantity = int.Parse(match.Groups[3].Value),
                    UnitPrice = decimal.Parse(match.Groups[5].Value),
                    TotalAmount = decimal.Parse(match.Groups[6].Value)
                });
            }
        }
        
        // Extract grand total
        var totalMatch = Regex.Match(fullText, @"Grand Total\s+\$?([\d,]+\.?\d*)");
        if (totalMatch.Success)
            invoice.GrandTotal = decimal.Parse(totalMatch.Groups[1].Value.Replace(",", ""));
        
        return invoice;
    }
    
    // VENDOR 2: SAFA GOODS
    private ParsedInvoice ParseSafaGoods(PdfDocument document, string fullText)
    {
        var invoice = new ParsedInvoice { VendorName = "SAFA GOODS" };
        
        // Extract invoice number (pattern: SALES ORDER : 40155)
        var invMatch = Regex.Match(fullText, @"SALES ORDER\s*:\s*(\d+)");
        if (invMatch.Success)
            invoice.InvoiceNumber = invMatch.Groups[1].Value;
        
        // Extract date (pattern: Date: 14 Jan 26)
        var dateMatch = Regex.Match(fullText, @"Date:\s*(\d+)\s+(\w+)\s+(\d+)");
        if (dateMatch.Success)
        {
            try
            {
                var day = int.Parse(dateMatch.Groups[1].Value);
                var month = dateMatch.Groups[2].Value;
                var year = 2000 + int.Parse(dateMatch.Groups[3].Value);
                invoice.InvoiceDate = ParseDate(day, month, year);
            }
            catch { }
        }
        
        // Parse line items (similar structure to Tri State)
        var lines = fullText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Pattern with UPC
            var match = Regex.Match(line, @"^\d+\s+(\d+)\s+(\d{13})\s+(.+?)\s+(\d+)\s+\d+\s+\d+\s+\$?([\d.]+)\s+\$?([\d.]+)\s+\$?([\d.]+)$");
            
            if (match.Success)
            {
                invoice.LineItems.Add(new InvoiceLineItem
                {
                    SKU = match.Groups[1].Value,
                    UPC = match.Groups[2].Value,
                    ProductName = match.Groups[3].Value.Trim(),
                    Quantity = int.Parse(match.Groups[4].Value),
                    UnitPrice = decimal.Parse(match.Groups[6].Value),
                    TotalAmount = decimal.Parse(match.Groups[7].Value)
                });
            }
        }
        
        // Extract grand total
        var totalMatch = Regex.Match(fullText, @"Grand Total\s+\$?([\d,]+\.?\d*)");
        if (totalMatch.Success)
            invoice.GrandTotal = decimal.Parse(totalMatch.Groups[1].Value.Replace(",", ""));
        
        return invoice;
    }
    
    // VENDOR 3: SKYGATE WHOLESALE
    private ParsedInvoice ParseSkygateWholesale(PdfDocument document, string fullText)
    {
        var invoice = new ParsedInvoice { VendorName = "SKYGATE WHOLESALE" };
        
        // Extract invoice number (pattern: S036382)
        var invMatch = Regex.Match(fullText, @"INVOICE NO\.\s*[:\s]*([S]\d+)", RegexOptions.IgnoreCase);
        if (invMatch.Success)
            invoice.InvoiceNumber = invMatch.Groups[1].Value;
        
        // Extract date
        var dateMatch = Regex.Match(fullText, @"DATE\s+(\d{1,2}/\d{1,2}/\d{4})");
        if (dateMatch.Success)
        {
            if (DateTime.TryParse(dateMatch.Groups[1].Value, out var date))
                invoice.InvoiceDate = date;
        }
        
        // Parse line items
        var lines = fullText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Pattern: ITEM# | quantities | DESCRIPTION | PRICE | TAX | AMOUNT
            var match = Regex.Match(line, @"^\d+\s+(\d+)\s+(\d+)\s+(\d+)\s+EA\s+(.+?)\s+\$[\d.]+\s+\$?([\d.]+)\s+\$?([\d.]+)$");
            
            if (match.Success)
            {
                invoice.LineItems.Add(new InvoiceLineItem
                {
                    SKU = match.Groups[1].Value,
                    ProductName = match.Groups[4].Value.Trim(),
                    Quantity = int.Parse(match.Groups[2].Value),
                    UnitPrice = decimal.Parse(match.Groups[5].Value),
                    TotalAmount = decimal.Parse(match.Groups[6].Value)
                });
            }
        }
        
        // Extract total
        var totalMatch = Regex.Match(fullText, @"Total\s+\$?([\d,]+\.?\d*)");
        if (totalMatch.Success)
            invoice.GrandTotal = decimal.Parse(totalMatch.Groups[1].Value.Replace(",", ""));
        
        return invoice;
    }
    
    // VENDOR 4: 1OAK WHOLESALE
    private ParsedInvoice Parse1OakWholesale(PdfDocument document, string fullText)
    {
        var invoice = new ParsedInvoice { VendorName = "1OAK WHOLESALE" };
        
        // Order vs Invoice number
        var orderMatch = Regex.Match(fullText, @"Order:\s*#(\d+)");
        var invoiceMatch = Regex.Match(fullText, @"Invoice:\s*#(\d+)");
        
        if (invoiceMatch.Success)
            invoice.InvoiceNumber = invoiceMatch.Groups[1].Value;
        else if (orderMatch.Success)
            invoice.InvoiceNumber = "ORD-" + orderMatch.Groups[1].Value;
        
        // Extract date
        var dateMatch = Regex.Match(fullText, @"Date:\s*(\w+\s+\d+,\s+\d{4})");
        if (dateMatch.Success)
        {
            if (DateTime.TryParse(dateMatch.Groups[1].Value, out var date))
                invoice.InvoiceDate = date;
        }
        
        // Parse line items - simpler format
        var lines = fullText.Split('\n');
        foreach (var line in lines)
        {
            // Look for: SKU: xxx followed by product name, qty, price
            if (line.Contains("SKU:"))
            {
                var skuMatch = Regex.Match(line, @"SKU:\s*(\w+)");
                if (skuMatch.Success)
                {
                    var sku = skuMatch.Groups[1].Value;
                    
                    // Next lines should have product name, qty, price
                    // This is simplified - you'll need to parse the actual structure
                }
            }
        }
        
        // Extract grand total
        var totalMatch = Regex.Match(fullText, @"Grand Total \(Incl\.Tax\)\s+\$?([\d,]+\.?\d*)");
        if (totalMatch.Success)
            invoice.GrandTotal = decimal.Parse(totalMatch.Groups[1].Value.Replace(",", ""));
        
        return invoice;
    }
    
    // VENDOR 5: HS WHOLESALE
    private ParsedInvoice ParseHSWholesale(PdfDocument document, string fullText)
    {
        var invoice = new ParsedInvoice { VendorName = "HS WHOLESALE" };
        
        // Extract order number
        var orderMatch = Regex.Match(fullText, @"Order:\s*#(\d+)");
        if (orderMatch.Success)
            invoice.InvoiceNumber = orderMatch.Groups[1].Value;
        
        // Extract date
        var dateMatch = Regex.Match(fullText, @"Order Date:\s*(\w+\s+\d+\w+\s+\d{4})");
        if (dateMatch.Success)
        {
            // Parse "Jan 13th 2026" format
            var dateStr = dateMatch.Groups[1].Value;
            dateStr = Regex.Replace(dateStr, @"(\d+)(st|nd|rd|th)", "$1");
            if (DateTime.TryParse(dateStr, out var date))
                invoice.InvoiceDate = date;
        }
        
        // Parse items
        var lines = fullText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Pattern: Qty | Code/SKU | Product Name | Price | Total
            var match = Regex.Match(line, @"(\d+)\s+(\w+)\s+(.+?)\s+\$?([\d.]+)\s+\$?([\d.]+)$");
            
            if (match.Success)
            {
                invoice.LineItems.Add(new InvoiceLineItem
                {
                    Quantity = int.Parse(match.Groups[1].Value),
                    SKU = match.Groups[2].Value,
                    ProductName = match.Groups[3].Value.Trim(),
                    UnitPrice = decimal.Parse(match.Groups[4].Value),
                    TotalAmount = decimal.Parse(match.Groups[5].Value)
                });
            }
        }
        
        // Extract grand total
        var totalMatch = Regex.Match(fullText, @"Grand total\s+\$?([\d,]+\.?\d*)");
        if (totalMatch.Success)
            invoice.GrandTotal = decimal.Parse(totalMatch.Groups[1].Value.Replace(",", ""));
        
        return invoice;
    }
    
    // VENDOR 6: AMERICAN DISTRIBUTORS
    private ParsedInvoice ParseAmericanDistributors(PdfDocument document, string fullText)
    {
        var invoice = new ParsedInvoice { VendorName = "AMERICAN DISTRIBUTORS" };
        
        // Extract transaction/invoice number
        var invMatch = Regex.Match(fullText, @"TRANSACTION NO\.\s+(\d+)");
        if (invMatch.Success)
            invoice.InvoiceNumber = invMatch.Groups[1].Value;
        
        // Extract date
        var dateMatch = Regex.Match(fullText, @"DATE\s+(\d+\s+\w+\s+\d{4})");
        if (dateMatch.Success)
        {
            if (DateTime.TryParse(dateMatch.Groups[1].Value, out var date))
                invoice.InvoiceDate = date;
        }
        
        // Parse line items - multi-column table format
        var lines = fullText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Pattern: SKU | QTY | DESCRIPTION | UNIT PRICE | UNIT TAX | EXTENDED PRICE
            var match = Regex.Match(line, @"^(\w+)\s+(\d+)\s+(.+?)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)$");
            
            if (match.Success)
            {
                invoice.LineItems.Add(new InvoiceLineItem
                {
                    SKU = match.Groups[1].Value,
                    Quantity = int.Parse(match.Groups[2].Value),
                    ProductName = match.Groups[3].Value.Trim(),
                    UnitPrice = decimal.Parse(match.Groups[4].Value),
                    TotalAmount = decimal.Parse(match.Groups[6].Value)
                });
            }
        }
        
        // Extract total
        var totalMatch = Regex.Match(fullText, @"TOTAL AMOUNT\s+([\d,]+\.?\d*)");
        if (totalMatch.Success)
            invoice.GrandTotal = decimal.Parse(totalMatch.Groups[1].Value.Replace(",", ""));
        
        return invoice;
    }
    
    // VENDOR 7: AK WHOLESALE
    private ParsedInvoice ParseAKWholesale(PdfDocument document, string fullText)
    {
        var invoice = new ParsedInvoice { VendorName = "AK WHOLESALE" };
        
        // Extract invoice number
        var invMatch = Regex.Match(fullText, @"Invoice No\s+([S]\d+)", RegexOptions.IgnoreCase);
        if (invMatch.Success)
            invoice.InvoiceNumber = invMatch.Groups[1].Value;
        
        // Extract date
        var dateMatch = Regex.Match(fullText, @"DATE\s+(\d{2}/\d{2}/\d{2})");
        if (dateMatch.Success)
        {
            if (DateTime.TryParseExact(dateMatch.Groups[1].Value, "MM/dd/yy", 
                System.Globalization.CultureInfo.InvariantCulture, 
                System.Globalization.DateTimeStyles.None, out var date))
            {
                invoice.InvoiceDate = date;
            }
        }
        
        // Parse line items
        var lines = fullText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // Pattern: UPC | ORD | SHIP | UNIT | DESCRIPTION | VOL(ML) | TAX | PRICE | AMOUNT
            var match = Regex.Match(line, @"(\d{13})\s+(\d+)\s+(\d+)\s+BOX\s+(.+?)\s+([\d.]+)\s+([\d.]+)\s+\$[\d.]+\s+([\d.]+)\s+([\d.]+)");
            
            if (match.Success)
            {
                invoice.LineItems.Add(new InvoiceLineItem
                {
                    UPC = match.Groups[1].Value,
                    Quantity = int.Parse(match.Groups[3].Value),
                    ProductName = match.Groups[4].Value.Trim(),
                    UnitPrice = decimal.Parse(match.Groups[7].Value),
                    TotalAmount = decimal.Parse(match.Groups[8].Value)
                });
            }
        }
        
        // Extract total
        var totalMatch = Regex.Match(fullText, @"Total\s+\$?([\d,]+\.?\d*)");
        if (totalMatch.Success)
            invoice.GrandTotal = decimal.Parse(totalMatch.Groups[1].Value.Replace(",", ""));
        
        return invoice;
    }
    
    // Helper method to parse dates
    private DateTime ParseDate(int day, string monthStr, int year)
    {
        var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"Jan", 1}, {"January", 1},
            {"Feb", 2}, {"February", 2},
            {"Mar", 3}, {"March", 3},
            {"Apr", 4}, {"April", 4},
            {"May", 5},
            {"Jun", 6}, {"June", 6},
            {"Jul", 7}, {"July", 7},
            {"Aug", 8}, {"August", 8},
            {"Sep", 9}, {"Sept", 9}, {"September", 9},
            {"Oct", 10}, {"October", 10},
            {"Nov", 11}, {"November", 11},
            {"Dec", 12}, {"December", 12}
        };
        
        if (months.TryGetValue(monthStr, out var month))
        {
            return new DateTime(year, month, day);
        }
        
        return DateTime.Today;
    }
}
```

---

### PRICE CHANGE DETECTION SERVICE

**File**: `src/ManagerPaperworkSystem.Core/Services/PriceChangeDetector.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ManagerPaperworkSystem.Core.Models;
using ManagerPaperworkSystem.Data.Db;

namespace ManagerPaperworkSystem.Core.Services;

public class PriceChangeDetector
{
    private readonly AppDbContext _db;
    
    public PriceChangeDetector(AppDbContext db)
    {
        _db = db;
    }
    
    public async Task<List<PriceAlert>> ProcessInvoiceAndDetectChanges(ParsedInvoice invoice)
    {
        var alerts = new List<PriceAlert>();
        
        foreach (var item in invoice.LineItems)
        {
            // Find existing product cost by UPC, SKU, or name
            var existing = await _db.ProductCosts
                .Where(pc => 
                    (!string.IsNullOrEmpty(item.UPC) && pc.UPC == item.UPC) ||
                    (!string.IsNullOrEmpty(item.SKU) && pc.SKU == item.SKU) ||
                    pc.ProductName == item.ProductName)
                .FirstOrDefaultAsync();
            
            if (existing != null)
            {
                // Check if price changed
                if (existing.CurrentCost != item.UnitPrice)
                {
                    var changePercent = ((item.UnitPrice - existing.CurrentCost) / existing.CurrentCost) * 100;
                    
                    // Create alert
                    var alert = new PriceAlert
                    {
                        ProductName = item.ProductName,
                        UPC = item.UPC,
                        SKU = item.SKU,
                        OldPrice = existing.CurrentCost,
                        NewPrice = item.UnitPrice,
                        ChangePercent = changePercent,
                        VendorName = invoice.VendorName,
                        InvoiceNumber = invoice.InvoiceNumber,
                        DetectedDate = DateTime.UtcNow,
                        IsRead = false,
                        IsAcknowledged = false
                    };
                    
                    _db.PriceAlerts.Add(alert);
                    alerts.Add(alert);
                    
                    // Update product cost
                    existing.PreviousCost = existing.CurrentCost;
                    existing.CurrentCost = item.UnitPrice;
                    existing.VendorName = invoice.VendorName;
                    existing.LastUpdated = DateTime.UtcNow;
                    existing.InvoiceNumber = invoice.InvoiceNumber;
                    existing.ChangePercent = changePercent;
                }
            }
            else
            {
                // New product - add to tracking
                var newCost = new ProductCost
                {
                    UPC = item.UPC,
                    SKU = item.SKU,
                    ProductName = item.ProductName,
                    CurrentCost = item.UnitPrice,
                    PreviousCost = null,
                    VendorName = invoice.VendorName,
                    LastUpdated = DateTime.UtcNow,
                    InvoiceNumber = invoice.InvoiceNumber,
                    ChangePercent = null
                };
                
                _db.ProductCosts.Add(newCost);
            }
        }
        
        await _db.SaveChangesAsync();
        return alerts;
    }
    
    public async Task<int> GetUnreadAlertCount()
    {
        return await _db.PriceAlerts.CountAsync(a => !a.IsRead);
    }
    
    public async Task MarkAlertAsRead(int alertId)
    {
        var alert = await _db.PriceAlerts.FindAsync(alertId);
        if (alert != null)
        {
            alert.IsRead = true;
            await _db.SaveChangesAsync();
        }
    }
    
    public async Task MarkAlertAsAcknowledged(int alertId)
    {
        var alert = await _db.PriceAlerts.FindAsync(alertId);
        if (alert != null)
        {
            alert.IsAcknowledged = true;
            alert.IsRead = true;
            await _db.SaveChangesAsync();
        }
    }
}
```

---

## CRITICAL NOTES FOR DEVELOPER

### 1. PDF Parsing Challenges
- The regex patterns provided are **starting points** - they will need refinement based on actual PDF text extraction
- PdfPig text extraction can be unpredictable - text may not be in the order it appears visually
- You may need to use coordinate-based extraction for more reliable parsing
- Test extensively with multiple invoices from each vendor

### 2. Performance Considerations
- Index the ProductCosts table properly (UPC, SKU, ProductName)
- Consider caching vendor patterns
- For large invoices (100+ items), process in batches

### 3. Error Handling
- Wrap all parsing in try-catch blocks
- Log failed parses with invoice details
- Provide meaningful error messages to users
- Allow manual fallback for unparseable invoices

### 4. Testing Strategy
- Create unit tests for each vendor parser
- Use the provided sample PDFs for integration testing
- Test edge cases: missing fields, malformed data, multi-page invoices

### 5. UI/UX Recommendations
- Show parsing progress for large files
- Display preview of extracted data before saving
- Allow users to correct parsed data if needed
- Show clear alert notifications for price changes

---

## ESTIMATED BREAKDOWN

- Database schema & entities: 1 hour
- Simplified Purchases UI & logic: 2 hours
- Basic invoice parser structure: 2 hours
- Individual vendor parsers (7 vendors): 3-4 hours
- Price change detection: 1 hour
- UI for Product Costs section: 1 hour
- Testing & debugging: 2-3 hours

**TOTAL: 12-14 hours** (realistic estimate with testing)

---

## DELIVERABLES CHECKLIST

- [ ] Database migrations applied
- [ ] Entity classes created
- [ ] Simplified Purchases UI implemented
- [ ] Purchases CRUD operations working
- [ ] MultiVendorInvoiceParser class complete
- [ ] All 7 vendor parsers functional
- [ ] PriceChangeDetector service implemented
- [ ] Product Costs UI with upload feature
- [ ] Price alerts displayed in dashboard
- [ ] Comprehensive testing completed
- [ ] Error handling implemented
- [ ] User documentation provided

---

Good luck with the implementation! The regex patterns and logic provided should get you 70-80% there, but expect to spend time fine-tuning based on real-world PDF variations.
