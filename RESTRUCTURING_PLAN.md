# HB STORE LEDGER PRO - MAJOR RESTRUCTURING PLAN

## CRITICAL CHANGES NEEDED

### 1. LOGIN SCREEN - FINAL FIX
**Problem**: Buttons missing, size changing
**Solution**:
- Fixed size: 650x420 (increased from 450 to 420 for buttons)
- Regular buttons: Login, Cancel
- Link-style buttons: Create Account, Forgot Password (smaller, text-style)
- All buttons guaranteed to fit and be visible

### 2. HOME BUTTON SIZE
**Problem**: Home button too large
**Solution**:
- Match logo size: 35x35 pixels
- Simple icon button without excessive padding

### 3. PURCHASES SECTION - COMPLETE REDESIGN
**Current**: Complex invoice import with line items
**New Simple Version**:
```
Fields:
- Date (DatePicker)
- Vendor (ComboBox - from vendors list)
- Invoice Number (TextBox)
- Total Amount (TextBox - decimal)
- [Add Invoice Button]

Grid shows:
- Date | Vendor | Invoice# | Amount | Actions
```

**Remove**:
- Import Invoice (Auto) button
- Open File button  
- All line items grid
- Invoice file path
- Vendor name textbox
- Notes field
- Complex import functionality

### 4. PRODUCT COSTS SECTION - NEW INVOICE PARSER
**Purpose**: Track cost price changes across ALL vendors
**New Functionality**:
```
Top Section:
- [Upload Invoice Button]
- Vendor filter dropdown
- Date range filters

Main Grid:
- SKU/UPC | Product Name | Current Cost | Previous Cost | Change % | Last Updated | Vendor

Process:
1. User uploads ANY vendor invoice (PDF)
2. System extracts:
   - Vendor name (from PDF header)
   - Date
   - Line items: UPC/SKU, Description, Unit Price
3. System checks if price changed
4. If changed: Create alert notification
5. Update product costs table
```

## INVOICE PARSING LOGIC

Based on uploaded samples, need to handle:

### VENDOR 1: TRI STATE DISTRO
- Invoice# format: 4-digit number (1466)
- Line items in table format
- Columns: SKU | Product Name | SO | IO | Out | Price | Sold Price | Tax | Amount
- Extract: SKU, Product Name, Sold Price (unit cost)

### VENDOR 2: SAFA GOODS  
- Invoice# format: 5-digit (40155, 40149)
- Line items with UPC column
- Columns: UPC | Product Name | SO | IO | Out | Sold Price | Tax | Amount
- Extract: UPC, Product Name, Sold Price

### VENDOR 3: SKYGATE WHOLESALE
- Invoice# format: S + 6-digit (S036382)
- Line items with ITEM# column
- Columns: ITEM# | ORD | SHIP | UNIT | DESCRIPTION | PRICE | TAX | AMOUNT
- Extract: ITEM#, DESCRIPTION, PRICE

### VENDOR 4: 1OAK WHOLESALE
- Order# format: #000105366
- Invoice# format: #000078954
- Simple format: SKU, Product Name, Qty, Price, Subtotal
- Extract: SKU, Product Name, Price

### VENDOR 5: HS WHOLESALE
- Order# format: #486044
- Product list with Code/SKU, Product Name, Qty, Price, Total
- Extract: Code/SKU, Product Name, Price

### VENDOR 6: AMERICAN DISTRIBUTORS
- Invoice format: Transaction# 10603626
- Complex multi-page with many items
- Columns: SKU | QTY | DESCRIPTION | UNIT PRICE | UNIT TAX | EXTENDED PRICE
- Extract: SKU, DESCRIPTION, UNIT PRICE

### VENDOR 7: AK WHOLESALE
- Invoice format: S + 6-digit (S287801)
- Columns: UPC | ORD | SHIP | UNIT | DESCRIPTION | VOL(ML) | TAX | PRICE | AMOUNT
- Extract: UPC, DESCRIPTION, PRICE

## IMPLEMENTATION STEPS

### Step 1: Fix Login Window
- Set exact size 650x420
- Two regular buttons (Login, Cancel)
- Two link buttons (Create Account, Forgot Password)
- Test visibility

### Step 2: Fix Home Button
- Reduce size to match logo (35x35)
- Simple icon styling

### Step 3: Redesign Purchases Section
- Remove complex UI
- Add simple 4-field form
- Add simple grid
- Update database schema if needed

### Step 4: Create New Product Costs Section
- Add upload button
- Create invoice parser service
- Add vendor detection logic
- Add line item extraction for each vendor
- Create price change detection
- Add notification system
- Create costs tracking grid

### Step 5: Update Database
```sql
-- New/Updated tables needed

-- Simple purchase tracking
CREATE TABLE PurchaseRecords (
    Id INTEGER PRIMARY KEY,
    Date TEXT NOT NULL,
    VendorId INTEGER,
    InvoiceNumber TEXT,
    TotalAmount REAL,
    CreatedDate TEXT
);

-- Product cost tracking
CREATE TABLE ProductCosts (
    Id INTEGER PRIMARY KEY,
    UPC TEXT,
    SKU TEXT,
    ProductName TEXT,
    CurrentCost REAL,
    PreviousCost REAL,
    VendorId INTEGER,
    LastUpdated TEXT,
    InvoiceNumber TEXT
);

-- Price change alerts
CREATE TABLE PriceAlerts (
    Id INTEGER PRIMARY KEY,
    ProductName TEXT,
    OldPrice REAL,
    NewPrice REAL,
    ChangePercent REAL,
    DetectedDate TEXT,
    VendorId INTEGER,
    IsRead BOOLEAN
);
```

## FILES TO MODIFY

1. **LoginWindow.xaml** - Fix size and buttons
2. **MainWindow.xaml** - Fix home button, redesign Purchases section
3. **InvoiceImportService.cs** - Create multi-vendor parser
4. **Database schema** - Add new tables
5. **Create PriceAlertService.cs** - Price change detection
6. **Create ProductCostWindow.xaml** - New UI for cost tracking

## TESTING CHECKLIST

- [ ] Login window 650x420 with all 4 buttons visible
- [ ] Home button same size as logo
- [ ] Purchases section shows simple form
- [ ] Can add purchase record manually
- [ ] Can upload TRI STATE DISTRO invoice and extract data
- [ ] Can upload SAFA GOODS invoice and extract data
- [ ] Can upload SKYGATE invoice and extract data
- [ ] Can upload 1OAK invoice and extract data
- [ ] Can upload HS WHOLESALE invoice and extract data
- [ ] Can upload AMERICAN DISTRIBUTORS invoice and extract data
- [ ] Can upload AK WHOLESALE invoice and extract data
- [ ] Price changes detected correctly
- [ ] Alerts appear in dashboard
- [ ] Product costs grid shows current/previous prices

---

**This is a MAJOR refactoring. Estimated work: 8-12 hours.**

Would you like me to proceed with implementing all these changes?
