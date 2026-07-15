-- ================================================================
-- HISAB KITAB - New Store Database Setup Script
-- ================================================================
-- Run this against any new HBStoreLedger_* database to create
-- all required tables. Safe to re-run (uses IF NOT EXISTS).
--
-- INSTRUCTIONS:
-- 1. Create the database in Azure: HBStoreLedger_YourStoreName
-- 2. Connect to it in SSMS
-- 3. Replace 'YOUR STORE NAME' below with the actual store name
-- 4. Run this entire script
-- ================================================================

DECLARE @StoreName NVARCHAR(200) = 'YOUR STORE NAME';
DECLARE @StoreAddress NVARCHAR(500) = '';

-- 1. Stores
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Stores')
CREATE TABLE [Stores] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [Name] NVARCHAR(200) NOT NULL DEFAULT '',
    [Address] NVARCHAR(500) NOT NULL DEFAULT '',
    [Phone] NVARCHAR(50) NULL,
    [IsActive] BIT NOT NULL DEFAULT 1,
    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
IF NOT EXISTS (SELECT 1 FROM [Stores])
    INSERT INTO [Stores] ([Name], [Address], [IsActive]) VALUES (@StoreName, @StoreAddress, 1);

-- 2. AppSettings
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings')
CREATE TABLE [AppSettings] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreName] NVARCHAR(200) NOT NULL DEFAULT '',
    [StoreAddress] NVARCHAR(500) NOT NULL DEFAULT '',
    [DefaultStoreId] INT NOT NULL DEFAULT 1,
    [LastStoreId] INT NOT NULL DEFAULT 1
);
IF NOT EXISTS (SELECT 1 FROM [AppSettings])
    INSERT INTO [AppSettings] ([StoreName],[StoreAddress],[DefaultStoreId],[LastStoreId])
    VALUES (@StoreName, @StoreAddress, 1, 1);

-- 3. UserAccounts
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'UserAccounts')
CREATE TABLE [UserAccounts] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Username] NVARCHAR(100) NOT NULL DEFAULT '',
    [Email] NVARCHAR(200) NOT NULL DEFAULT '',
    [PasswordHashBase64] NVARCHAR(500) NOT NULL DEFAULT '',
    [SaltBase64] NVARCHAR(500) NOT NULL DEFAULT '',
    [DisplayName] NVARCHAR(200) NOT NULL DEFAULT '',
    [Role] INT NOT NULL DEFAULT 0,
    [IsActive] BIT NOT NULL DEFAULT 1,
    [SecurityQuestion] NVARCHAR(500) NULL,
    [SecurityAnswerHash] NVARCHAR(500) NULL,
    [LastLoginUtc] DATETIME2 NULL,
    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- 4. ShiftLogs
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ShiftLogs')
CREATE TABLE [ShiftLogs] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Date] DATE NOT NULL,
    [Employee] NVARCHAR(200) NOT NULL DEFAULT '',
    [ShiftNo] NVARCHAR(50) NOT NULL DEFAULT '',
    [CashTotal] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [CardTotal] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [NetSales] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Tax] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [CashDropReceived] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [RegisterPayout] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [PayoutReason] NVARCHAR(300) NOT NULL DEFAULT '',
    [IsCorrection] BIT NOT NULL DEFAULT 0,
    [CorrectsId] INT NULL,
    [CreatedByUserId] INT NULL,
    [CreatedByName] NVARCHAR(200) NOT NULL DEFAULT '',
    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    [PosReportPath] NVARCHAR(500) NULL
);

-- 5. CashOnHand
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CashOnHand')
CREATE TABLE [CashOnHand] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Date] DATE NOT NULL,
    [IsPayout] BIT NOT NULL DEFAULT 0,
    [CashAdded] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [PayoutAmount] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [VendorId] INT NULL,
    [PurposeId] INT NULL,
    [Description] NVARCHAR(500) NOT NULL DEFAULT '',
    [Reference] NVARCHAR(200) NULL,
    [IsCorrection] BIT NOT NULL DEFAULT 0,
    [CorrectsId] INT NULL,
    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- 6. CheckPayouts
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'CheckPayouts')
CREATE TABLE [CheckPayouts] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Date] DATE NOT NULL,
    [PayTo] NVARCHAR(200) NOT NULL DEFAULT '',
    [CheckNumber] NVARCHAR(50) NOT NULL DEFAULT '',
    [CheckAmount] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Cleared] BIT NOT NULL DEFAULT 0,
    [Description] NVARCHAR(500) NOT NULL DEFAULT '',
    [IsCorrection] BIT NOT NULL DEFAULT 0,
    [CorrectsId] INT NULL,
    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- 7. Vendors
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Vendors')
CREATE TABLE [Vendors] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Name] NVARCHAR(200) NOT NULL DEFAULT ''
);

-- 8. Purposes
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Purposes')
CREATE TABLE [Purposes] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Name] NVARCHAR(200) NOT NULL DEFAULT ''
);

-- 9. PurchaseInvoices + Lines
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PurchaseInvoices')
CREATE TABLE [PurchaseInvoices] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [VendorId] INT NULL,
    [InvoiceNumber] NVARCHAR(100) NOT NULL DEFAULT '',
    [InvoiceDate] DATE NOT NULL,
    [Total] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Notes] NVARCHAR(1000) NOT NULL DEFAULT '',
    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PurchaseInvoiceLines')
CREATE TABLE [PurchaseInvoiceLines] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [InvoiceId] INT NOT NULL,
    [ProductName] NVARCHAR(300) NOT NULL DEFAULT '',
    [Quantity] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [UnitCost] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Total] DECIMAL(18,2) NOT NULL DEFAULT 0,
    CONSTRAINT FK_InvoiceLines_Invoice FOREIGN KEY ([InvoiceId])
        REFERENCES [PurchaseInvoices]([Id]) ON DELETE CASCADE
);

-- 10. ProductCosts
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ProductCosts')
CREATE TABLE [ProductCosts] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [ProductName] NVARCHAR(300) NOT NULL DEFAULT '',
    [LatestUnitCost] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [PreviousUnitCost] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [VendorId] INT NULL,
    [LastUpdatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- 11. PriceAlerts
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PriceAlerts')
CREATE TABLE [PriceAlerts] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [ProductName] NVARCHAR(300) NOT NULL DEFAULT '',
    [OldPrice] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [NewPrice] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [ChangePercent] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [VendorName] NVARCHAR(200) NOT NULL DEFAULT '',
    [IsRead] BIT NOT NULL DEFAULT 0,
    [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- 12. BankStatementTransactions
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'BankStatementTransactions')
CREATE TABLE [BankStatementTransactions] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [StoreId] INT NOT NULL DEFAULT 1,
    [Date] DATETIME2 NOT NULL,
    [Description] NVARCHAR(500) NOT NULL DEFAULT '',
    [Credit] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Debit] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Balance] DECIMAL(18,2) NOT NULL DEFAULT 0,
    [Category] NVARCHAR(200) NULL,
    [CheckNumber] NVARCHAR(50) NULL,
    [Reference] NVARCHAR(200) NULL,
    [ImportedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

PRINT 'All tables created successfully for: ' + @StoreName;
