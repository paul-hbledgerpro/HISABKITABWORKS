using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.UI.Services;

public sealed class InvoiceImportResult
{
    public bool Success { get; init; }
    public string VendorName { get; init; } = "";
    public string InvoiceNumber { get; init; } = "";
    public DateOnly? InvoiceDate { get; init; }
    public decimal? Total { get; init; }
    public List<PurchaseInvoiceLine> Lines { get; init; } = new();
    /// <summary>
    /// If the uploaded file contains multiple invoices (e.g., a PDF with several invoices),
    /// this list will contain one entry per invoice/page.
    /// </summary>
    public List<ImportedInvoice> Invoices { get; init; } = new();
    public string RawText { get; init; } = "";
    public List<string> Warnings { get; init; } = new();
}

public sealed class ImportedInvoice
{
    public int? PageNumber { get; init; }
    public string VendorName { get; init; } = "";
    public string InvoiceNumber { get; init; } = "";
    public DateOnly? InvoiceDate { get; init; }
    public decimal? Total { get; init; }
    public List<PurchaseInvoiceLine> Lines { get; init; } = new();
}

/// <summary>
/// Attempts to import invoice header + line items from uploaded files.
/// Supports: PDF (text-based via PdfPig), Excel (xlsx/xls via ClosedXML).
/// Images are accepted but not OCR'd in this version.
/// </summary>
public sealed class InvoiceImportService
{
    public async Task<InvoiceImportResult> ImportAsync(string filePath, string? selectedVendor = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new InvoiceImportResult { Success = false, Warnings = { "Invoice file not found." } };

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            return ext switch
            {
                ".pdf" => await Task.Run(() => ImportFromPdf(filePath, selectedVendor), ct),
                ".xlsx" or ".xls" => await Task.Run(() => ImportFromExcel(filePath), ct),
                ".csv" => await Task.Run(() => ImportFromCsv(filePath), ct),
                _ => new InvoiceImportResult
                {
                    Success = false,
                    Warnings = { "Unsupported invoice format. Please upload a PDF, XLSX/XLS or CSV." }
                }
            };
        }
        catch (Exception ex)
        {
            return new InvoiceImportResult
            {
                Success = false,
                Warnings = { ex.Message }
            };
        }
    }


    /// <summary>
    /// Imports ONLY the columns needed for Product Cost tracking: UPC/SKU (numeric), Description, Unit Cost.
    /// Used by the Product Costs screen (Upload Invoice).
    /// </summary>
    public async Task<InvoiceImportResult> ImportCostsOnlyAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new InvoiceImportResult { Success = false, Warnings = { "Invoice file not found." } };

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            return ext switch
            {
                ".pdf" => await Task.Run(() => ImportCostsFromPdf(filePath), ct),
                ".xlsx" or ".xls" => await Task.Run(() => ImportFromExcel(filePath), ct),
                ".csv" => await Task.Run(() => ImportFromCsv(filePath), ct),
                _ => new InvoiceImportResult
                {
                    Success = false,
                    Warnings = { "Unsupported format. Please upload a PDF, XLSX/XLS or CSV." }
                }
            };
        }
        catch (Exception ex)
        {
            return new InvoiceImportResult
            {
                Success = false,
                Warnings = { ex.Message }
            };
        }
    }

    private static InvoiceImportResult ImportCostsFromPdf(string filePath)
    {
        var (rawByPage, linesByPage) = ExtractPdfByPage(filePath);
        var textLinesByPage = linesByPage;
        var layoutRawByPage = textLinesByPage.Select(page => string.Join("\n", page)).ToList();
        var combinedRaw = string.Join("\n\n", layoutRawByPage);

        var invoices = new List<ImportedInvoice>();
        var warnings = new List<string>();

        // Detect vendor from combined text
        var isAkWholesale = IsAkWholesale(combinedRaw);
        var isAmericanDistributors = IsAmericanDistributors(combinedRaw);

        // Real vendor templates are parsed deterministically before the universal heuristic.
        // Product-cost imports feed price alerts, so a known layout must not be guessed.
        ImportedInvoice? knownInvoice = null;
        if (isAkWholesale)
            knownInvoice = ParseAkWholesaleInvoiceFromPdf(filePath);
        else if (IsDemandVape(combinedRaw))
            knownInvoice = ParseDemandVapeInvoice(combinedRaw, textLinesByPage.SelectMany(x => x).ToList());
        else if (IsSkygate(combinedRaw))
            knownInvoice = ParseSkygateWholesaleInvoice(combinedRaw, textLinesByPage.SelectMany(x => x).ToList());
        else if (Is1OakWholesale(combinedRaw))
            knownInvoice = Parse1OakWholesaleInvoice(combinedRaw, textLinesByPage.SelectMany(x => x).ToList());

        if (knownInvoice is not null && knownInvoice.Lines.Count > 0)
        {
            return new InvoiceImportResult
            {
                Success = true,
                VendorName = knownInvoice.VendorName,
                InvoiceNumber = knownInvoice.InvoiceNumber,
                InvoiceDate = knownInvoice.InvoiceDate,
                Total = knownInvoice.Total,
                Lines = knownInvoice.Lines,
                Invoices = new List<ImportedInvoice> { knownInvoice },
                RawText = combinedRaw,
                Warnings = warnings
            };
        }

        if (isAmericanDistributors)
        {
            var americanPages = new List<ImportedInvoice>();
            for (var i = 0; i < rawByPage.Count; i++)
            {
                var parsed = ParseAmericanDistributorsInvoice(layoutRawByPage[i], textLinesByPage[i], i + 1);
                if (parsed.Lines.Count > 0 || parsed.Total is > 0m || !string.IsNullOrWhiteSpace(parsed.InvoiceNumber))
                    americanPages.Add(parsed);
            }

            var americanInvoices = MergeAmericanDistributorsInvoices(americanPages);
            var americanLines = americanInvoices.SelectMany(x => x.Lines).ToList();
            if (americanLines.Count > 0)
            {
                var firstAmerican = americanInvoices[0];
                return new InvoiceImportResult
                {
                    Success = true,
                    VendorName = firstAmerican.VendorName,
                    InvoiceNumber = firstAmerican.InvoiceNumber,
                    InvoiceDate = firstAmerican.InvoiceDate,
                    Total = firstAmerican.Total,
                    Lines = americanLines,
                    Invoices = americanInvoices,
                    RawText = combinedRaw,
                    Warnings = warnings
                };
            }
        }

        // Try the universal PDF line item extractor first (works for most invoice formats)
        var universalLines = ExtractInvoiceLineItemsUniversal(filePath, combinedRaw);
        if (universalLines.Count > 0)
        {
            var vendorName = DetectVendorName(combinedRaw);
            
            return new InvoiceImportResult
            {
                Success = true,
                VendorName = vendorName,
                InvoiceNumber = GuessInvoiceNumber(combinedRaw) ?? "",
                InvoiceDate = GuessInvoiceDate(combinedRaw),
                Total = GuessTotal(combinedRaw),
                Lines = universalLines,
                Invoices = new List<ImportedInvoice> { new ImportedInvoice { 
                    VendorName = vendorName, 
                    Lines = universalLines,
                    InvoiceNumber = GuessInvoiceNumber(combinedRaw) ?? "",
                    InvoiceDate = GuessInvoiceDate(combinedRaw)
                }},
                RawText = combinedRaw ?? "",
                Warnings = warnings
            };
        }

        // Fallback to vendor-specific parsers
        if (isAkWholesale)
        {
            try
            {
                var inv = ParseAkWholesaleInvoiceFromPdf(filePath);
                if (inv.Lines.Count > 0)
                {
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = inv.VendorName,
                        InvoiceNumber = inv.InvoiceNumber,
                        InvoiceDate = inv.InvoiceDate,
                        Total = inv.Total,
                        Lines = inv.Lines,
                        Invoices = new List<ImportedInvoice> { inv },
                        RawText = combinedRaw ?? "",
                        Warnings = warnings
                    };
                }
                
                var simpleCostLines = ParseAkWholesaleCostsSimple(combinedRaw);
                if (simpleCostLines.Count > 0)
                {
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = "AK Wholesale Inc",
                        InvoiceNumber = GuessInvoiceNumber(combinedRaw) ?? "",
                        InvoiceDate = GuessInvoiceDate(combinedRaw),
                        Total = GuessTotal(combinedRaw),
                        Lines = simpleCostLines,
                        Invoices = new List<ImportedInvoice> { new ImportedInvoice { 
                            VendorName = "AK Wholesale Inc", 
                            Lines = simpleCostLines,
                            InvoiceNumber = GuessInvoiceNumber(combinedRaw) ?? "",
                            InvoiceDate = GuessInvoiceDate(combinedRaw)
                        }},
                        RawText = combinedRaw ?? "",
                        Warnings = warnings
                    };
                }
            }
            catch { /* Fall through to generic parsing */ }
        }

        if (isAmericanDistributors)
        {
            try
            {
                var allLines = new List<PurchaseInvoiceLine>();
                for (int i = 0; i < rawByPage.Count; i++)
                {
                    var parsed = ParseAmericanDistributorsInvoice(layoutRawByPage[i], textLinesByPage[i], pageNumber: i + 1);
                    if (parsed.Lines.Count > 0)
                    {
                        allLines.AddRange(parsed.Lines);
                        if (invoices.Count == 0 || !string.IsNullOrWhiteSpace(parsed.InvoiceNumber))
                        {
                            invoices.Add(parsed);
                        }
                    }
                }
                invoices = MergeAmericanDistributorsInvoices(invoices);
                if (allLines.Count > 0)
                {
                    var first = invoices.FirstOrDefault() ?? new ImportedInvoice { VendorName = "American Distributors LLC" };
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = first.VendorName,
                        InvoiceNumber = first.InvoiceNumber,
                        InvoiceDate = first.InvoiceDate,
                        Total = first.Total,
                        Lines = allLines,
                        Invoices = invoices,
                        RawText = combinedRaw ?? "",
                        Warnings = warnings
                    };
                }
            }
            catch { /* Fall through to generic parsing */ }
        }

        // Generic parsing fallback
        for (int i = 0; i < linesByPage.Count; i++)
        {
            var raw = rawByPage[i];
            var lines = linesByPage[i];

            var parsed = ParseCostOnlyLines(lines);
            if (parsed.Count == 0)
                parsed = ParseCostOnlyLines((raw ?? "").Split('\n'));

            if (parsed.Count == 0)
                continue;

            invoices.Add(new ImportedInvoice
            {
                PageNumber = i + 1,
                VendorName = GuessVendorName(raw ?? "") ?? "",
                InvoiceNumber = GuessInvoiceNumber(raw ?? "") ?? "",
                InvoiceDate = GuessInvoiceDate(raw ?? ""),
                Lines = parsed
            });
        }

        if (invoices.Count == 0)
        {
            // Last resort: combine all lines
            var allLines = new List<string>();
            foreach (var pageLines in linesByPage) allLines.AddRange(pageLines);

            var parsed = ParseCostOnlyLines(allLines);
            if (parsed.Count == 0)
                parsed = ParseCostOnlyLines((combinedRaw ?? "").Split('\n'));

            if (parsed.Count == 0)
            {
                return new InvoiceImportResult
                {
                    Success = false,
                    RawText = combinedRaw ?? "",
                    Warnings =
                    {
                        "No cost table found. Make sure the invoice has columns like: SKU/UPC, Description, Cost/Unit Price.",
                        "Tip: If this is a scanned image PDF, this version cannot OCR it. Export to text-based PDF or upload XLSX/CSV."
                    }
                };
            }

            invoices.Add(new ImportedInvoice
            {
                PageNumber = null,
                VendorName = GuessVendorName(combinedRaw ?? "") ?? "",
                InvoiceNumber = GuessInvoiceNumber(combinedRaw ?? "") ?? "",
                InvoiceDate = GuessInvoiceDate(combinedRaw ?? ""),
                Lines = parsed
            });
        }

        if (invoices.Count > 1)
            warnings.Add($"Detected {invoices.Count} page(s) with cost lines. Imported costs from each detected page.");

        var first2 = invoices[0];
        var all2 = invoices.SelectMany(x => x.Lines).ToList();

        return new InvoiceImportResult
        {
            Success = true,
            VendorName = first2.VendorName,
            InvoiceNumber = first2.InvoiceNumber,
            InvoiceDate = first2.InvoiceDate,
            Lines = all2,
            Invoices = invoices,
            RawText = combinedRaw ?? "",
            Warnings = warnings
        };
    }

    /// <summary>
    /// Strict cost-only parsing:
    /// - SKU/UPC: numeric-only (4+ digits)
    /// - Cost: last value in the row, supports $ format
    /// - Description: whatever is between sku and cost
    /// </summary>
    private static List<PurchaseInvoiceLine> ParseCostOnlyLines(IEnumerable<string> lines)
    {
        var norm = lines
            .Select(l => (l ?? string.Empty).Replace('\t', ' '))
            .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        static bool LooksLikeHeader(string s)
        {
            var t = s.ToUpperInvariant();
            var hasSku = t.Contains("SKU") || t.Contains("UPC");
            var hasDesc = t.Contains("DESC");
            var hasCost = t.Contains("COST") || t.Contains("UNIT") || t.Contains("PRICE");
            return hasSku && hasDesc && hasCost;
        }

        static bool IsStopLine(string s)
        {
            var t = s.ToUpperInvariant();
            return t.Contains("SUBTOTAL") || t.Contains("TOTAL") || t.Contains("BALANCE") || t.Contains("AMOUNT DUE") || t.Contains("INVOICE TOTAL") || t.Contains("PAYMENT");
        }

        static bool LooksLikeNumericSku(string sku)
            => Regex.IsMatch((sku ?? "").Trim(), @"^\d{4,}$");

        static bool TryParseTrailingCost(string line, out decimal cost, out string withoutCost)
        {
            cost = 0m;
            withoutCost = line;

            // last money-like token at end of line
            var m = Regex.Match(line, @"(?<val>\$?\(?-?\d[\d,]*\.?\d*\)?)\s*$");
            if (!m.Success) return false;

            var val = (m.Groups["val"].Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(val)) return false;

            var parsed = MoneyOrZero(val);
            if (parsed <= 0m) return false;

            cost = parsed;
            withoutCost = line.Substring(0, m.Index).TrimEnd();
            return true;
        }

        var headerIdx = norm.FindIndex(LooksLikeHeader);
        var start = headerIdx >= 0 ? headerIdx + 1 : 0;

        var results = new List<PurchaseInvoiceLine>();

        string? pendingSku = null;
        var pendingDesc = "";

        for (int i = start; i < norm.Count; i++)
        {
            var line = norm[i];

            if (IsStopLine(line))
            {
                if (results.Count > 0) break;
                continue;
            }

            if (LooksLikeHeader(line))
                continue;

            // multi-line continuation
            if (pendingSku is not null)
            {
                if (TryParseTrailingCost(line, out var cost2, out var noCost2))
                {
                    var extra = noCost2.Trim();
                    if (!string.IsNullOrWhiteSpace(extra))
                        pendingDesc = (pendingDesc + " " + extra).Trim();

                    if (!string.IsNullOrWhiteSpace(pendingDesc))
                    {
                        results.Add(new PurchaseInvoiceLine
                        {
                            ItemCode = pendingSku,
                            ProductName = pendingDesc,
                            Quantity = 1m,
                            UnitCost = cost2
                        });
                    }

                    pendingSku = null;
                    pendingDesc = "";
                    continue;
                }

                pendingDesc = (pendingDesc + " " + line).Trim();
                continue;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var sku = parts[0].Trim();
            if (!LooksLikeNumericSku(sku))
                continue;

            if (TryParseTrailingCost(line, out var cost, out var noCost))
            {
                var desc = noCost;
                if (desc.StartsWith(sku + " ", StringComparison.OrdinalIgnoreCase))
                    desc = desc.Substring(sku.Length).Trim();
                else
                    desc = parts[1].Trim();

                if (string.IsNullOrWhiteSpace(desc))
                    continue;

                results.Add(new PurchaseInvoiceLine
                {
                    ItemCode = sku,
                    ProductName = desc,
                    Quantity = 1m,
                    UnitCost = cost
                });
            }
            else
            {
                pendingSku = sku;
                pendingDesc = parts[1].Trim();
            }
        }

        return results;
    }

    private static InvoiceImportResult ImportFromPdf(string filePath, string? selectedVendor)
    {
        // Read PDF text by page (better for multi-invoice PDFs).
        var (rawByPage, linesByPage) = ExtractPdfByPage(filePath);
        // Vendor templates depend on visual rows. PdfPig's page.Text can concatenate
        // columns in content-stream order, so use position-reconstructed rows instead.
        var textLinesByPage = linesByPage;
        var layoutRawByPage = textLinesByPage.Select(page => string.Join("\n", page)).ToList();
        var raw = string.Join("\n\n", layoutRawByPage);
        var linesText = textLinesByPage.SelectMany(x => x).ToList();

        var warnings = new List<string>();

        // If the user selected a vendor in the Purchases screen, force that vendor's parser.
        // This avoids generic parsing mistakes when different vendors use different invoice layouts.
        var selectedKey = NormalizeVendorKey(selectedVendor);
        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            try
            {
                if (selectedKey == VendorKeyAmerican)
                {
                    var invoices = new List<ImportedInvoice>();
                    for (int i = 0; i < rawByPage.Count; i++)
                    {
                        var parsed = ParseAmericanDistributorsInvoice(layoutRawByPage[i], textLinesByPage[i], pageNumber: i + 1);
                        if (parsed.Lines.Count == 0 && parsed.Total is null && string.IsNullOrWhiteSpace(parsed.InvoiceNumber))
                            continue;
                        invoices.Add(parsed);
                    }
                    invoices = MergeAmericanDistributorsInvoices(invoices);

                    var first = invoices.FirstOrDefault() ?? new ImportedInvoice { VendorName = "American Distributors LLC" };
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = first.VendorName,
                        InvoiceNumber = first.InvoiceNumber,
                        InvoiceDate = first.InvoiceDate,
                        Total = first.Total,
                        Lines = first.Lines,
                        Invoices = invoices,
                        RawText = raw,
                        Warnings = invoices.Count > 1
                            ? warnings.Append($"Multiple invoices detected ({invoices.Count}). You can import them all at once.").ToList()
                            : warnings
                    };
                }

                if (selectedKey == VendorKeyAK)
                {
                    var inv = ParseAkWholesaleInvoiceFromPdf(filePath);
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = inv.VendorName,
                        InvoiceNumber = inv.InvoiceNumber,
                        InvoiceDate = inv.InvoiceDate,
                        Total = inv.Total,
                        Lines = inv.Lines,
                        Invoices = new List<ImportedInvoice> { inv },
                        RawText = raw,
                        Warnings = warnings
                    };
                }

                if (selectedKey == VendorKeySkygate)
                {
                    var inv = ParseSkygateWholesaleInvoice(raw, linesText);
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = inv.VendorName,
                        InvoiceNumber = inv.InvoiceNumber,
                        InvoiceDate = inv.InvoiceDate,
                        Total = inv.Total,
                        Lines = inv.Lines,
                        Invoices = new List<ImportedInvoice> { inv },
                        RawText = raw,
                        Warnings = warnings
                    };
                }

                if (selectedKey == VendorKey1Oak)
                {
                    var inv = Parse1OakWholesaleInvoice(raw, linesText);
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = inv.VendorName,
                        InvoiceNumber = inv.InvoiceNumber,
                        InvoiceDate = inv.InvoiceDate,
                        Total = inv.Total,
                        Lines = inv.Lines,
                        Invoices = new List<ImportedInvoice> { inv },
                        RawText = raw,
                        Warnings = warnings
                    };
                }

                if (selectedKey == VendorKeyDemandVape)
                {
                    var inv = ParseDemandVapeInvoice(raw, linesText);
                    return CreateSingleInvoiceResult(inv, raw, warnings);
                }

                if (selectedKey == VendorKeySafa)
                {
                    var inv = ParseSafaGoodsInvoice(raw, linesText);
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = inv.VendorName,
                        InvoiceNumber = inv.InvoiceNumber,
                        InvoiceDate = inv.InvoiceDate,
                        Total = inv.Total,
                        Lines = inv.Lines,
                        Invoices = new List<ImportedInvoice> { inv },
                        RawText = raw,
                        Warnings = warnings
                    };
                }

                if (selectedKey == VendorKeyTriState)
                {
                    var inv = ParseTriStateDistroInvoice(raw, linesText);
                    return new InvoiceImportResult
                    {
                        Success = true,
                        VendorName = inv.VendorName,
                        InvoiceNumber = inv.InvoiceNumber,
                        InvoiceDate = inv.InvoiceDate,
                        Total = inv.Total,
                        Lines = inv.Lines,
                        Invoices = new List<ImportedInvoice> { inv },
                        RawText = raw,
                        Warnings = warnings
                    };
                }

                if (selectedKey == VendorKeyHS)
                {
                    return new InvoiceImportResult
                    {
                        Success = false,
                        Warnings = { "HS WHOLESALE invoices appear to be scanned images (no selectable text). OCR support is not enabled yet. Please request HS OCR support or export a text-based PDF/Excel." },
                        RawText = raw
                    };
                }
            }
            catch (Exception ex)
            {
                return new InvoiceImportResult
                {
                    Success = false,
                    Warnings = { $"Vendor-specific parser failed: {ex.Message}" },
                    RawText = raw
                };
            }
        }

        // Vendor-specific parsing (handles the user's real invoices much more reliably than generic heuristics).
        if (IsAmericanDistributors(raw))
        {
            var invoices = new List<ImportedInvoice>();
            for (int i = 0; i < rawByPage.Count; i++)
            {
                // IMPORTANT: American Distributors PDFs preserve line breaks in page.Text much better than
                // word-position reconstruction. Use the page.Text split-lines for accurate table parsing.
                var parsed = ParseAmericanDistributorsInvoice(layoutRawByPage[i], textLinesByPage[i], pageNumber: i + 1);
                if (parsed.Lines.Count == 0 && parsed.Total is null && string.IsNullOrWhiteSpace(parsed.InvoiceNumber))
                    continue;
                invoices.Add(parsed);
            }
            invoices = MergeAmericanDistributorsInvoices(invoices);

            if (invoices.Count == 0)
                warnings.Add("American Distributors invoice detected, but line items could not be extracted.");

            // For backward compatibility, expose the first invoice in the legacy fields.
            var first = invoices.FirstOrDefault() ?? new ImportedInvoice { VendorName = "American Distributors LLC" };
            return new InvoiceImportResult
            {
                Success = true,
                VendorName = first.VendorName,
                InvoiceNumber = first.InvoiceNumber,
                InvoiceDate = first.InvoiceDate,
                Total = first.Total,
                Lines = first.Lines,
                Invoices = invoices,
                RawText = raw,
                Warnings = invoices.Count > 1
                    ? warnings.Append($"Multiple invoices detected ({invoices.Count}). You can import them all at once.").ToList()
                    : warnings
            };
        }

        if (IsDemandVape(raw))
        {
            var inv = ParseDemandVapeInvoice(raw, linesText);
            return CreateSingleInvoiceResult(inv, raw, warnings);
        }

        if (IsSkygate(raw))
        {
            var inv = ParseSkygateWholesaleInvoice(raw, linesText);
            return CreateSingleInvoiceResult(inv, raw, warnings);
        }

        if (Is1OakWholesale(raw))
        {
            var inv = Parse1OakWholesaleInvoice(raw, linesText);
            return CreateSingleInvoiceResult(inv, raw, warnings);
        }

        if (IsSafaGoods(raw))
        {
            var inv = ParseSafaGoodsInvoice(raw, linesText);
            return CreateSingleInvoiceResult(inv, raw, warnings);
        }

        string? vendor;
        string? invNo;
        DateOnly? invDate;
        decimal? total;
        List<PurchaseInvoiceLine> parsedLines;

        if (IsAkWholesale(raw))
        {
            // AK Wholesale invoices often require position-based extraction for reliable table parsing.
            var inv = ParseAkWholesaleInvoiceFromPdf(filePath);
            vendor = inv.VendorName;
            invNo = inv.InvoiceNumber;
            invDate = inv.InvoiceDate;
            total = inv.Total;
            parsedLines = inv.Lines;
        }
        else
        {
            vendor = GuessVendorName(raw);
            invNo = GuessInvoiceNumberEnhanced(raw) ?? GuessInvoiceNumber(raw);
            invDate = GuessInvoiceDate(raw);
            total = GuessTotalEnhanced(raw) ?? GuessTotal(raw);
            parsedLines = ParseLineItemsEnhanced(linesText);
            if (parsedLines.Count == 0)
            {
                // Fallback to original parser
                parsedLines = ParseLineItems(linesText);
                if (parsedLines.Count == 0)
                {
                    // Try raw text split as last resort
                    parsedLines = ParseLineItems(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
                }
            }
        }

        if (string.IsNullOrWhiteSpace(vendor)) warnings.Add("Could not confidently detect vendor name. You can select a Vendor manually.");
        if (string.IsNullOrWhiteSpace(invNo)) warnings.Add("Could not detect invoice number; a default will be generated on save.");
        if (invDate is null) warnings.Add("Could not detect invoice date; today will be used.");
        if (total is null) warnings.Add("Could not detect invoice total; it will be calculated from line items if available.");

        if (parsedLines.Count == 0)
            warnings.Add("Could not extract line items from this PDF. If this is a scanned invoice, export as Excel/CSV or use a text-based PDF.");

        if (total is null && parsedLines.Count > 0)
        {
            // compute best-effort total
            var computed = parsedLines.Sum(x => x.Quantity * x.UnitCost);
            total = computed > 0 ? computed : null;
        }

        return new InvoiceImportResult
        {
            Success = true,
            VendorName = vendor ?? "",
            InvoiceNumber = invNo ?? "",
            InvoiceDate = invDate,
            Total = total,
            Lines = parsedLines,
            Invoices = new List<ImportedInvoice>
            {
                new ImportedInvoice
                {
                    VendorName = vendor ?? "",
                    InvoiceNumber = invNo ?? "",
                    InvoiceDate = invDate,
                    Total = total,
                    Lines = parsedLines
                }
            },
            RawText = raw,
            Warnings = warnings
        };
    }

    private static InvoiceImportResult CreateSingleInvoiceResult(
        ImportedInvoice invoice,
        string raw,
        List<string> warnings)
    {
        if (invoice.Lines.Count == 0)
            warnings.Add($"{invoice.VendorName} was recognized, but no line items were extracted. Review the PDF before saving.");

        if (invoice.Total is null or <= 0m)
            warnings.Add($"{invoice.VendorName} was recognized, but the original invoice total could not be confirmed.");

        return new InvoiceImportResult
        {
            Success = true,
            VendorName = invoice.VendorName,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            Total = invoice.Total,
            Lines = invoice.Lines,
            Invoices = new List<ImportedInvoice> { invoice },
            RawText = raw,
            Warnings = warnings
        };
    }

    private static bool IsAkWholesale(string text)
    {
        var t = (text ?? "").ToUpperInvariant();
        return t.Contains("AK WHOLESALE")
               || Regex.IsMatch(t, @"\bAKWHOLESALE\.COM\b");
    }

    private static bool IsAmericanDistributors(string text)
    {
        var t = (text ?? "").ToUpperInvariant();

        if (t.Contains("AMERICAN DISTRIBUTORS") || t.Contains("AMERICANDISTRIBUTORSLLC.COM"))
            return true;

        // Many American Distributors invoices have the company name as a logo image,
        // so extracted text may not include it. Detect by stable template fields.
        if (t.Contains("TRANSACTION NO") && t.Contains("ACCOUNT NO") && t.Contains("INVOICE BALANCE"))
            return true;

        if (t.Contains("TOTAL EXCISE TAX COLLECTED") && t.Contains("ILLINOIS TP LICENSE"))
            return true;

        return false;
    }

    private static bool IsHsWholesale(string text)
    {
        var t = (text ?? "").ToUpperInvariant();
        return t.Contains("HS WHOLESALE") || t.Contains("HSWSUPPLY.COM") || t.Contains("HSWHOLESALE");
    }

    private static bool IsSafaGoods(string text)
    {
        var t = (text ?? "").ToUpperInvariant();
        return t.Contains("SAFA GOODS") || t.Contains("SAFAGOODS.COM") || t.Contains("SAFAGOODS");
    }

    private static bool IsSkygate(string text)
    {
        var t = (text ?? "").ToUpperInvariant();
        return t.Contains("SKYGATE WHOLESALE") || t.Contains("SKYGATEWHOLESALE.COM");
    }

    private static bool Is1OakWholesale(string text)
    {
        var t = (text ?? "").ToUpperInvariant();
        return t.Contains("1 OAK WHOLESALE")
               || t.Contains("1OAK WHOLESALE")
               || t.Contains("1OAKWHOLESALE");
    }

    private static bool IsDemandVape(string text)
    {
        var t = (text ?? "").ToUpperInvariant();
        return t.Contains("DEMANDVAPE.COM")
               || (t.Contains("INVOICE NUMBER") && t.Contains("CUSTOMER#") && t.Contains("PARTNER STATUS"));
    }

    private static string DetectVendorName(string text)
    {
        if (IsAkWholesale(text)) return "AK Wholesale Inc";
        if (IsAmericanDistributors(text)) return "American Distributors LLC";
        if (IsHsWholesale(text)) return "HS Wholesale";
        if (IsSafaGoods(text)) return "SAFA Goods";
        if (IsSkygate(text)) return "Skygate Wholesale";
        if (Is1OakWholesale(text)) return "1 Oak Wholesale";
        if (IsDemandVape(text)) return "DemandVape";
        return GuessVendorName(text) ?? "Unknown";
    }

    // ==========================
    // Universal PDF Line Item Extractor
    // ==========================
    
    /// <summary>
    /// Universal PDF line item extractor that works with most invoice formats.
    /// Uses PdfPig to extract word positions and build structured rows.
    /// Looks for: UPC/SKU (numeric codes), Description, and Price/Cost values.
    /// </summary>
    private static List<PurchaseInvoiceLine> ExtractInvoiceLineItemsUniversal(string filePath, string rawText)
    {
        var results = new List<PurchaseInvoiceLine>();
        
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(filePath);
            
            foreach (var page in doc.GetPages())
            {
                var pageResults = ExtractLineItemsFromPageUniversal(page);
                results.AddRange(pageResults);
            }
        }
        catch
        {
            // If PdfPig fails, fall back to text-based extraction
            results = ExtractLineItemsFromTextUniversal(rawText);
        }
        
        // If no results from position-based extraction, try text-based
        if (results.Count == 0)
        {
            results = ExtractLineItemsFromTextUniversal(rawText);
        }
        
        // De-duplicate by SKU/UPC
        return results
            .Where(x => !string.IsNullOrWhiteSpace(x.ItemCode) && !string.IsNullOrWhiteSpace(x.ProductName) && x.UnitCost > 0)
            .GroupBy(x => x.ItemCode)
            .Select(g => g.First())
            .ToList();
    }
    
    private static List<PurchaseInvoiceLine> ExtractLineItemsFromPageUniversal(UglyToad.PdfPig.Content.Page page)
    {
        var results = new List<PurchaseInvoiceLine>();
        
        var words = page.GetWords()
            .Select(w => new { Text = (w.Text ?? "").Trim(), X = w.BoundingBox.Left, Y = w.BoundingBox.Bottom })
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
        
        if (words.Count == 0) return results;
        
        // Group words into rows by Y coordinate (with tolerance)
        const double yTolerance = 5.0;
        var rows = new List<List<dynamic>>();
        
        foreach (var word in words.OrderByDescending(w => w.Y).ThenBy(w => w.X))
        {
            var existingRow = rows.FirstOrDefault(r => Math.Abs(r[0].Y - word.Y) <= yTolerance);
            if (existingRow != null)
                existingRow.Add(word);
            else
                rows.Add(new List<dynamic> { word });
        }
        
        // Sort words within each row by X coordinate
        foreach (var row in rows)
            row.Sort((a, b) => ((double)a.X).CompareTo((double)b.X));
        
        // Process each row
        foreach (var row in rows)
        {
            var rowText = string.Join(" ", row.Select(w => (string)w.Text));
            var result = ParseLineItemFromRowText(rowText);
            if (result != null)
                results.Add(result);
        }
        
        return results;
    }
    
    private static List<PurchaseInvoiceLine> ExtractLineItemsFromTextUniversal(string rawText)
    {
        var results = new List<PurchaseInvoiceLine>();
        if (string.IsNullOrWhiteSpace(rawText)) return results;
        
        var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            var result = ParseLineItemFromRowText(line);
            if (result != null)
                results.Add(result);
        }
        
        return results;
    }
    
    /// <summary>
    /// Universal line item parser that handles multiple invoice formats:
    /// - HS Wholesale: Qty | Code/SKU | Product Name | Price | Total
    /// - American Distributors: SKU | QTY | DESCRIPTION | UNIT PRICE | EXCISE TAX | EXTENDED PRICE
    /// - SAFA Goods: UPC | Product Name/Description | SO | IO | Out | Sold Price | Tax | Amount
    /// - Skygate: ITEM# | SKU | ORD | SHIP | UNIT | DESCRIPTION | TAX | PRICE | AMOUNT
    /// </summary>
    private static PurchaseInvoiceLine? ParseLineItemFromRowText(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        
        var upperLine = line.ToUpperInvariant();
        if (IsSkipLineUniversal(upperLine)) return null;
        
        // Pattern 1: Look for UPC (10-14 digits)
        var upcMatch = Regex.Match(line, @"\b(\d{10,14})\b");
        
        // Pattern 2: Alphanumeric SKU patterns
        // Examples: GVDSP0245, STAXNT80MGTBJR0, AC1230-90-MG, ES23137-B, WPQ178BW, P2B-G35S8WT
        Match? skuMatch = null;
        
        // Try multiple patterns for different SKU formats
        var skuPatterns = new[] {
            @"(?:^|\s)([A-Z][A-Z0-9\-]{3,}[0-9A-Z])\b",          // Letters then alphanum: GVDSP0245
            @"(?:^|\s)([A-Z]{2,}[0-9][A-Z0-9\-]*)\b",            // 2+ letters, number: AC1230
            @"(?:^|\s)(\d+[A-Z][A-Z0-9\-]+)\b",                  // Number then letters: 4761ABC
            @"(?:^|\s)([A-Z0-9]{2,}\-[A-Z0-9\-]+)\b",            // With dash: AC1230-90-MG
            @"(?:^|\s)([A-Z]{2}[A-Z0-9]{4,})\b"                  // Short prefix: HXCP042
        };
        
        foreach (var pattern in skuPatterns)
        {
            var m = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.Length >= 4)
            {
                var candidateSku = m.Groups[1].Value.Trim();
                if (!IsCommonWord(candidateSku) && !Regex.IsMatch(candidateSku, @"^\d+$"))
                {
                    skuMatch = m;
                    break;
                }
            }
        }
        
        // Also try for short numeric SKUs (3-6 digits followed by alpha)
        if (skuMatch == null || !skuMatch.Success)
        {
            var numericSkuMatch = Regex.Match(line, @"(?:^|\s)(\d{3,6})\s+[A-Za-z]");
            if (numericSkuMatch.Success)
                skuMatch = numericSkuMatch;
        }
        
        string? sku = null;
        int skuEndPos = 0;
        
        if (upcMatch.Success)
        {
            sku = upcMatch.Groups[1].Value;
            skuEndPos = upcMatch.Index + upcMatch.Length;
        }
        else if (skuMatch != null && skuMatch.Success && skuMatch.Groups[1].Value.Length >= 3)
        {
            var candidateSku = skuMatch.Groups[1].Value.Trim();
            if (!IsCommonWord(candidateSku))
            {
                sku = candidateSku;
                skuEndPos = skuMatch.Index + skuMatch.Length;
            }
        }
        
        if (string.IsNullOrWhiteSpace(sku)) return null;
        
        // Extract all money values from the line (with or without $)
        var moneyMatches = Regex.Matches(line, @"\$?([\d,]+\.\d{2})\b");
        
        // Also look for decimal numbers without $ that could be prices (e.g., "70.00")
        var decimalMatches = Regex.Matches(line, @"(?<!\d)([\d,]+\.\d{2})(?!\d)");
        
        decimal unitPrice = 0;
        decimal extendedPrice = 0;
        
        // Try to find prices - prefer money matches with $, then decimals
        var allPrices = new List<decimal>();
        foreach (Match m in moneyMatches)
        {
            if (decimal.TryParse(m.Groups[1].Value.Replace(",", ""), out var v) && v > 0 && v < 100000)
                allPrices.Add(v);
        }
        
        if (allPrices.Count == 0)
        {
            foreach (Match m in decimalMatches)
            {
                if (decimal.TryParse(m.Groups[1].Value.Replace(",", ""), out var v) && v > 0 && v < 100000)
                    allPrices.Add(v);
            }
        }
        
        if (allPrices.Count == 0) return null;
        
        // Determine unit price and extended price based on number of prices found
        if (allPrices.Count >= 2)
        {
            // Usually: ... | Unit Price | Extended Price
            // Or: ... | Tax | Unit Price | Extended Price
            // Take second-to-last as unit price, last as extended
            unitPrice = allPrices[allPrices.Count - 2];
            extendedPrice = allPrices[allPrices.Count - 1];
            
            // Sanity check: if "unit price" is larger than "extended", swap them or use the smaller one
            if (unitPrice > extendedPrice && extendedPrice > 0)
            {
                // Extended should be >= unit price, so something's wrong
                // Just use the smaller non-zero value as unit price
                unitPrice = Math.Min(unitPrice, extendedPrice);
            }
        }
        else
        {
            unitPrice = allPrices[0];
        }
        
        if (unitPrice <= 0) return null;
        
        // Extract description: everything between SKU and first price
        var description = ExtractDescriptionUniversal(line, sku, skuEndPos, moneyMatches.Count > 0 ? moneyMatches : decimalMatches);
        
        if (string.IsNullOrWhiteSpace(description) || description.Length < 2)
        {
            // Try to get description from after SKU
            var afterSku = line.Substring(Math.Min(skuEndPos, line.Length)).Trim();
            // Remove leading/trailing numbers
            afterSku = Regex.Replace(afterSku, @"^\d+\s+", "");
            afterSku = Regex.Replace(afterSku, @"[\$\d\.,]+.*$", "").Trim();
            if (afterSku.Length >= 3)
                description = afterSku;
        }
        
        if (string.IsNullOrWhiteSpace(description) || description.Length < 2) return null;
        
        // Calculate quantity
        var quantity = 1m;
        if (unitPrice > 0 && extendedPrice > 0 && extendedPrice >= unitPrice)
        {
            quantity = Math.Round(extendedPrice / unitPrice);
            if (quantity < 1) quantity = 1;
        }
        
        // Also try to extract quantity from the line (common pattern: number at start or after SKU)
        var qtyMatch = Regex.Match(line, @"^\s*(\d+)\s+[A-Z]", RegexOptions.IgnoreCase);
        if (!qtyMatch.Success)
            qtyMatch = Regex.Match(line, $@"{Regex.Escape(sku)}\s+(\d+)\s+");
        if (qtyMatch.Success && int.TryParse(qtyMatch.Groups[1].Value, out var parsedQty) && parsedQty >= 1 && parsedQty <= 9999)
        {
            quantity = parsedQty;
        }
        
        return new PurchaseInvoiceLine
        {
            ItemCode = sku,
            ProductName = description,
            Quantity = quantity,
            UnitCost = unitPrice,
            Price = unitPrice,
            Amount = extendedPrice > 0 ? extendedPrice : (decimal?)null
        };
    }
    
    private static bool IsSkipLineUniversal(string upperText)
    {
        // Skip header rows, footer rows, summary rows
        var skipPatterns = new[] {
            "SUBTOTAL", "SUB-TOTAL", "TOTAL AMOUNT", "TOTAL QTY", "GRAND TOTAL", "BALANCE",
            "AMOUNT DUE", "DUE AMOUNT", "INVOICE BALANCE",
            "TAXABLE", "IL TAX", "CT TAX", "TAX COLLECTED", "EXCISE TAX", "VAPE (VAPOR)",
            "YOU SAVED", "DISCOUNT", "PROMO", "SHIPPING", "S & H",
            "TRACKING", "SHIP TO:", "BILL TO:", "SOLD TO:", "INVOICE NO", "ORDER DATE",
            "SHIP DATE", "ORDER #", "SALES ORDER", "TRANSACTION NO",
            "PAGE ", "1 OF", "2 OF", "3 OF",
            "UPC\t", "DESCRIPTION\t", "QTY\t", "PRODUCT NAME\t", "PRICE\t", "AMOUNT\t",
            "ORD SHIP UNIT", "ITEM# ORD",
            "PAYMENT", "CASH", "CHECK", "CREDIT CARD", "CREDIT APPLIED", "WIRE-IN",
            "LBS", "HAZMAT", "TOTAL CASES",
            "THANK YOU", "QUESTIONS?", "CUSTOMER SERVICE", "CUSTOMER BALANCE",
            "APPRECIATE YOUR BUSINESS", "TERMS AND CONDITIONS",
            "PICK YOUR FLAVOR", "COMMENTS"
        };
        
        return skipPatterns.Any(p => upperText.Contains(p));
    }
    
    private static bool IsCommonWord(string text)
    {
        // Only filter out very common non-SKU words
        var commonWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "THE", "AND", "FOR", "WITH", "FROM", "QTY", "UNIT", "PRICE", "TOTAL", "TAX",
            "AMOUNT", "DATE", "ORDER", "SHIP", "INVOICE", "SOLD", "BILL", "PAGE", "ITEM",
            "DESCRIPTION", "PRODUCT", "NAME", "SUBTOTAL", "BALANCE", "DUE", "CREDIT",
            "STATUS", "REP", "LICENSE", "PICK", "FREE", "CODE"
        };
        return commonWords.Contains(text);
    }
    
    private static string ExtractDescriptionUniversal(string line, string sku, int skuEndPos, MatchCollection priceMatches)
    {
        if (priceMatches.Count == 0) return "";
        
        // Find first price position
        var firstPricePos = priceMatches[0].Index;
        
        // Description is between SKU end and first price
        var startPos = skuEndPos;
        var endPos = firstPricePos;
        
        if (endPos <= startPos || startPos < 0 || startPos >= line.Length) 
        {
            // Try to find description after SKU
            var skuPos = line.IndexOf(sku, StringComparison.OrdinalIgnoreCase);
            if (skuPos >= 0)
            {
                startPos = skuPos + sku.Length;
                endPos = firstPricePos;
            }
        }
        
        if (endPos <= startPos) return "";
        
        var description = line.Substring(startPos, Math.Min(endPos - startPos, line.Length - startPos)).Trim();
        
        // Clean up the description
        // Remove leading numbers (line numbers, quantities like "1 1 EA" or "2 2 0")
        description = Regex.Replace(description, @"^\s*\d+\s+\d+\s+(0\s+)?", "");
        description = Regex.Replace(description, @"^\s*\d+\s+", "");
        // Remove leading unit types
        description = Regex.Replace(description, @"^(BOX|EA|PK|CT|EACH|CASE|BOT|JAR|2|12|14|24)\s+", "", RegexOptions.IgnoreCase);
        // Remove trailing numbers (volume, tax columns, quantities)
        description = Regex.Replace(description, @"(\s+\d+\.?\d*)+\s*$", "");
        // Remove trailing asterisks (tax indicator)
        description = Regex.Replace(description, @"\s*\*+\s*$", "");
        // Remove trailing $0.00 patterns
        description = Regex.Replace(description, @"\s*\$0\.00\s*$", "");
        // Remove leading special chars
        description = Regex.Replace(description, @"^[\-\s]+", "");
        
        return description.Trim();
    }

    // ==========================
    // Vendor-specific parsers
    // ==========================

    private static ImportedInvoice ParseAkWholesaleInvoiceFromPdf(string filePath)
    {
        var (rawByPage, linesByPage) = ExtractPdfByPage(filePath);
        var raw = string.Join("\n\n", linesByPage.Select(page => string.Join("\n", page)));
        var allLines = linesByPage.SelectMany(x => x).ToList();

        // Vendor + header fields
        var vendor = "AK Wholesale Inc";

        // Invoice number and order date in AK PDFs are frequently on the *next line* below the labels.
        // Example:
        //   "DATE  Invoice No"  -> next line: "01/13/26  S287801"
        //   "ORDER DATE ..."     -> next line: "1/13/2026"
        static string NextNonEmptyLine(List<string> lines, int startIdx)
        {
            for (var j = startIdx + 1; j < lines.Count && j < startIdx + 6; j++)
            {
                var s = (lines[j] ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return string.Empty;
        }

        static string ExtractFirstToken(string s)
        {
            var parts = (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length > 0 ? parts[0] : string.Empty;
        }

        static string ExtractSecondToken(string s)
        {
            var parts = (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length > 1 ? parts[1] : string.Empty;
        }

        static bool LooksLikeInvoiceNo(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return Regex.IsMatch(s, @"^[A-Z0-9\-]{4,}$", RegexOptions.IgnoreCase);
        }

        // AK invoice numbers use the stable S###### format. Searching that token avoids
        // mistaking the address line beneath the "Invoice No" heading for the number.
        var stableNumber = Regex.Match(
            string.Join("\n", allLines.Take(20)),
            @"\b(S\d{5,})\b",
            RegexOptions.IgnoreCase);
        var invNo = stableNumber.Success ? stableNumber.Groups[1].Value.Trim() : "";
        if (string.IsNullOrWhiteSpace(invNo))
        {
            // Fallback: locate label row in reconstructed lines then read value row.
            for (var i = 0; i < Math.Min(allLines.Count, 120); i++)
            {
                var l = allLines[i] ?? "";
                var up = l.ToUpperInvariant();
                if (!up.Contains("INVOICE") || !up.Contains("NO")) continue;

                // If invoice number is on the same line, use it.
                var same = Regex.Match(l, @"INVOICE\s*NO\.?\s*[:#\-]?\s*([A-Z0-9\-]{4,})", RegexOptions.IgnoreCase);
                if (same.Success)
                {
                    invNo = same.Groups[1].Value.Trim();
                    break;
                }

                var next = NextNonEmptyLine(allLines, i);
                // Often the next line is "<date> <invoiceNo>".
                var tok2 = ExtractSecondToken(next);
                if (LooksLikeInvoiceNo(tok2)) { invNo = tok2; break; }

                var tok1 = ExtractFirstToken(next);
                if (LooksLikeInvoiceNo(tok1)) { invNo = tok1; break; }
            }
        }

        // Order date (prefer ORDER DATE; fallback to DATE label row)
        DateOnly? invDate = null;
        for (var i = 0; i < Math.Min(allLines.Count, 160); i++)
        {
            var l = (allLines[i] ?? "").Trim();
            var up = l.ToUpperInvariant();

            // Prefer explicit ORDER DATE label.
            if (up.Contains("ORDER") && up.Contains("DATE"))
            {
                var md = Regex.Match(l, @"([0-9]{1,2}[\/\-][0-9]{1,2}[\/\-][0-9]{2,4})");
                if (md.Success && TryParseDate(md.Groups[1].Value, out var d0)) { invDate = d0; break; }

                var next = NextNonEmptyLine(allLines, i);
                var md2 = Regex.Match(next, @"([0-9]{1,2}[\/\-][0-9]{1,2}[\/\-][0-9]{2,4})");
                if (md2.Success && TryParseDate(md2.Groups[1].Value, out var d1)) { invDate = d1; break; }
            }

            // Some templates have a "DATE  Invoice No" label row.
            if (invDate is null && up.Contains("DATE") && up.Contains("INVOICE") && !(up.Contains("SHIP") && up.Contains("DATE")))
            {
                var next = NextNonEmptyLine(allLines, i);
                var tok = ExtractFirstToken(next);
                if (TryParseDate(tok, out var d2)) { invDate = d2; break; }
            }
        }
        invDate ??= GuessInvoiceDate(raw);

        // Balance is the final amount in AK invoices (label "Balance").
        var total = FindLabeledMoney(allLines,
            "balance", "balance due", "amount due", "grand total", "total");
        total ??= GuessTotal(raw);

        // Extract line items using position-based parsing (most reliable for AK template)
        var parsedLines = new List<PurchaseInvoiceLine>();
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(filePath);
            foreach (var page in doc.GetPages())
                parsedLines.AddRange(ParseAkWholesaleLineItemsFromPage(page));
        }
        catch
        {
            // ignore; fallback below
        }

        var layoutLines = ParseAkWholesaleLineItemsFromLayout(allLines);
        if (layoutLines.Count > parsedLines.Count)
            parsedLines = layoutLines;

        // Fallback to text-based parser if needed (AK invoices often wrap each row across multiple lines)
        if (parsedLines.Count == 0)
        {
            var textLines = rawByPage
                .SelectMany(p => (p ?? "").Split('\n'))
                .Select(l => (l ?? "").Trim())
                .ToList();

            // New AK multiline parser (pdftotext-like output)
            parsedLines = ParseAkWholesaleLineItemsFromTextLines(textLines);

            // Then try a flat-table regex parser against reconstructed lines.
            if (parsedLines.Count == 0)
                parsedLines = ParseAkWholesaleLineItemsFlat(allLines);

            // Older multi-line text parsers
            if (parsedLines.Count == 0)
            {
                parsedLines = ParseAkWholesaleLineItems(allLines);
                if (parsedLines.Count == 0)
                {
                    parsedLines = ParseAkWholesaleLineItems(
                        raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
                }
            }
        }

        return new ImportedInvoice
        {
            VendorName = vendor,
            InvoiceNumber = invNo,
            InvoiceDate = invDate,
            Total = total,
            Lines = parsedLines
        };
    }

    private sealed record AkWord(string Text, double X, double Y);

    private static List<PurchaseInvoiceLine> ParseAkWholesaleLineItemsFromLayout(List<string> lines)
    {
        var items = new List<PurchaseInvoiceLine>();
        var rowPattern = new Regex(
            @"^\s*\d+\s+" +
            @"(?<upc>\d{8,14})\s+" +
            @"(?<ordered>\d+(?:\.\d+)?)\s+" +
            @"(?<shipped>\d+(?:\.\d+)?)\s+" +
            @"(?<unit>[A-Z]+)\s+" +
            @"(?<description>.+?)\s+" +
            @"(?<volume>\d[\d,]*\.\d{2})\s+" +
            @"(?<totalVolume>\d[\d,]*\.\d{2})\s+" +
            @"(?<tax>\*|\$?\d[\d,]*\.\d{2})\s+" +
            @"\$?(?<price>\d[\d,]*\.\d{2})\s+" +
            @"\$?(?<amount>\d[\d,]*\.\d{2})\s*$",
            RegexOptions.IgnoreCase);

        PurchaseInvoiceLine? current = null;
        foreach (var rawLine in lines)
        {
            var line = Regex.Replace((rawLine ?? "").Trim(), @"\s+", " ");
            var upper = line.ToUpperInvariant();

            if (upper.Contains("SUB-TOTAL")
                || upper.Contains("SUBTOTAL")
                || upper.StartsWith("BALANCE")
                || upper.Contains("TAXABLE QTY"))
                break;

            var match = rowPattern.Match(line);
            if (match.Success)
            {
                _ = TryParseDecimal(match.Groups["ordered"].Value, out var ordered);
                _ = TryParseDecimal(match.Groups["shipped"].Value, out var shipped);
                var price = MoneyOrNull(match.Groups["price"].Value);
                var amount = MoneyOrNull(match.Groups["amount"].Value);
                var tax = match.Groups["tax"].Value == "*" ? null : MoneyOrNull(match.Groups["tax"].Value);
                var quantity = shipped > 0m ? shipped : ordered;
                _ = decimal.TryParse(
                    match.Groups["volume"].Value.Replace(",", ""),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var volume);

                current = new PurchaseInvoiceLine
                {
                    ItemCode = match.Groups["upc"].Value.Trim(),
                    ProductName = match.Groups["description"].Value.Trim(),
                    OrdQuantity = ordered,
                    ShipQuantity = shipped,
                    Quantity = quantity,
                    VolumeMl = volume > 0m && volume <= int.MaxValue ? (int)Math.Round(volume) : null,
                    Tax = tax,
                    Price = price,
                    Amount = amount,
                    UnitCost = ComputeUnitCost(price, amount, quantity)
                };
                items.Add(current);
                continue;
            }

            if (current is not null
                && !string.IsNullOrWhiteSpace(line)
                && !upper.Contains("PAGE ")
                && !upper.Contains("ORDER DATE")
                && !upper.Contains("UPC ORD SHIP")
                && !Regex.IsMatch(line, @"^\d+\.\d{2}\s+LBS"))
            {
                current.ProductName = $"{current.ProductName} {line}".Trim();
            }
        }

        return items;
    }

    private static List<PurchaseInvoiceLine> ParseAkWholesaleLineItemsFromPage(UglyToad.PdfPig.Content.Page page)
    {
        var result = new List<PurchaseInvoiceLine>();

        var words = page.GetWords()
            .Select(w => new AkWord((w.Text ?? "").Trim(), w.BoundingBox.Left, w.BoundingBox.Bottom))
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();

        if (words.Count == 0) return result;

        // Group words into rows by Y.
        // AK's table headers and cells can sit on slightly different baselines
        // depending on PDF generator/font (and Windows text rendering), so we
        // intentionally use a more generous tolerance to avoid splitting what
        // should be a single row into multiple rows.
        const double yTol = 4.75;
        var rows = new List<List<AkWord>>();
        foreach (var w in words.OrderByDescending(w => w.Y).ThenBy(w => w.X))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].Y - w.Y) <= yTol);
            if (row is null)
                rows.Add(new List<AkWord> { w });
            else
                row.Add(w);
        }
        // Find header region (UPC / ORD / SHIP / DESCRIPTION / VOL(ML) / TAX / PRICE / AMOUNT)
        // AK invoices often split these labels across multiple baselines/rows in PdfPig word extraction.
        // Scan a sliding window of rows and union words until we see the required header tokens.
        int headerIdx = -1;
        int startDataIdx = -1;
        List<AkWord>? headerRowWords = null;
        const int headerWindow = 6;

        for (int i = 0; i < rows.Count; i++)
        {
            var windowWords = new List<AkWord>();
            var sb = new StringBuilder();

            for (int j = i; j < rows.Count && j < i + headerWindow; j++)
            {
                windowWords.AddRange(rows[j]);
                sb.Append(' ');
                sb.Append(string.Join(" ", rows[j].Select(t => t.Text.ToUpperInvariant())));
            }

            var joined = sb.ToString();
            if (joined.Contains("UPC") && joined.Contains("DESCRIPTION") && joined.Contains("AMOUNT") && joined.Contains("ORD") && joined.Contains("SHIP"))
            {
                headerIdx = i;
                headerRowWords = windowWords;
                startDataIdx = Math.Min(rows.Count, i + headerWindow);
                break;
            }
        }

        if (headerIdx < 0 || headerRowWords is null) return result;

        // Determine x anchors from header labels
        double xUPC = FindHeaderX(headerRowWords, "UPC");
        double xORD = FindHeaderX(headerRowWords, "ORD");
        double xSHIP = FindHeaderX(headerRowWords, "SHIP");
        double xDESC = FindHeaderX(headerRowWords, "DESCRIPTION");
        double xVOL = FindHeaderX(headerRowWords, "VOL"); // VOL(ML)
        double xTAX = FindHeaderX(headerRowWords, "TAX");
        double xPRICE = FindHeaderX(headerRowWords, "PRICE");
        double xAMT = FindHeaderX(headerRowWords, "AMOUNT");

        var anchors = new List<(string key, double x)>
        {
            ("UPC", xUPC),
            ("ORD", xORD),
            ("SHIP", xSHIP),
            ("DESC", xDESC),
            ("VOL", xVOL),
            ("TAX", xTAX),
            ("PRICE", xPRICE),
            ("AMOUNT", xAMT)
        }
        .Where(a => a.x > 0)
        .OrderBy(a => a.x)
        .ToList();

        if (anchors.Count < 5) return result;

        // Build column boundaries (midpoints between anchors)
        var bounds = new List<(string key, double start, double end)>();
        for (int i = 0; i < anchors.Count; i++)
        {
            var start = i == 0 ? double.NegativeInfinity : (anchors[i - 1].x + anchors[i].x) / 2.0;
            var end = i == anchors.Count - 1 ? double.PositiveInfinity : (anchors[i].x + anchors[i + 1].x) / 2.0;
            bounds.Add((anchors[i].key, start, end));
        }

        PurchaseInvoiceLine? current = null;

        for (int i = Math.Max(startDataIdx, 0); i < rows.Count; i++)
        {
            var rowWords = rows[i].OrderBy(w => w.X).ToList();
            var rowTextUp = string.Join(" ", rowWords.Select(w => w.Text)).ToUpperInvariant();
            if (rowTextUp.StartsWith("SUB-TOTAL") || rowTextUp.StartsWith("SUBTOTAL") || rowTextUp.StartsWith("TOTAL") || rowTextUp.Contains("BALANCE"))
                break;

            var buckets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in bounds) buckets[b.key] = new List<string>();

            foreach (var w in rowWords)
            {
                var b = bounds.FirstOrDefault(bb => w.X >= bb.start && w.X < bb.end);
                if (string.IsNullOrWhiteSpace(b.key)) continue;
                buckets[b.key].Add(w.Text);
            }

            string upcRaw = string.Join(" ", buckets["UPC"]).Trim();
            string desc = string.Join(" ", buckets["DESC"]).Trim();
            string ordRaw = string.Join(" ", buckets["ORD"]).Trim();
            string shipRaw = string.Join(" ", buckets["SHIP"]).Trim();
            string volRaw = buckets.ContainsKey("VOL") ? string.Join(" ", buckets["VOL"]).Trim() : "";
            string taxRaw = buckets.ContainsKey("TAX") ? string.Join(" ", buckets["TAX"]).Trim() : "";
            string priceRaw = buckets.ContainsKey("PRICE") ? string.Join(" ", buckets["PRICE"]).Trim() : "";
            string amtRaw = buckets.ContainsKey("AMOUNT") ? string.Join(" ", buckets["AMOUNT"]).Trim() : "";

            // Continuation description line (wrapped text): sometimes the continuation text
            // ends up in the first bucket (UPC) depending on how the PDF was authored.
            var hasKeyFields = !string.IsNullOrWhiteSpace(ordRaw) || !string.IsNullOrWhiteSpace(shipRaw) || !string.IsNullOrWhiteSpace(priceRaw) || !string.IsNullOrWhiteSpace(amtRaw);
            var possibleContinuationText = !string.IsNullOrWhiteSpace(desc) ? desc : upcRaw;
            if (!hasKeyFields && current is not null && !string.IsNullOrWhiteSpace(possibleContinuationText))
            {
                current.ProductName = (current.ProductName + " " + possibleContinuationText).Trim();
                continue;
            }

            // Clean UPC (often includes line number as the first token)
            var upcTokens = upcRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            string itemCode = "";
            if (upcTokens.Count >= 2 && Regex.IsMatch(upcTokens[0], @"^\d{1,3}$") && Regex.IsMatch(upcTokens[1], @"^[0-9]{8,14}$"))
                itemCode = upcTokens[1];
            else if (upcTokens.Count >= 1 && Regex.IsMatch(upcTokens[0], @"^[0-9]{8,14}$"))
                itemCode = upcTokens[0];
            else if (upcTokens.Count >= 2)
                itemCode = upcTokens.Last();

            if (!TryParseDecimal(ordRaw, out var ordQty)) ordQty = 0m;
            if (!TryParseDecimal(shipRaw, out var shipQty)) shipQty = 0m;

            int? volMl = null;
            if (!string.IsNullOrWhiteSpace(volRaw))
            {
                var mv = Regex.Match(volRaw, @"(\d{1,5})");
                if (mv.Success && int.TryParse(mv.Groups[1].Value, out var vv))
                {
                    // Treat 0 / 0.00 as "no volume" (common for non-liquid items)
                    volMl = vv == 0 ? null : vv;
                }
            }

            decimal? tax = null;
            if (!string.IsNullOrWhiteSpace(taxRaw) && taxRaw != "*")
            {
                if (TryParseDecimal(taxRaw, out var taxVal)) tax = taxVal;
            }

            decimal? price = null;
            if (!string.IsNullOrWhiteSpace(priceRaw))
            {
                var p = MoneyOrNull(priceRaw);
                if (p.HasValue) price = p.Value;
            }

            decimal? amount = null;
            if (!string.IsNullOrWhiteSpace(amtRaw))
            {
                var a = MoneyOrNull(amtRaw);
                if (a.HasValue) amount = a.Value;
            }

            // If description is empty but we have numbers, skip
            if (string.IsNullOrWhiteSpace(desc) || shipQty <= 0m)
            {
                current = null;
                continue;
            }

            var unitCost = price ?? (amount.HasValue && shipQty > 0m ? amount.Value / shipQty : 0m);

            current = new PurchaseInvoiceLine
            {
                ItemCode = itemCode,
                ProductName = desc,
                OrdQuantity = ordQty,
                ShipQuantity = shipQty,
                VolumeMl = volMl,
                Tax = tax,
                Price = price,
                Amount = amount,
                Quantity = shipQty,
                UnitCost = unitCost
            };
            result.Add(current);
        }

        return result;
    }


    private static List<PurchaseInvoiceLine> ParseAkWholesaleLineItemsFromTextLines(List<string> rawLines)
    {
        // Text-line parser for AK invoices.
        // Works with PdfPig's page.Text output, which often looks similar to pdftotext output:
        //   ORD UPC
        //   SHIP
        //   SHIP UNIT
        //   DESCRIPTION (may wrap)
        //   VOL(ML)
        //   T.VOL(ML)
        //   TAX or *
        //   PRICE
        //   AMOUNT
        var lines = (rawLines ?? new List<string>())
            .Select(l => (l ?? "").Trim())
            .ToList();

        var result = new List<PurchaseInvoiceLine>();

        bool IsStop(string s)
        {
            var up = (s ?? "").ToUpperInvariant();
            return up.StartsWith("SUB-TOTAL") || up.StartsWith("SUBTOTAL") || up.StartsWith("TOTAL") || up.Contains("BALANCE") || up.StartsWith("PAGE ");
        }

        bool IsMoney(string s) => MoneyOrNull(s).HasValue;
        bool IsVol(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            // AK uses 0.00, 95.00, 190.00 etc
            if (!Regex.IsMatch(s, @"^\d{1,5}(?:\.\d{2})$") ) return false;
            if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return false;
            return v >= 0m && v <= 5000m;
        }

        var startRe = new Regex(@"^(?<ord>\d{1,3})\s+(?<upc>(?:\d{8,14}|[A-Z0-9\-]{4,}))$", RegexOptions.Compiled);
        var shipUnitRe = new Regex(@"^(?<ship>\d+(?:\.\d+)?)\s+(?<unit>[A-Z]{1,5})$", RegexOptions.Compiled);

        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            if (IsStop(line)) { i++; continue; }

            var m = startRe.Match(line.ToUpperInvariant());
            if (!m.Success)
            {
                i++;
                continue;
            }

            var ordRaw = m.Groups["ord"].Value.Trim();
            var upc = m.Groups["upc"].Value.Trim();

            decimal ordQty = 0m;
            TryParseDecimal(ordRaw, out ordQty);

            // Move to next meaningful lines for ship/unit and description
            i++;
            // ship qty sometimes appears alone on its own line
            decimal shipQty = 0m;
            string unit = "";

            // Read next non-empty line as possible ship qty
            int j = i;
            while (j < lines.Count && string.IsNullOrWhiteSpace(lines[j])) j++;
            if (j >= lines.Count) break;

            // If it's a pure number, treat as ship qty and advance
            if (Regex.IsMatch(lines[j], @"^\d+(?:\.\d+)?$") && !lines[j].Contains('.'))
            {
                TryParseDecimal(lines[j], out shipQty);
                j++;
                while (j < lines.Count && string.IsNullOrWhiteSpace(lines[j])) j++;
            }

            if (j >= lines.Count) break;

            // Next line usually contains "<ship> <unit>" (e.g., "2 EA", "1 BOX")
            var su = shipUnitRe.Match(lines[j].ToUpperInvariant());
            if (su.Success)
            {
                TryParseDecimal(su.Groups["ship"].Value, out shipQty);
                unit = su.Groups["unit"].Value.Trim();
                j++;
            }

            // Description: collect lines until we hit first VOL(ML) value
            var descParts = new List<string>();
            while (j < lines.Count)
            {
                var s = lines[j].Trim();
                if (string.IsNullOrWhiteSpace(s)) { j++; continue; }
                if (IsStop(s)) break;
                if (IsVol(s)) break;
                // Some PDFs repeat UPC or show next item start; stop if we see another item start
                if (startRe.IsMatch(s.ToUpperInvariant())) break;

                descParts.Add(s);
                j++;
            }
            var desc = Regex.Replace(string.Join(" ", descParts), @"\s+", " ").Trim();

            int? volMl = null;
            decimal? tax = null;
            decimal? price = null;
            decimal? amount = null;

            // VOL + TVOL
            if (j < lines.Count && IsVol(lines[j].Trim()))
            {
                var volStr = lines[j].Trim();
                if (decimal.TryParse(volStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                {
                    var vi = (int)Math.Round(v);
                    volMl = vi == 0 ? null : vi;
                }
                j++;

                // T.VOL line (optional)
                while (j < lines.Count && string.IsNullOrWhiteSpace(lines[j])) j++;
                if (j < lines.Count && IsVol(lines[j].Trim()))
                {
                    j++;
                }
            }

            // TAX (either * or $xx.xx)
            while (j < lines.Count && string.IsNullOrWhiteSpace(lines[j])) j++;
            if (j < lines.Count)
            {
                var t = lines[j].Trim();
                if (t == "*")
                {
                    tax = null;
                    j++;
                }
                else if (IsMoney(t))
                {
                    tax = MoneyOrNull(t);
                    j++;
                }
            }

            // PRICE
            while (j < lines.Count && string.IsNullOrWhiteSpace(lines[j])) j++;
            if (j < lines.Count && IsMoney(lines[j].Trim()))
            {
                price = MoneyOrNull(lines[j].Trim());
                j++;
            }

            // AMOUNT
            while (j < lines.Count && string.IsNullOrWhiteSpace(lines[j])) j++;
            if (j < lines.Count && IsMoney(lines[j].Trim()))
            {
                amount = MoneyOrNull(lines[j].Trim());
                j++;
            }

            // Validate
            if (!string.IsNullOrWhiteSpace(desc) && shipQty >= 0m)
            {
                // Some AK PDFs omit the line-item AMOUNT (or it may fall on the next page). If missing, compute Amount = Price * Ship.
                var fixedAmount = amount;
                if ((!fixedAmount.HasValue || fixedAmount.Value == 0m) && price.HasValue && shipQty > 0m)
                    fixedAmount = price.Value * shipQty;

                var unitCost = price ?? (fixedAmount.HasValue && shipQty > 0m ? fixedAmount.Value / shipQty : 0m);

                result.Add(new PurchaseInvoiceLine
                {
                    ItemCode = upc,
                    ProductName = desc,
                    OrdQuantity = ordQty,
                    ShipQuantity = shipQty,
                    VolumeMl = volMl,
                    Tax = tax,
                    Price = price,
                    Amount = fixedAmount,
                    Quantity = shipQty,
                    UnitCost = unitCost
                });
            }

            // Continue scanning from j (not i) to avoid missing the next item
            i = j;
        }

        // Filter obvious garbage rows
        result = result
            .Where(r => !string.IsNullOrWhiteSpace(r.ProductName))
            .Where(r => !string.IsNullOrWhiteSpace(r.ItemCode))
            .ToList();

        return result;
    }

    /// <summary>
    /// Fallback AK parser for cases where word-position bucketing fails.
    /// Many AK invoices render each line item as a single text line containing all columns.
    /// This parser uses a regex to capture the important columns.
    /// </summary>
    private static List<PurchaseInvoiceLine> ParseAkWholesaleLineItemsFlat(List<string> lines)
    {
        var parsed = new List<PurchaseInvoiceLine>();
        if (lines is null || lines.Count == 0) return parsed;

        // Normalize spacing but keep ordering.
        var norm = lines
            .Select(l => Regex.Replace((l ?? "").Trim(), @"\s+", " "))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        static bool IsTotals(string up) =>
            up.StartsWith("SUB-TOTAL") || up.StartsWith("SUBTOTAL") || up.StartsWith("TOTAL") ||
            up.Contains("BALANCE") || up.Contains("AMOUNT DUE") || up.Contains("GRAND TOTAL") ||
            up.Contains("TAXABLE QTY") || up.Contains("TOTAL CASES") || up.Contains("TRACKING");

        bool inItems = false;
        PurchaseInvoiceLine? current = null;

        // Example header contains: UPC ORD SHIP UNIT DESCRIPTION VOL(ML) ... TAX PRICE AMOUNT
        // Example row contains: 1 015568020650 2 2 EA ... 0.00 0.00 * $8.10 $16.20
        var rx = new Regex(
            @"^(?<lineno>\d{1,4})\s+" +
            @"(?<upc>[A-Za-z0-9]{3,14}|\d{8,14})\s+" +
            @"(?<ord>\d+(?:\.\d+)?)\s+" +
            @"(?<ship>\d+(?:\.\d+)?)\s+" +
            @"(?<unit>[A-Za-z]{1,6})\s+" +
            @"(?<desc>.+?)\s+" +
            @"(?<vol>\d+(?:\.\d+)?)\s+" +
            @"(?<tvol>\d+(?:\.\d+)?)\s+" +
            @"(?<tax>\*|\$?\d[\d,]*\.?\d{0,2})\s+" +
            @"(?<price>\$?\d[\d,]*\.\d{2})\s+" +
            @"(?<amount>\$?\d[\d,]*\.\d{2})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        for (int i = 0; i < norm.Count; i++)
        {
            var line = norm[i];
            var up = line.ToUpperInvariant();

            if (!inItems)
            {
                if (up.Contains("UPC") && up.Contains("DESCRIPTION") && (up.Contains("ORD") || up.Contains("SHIP")))
                    inItems = true;
                continue;
            }

            if (IsTotals(up)) break;

            var m = rx.Match(line);
            if (m.Success)
            {
                // finalize previous
                if (current is not null)
                    parsed.Add(current);

                var upc = m.Groups["upc"].Value.Trim();
                var desc = m.Groups["desc"].Value.Trim();

                var ordQty = MoneyOrZero(m.Groups["ord"].Value);
                var shipQty = MoneyOrZero(m.Groups["ship"].Value);
                var qty = shipQty > 0 ? shipQty : ordQty;

                int? volMl = null;
                var volRaw = m.Groups["vol"].Value;
                if (TryParseDecimal(volRaw, out var volDec))
                {
                    var v = (int)Math.Round((double)volDec);
                    volMl = v == 0 ? null : v;
                }

                decimal? tax = null;
                var taxRaw = m.Groups["tax"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(taxRaw) && taxRaw != "*")
                {
                    var tv = MoneyOrNull(taxRaw);
                    if (tv.HasValue) tax = tv.Value;
                }

                var price = MoneyOrNull(m.Groups["price"].Value);
                var amount = MoneyOrNull(m.Groups["amount"].Value);
                var unitCost = (qty > 0 && amount.GetValueOrDefault() > 0) ? amount.GetValueOrDefault() / qty : price.GetValueOrDefault();

                current = new PurchaseInvoiceLine
                {
                    ItemCode = upc,
                    ProductName = desc,
                    OrdQuantity = Decimal.Round(ordQty, 2),
                    ShipQuantity = Decimal.Round(shipQty, 2),
                    VolumeMl = volMl,
                    Tax = tax,
                    Price = price,
                    Amount = amount,
                    Quantity = Decimal.Round(qty, 2),
                    UnitCost = Decimal.Round(unitCost, 4)
                };
                continue;
            }

            // Continuation line: append to description if we're in an item.
            if (current is not null && !string.IsNullOrWhiteSpace(line))
            {
                // Ignore rows that are just standalone money tokens
                if (Regex.IsMatch(line, @"^\$?\d[\d,]*\.\d{2}$"))
                    continue;

                current.ProductName = (current.ProductName + " " + line).Trim();
            }
        }

        if (current is not null)
            parsed.Add(current);

        // De-dup by UPC+amount (AK invoices can include repeated lines due to pagination)
        return parsed
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductName) && x.Quantity > 0)
            .GroupBy(x => (NormalizeKey(x.ItemCode), x.Amount ?? 0m, NormalizeKey(x.ProductName)))
            .Select(g =>
            {
                var first = g.First();
                first.Quantity = g.Sum(z => z.Quantity);
                first.ShipQuantity = g.Sum(z => z.ShipQuantity);
                first.OrdQuantity = g.Sum(z => z.OrdQuantity);
                return first;
            })
            .ToList();
    }

    /// <summary>
    /// Simplified AK Wholesale cost parser that extracts UPC, Description, and PRICE (unit cost)
    /// from the raw text. This is a fallback when position-based parsing fails.
    /// AK Wholesale format: LineNo UPC ORD SHIP UNIT DESCRIPTION VOL TVOL TAX PRICE AMOUNT
    /// Example: 1 5061067871120 1 1 BOX ADJUST MYRUSHER 40KPF (D) 5CT -MOUNTAIN BERRY 100.00 100.00 $20.25 $52.49 $52.49
    /// </summary>
    private static List<PurchaseInvoiceLine> ParseAkWholesaleCostsSimple(string rawText)
    {
        var results = new List<PurchaseInvoiceLine>();
        if (string.IsNullOrWhiteSpace(rawText)) return results;

        // Stop keywords - don't parse lines containing these
        var stopKeywords = new[] { "SUB-TOTAL", "SUBTOTAL", "TOTAL CASES", "TRACKING", "BALANCE", 
            "IL TAX", "PAYMENT", "TAXABLE QTY", "YOU SAVED", "HAZMAT", "S & H", "DISCOUNT", 
            "PROMO", "CREDIT APPLIED", "WIRE-IN", "CASH", "CHECK", "CREDIT CARD", "LBS" };

        // Pattern to match AK Wholesale line items
        // Format: LineNo UPC ORD SHIP UNIT DESCRIPTION ... PRICE AMOUNT
        // The PRICE column is the unit cost we want
        var linePattern = new Regex(
            @"^\s*(?<lineno>\d{1,3})\s+" +                    // Line number (1-3 digits)
            @"(?<upc>\d{10,14})\s+" +                          // UPC (10-14 digits)
            @"(?<ord>\d+)\s+" +                                // ORD quantity
            @"(?<ship>\d+)\s+" +                               // SHIP quantity  
            @"(?<unit>BOX|EA|PK|BOT|CT|EACH|CASE)\s+" +        // Unit type
            @"(?<desc>.+?)\s+" +                               // Description (non-greedy)
            @"(?<vol>\d+\.?\d*)\s+" +                          // VOL(ML)
            @"(?<tvol>\d+\.?\d*)\s+" +                         // T.VOL(ML)
            @"(?:\*|\$?[\d,]+\.?\d*)\s+" +                     // TAX (* or amount)
            @"\$?(?<price>[\d,]+\.\d{2})\s+" +                 // PRICE (unit cost)
            @"\$?(?<amount>[\d,]+\.\d{2})\s*$",                // AMOUNT (total)
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // Also try a simpler pattern for lines that might be formatted differently
        var simplePattern = new Regex(
            @"^\s*\d{1,3}\s+" +                                // Line number
            @"(?<upc>\d{10,14})\s+" +                          // UPC
            @".+?" +                                           // Everything in between
            @"\$?(?<price>[\d,]+\.\d{2})\s+" +                 // Second-to-last money amount (PRICE)
            @"\$?[\d,]+\.\d{2}\s*$",                           // Last money amount (AMOUNT)
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool inItemSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var upper = line.ToUpperInvariant();

            // Detect header to start parsing
            if (upper.Contains("UPC") && upper.Contains("DESCRIPTION") && 
                (upper.Contains("PRICE") || upper.Contains("AMOUNT")))
            {
                inItemSection = true;
                continue;
            }

            // Skip lines with stop keywords
            if (stopKeywords.Any(kw => upper.Contains(kw)))
                continue;

            if (!inItemSection) continue;

            // Try the detailed pattern first
            var match = linePattern.Match(line);
            if (match.Success)
            {
                var upc = match.Groups["upc"].Value.Trim();
                var desc = match.Groups["desc"].Value.Trim();
                var priceStr = match.Groups["price"].Value.Replace(",", "");
                var amountStr = match.Groups["amount"].Value.Replace(",", "");
                var shipQty = decimal.TryParse(match.Groups["ship"].Value, out var sq) ? sq : 1m;

                if (decimal.TryParse(priceStr, out var price) && !string.IsNullOrWhiteSpace(desc))
                {
                    results.Add(new PurchaseInvoiceLine
                    {
                        ItemCode = upc,
                        ProductName = desc,
                        Quantity = shipQty,
                        ShipQuantity = shipQty,
                        UnitCost = price,
                        Price = price,
                        Amount = decimal.TryParse(amountStr, out var amt) ? amt : (decimal?)null
                    });
                }
                continue;
            }

            // Fallback: Try to find UPC and prices in any line that starts with a line number
            if (Regex.IsMatch(line, @"^\s*\d{1,3}\s+\d{10,14}"))
            {
                // Extract UPC
                var upcMatch = Regex.Match(line, @"\b(\d{10,14})\b");
                if (!upcMatch.Success) continue;
                var upc = upcMatch.Groups[1].Value;

                // Extract all money amounts from the line
                var moneyMatches = Regex.Matches(line, @"\$?([\d,]+\.\d{2})");
                if (moneyMatches.Count < 2) continue;

                // Last amount is AMOUNT (total), second-to-last is PRICE (unit cost)
                var priceStr = moneyMatches[moneyMatches.Count - 2].Groups[1].Value.Replace(",", "");
                var amountStr = moneyMatches[moneyMatches.Count - 1].Groups[1].Value.Replace(",", "");

                if (!decimal.TryParse(priceStr, out var price)) continue;
                if (!decimal.TryParse(amountStr, out var amount)) continue;

                // Extract description: everything between UPC and the money values
                var descStart = line.IndexOf(upc) + upc.Length;
                var descEnd = line.IndexOf(moneyMatches[0].Value);
                var desc = "";
                if (descEnd > descStart)
                {
                    desc = line.Substring(descStart, descEnd - descStart).Trim();
                    // Remove leading unit type (BOX, EA, etc.)
                    desc = Regex.Replace(desc, @"^\d+\s+\d+\s+(?:BOX|EA|PK|BOT|CT|EACH|CASE)\s+", "", RegexOptions.IgnoreCase).Trim();
                    // Remove trailing numeric values (VOL, TVOL, TAX)
                    desc = Regex.Replace(desc, @"(\s+\d+\.?\d*)+\s*$", "").Trim();
                }

                if (string.IsNullOrWhiteSpace(desc) || desc.Length < 3) continue;

                // Calculate quantity from amount/price
                var qty = price > 0 ? Math.Round(amount / price, 0) : 1m;
                if (qty < 1) qty = 1;

                results.Add(new PurchaseInvoiceLine
                {
                    ItemCode = upc,
                    ProductName = desc,
                    Quantity = qty,
                    ShipQuantity = qty,
                    UnitCost = price,
                    Price = price,
                    Amount = amount
                });
            }
        }

        // De-duplicate by UPC
        return results
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductName) && !string.IsNullOrWhiteSpace(x.ItemCode))
            .GroupBy(x => x.ItemCode)
            .Select(g => g.First())
            .ToList();
    }

    private static double FindHeaderX(List<AkWord> row, string contains)
    {
        var w = row
            .OrderBy(r => r.X)
            .FirstOrDefault(r => r.Text.ToUpperInvariant().Contains(contains));
        return w is null ? -1 : w.X;
    }

    private static ImportedInvoice ParseAkWholesaleInvoice(string raw, List<string> lines)
    {
        var mNo = Regex.Match(raw, @"Invoice\s*No\.?\s*[:#\-]?\s*([A-Z0-9\-]{4,})", RegexOptions.IgnoreCase);
        var invNo = mNo.Success ? mNo.Groups[1].Value.Trim() : "";

        DateOnly? invDate = null;
        var md = Regex.Match(raw, @"Invoice\s*Date\s*[:#\-]?\s*([0-9]{1,2}[\/\-][0-9]{1,2}[\/\-][0-9]{2,4})", RegexOptions.IgnoreCase);
        if (md.Success && TryParseDate(md.Groups[1].Value, out var d1)) invDate = d1;
        invDate ??= GuessInvoiceDate(raw);

        decimal? total = null;

        // Prefer totals from reconstructed lines (more reliable and can handle label/value split across lines).
        total = FindLabeledMoney(lines,
            "grand total", "total amount", "amount due", "balance due", "total");

        // Fallback to raw heuristics.
        total ??= GuessTotal(raw);

        var parsedLines = ParseAkWholesaleLineItems(lines);
        if (parsedLines.Count == 0)
        {
            // fallback to raw split
            parsedLines = ParseAkWholesaleLineItems(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
        }

        return new ImportedInvoice
        {
            VendorName = "AK Wholesale Inc",
            InvoiceNumber = invNo,
            InvoiceDate = invDate,
            Total = total,
            Lines = parsedLines
        };
    }

    private static List<PurchaseInvoiceLine> ParseAkWholesaleLineItems(List<string> lines)
    {
        var parsed = new List<PurchaseInvoiceLine>();
        if (lines is null || lines.Count == 0) return parsed;

        // Normalize and keep ordering.
        var norm = lines
            .Select(l => Regex.Replace((l ?? "").Trim(), @"\s+", " "))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        static bool IsTotals(string up) =>
            up.StartsWith("SUB-TOTAL") || up.StartsWith("SUBTOTAL") || up.StartsWith("IL TAX") || up.StartsWith("TOTAL") ||
            up.StartsWith("PAYMENT") || up.Contains("BALANCE") || up.Contains("TAXABLE QTY") || up.Contains("TOTAL CASES") || up.Contains("TRACKING");

        static bool IsItemStart(string s) => Regex.IsMatch(s.Trim(), @"^\d+\s+([A-Za-z0-9]{3,14}|\d{10,14})\b");
        static bool IsQtyOnly(string s) => Regex.IsMatch(s, @"^\d+(?:\.\d+)?$");
        static bool IsShipUnit(string s) => Regex.IsMatch(s, @"^\d+(?:\.\d+)?\s+[A-Za-z]{1,6}$");

        int NextNonEmpty(int idx)
        {
            while (idx < norm.Count && string.IsNullOrWhiteSpace(norm[idx])) idx++;
            return idx;
        }

        bool inItems = false;
        for (int i = 0; i < norm.Count; i++)
        {
            var line = norm[i];
            var up = line.ToUpperInvariant();

            if (!inItems)
            {
                // AK invoices often split the table header into multiple lines in page.Text.
                if ((up.Contains("ORD") && up.Contains("SHIP") && up.Contains("UNIT")) || (up.Contains("UPC") && up.Contains("DESCRIPTION")))
                    inItems = true;
                continue;
            }

            if (IsTotals(up)) break;

            // Page.Text layout (from user PDFs):
            // "<LineNo> <UPC>" -> "<ORD>" -> "<SHIP> <UNIT>" -> Description lines -> "*"/volume -> $ lines
            if (IsItemStart(line))
            {
                var m = Regex.Match(line, @"^(?<lineNo>\d+)\s+(?<code>[A-Za-z0-9]{3,14})\b.*$");
                var code = m.Groups["code"].Value.Trim();

                int j = NextNonEmpty(i + 1);
                if (j >= norm.Count) break;

                // ORD
                var ord = IsQtyOnly(norm[j]) ? MoneyOrZero(norm[j]) : 0m;
                j = NextNonEmpty(j + 1);
                if (j >= norm.Count) break;

                // SHIP + UNIT
                decimal ship = 0m;
                string unit = "";
                if (IsShipUnit(norm[j]))
                {
                    var mShip = Regex.Match(norm[j], @"^(?<ship>\d+(?:\.\d+)?)\s+(?<unit>[A-Za-z]{1,6})$", RegexOptions.IgnoreCase);
                    ship = MoneyOrZero(mShip.Groups["ship"].Value);
                    unit = mShip.Groups["unit"].Value.Trim();
                    j++;
                }
                else if (IsQtyOnly(norm[j]))
                {
                    ship = MoneyOrZero(norm[j]);
                    var j2 = NextNonEmpty(j + 1);
                    if (j2 < norm.Count)
                    {
                        var mUnit = Regex.Match(norm[j2], @"^(?<ship>\d+(?:\.\d+)?)\s+(?<unit>[A-Za-z]{1,6})$", RegexOptions.IgnoreCase);
                        if (mUnit.Success)
                        {
                            ship = MoneyOrZero(mUnit.Groups["ship"].Value);
                            unit = mUnit.Groups["unit"].Value.Trim();
                            j = j2 + 1;
                        }
                        else
                        {
                            j = j2;
                        }
                    }
                    else
                    {
                        j++;
                    }
                }
                if (ship <= 0) ship = ord;

                // Description lines
                var descSb = new StringBuilder();
                j = NextNonEmpty(j);
                for (; j < norm.Count; j++)
                {
                    var l = norm[j];
                    var u = l.ToUpperInvariant();
                    if (IsItemStart(l)) break;
                    if (IsTotals(u)) break;

                    // Stop before volume/price sections
                    if (l == "*" || l.Contains("$") || Regex.IsMatch(l, @"^\d{1,3}(?:,\d{3})*\.\d{2}$"))
                        break;

                    if (descSb.Length > 0) descSb.Append(' ');
                    descSb.Append(l);
                }

                // Price lines: capture $ amounts until next item/totals
                var money = new List<decimal>();
                for (; j < norm.Count; j++)
                {
                    var l = norm[j];
                    var u = l.ToUpperInvariant();
                    if (IsItemStart(l)) break;
                    if (IsTotals(u)) break;
                    if (l.Contains("$"))
                        money.AddRange(ExtractMoneyTokens(l));
                }

                var desc = descSb.ToString().Trim();
                var ext = money.Count > 0 ? money.Max() : 0m;
                var unitCost = (ship > 0 && ext > 0) ? ext / ship : 0m;

                if (!string.IsNullOrWhiteSpace(desc) && ship > 0 && unitCost > 0)
                {
                    parsed.Add(new PurchaseInvoiceLine
                    {
                        ProductName = $"{desc} ({code})",
                        Quantity = ship,
                        UnitCost = Decimal.Round(unitCost, 4)
                    });
                }

                i = j - 1;
                continue;
            }

            // Fallback for word-position reconstructed lines (older AK templates)
            var moneyTokens = ExtractMoneyTokens(line);
            if (moneyTokens.Count < 2) continue;

            var mFull = Regex.Match(line, @"^(?<lineNo>\d+)\s+(?<code>[A-Za-z0-9]{4,14})\s+(?<ord>\d+(?:\.\d+)?)\s+(?<ship>\d+(?:\.\d+)?)\s+(?<unit>[A-Za-z]{1,6})\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
            if (!mFull.Success) continue;

            var code2 = mFull.Groups["code"].Value.Trim();
            var ship2 = MoneyOrZero(mFull.Groups["ship"].Value);
            var ord2 = MoneyOrZero(mFull.Groups["ord"].Value);
            var qty2 = ship2 > 0 ? ship2 : ord2;
            var desc2 = CleanAkDescription(mFull.Groups["rest"].Value);
            var amount2 = moneyTokens[^1];
            var unitCost2 = qty2 > 0 ? amount2 / qty2 : 0m;
            if (!string.IsNullOrWhiteSpace(desc2) && qty2 > 0 && unitCost2 > 0)
            {
                parsed.Add(new PurchaseInvoiceLine
                {
                    ProductName = $"{desc2.Trim()} ({code2})",
                    Quantity = qty2,
                    UnitCost = Decimal.Round(unitCost2, 4)
                });
            }
        }

        return parsed
            .GroupBy(x => (NormalizeKey(x.ProductName), x.UnitCost))
            .Select(g => new PurchaseInvoiceLine { ProductName = g.First().ProductName, Quantity = g.Sum(x => x.Quantity), UnitCost = g.First().UnitCost })
            .ToList();
    }

    private static string CleanAkDescription(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var line = Regex.Replace(s.Trim(), @"\s+", " ");

        // Remove trailing money columns
        line = Regex.Replace(line, @"(\$?\s*\d[\d,]*\.\d{2}\s*)+$", "").Trim();
        // Cut at the '*' marker if present
        var star = line.LastIndexOf('*');
        if (star >= 0) line = line.Substring(0, star).Trim();
        // Remove trailing volume/tax decimals (e.g. "90.00 90.00" etc)
        line = Regex.Replace(line, @"(\s+\d+\.\d{2}){1,5}\s*$", "").Trim();
        // Remove leading numeric columns if they remained
        line = Regex.Replace(line, @"^\d+(\s+\d+){0,3}\s+(EA|BOX|DIS|PK|CS)\s+", "", RegexOptions.IgnoreCase).Trim();
        return line;
    }

    private static ImportedInvoice ParseAmericanDistributorsInvoice(string raw, List<string> lines, int pageNumber)
    {
        var vendorName = "American Distributors LLC";

        var invNo = "";
        var mInv = Regex.Match(raw, @"TRANSACTION\s+NO\.?\s*[:#\-]?\s*([0-9]{4,})", RegexOptions.IgnoreCase);
        if (mInv.Success) invNo = mInv.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(invNo))
        {
            var mInv2 = Regex.Match(raw, @"INVOICE\s*(?:NO\.?|#)?\s*[:#\-]?\s*([A-Z0-9\-]{4,})", RegexOptions.IgnoreCase);
            if (mInv2.Success) invNo = mInv2.Groups[1].Value.Trim();
        }

        DateOnly? invDate = null;
        var mDate = Regex.Match(raw.ToUpperInvariant(), @"\bDATE\b\s+([0-9]{2}\s+[A-Z]{3}\s+[0-9]{4})");
        if (mDate.Success)
        {
            if (DateTime.TryParseExact(mDate.Groups[1].Value, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                invDate = DateOnly.FromDateTime(dt);
        }
        invDate ??= GuessInvoiceDate(raw);

        decimal? total = null;

        // The same row may contain both state tax and the invoice balance. Capture the
        // value following INVOICE BALANCE specifically so the tax is never used as total.
        var balanceMatch = Regex.Match(
            raw,
            @"INVOICE\s+BALANCE\s+\$?\s*([0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2})",
            RegexOptions.IgnoreCase);
        if (balanceMatch.Success)
            total = MoneyOrNull(balanceMatch.Groups[1].Value);
        total ??= FindLabeledMoney(lines, "invoice balance", "total amount", "amount due", "balance due", "grand total");
        total ??= GuessTotal(raw);

        var parsedLines = ParseAmericanDistributorsLineItems(lines);

        return new ImportedInvoice
        {
            PageNumber = pageNumber,
            VendorName = vendorName,
            InvoiceNumber = invNo,
            InvoiceDate = invDate,
            Total = total,
            Lines = parsedLines
        };
    }

    private static List<ImportedInvoice> MergeAmericanDistributorsInvoices(IEnumerable<ImportedInvoice> pages)
    {
        var merged = new List<ImportedInvoice>();

        foreach (var group in pages
                     .Where(x => !string.IsNullOrWhiteSpace(x.InvoiceNumber))
                     .GroupBy(x => x.InvoiceNumber, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(x => x.PageNumber ?? int.MaxValue).ToList();
            var first = ordered[0];
            var total = ordered
                .Where(x => x.Total is > 0m)
                .Select(x => x.Total)
                .LastOrDefault();

            merged.Add(new ImportedInvoice
            {
                PageNumber = first.PageNumber,
                VendorName = first.VendorName,
                InvoiceNumber = first.InvoiceNumber,
                InvoiceDate = ordered.Select(x => x.InvoiceDate).FirstOrDefault(x => x.HasValue),
                Total = total,
                Lines = ordered.SelectMany(x => x.Lines).ToList()
            });
        }

        // Keep any page that truly has no transaction number visible rather than silently discarding it.
        merged.AddRange(pages.Where(x => string.IsNullOrWhiteSpace(x.InvoiceNumber)));
        return merged.OrderBy(x => x.PageNumber ?? int.MaxValue).ToList();
    }

    private static List<PurchaseInvoiceLine> ParseAmericanDistributorsLineItems(List<string> lines)
    {
        var parsed = new List<PurchaseInvoiceLine>();
        if (lines is null || lines.Count == 0) return parsed;

        // Normalize and preserve page.Text ordering.
        var norm = lines
            .Select(l => Regex.Replace((l ?? "").Trim(), @"\s+", " "))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        static bool IsTotalsOrFooter(string up) =>
            up.Contains("TOTAL AMOUNT") || up.Contains("INVOICE BALANCE") || up.StartsWith("SUBTOTAL") || up.StartsWith("TOTAL") ||
            up.Contains("TOTAL EXCISE TAX COLLECTED") || up.Contains("ILLINOIS TP LICENSE") || up.Contains("QUESTIONS?") || up.StartsWith("PAGE ");

        static bool IsSku(string s) => Regex.IsMatch(s, @"^[A-Z0-9]{6,}$", RegexOptions.IgnoreCase);
        static bool IsQty(string s) => Regex.IsMatch(s, @"^\d+(?:\.\d+)?$", RegexOptions.IgnoreCase);
        static bool IsMoneyOnlyLine(string s) => Regex.IsMatch(s, @"^\$?\s*[0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2}$");

        int i = 0;
        while (i < norm.Count)
        {
            var line = norm[i];
            var up = line.ToUpperInvariant();

            if (IsTotalsOrFooter(up)) { i++; continue; }


            // Single-line table row layout (as in the user's SI_10600040 PDF):
            // "<SKU> <QTY> <DESCRIPTION...> <UNIT PRICE> <EXTENDED PRICE>"
            // with optional continuation lines (e.g. "(FLORIDA COMPLIANT) ...") on the next line(s).
            // Single-line table row layout:
            // "<SKU> <QTY> <DESCRIPTION...> <UNIT PRICE> [<EXCISE TAX>] <EXTENDED PRICE>"
            var mRow = Regex.Match(line,
                @"^(?<sku>[A-Z0-9]{6,})\s+(?<qty>\d+(?:\.\d+)?)\s+(?<desc>.+?)\s+" +
                @"(?<unit>[0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2})\s+" +
                @"(?:(?<exc>[0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2})\s+)?" +
                @"(?<ext>[0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2})$",
                RegexOptions.IgnoreCase);
            if (mRow.Success && IsSku(mRow.Groups["sku"].Value) && IsQty(mRow.Groups["qty"].Value))
            {
                var sku = mRow.Groups["sku"].Value.Trim();
                var qty = MoneyOrZero(mRow.Groups["qty"].Value);
                var unit = MoneyOrZero(mRow.Groups["unit"].Value);
                var ext = MoneyOrZero(mRow.Groups["ext"].Value);
                decimal? excise = null;
                if (mRow.Groups["exc"].Success)
                    excise = MoneyOrNull(mRow.Groups["exc"].Value);
                if (qty > 0)
                {
                    var descSb = new StringBuilder(mRow.Groups["desc"].Value.Trim());

                    // Collect continuation lines that do not start a new item and don't look like totals.
                    int j = i + 1;
                    while (j < norm.Count)
                    {
                        var next = norm[j];
                        var nextUp = next.ToUpperInvariant();
                        if (IsTotalsOrFooter(nextUp)) break;

                        // Stop if next looks like a new SKU+QTY row.
                        var parts = next.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length >= 2 && IsSku(parts[0]) && IsQty(parts[1]) && ExtractDecimalTokens(next).Count >= 2)
                            break;

                        // Prefer parenthetical continuation lines and similar short fragments.
                        if (next.StartsWith("(", StringComparison.OrdinalIgnoreCase) || !ExtractDecimalTokens(next).Any())
                        {
                            descSb.Append(" ");
                            descSb.Append(next.Trim());
                            j++;
                            continue;
                        }

                        break;
                    }

                    i = j;

                    var finalDesc = descSb.ToString().Trim();
                    var unitCost = unit > 0 ? unit : (ext > 0 && qty > 0 ? ext / qty : 0m);
                    if (!string.IsNullOrWhiteSpace(finalDesc) && unitCost > 0)
                    {
                        parsed.Add(new PurchaseInvoiceLine
                        {
                            ItemCode = sku,
                            ProductName = finalDesc,
                            OrdQuantity = qty,
                            ShipQuantity = qty,
                            Quantity = qty,
                            Price = unit,
                            Tax = excise,
                            Amount = ext,
                            UnitCost = Decimal.Round(unitCost, 4)
                        });
                    }

                    continue;
                }
            }

            // Layout (from user PDFs):
            // SKU (own line) -> QTY (own line) -> DESCRIPTION (1+ lines) -> UNIT PRICE (line) -> EXTENDED PRICE (line)
            if (IsSku(line) && i + 1 < norm.Count && IsQty(norm[i + 1]))
            {
                var sku = line.Trim();
                var qty = MoneyOrZero(norm[i + 1]);
                i += 2;
                if (qty <= 0) continue;

                // Collect description lines until we hit price lines or next SKU+QTY.
                var descSb = new StringBuilder();
                while (i < norm.Count)
                {
                    var l = norm[i];
                    var u = l.ToUpperInvariant();
                    if (IsTotalsOrFooter(u)) break;
                    if (IsSku(l) && i + 1 < norm.Count && IsQty(norm[i + 1])) break;
                    if (IsMoneyOnlyLine(l)) break;

                    // Skip header fragments if they re-appear.
                    if (u is "SKU" or "QTY" or "DESCRIPTION" or "UNIT" or "PRICE" or "UNIT PRICE" or "EXTENDED" or "EXTENDED PRICE" or "EXCISE" or "EXCISE TAX")
                    {
                        i++;
                        continue;
                    }

                    if (descSb.Length > 0) descSb.Append(' ');
                    descSb.Append(l);
                    i++;
                }

                // Read up to 3 consecutive money lines (unit, excise tax, extended).
                var nums = new List<decimal>();
                while (i < norm.Count && nums.Count < 3 && IsMoneyOnlyLine(norm[i]))
                {
                    nums.AddRange(ExtractDecimalTokens(norm[i]));
                    i++;
                }

                decimal unitCost = 0m;
                decimal? tax = null;
                decimal? price = null;
                decimal? amount = null;
                if (nums.Count == 1)
                {
                    var ext = nums[0];
                    amount = ext;
                    unitCost = qty > 0 ? ext / qty : 0m;
                }
                else if (nums.Count >= 2)
                {
                    amount = nums[^1];
                    if (nums.Count >= 3)
                        tax = nums[^2];
                    price = nums[0];
                    unitCost = qty > 0 ? amount.Value / qty : (nums.Count >= 2 ? nums[0] : 0m);
                }

                var desc = descSb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(desc) && qty > 0 && unitCost > 0)
                {
                    parsed.Add(new PurchaseInvoiceLine
                    {
                        ItemCode = sku,
                        ProductName = desc,
                        OrdQuantity = qty,
                        ShipQuantity = qty,
                        Quantity = qty,
                        Price = price,
                        Tax = tax,
                        Amount = amount,
                        UnitCost = Decimal.Round(unitCost, 4)
                    });
                }

                continue;
            }

            i++;
        }

        return parsed
            .GroupBy(x => (NormalizeKey(x.ProductName), x.UnitCost))
            .Select(g => new PurchaseInvoiceLine { ProductName = g.First().ProductName, Quantity = g.Sum(x => x.Quantity), UnitCost = g.First().UnitCost })
            .ToList();
    }

    private static decimal? FindLabeledMoney(List<string> lines, params string[] labels)
    {
        if (lines is null || lines.Count == 0) return null;

        var wanted = labels.Select(l => l.ToLowerInvariant()).ToList();
        var norm = lines
            .Select(l => Regex.Replace((l ?? "").Trim(), @"\s+", " "))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        // Prefer the bottom of the document.
        var start = Math.Max(0, norm.Count - 140);
        for (int i = norm.Count - 1; i >= start; i--)
        {
            var l = norm[i];
            var low = l.ToLowerInvariant();
            if (!wanted.Any(w => low.Contains(w))) continue;

            // Same line
            var nums = ExtractDecimalTokens(l);
            if (nums.Count > 0) return nums[^1];

            // Next few lines (label/value split)
            for (int j = i + 1; j < Math.Min(norm.Count, i + 5); j++)
            {
                var nums2 = ExtractDecimalTokens(norm[j]);
                if (nums2.Count > 0) return nums2[^1];
            }
        }

        return null;
    }

    private static List<decimal> ExtractMoneyTokens(string line)
    {
        var matches = Regex.Matches(line ?? "", @"\$\s*([0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2})");
        var list = new List<decimal>();
        foreach (Match m in matches)
        {
            var d = MoneyOrZero(m.Groups[1].Value);
            if (d > 0) list.Add(d);
        }
        // If no $-prefixed numbers, also accept plain decimals near end
        if (list.Count == 0)
            list = ExtractDecimalTokens(line);
        return list;
    }

    private static List<decimal> ExtractDecimalTokens(string? line)
    {
        var matches = Regex.Matches(line ?? "", @"\b([0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2})\b");
        var list = new List<decimal>();
        foreach (Match m in matches)
        {
            var d = MoneyOrZero(m.Groups[1].Value);
            if (d > 0) list.Add(d);
        }
        return list;
    }

    private static bool Approximately(decimal a, decimal b)
    {
        if (a == 0 || b == 0) return false;
        var diff = Math.Abs(a - b);
        var tol = Math.Max(0.02m, Math.Max(Math.Abs(a), Math.Abs(b)) * 0.02m);
        return diff <= tol;
    }

    private static InvoiceImportResult ImportFromExcel(string filePath)
    {
        var warnings = new List<string>();

        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheets.First();

        // Grab a reasonable range of text for header detection
        var headerText = new StringBuilder();
        for (int r = 1; r <= 20; r++)
        {
            for (int c = 1; c <= 10; c++)
            {
                var v = ws.Cell(r, c).GetString();
                if (!string.IsNullOrWhiteSpace(v)) headerText.AppendLine(v);
            }
        }

        var raw = headerText.ToString();
        var vendor = GuessVendorName(raw);
        var invNo = GuessInvoiceNumber(raw);
        var invDate = GuessInvoiceDate(raw);
        // Total is often located near the bottom of the sheet, not in the header region.
        var total = GuessTotal(raw) ?? GuessTotalFromExcel(ws);

        // Find header row and column mapping
        (int headerRow, int colProduct, int colQty, int colUnitCost) = FindLineHeader(ws);

        if (headerRow == 0)
        {
            warnings.Add("Could not find a line-items header row (Product/Qty/Cost). Please ensure the invoice sheet has those columns.");
            return new InvoiceImportResult
            {
                Success = true,
                VendorName = vendor ?? "",
                InvoiceNumber = invNo ?? "",
                InvoiceDate = invDate,
                Total = total,
                Lines = new List<PurchaseInvoiceLine>(),
                RawText = raw,
                Warnings = warnings
            };
        }

        var lines = new List<PurchaseInvoiceLine>();

        // Read rows after header
        var blankCount = 0;
        for (int r = headerRow + 1; r <= headerRow + 500; r++)
        {
            var product = ws.Cell(r, colProduct).GetString().Trim();
            var qtyStr = ws.Cell(r, colQty).GetString().Trim();
            var costStr = ws.Cell(r, colUnitCost).GetString().Trim();

            if (string.IsNullOrWhiteSpace(product) && string.IsNullOrWhiteSpace(qtyStr) && string.IsNullOrWhiteSpace(costStr))
            {
                blankCount++;
                if (blankCount >= 5) break;
                continue;
            }
            blankCount = 0;

            if (string.IsNullOrWhiteSpace(product)) continue;

            var qty = MoneyOrZero(qtyStr);
            var unit = MoneyOrZero(costStr);

            // Sometimes qty is numeric typed
            if (qty == 0m)
            {
                if (ws.Cell(r, colQty).TryGetValue<double>(out var qd)) qty = (decimal)qd;
            }
            if (unit == 0m)
            {
                if (ws.Cell(r, colUnitCost).TryGetValue<double>(out var ud)) unit = (decimal)ud;
            }

            if (qty <= 0 && unit <= 0) continue;

            lines.Add(new PurchaseInvoiceLine
            {
                ProductName = product,
                Quantity = qty <= 0 ? 1m : qty,
                UnitCost = unit
            });
        }

        if (lines.Count == 0)
            warnings.Add("No line items were detected. Please check the invoice sheet column names.");

        if (total is null && lines.Count > 0)
        {
            var computed = lines.Sum(x => x.Quantity * x.UnitCost);
            total = computed > 0 ? computed : null;
        }

        return new InvoiceImportResult
        {
            Success = true,
            VendorName = vendor ?? "",
            InvoiceNumber = invNo ?? "",
            InvoiceDate = invDate,
            Total = total,
            Lines = lines,
            RawText = raw,
            Warnings = warnings
        };
    }

    private static decimal? GuessTotalFromExcel(IXLWorksheet ws)
    {
        try
        {
            var used = ws.RangeUsed();
            if (used is null) return null;

            // Scan a bounded range to avoid expensive full-sheet loops.
            var firstRow = Math.Max(1, used.RangeAddress.FirstAddress.RowNumber);
            var lastRow = Math.Min(used.RangeAddress.LastAddress.RowNumber, firstRow + 800);
            var firstCol = Math.Max(1, used.RangeAddress.FirstAddress.ColumnNumber);
            var lastCol = Math.Min(used.RangeAddress.LastAddress.ColumnNumber, firstCol + 60);

            // Prefer matches near the bottom.
            var priorities = new (int prio, string[] labels)[]
            {
                (1, new[] { "grand total", "invoice total", "total due", "amount due", "balance due" }),
                (2, new[] { "total" }),
            };

            decimal? best = null;
            int bestPrio = int.MaxValue;

            for (int r = lastRow; r >= firstRow; r--)
            {
                for (int c = firstCol; c <= lastCol; c++)
                {
                    var label = (ws.Cell(r, c).GetString() ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    var low = label.ToLowerInvariant();

                    int prio = int.MaxValue;
                    foreach (var p in priorities)
                    {
                        if (p.labels.Any(l => low.Contains(l))) { prio = p.prio; break; }
                    }
                    if (prio == int.MaxValue) continue;

                    // Find a numeric value in the next few cells to the right.
                    decimal val = 0m;
                    bool found = false;
                    for (int dx = 1; dx <= 5; dx++)
                    {
                        var cell = ws.Cell(r, c + dx);
                        var s = (cell.GetString() ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(s))
                        {
                            // try numeric typed cell
                            if (cell.TryGetValue<double>(out var dnum) && dnum > 0)
                            {
                                val = (decimal)dnum;
                                found = true;
                                break;
                            }
                            continue;
                        }
                        val = MoneyOrZero(s);
                        if (val > 0)
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found || val <= 0) continue;

                    // Keep best priority; within same priority, keep the larger value.
                    if (prio < bestPrio || (prio == bestPrio && (best is null || val >= best.Value)))
                    {
                        bestPrio = prio;
                        best = val;
                    }
                }

                // If we already found a high-priority match, don't scan too far upward.
                if (bestPrio == 1 && best is not null) break;
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static InvoiceImportResult ImportFromCsv(string filePath)
    {
        var warnings = new List<string>();
        var all = File.ReadAllLines(filePath);
        if (all.Length == 0)
            return new InvoiceImportResult { Success = false, Warnings = { "CSV is empty." } };

        // Find header row - look for SKU/UPC or Description with Cost/Price
        var headerRowIndex = -1;
        string[] header = Array.Empty<string>();
        for (int i = 0; i < Math.Min(all.Length, 20); i++)
        {
            var cols = SplitCsv(all[i]);
            var joined = string.Join(" ", cols).ToLowerInvariant();
            // Accept headers with: (sku/upc OR description/product) AND (cost/price)
            var hasIdentifier = joined.Contains("sku") || joined.Contains("upc") || 
                               joined.Contains("product") || joined.Contains("description") || joined.Contains("item");
            var hasPrice = joined.Contains("cost") || joined.Contains("price") || joined.Contains("unit");
            if (hasIdentifier && hasPrice)
            {
                headerRowIndex = i;
                header = cols;
                break;
            }
        }

        if (headerRowIndex < 0)
        {
            warnings.Add("Could not find CSV header row. Expected columns like SKU/UPC, Description, Cost/Price.");
            return new InvoiceImportResult { Success = true, Warnings = warnings };
        }

        // Find column indices
        int colSku = FindCol(header, "sku", "upc", "item code", "itemcode", "barcode", "code");
        int colProduct = FindCol(header, "product", "item", "description", "name", "item name");
        int colQty = FindCol(header, "qty", "quantity", "ship", "ord");
        int colCost = FindCol(header, "unit cost", "unitcost", "cost", "price", "unit price", "unitprice");

        // If we don't have SKU but have product, that's okay
        // If we don't have product but have SKU, we'll use SKU as identifier
        if (colSku < 0 && colProduct < 0)
        {
            warnings.Add("CSV must have either SKU/UPC or Product/Description column.");
            return new InvoiceImportResult { Success = true, Warnings = warnings };
        }
        
        if (colCost < 0)
        {
            warnings.Add("CSV must have a Cost or Price column.");
            return new InvoiceImportResult { Success = true, Warnings = warnings };
        }

        var lines = new List<PurchaseInvoiceLine>();
        for (int i = headerRowIndex + 1; i < all.Length; i++)
        {
            var cols = SplitCsv(all[i]);
            if (cols.Length == 0) continue;

            var sku = colSku >= 0 && colSku < cols.Length ? cols[colSku].Trim() : "";
            var product = colProduct >= 0 && colProduct < cols.Length ? cols[colProduct].Trim() : "";
            var qtyStr = colQty >= 0 && colQty < cols.Length ? cols[colQty].Trim() : "";
            var costStr = colCost >= 0 && colCost < cols.Length ? cols[colCost].Trim() : "";

            // Skip if no identifier
            if (string.IsNullOrWhiteSpace(sku) && string.IsNullOrWhiteSpace(product)) continue;

            // Use SKU as product name if no description
            if (string.IsNullOrWhiteSpace(product) && !string.IsNullOrWhiteSpace(sku))
                product = $"SKU: {sku}";

            var qty = MoneyOrZero(qtyStr);
            var unit = MoneyOrZero(costStr);
            if (qty <= 0) qty = 1m;
            if (unit <= 0) continue;

            lines.Add(new PurchaseInvoiceLine 
            { 
                ItemCode = sku,
                ProductName = product, 
                Quantity = qty, 
                UnitCost = unit 
            });
        }

        return new InvoiceImportResult { Success = true, Lines = lines, Warnings = warnings };
    }

    private static (int headerRow, int colProduct, int colQty, int colUnitCost) FindLineHeader(IXLWorksheet ws)
    {
        for (int r = 1; r <= 30; r++)
        {
            var rowText = new List<string>();
            for (int c = 1; c <= 30; c++)
            {
                var s = ws.Cell(r, c).GetString();
                if (!string.IsNullOrWhiteSpace(s)) rowText.Add(s);
            }
            var joined = string.Join(" ", rowText).ToLowerInvariant();
            if (!joined.Contains("qty") && !joined.Contains("quantity")) continue;
            if (!(joined.Contains("cost") || joined.Contains("price") || joined.Contains("unit"))) continue;

            // map columns
            int colProduct = -1, colQty = -1, colUnit = -1;
            for (int c = 1; c <= 50; c++)
            {
                var h = ws.Cell(r, c).GetString().Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(h)) continue;

                if (colProduct < 0 && (h.Contains("product") || h.Contains("item") || h.Contains("description") || h.Contains("name"))) colProduct = c;
                if (colQty < 0 && (h.Contains("qty") || h.Contains("quantity"))) colQty = c;
                if (colUnit < 0 && (h.Contains("unit") && (h.Contains("cost") || h.Contains("price")) || h == "cost" || h == "price")) colUnit = c;
            }

            if (colProduct > 0 && colQty > 0 && colUnit > 0)
                return (r, colProduct, colQty, colUnit);
        }

        return (0, 0, 0, 0);
    }

    private static List<PurchaseInvoiceLine> ParseLineItems(List<string> lines)
    {
        var parsed = new List<PurchaseInvoiceLine>();

        foreach (var rawLine in lines)
        {
            var line = Regex.Replace(rawLine ?? "", @"\s+", " ").Trim();
            if (line.Length < 6) continue;

            var lower = line.ToLowerInvariant();
            // Skip summary lines, but avoid false positives where a product name contains "total".
            if (Regex.IsMatch(lower, @"^(subtotal|tax|amount due|balance due|grand total|total)\b"))
                continue;

            // Pattern: Name ... Qty UnitCost [Amount]
            // Try 3-number pattern first
            // Pattern: Name ... Qty [UOM] UnitCost Amount
            // UOM examples: EA, PCS, BOX, PK, etc.
            var m3 = Regex.Match(line,
                @"^(?<name>.+?)\s+(?<qty>\d+(?:\.\d+)?)\s+(?<uom>[A-Za-z]{1,6})?\s*(?<unit>\$?\d[\d,]*(?:\.\d{2,4})?)\s+(?<amt>\$?\d[\d,]*(?:\.\d{2,4})?)$",
                RegexOptions.IgnoreCase);

            if (m3.Success)
            {
                var name = m3.Groups["name"].Value.Trim();
                var qty = MoneyOrZero(m3.Groups["qty"].Value);
                var unit = MoneyOrZero(m3.Groups["unit"].Value);
                var amt = MoneyOrZero(m3.Groups["amt"].Value);
                if (unit <= 0 && amt > 0 && qty > 0) unit = amt / qty;
                if (!string.IsNullOrWhiteSpace(name) && qty > 0 && unit > 0)
                {
                    parsed.Add(new PurchaseInvoiceLine { ProductName = name, Quantity = qty, UnitCost = unit });
                    continue;
                }
            }

            // Fallback: allow extra tokens/columns between Qty and unit/amount (common in distributor invoices)
            var m3b = Regex.Match(line,
                @"^(?<name>.+?)\s+(?<qty>\d+(?:\.\d+)?)\s+.*?(?<unit>\$?\d[\d,]*(?:\.\d{2,4})?)\s+(?<amt>\$?\d[\d,]*(?:\.\d{2,4})?)$",
                RegexOptions.IgnoreCase);

            if (m3b.Success)
            {
                var name = m3b.Groups["name"].Value.Trim();
                var qty = MoneyOrZero(m3b.Groups["qty"].Value);
                var unit = MoneyOrZero(m3b.Groups["unit"].Value);
                var amt = MoneyOrZero(m3b.Groups["amt"].Value);
                if (unit <= 0 && amt > 0 && qty > 0) unit = amt / qty;
                if (!string.IsNullOrWhiteSpace(name) && qty > 0 && unit > 0)
                {
                    parsed.Add(new PurchaseInvoiceLine { ProductName = name, Quantity = qty, UnitCost = unit });
                    continue;
                }
            }

            // Try 2-number pattern
            // Pattern: Name ... Qty [UOM] UnitCost
            var m2 = Regex.Match(line,
                @"^(?<name>.+?)\s+(?<qty>\d+(?:\.\d+)?)\s+(?<uom>[A-Za-z]{1,6})?\s*(?<unit>\$?\d[\d,]*(?:\.\d{2,4})?)$",
                RegexOptions.IgnoreCase);

            if (m2.Success)
            {
                var name = m2.Groups["name"].Value.Trim();
                var qty = MoneyOrZero(m2.Groups["qty"].Value);
                var unit = MoneyOrZero(m2.Groups["unit"].Value);
                if (!string.IsNullOrWhiteSpace(name) && qty > 0 && unit > 0)
                {
                    parsed.Add(new PurchaseInvoiceLine { ProductName = name, Quantity = qty, UnitCost = unit });
                    continue;
                }
            }

            // Fallback: allow extra tokens between Qty and the final unit cost
            var m2b = Regex.Match(line,
                @"^(?<name>.+?)\s+(?<qty>\d+(?:\.\d+)?)\s+.*?(?<unit>\$?\d[\d,]*(?:\.\d{2,4})?)$",
                RegexOptions.IgnoreCase);

            if (m2b.Success)
            {
                var name = m2b.Groups["name"].Value.Trim();
                var qty = MoneyOrZero(m2b.Groups["qty"].Value);
                var unit = MoneyOrZero(m2b.Groups["unit"].Value);
                if (!string.IsNullOrWhiteSpace(name) && qty > 0 && unit > 0)
                {
                    parsed.Add(new PurchaseInvoiceLine { ProductName = name, Quantity = qty, UnitCost = unit });
                    continue;
                }
            }

        }

        // De-dup by (name, unit)
        return parsed
            .GroupBy(x => (NormalizeKey(x.ProductName), x.UnitCost))
            .Select(g => new PurchaseInvoiceLine { ProductName = g.First().ProductName, Quantity = g.Sum(x => x.Quantity), UnitCost = g.First().UnitCost })
            .ToList();
    }

    private static (List<string> rawByPage, List<List<string>> linesByPage) ExtractPdfByPage(string filePath)
    {
        var rawByPage = new List<string>();
        var linesByPage = new List<List<string>>();

        using var doc = UglyToad.PdfPig.PdfDocument.Open(filePath);
        foreach (var page in doc.GetPages())
        {
            rawByPage.Add(page.Text ?? "");
            linesByPage.Add(ReconstructLines(page));
        }

        return (rawByPage, linesByPage);
    }

    private static List<string> ReconstructLines(UglyToad.PdfPig.Content.Page page)
    {
        // Attempt to reconstruct lines by reading words with positions.
        var lines = new List<string>();
        var words = page.GetWords()
            .Select(w => new { w.Text, X = w.BoundingBox.Left, Y = w.BoundingBox.Bottom })
            .OrderByDescending(w => w.Y)
            .ThenBy(w => w.X)
            .ToList();

        const double tol = 2.0;
        var current = new List<(string text, double x)>();
        double? currentY = null;

        void flush()
        {
            if (current.Count == 0) return;
            var s = string.Join(" ", current.OrderBy(t => t.x).Select(t => t.text));
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(s)) lines.Add(s);
            current.Clear();
        }

        foreach (var w in words)
        {
            if (currentY is null)
            {
                currentY = w.Y;
                current.Add((w.Text, w.X));
                continue;
            }

            if (Math.Abs(w.Y - currentY.Value) <= tol)
            {
                current.Add((w.Text, w.X));
            }
            else
            {
                flush();
                currentY = w.Y;
                current.Add((w.Text, w.X));
            }
        }
        flush();
        return lines;
    }

    // Legacy helpers (keep existing callers working)
    private static string ExtractPdfText(string filePath)
        => string.Join("\n\n", ExtractPdfByPage(filePath).rawByPage);

    private static List<string> ExtractPdfLines(string filePath)
        => ExtractPdfByPage(filePath).linesByPage.SelectMany(x => x).ToList();

    private static string? GuessVendorName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 2)
            .Take(12)
            .ToList();

        foreach (var l in lines)
        {
            var s = l.Trim();
            var lower = s.ToLowerInvariant();
            if (lower.Contains("invoice") || lower.Contains("bill to") || lower.Contains("ship to"))
                continue;
            if (Regex.IsMatch(s, @"^\d")) continue; // skip address line beginning with number
            if (s.Length > 4)
                return s;
        }
        return lines.FirstOrDefault();
    }

    private static string? GuessInvoiceNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var patterns = new[]
        {
            @"Invoice\s*(?:No\.|Number|#)?\s*[:#\-]?\s*([A-Z0-9\-]{4,})",
            @"Inv\s*(?:No\.|#)?\s*[:#\-]?\s*([A-Z0-9\-]{4,})"
        };

        foreach (var p in patterns)
        {
            var m = Regex.Match(text, p, RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    private static DateOnly? GuessInvoiceDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Prefer labeled date
        var m = Regex.Match(text, @"\bDate\b\s*[:\-]?\s*([0-9]{1,2}[\/\-][0-9]{1,2}[\/\-][0-9]{2,4})", RegexOptions.IgnoreCase);
        if (m.Success && TryParseDate(m.Groups[1].Value, out var dt1)) return dt1;

        // Month name date
        var m2 = Regex.Match(text, @"([A-Za-z]{3,9}\s+[0-9]{1,2},\s*[0-9]{4})");
        if (m2.Success && TryParseDate(m2.Groups[1].Value, out var dt2)) return dt2;

        // Any mm/dd/yyyy in the first 40 lines
        var first = string.Join("\n", text.Split('\n').Take(40));
        var m3 = Regex.Match(first, @"([0-9]{1,2}[\/\-][0-9]{1,2}[\/\-][0-9]{2,4})");
        if (m3.Success && TryParseDate(m3.Groups[1].Value, out var dt3)) return dt3;

        return null;
    }

    private static decimal? GuessTotal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
            .ToList();

        // Totals are typically near the bottom of invoices; prefer the bottom section.
        var bottom = lines.Skip(Math.Max(0, lines.Count - 90)).ToList();

        // Priority patterns (most reliable first)
        var prio1 = new[] { "grand total", "invoice total", "total amount", "amount due", "total due", "balance due" };

        decimal? best = null;
        int bestPrio = int.MaxValue;

        foreach (var l in bottom)
        {
            var low = l.ToLowerInvariant();
            int prio = prio1.Any(x => low.Contains(x)) ? 1 : (low.Contains("total") ? 2 : int.MaxValue);
            if (prio == int.MaxValue) continue;

            // Extract the last currency-like number from the line.
            var m = Regex.Matches(l, @"\$?\s*([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]{2})?)");
            if (m.Count == 0) continue;
            var val = MoneyOrZero(m[^1].Groups[1].Value);

            // Guardrails: avoid obviously-wrong tiny totals when "total" appears in line items.
            if (val <= 0) continue;
            if (prio == 2 && val < 1m) continue;

            if (prio < bestPrio || (prio == bestPrio && (best is null || val >= best.Value)))
            {
                bestPrio = prio;
                best = val;
            }
        }

        return best;
    }

    private static bool TryParseDate(string s, out DateOnly date)
    {
        date = default;
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }
        return false;
    }

    private static bool TryParseDecimal(string? s, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Replace("$", "").Replace(",", "").Trim();
        return decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
               || decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out value);
    }

    private static decimal? MoneyOrNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Replace("$", "").Replace(",", "").Trim();
        if (decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)) return d;
        if (decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out d)) return d;
        return null;
    }

    private static decimal MoneyOrZero(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        s = s.Replace("$", "").Replace(",", "").Trim();
        if (decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)) return d;
        if (decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out d)) return d;
        return 0m;
    }

    private static string NormalizeKey(string? name)
    {
        var s = (name ?? "").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return s.ToUpperInvariant();
    }

    private static string[] SplitCsv(string line)
    {
        // Minimal CSV split: handles quoted commas.
        var cols = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (ch == ',' && !inQuotes)
            {
                cols.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        cols.Add(sb.ToString());
        return cols.ToArray();
    }

    private static int FindCol(string[] header, params string[] contains)
    {
        for (int i = 0; i < header.Length; i++)
        {
            var h = header[i].Trim().ToLowerInvariant();
            if (contains.Any(k => h.Contains(k))) return i;
        }
        return -1;
    }

    // ===========================
    // Vendor Routing Helpers
    // ===========================
    private const string VendorKeyAK = "AK";
    private const string VendorKeyAmerican = "AMERICAN";
    private const string VendorKeyHS = "HS";
    private const string VendorKeySkygate = "SKYGATE";
    private const string VendorKey1Oak = "1OAK";
    private const string VendorKeyDemandVape = "DEMANDVAPE";
    private const string VendorKeySafa = "SAFA";
    private const string VendorKeyTriState = "TRISTATE";

    private static string NormalizeVendorKey(string? vendor)
    {
        var s = (vendor ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.ToUpperInvariant();
        s = Regex.Replace(s, @"[^A-Z0-9]+", "");

        // Common variants from your vendor list / dropdown
        if (s.Contains("AK") && s.Contains("WHOLE")) return VendorKeyAK;
        if (s.Contains("AMERICAN") && (s.Contains("DISTR") || s.Contains("DIST"))) return VendorKeyAmerican;
        if (s.Contains("HS") && s.Contains("WHOLE")) return VendorKeyHS;
        if (s.Contains("SKYGATE")) return VendorKeySkygate;
        if (s.Contains("1OAK") || s.Contains("ONEOAK") || s.Contains("1OAKWHOLESALE")) return VendorKey1Oak;
        if (s.Contains("DEMAND") && s.Contains("VAPE")) return VendorKeyDemandVape;
        if (s.Contains("SAFA")) return VendorKeySafa;
        if (s.Contains("TRISTATE") || (s.Contains("TRI") && s.Contains("STATE"))) return VendorKeyTriState;

        return "";
    }

    private static decimal ComputeUnitCost(decimal? price, decimal? amount, decimal qty)
    {
        if (price.HasValue) return price.Value;
        if (amount.HasValue && qty > 0m) return Math.Round(amount.Value / qty, 4);
        return 0m;
    }

    // ===========================
    // SKYGATE WHOLESALE
    // ===========================
    private static ImportedInvoice ParseSkygateWholesaleInvoice(string raw, List<string> lines)
    {
        var vendor = "SKYGATE WHOLESALE";

        var mInv = Regex.Match(
            string.Join("\n", lines.Take(15)),
            @"\b(S\d{5,})\b",
            RegexOptions.IgnoreCase);
        var invNo = mInv.Success ? mInv.Groups[1].Value.Trim() : "";

        DateOnly? invDate = null;
        var mDate = Regex.Match(raw, @"\bDATE\b\s*([0-9]{1,2}/[0-9]{1,2}/[0-9]{4})", RegexOptions.IgnoreCase);
        if (mDate.Success && TryParseDate(mDate.Groups[1].Value, out var d)) invDate = d;
        invDate ??= GuessInvoiceDate(raw);

        decimal? total = null;
        var balanceMatch = Regex.Match(
            raw,
            @"(?m)^\s*Balance\s+\$?\s*([0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2})\s*$",
            RegexOptions.IgnoreCase);
        if (balanceMatch.Success)
            total = MoneyOrNull(balanceMatch.Groups[1].Value);
        total ??= FindLabeledMoney(lines, "balance", "total");
        total ??= GuessTotal(raw);

        var items = new List<PurchaseInvoiceLine>();

        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var up = lines[i].ToUpperInvariant();
            if (up.Contains("ITEM#") && up.Contains("ORD") && up.Contains("SHIP") && up.Contains("DESCRIPTION") && up.Contains("AMOUNT"))
            {
                start = i + 1;
                break;
            }
        }

        if (start >= 0)
        {
            for (int i = start; i < lines.Count; i++)
            {
                var line = lines[i];
                var up = line.ToUpperInvariant();
                if (up.Contains("SUB-TOTAL") || up.Contains("SUBTOTAL") || up.Contains("BALANCE") || up.StartsWith("TOTAL"))
                    break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var m = Regex.Match(line,
                    @"^\s*(\d+)\s+([A-Z0-9\-\[\]]{2,})\s+(\d+(?:\.\d+)?)\s+(\d+(?:\.\d+)?)\s+([A-Z0-9]+)\s+(.+?)\s+\$?([0-9.,]+)\s+\$?([0-9.,]+)\s+\$?([0-9.,]+)\s*$");
                if (!m.Success) continue;

                _ = TryParseDecimal(m.Groups[3].Value, out var ord);
                _ = TryParseDecimal(m.Groups[4].Value, out var ship);

                var itemCode = m.Groups[2].Value.Trim();
                var desc = Regex.Replace(m.Groups[6].Value.Trim(), @"\s+", " ");
                var tax = MoneyOrNull(m.Groups[7].Value);
                var price = MoneyOrNull(m.Groups[8].Value);
                var amount = MoneyOrNull(m.Groups[9].Value);

                var qty = ship > 0m ? ship : (ord > 0m ? ord : 0m);
                var unitCost = ComputeUnitCost(price, amount, qty);

                items.Add(new PurchaseInvoiceLine
                {
                    ItemCode = itemCode,
                    ProductName = desc,
                    OrdQuantity = ord,
                    ShipQuantity = ship,
                    VolumeMl = null,
                    Tax = tax,
                    Price = price,
                    Amount = amount,
                    Quantity = qty,
                    UnitCost = unitCost
                });
            }
        }

        return new ImportedInvoice
        {
            VendorName = vendor,
            InvoiceNumber = invNo,
            InvoiceDate = invDate,
            Total = total,
            Lines = items
        };
    }

    // ===========================
    // DEMANDVAPE
    // ===========================
    private static ImportedInvoice ParseDemandVapeInvoice(string raw, List<string> lines)
    {
        var invoiceNumberMatch = Regex.Match(
            raw,
            @"\bInvoice\s+Number\s*:\s*([A-Z0-9\-]+)",
            RegexOptions.IgnoreCase);
        var invoiceNumber = invoiceNumberMatch.Success
            ? invoiceNumberMatch.Groups[1].Value.Trim()
            : "";

        DateOnly? invoiceDate = null;
        var invoiceDateMatch = Regex.Match(
            raw,
            @"\bInvoice\s+Date\s*:\s*(\d{1,2}/\d{1,2}/\d{4})",
            RegexOptions.IgnoreCase);
        if (invoiceDateMatch.Success && TryParseDate(invoiceDateMatch.Groups[1].Value, out var parsedDate))
            invoiceDate = parsedDate;
        invoiceDate ??= GuessInvoiceDate(raw);

        var subtotal = FindLabeledMoney(lines, "subtotal");
        var freight = FindLabeledMoney(lines, "freight");
        var tax = FindLabeledMoney(lines, "illinois 45% tax");
        var payment = FindLabeledMoney(lines, "payments");

        decimal? total = null;
        if (subtotal is > 0m)
            total = subtotal.Value + (freight ?? 0m) + (tax ?? 0m);
        else if (payment is > 0m)
            total = payment;

        var normalized = lines
            .Select(line => Regex.Replace((line ?? "").Trim(), @"\s+", " "))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var items = new List<PurchaseInvoiceLine>();
        var rowPattern = new Regex(
            @"^(?<line>\d{4})\s+" +
            @"(?<ordered>\d+(?:\.\d+)?)\s+" +
            @"(?<shipped>\d+(?:\.\d+)?)\s+" +
            @"(?<sku>[A-Z0-9\-]{4,})\s+" +
            @"(?<description>.+?)\s+" +
            @"(?<price>\d[\d,]*\.\d{2})\s+" +
            @"(?<extension>\d[\d,]*\.\d{2})$",
            RegexOptions.IgnoreCase);

        for (var i = 0; i < normalized.Count; i++)
        {
            var match = rowPattern.Match(normalized[i]);
            if (!match.Success)
                continue;

            _ = TryParseDecimal(match.Groups["ordered"].Value, out var ordered);
            _ = TryParseDecimal(match.Groups["shipped"].Value, out var shipped);
            var price = MoneyOrNull(match.Groups["price"].Value);
            var extension = MoneyOrNull(match.Groups["extension"].Value);
            var description = new StringBuilder(match.Groups["description"].Value.Trim());

            var next = i + 1;
            while (next < normalized.Count)
            {
                var continuation = normalized[next];
                var upper = continuation.ToUpperInvariant();

                if (rowPattern.IsMatch(continuation)
                    || upper.StartsWith("SUBTOTAL")
                    || upper.StartsWith("FREIGHT")
                    || upper.Contains("ILLINOIS 45% TAX")
                    || upper.StartsWith("PAYMENTS")
                    || upper.StartsWith("TOTAL USD")
                    || upper.StartsWith("ALL SALES")
                    || upper.StartsWith("NO RETURNS")
                    || upper.StartsWith("THE SALE")
                    || upper.StartsWith("PAGE:")
                    || upper == "INVOICE"
                    || upper.Contains("LINE ORDER SHIP ITEM"))
                {
                    break;
                }

                if (!upper.StartsWith("**SINGLE")
                    && !Regex.IsMatch(continuation, @"^\d+$")
                    && !upper.StartsWith("CONTINUED ON NEXT PAGE"))
                {
                    description.Append(' ');
                    description.Append(continuation);
                }

                next++;
            }

            var quantity = shipped > 0m ? shipped : ordered;
            var unitCost = ComputeUnitCost(price, extension, quantity);
            items.Add(new PurchaseInvoiceLine
            {
                ItemCode = match.Groups["sku"].Value.Trim(),
                ProductName = Regex.Replace(description.ToString(), @"\s+", " ").Trim(),
                OrdQuantity = ordered,
                ShipQuantity = shipped,
                Quantity = quantity,
                Price = price,
                Amount = extension,
                UnitCost = unitCost
            });

            i = next - 1;
        }

        return new ImportedInvoice
        {
            VendorName = "DemandVape",
            InvoiceNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            Total = total,
            Lines = items
        };
    }

    // ===========================
    // 1 OAK WHOLESALE
    // ===========================
    private static ImportedInvoice Parse1OakWholesaleInvoice(string raw, List<string> lines)
    {
        var vendor = "1 OAK WHOLESALE";

        var mInv = Regex.Match(raw, @"Invoice\s*:\s*#?\s*([0-9]{3,})", RegexOptions.IgnoreCase);
        var invNo = mInv.Success ? mInv.Groups[1].Value.Trim() : "";

        DateOnly? invDate = null;
        var mDate = Regex.Match(raw, @"\bDate\s*:\s*([A-Za-z]{3,9}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4}|[A-Za-z]{3}\s+[0-9]{1,2},\s*[0-9]{4})", RegexOptions.IgnoreCase);
        if (mDate.Success && TryParseDate(mDate.Groups[1].Value, out var d)) invDate = d;
        invDate ??= GuessInvoiceDate(raw);

        decimal? total = null;
        var grandTotalMatch = Regex.Match(
            raw,
            @"Grand\s+Total\s+\(Incl\.?\s*Tax\)\s+\$?\s*([0-9]{1,3}(?:,[0-9]{3})*\.[0-9]{2})",
            RegexOptions.IgnoreCase);
        if (grandTotalMatch.Success)
            total = MoneyOrNull(grandTotalMatch.Groups[1].Value);
        total ??= FindLabeledMoney(lines, "grand total", "amount", "balance", "total");
        total ??= GuessTotal(raw);

        var items = new List<PurchaseInvoiceLine>();

        // Locate "Items" table
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var up = lines[i].ToUpperInvariant();
            if (up.StartsWith("ITEMS") && up.Contains("QTY") && up.Contains("PRICE") && up.Contains("SUBTOTAL"))
            {
                start = i + 1;
                break;
            }
        }

        string pendingDesc = "";
        string pendingSku = "";

        for (int i = (start >= 0 ? start : 0); i < lines.Count; i++)
        {
            var l = lines[i];
            var up = l.ToUpperInvariant();
            if (up.Contains("PAGE") && up.Contains("OF")) continue;
            if (up.StartsWith("TOTAL") || up.Contains("SUBTOTAL") || up.Contains("BALANCE"))
                break;
            if (string.IsNullOrWhiteSpace(l)) continue;

            // SKU line
            if (up.TrimStart().StartsWith("SKU:"))
            {
                pendingSku = l.Split(':', 2)[1].Trim();
                continue;
            }

            // Full item line with qty/price/subtotal
            var m = Regex.Match(l, @"^\s*(?<desc>.+?)\s+(?<qty>\d+)\s+\$(?<price>[0-9.,]+)\s+\$(?<sub>[0-9.,]+)\s*$");
            if (m.Success)
            {
                pendingDesc = Regex.Replace(m.Groups["desc"].Value.Trim(), @"\s+", " ");
                var qty = decimal.TryParse(m.Groups["qty"].Value, out var q) ? q : 0m;
                var price = MoneyOrNull(m.Groups["price"].Value);
                var amount = MoneyOrNull(m.Groups["sub"].Value);
                var unitCost = ComputeUnitCost(price, amount, qty);

                // Create line now; SKU may appear next line. We'll patch ItemCode later if found.
                items.Add(new PurchaseInvoiceLine
                {
                    ItemCode = "",
                    ProductName = pendingDesc,
                    OrdQuantity = qty,
                    ShipQuantity = qty,
                    VolumeMl = null,
                    Tax = null,
                    Price = price,
                    Amount = amount,
                    Quantity = qty,
                    UnitCost = unitCost
                });

                pendingDesc = "";
                pendingSku = "";
                continue;
            }

            // Sometimes the description wraps, and the qty/price line comes later as just: "1  $6.98  $6.98"
            var m2 = Regex.Match(l, @"^\s*(?<qty>\d+)\s+\$(?<price>[0-9.,]+)\s+\$(?<sub>[0-9.,]+)\s*$");
            if (m2.Success && items.Count > 0)
            {
                var qty = decimal.TryParse(m2.Groups["qty"].Value, out var q) ? q : 0m;
                var price = MoneyOrNull(m2.Groups["price"].Value);
                var amount = MoneyOrNull(m2.Groups["sub"].Value);
                var unitCost = ComputeUnitCost(price, amount, qty);

                var last = items[^1];
                // If the last line seems incomplete (0 qty), fill it.
                if (last.Quantity <= 0m)
                {
                    last.OrdQuantity = qty;
                    last.ShipQuantity = qty;
                    last.Quantity = qty;
                    last.Price = price;
                    last.Amount = amount;
                    last.UnitCost = unitCost;
                }
                continue;
            }

            // Otherwise treat as description continuation if we're within the items section.
            if (start >= 0 && !up.StartsWith("BILLING") && !up.StartsWith("SHIPPING") && !up.StartsWith("PAYMENT"))
            {
                // Append to previous item's description if it doesn't look like a header.
                if (items.Count > 0 && !up.Contains("BILLING ADDRESS") && !up.Contains("SHIPPING ADDRESS"))
                {
                    var last = items[^1];
                    if (!string.IsNullOrWhiteSpace(last.ProductName) && !last.ProductName.EndsWith(l.Trim(), StringComparison.OrdinalIgnoreCase))
                        last.ProductName = (last.ProductName + " " + l.Trim()).Trim();
                }
            }

            // Apply SKU to last item if we have it.
            if (!string.IsNullOrWhiteSpace(pendingSku) && items.Count > 0)
            {
                var last = items[^1];
                if (string.IsNullOrWhiteSpace(last.ItemCode))
                    last.ItemCode = pendingSku;
                pendingSku = "";
            }
        }

        return new ImportedInvoice
        {
            VendorName = vendor,
            InvoiceNumber = invNo,
            InvoiceDate = invDate,
            Total = total,
            Lines = items
        };
    }

    // ===========================
    // SAFA GOODS (SalesGent template)
    // ===========================
    private static ImportedInvoice ParseSafaGoodsInvoice(string raw, List<string> lines)
    {
        var vendor = "SAFA GOODS";

        var mInv = Regex.Match(raw, @"\bSALES\s*ORDER\s*:?\s*\n?\s*([0-9]{3,})", RegexOptions.IgnoreCase);
        var invNo = mInv.Success ? mInv.Groups[1].Value.Trim() : GuessInvoiceNumber(raw) ?? "";

        var invDate = GuessInvoiceDate(raw);
        var total = FindLabeledMoney(lines, "grand total", "total", "balance");
        total ??= GuessTotal(raw);

        var items = new List<PurchaseInvoiceLine>();

        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var up = lines[i].ToUpperInvariant();
            if (up.Contains("UPC") && up.Contains("PRODUCT NAME") && up.Contains("AMOUNT"))
            {
                start = i + 1;
                break;
            }
        }

        string curUpc = "";
        string curDesc = "";
        decimal so = 0m, io = 0m, outQty = 0m;
        bool haveHeader = false;

        for (int i = (start >= 0 ? start : 0); i < lines.Count; i++)
        {
            var l = lines[i];
            var up = l.ToUpperInvariant();
            if (up.StartsWith("PAGE") && up.Contains("OF")) continue;
            if (up.Contains("SUBTOTAL") || up.Contains("GRAND TOTAL") || up.Contains("BALANCE"))
                break;
            if (string.IsNullOrWhiteSpace(l)) continue;

            // New item line: "1 6978... Description ... 2 0 2"
            var m = Regex.Match(l, @"^\s*(\d+)\s+([0-9]{8,14})\s+(.+?)\s+(\d+)\s+(\d+)\s+(\d+)\s*$");
            if (m.Success)
            {
                // Flush previous item if we already parsed money
                curUpc = m.Groups[2].Value.Trim();
                curDesc = Regex.Replace(m.Groups[3].Value.Trim(), @"\s+", " ");
                _ = TryParseDecimal(m.Groups[4].Value, out so);
                _ = TryParseDecimal(m.Groups[5].Value, out io);
                _ = TryParseDecimal(m.Groups[6].Value, out outQty);
                haveHeader = true;
                continue;
            }

            if (!haveHeader) continue;

            // Continuation line for description (indented)
            if (!l.TrimStart().StartsWith("$") && !Regex.IsMatch(l, @"\$[0-9]") && !Regex.IsMatch(l.Trim(), @"^[0-9]{3,}$"))
            {
                // If it looks like a flavor line, append
                if (l.StartsWith(" ") || l.StartsWith("\t"))
                {
                    var add = l.Trim();
                    if (!string.IsNullOrWhiteSpace(add))
                        curDesc = (curDesc + " " + add).Trim();
                }
                continue;
            }

            // Money line: often has $Price  $Tax  $Amount
            var money = Regex.Matches(l, @"\$\s*([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]{2})?)");
            if (money.Count >= 2)
            {
                var price = MoneyOrNull(money[0].Groups[1].Value);
                var tax = MoneyOrNull(money[1].Groups[1].Value);
                decimal? amount = null;
                if (money.Count >= 3) amount = MoneyOrNull(money[2].Groups[1].Value);
                else amount = (price.HasValue ? price.Value * outQty : (decimal?)null);

                var qty = outQty > 0m ? outQty : so;
                var unitCost = ComputeUnitCost(price, amount, qty);

                items.Add(new PurchaseInvoiceLine
                {
                    ItemCode = curUpc,
                    ProductName = curDesc,
                    OrdQuantity = so,
                    ShipQuantity = outQty,
                    VolumeMl = null,
                    Tax = tax,
                    Price = price,
                    Amount = amount,
                    Quantity = qty,
                    UnitCost = unitCost
                });

                // reset
                curUpc = "";
                curDesc = "";
                so = io = outQty = 0m;
                haveHeader = false;
            }
        }

        return new ImportedInvoice
        {
            VendorName = vendor,
            InvoiceNumber = invNo,
            InvoiceDate = invDate,
            Total = total,
            Lines = items
        };
    }

    // ===========================
    // TRI STATE DISTRO (SalesGent template)
    // ===========================
    private static ImportedInvoice ParseTriStateDistroInvoice(string raw, List<string> lines)
    {
        var vendor = "TRI STATE DISTRO";

        var mInv = Regex.Match(raw, @"\bSALES\s*ORDER\s*:?\s*\n?\s*([0-9]{3,})", RegexOptions.IgnoreCase);
        var invNo = mInv.Success ? mInv.Groups[1].Value.Trim() : GuessInvoiceNumber(raw) ?? "";

        var invDate = GuessInvoiceDate(raw);
        var total = FindLabeledMoney(lines, "grand total", "total", "balance");
        total ??= GuessTotal(raw);

        var items = new List<PurchaseInvoiceLine>();

        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var up = lines[i].ToUpperInvariant();
            if ((up.Contains("SKU") || up.Contains("UPC")) && up.Contains("PRODUCT NAME") && up.Contains("SO") && up.Contains("OUT") && up.Contains("AMOUNT"))
            {
                start = i + 1;
                break;
            }
        }

        PurchaseInvoiceLine? current = null;

        for (int i = (start >= 0 ? start : 0); i < lines.Count; i++)
        {
            var l = lines[i];
            var up = l.ToUpperInvariant();
            if (up.StartsWith("PAGE") && up.Contains("OF")) continue;
            if (up.Contains("SUBTOTAL") || up.Contains("GRAND TOTAL") || up.Contains("BALANCE"))
                break;
            if (string.IsNullOrWhiteSpace(l)) continue;

            // Main line:
            // 1  316439416313  DESCRIPTION ...  1 0 1  $58.00  $58.00  $0.00  $58.00
            var m = Regex.Match(l,
                @"^\s*(\d+)\s+([^\s]+)\s+(.+?)\s+(\d+)\s+(\d+)\s+(\d+)\s+\$?([0-9.,]+)\s+\$?([0-9.,]+)\s+\$?([0-9.,]+)\s+\$?([0-9.,]+)\s*$");
            if (m.Success)
            {
                var sku = m.Groups[2].Value.Trim();
                var desc = Regex.Replace(m.Groups[3].Value.Trim(), @"\s+", " ");
                _ = TryParseDecimal(m.Groups[4].Value, out var so);
                _ = TryParseDecimal(m.Groups[5].Value, out var io);
                _ = TryParseDecimal(m.Groups[6].Value, out var outQty);
                var price = MoneyOrNull(m.Groups[7].Value);
                var soldPrice = MoneyOrNull(m.Groups[8].Value);
                var tax = MoneyOrNull(m.Groups[9].Value);
                var amount = MoneyOrNull(m.Groups[10].Value);

                var qty = outQty > 0m ? outQty : so;
                var unitCost = ComputeUnitCost(soldPrice ?? price, amount, qty);

                current = new PurchaseInvoiceLine
                {
                    ItemCode = sku,
                    ProductName = desc,
                    OrdQuantity = so,
                    ShipQuantity = outQty,
                    VolumeMl = null,
                    Tax = tax,
                    Price = soldPrice ?? price,
                    Amount = amount,
                    Quantity = qty,
                    UnitCost = unitCost
                };
                items.Add(current);
                continue;
            }

            // Continuation description line (indented)
            if (current is not null && (l.StartsWith(" ") || l.StartsWith("\t")))
            {
                var add = l.Trim();
                if (!string.IsNullOrWhiteSpace(add))
                    current.ProductName = (current.ProductName + " " + add).Trim();
            }
        }

        return new ImportedInvoice
        {
            VendorName = vendor,
            InvoiceNumber = invNo,
            InvoiceDate = invDate,
            Total = total,
            Lines = items
        };
    }

    // ===========================
    // ENHANCED GENERIC PARSERS
    // ===========================
    
    /// <summary>
    /// Enhanced generic line item parser that looks for common column headers
    /// and extracts data more flexibly to handle different vendor formats.
    /// Specifically looks for: SKU/UPC, Description, Price, Amount, Ship, Tax, IL Tax
    /// </summary>
    private static List<PurchaseInvoiceLine> ParseLineItemsEnhanced(List<string> lines)
    {
        var items = new List<PurchaseInvoiceLine>();
        
        // Step 1: Find the header row by looking for common column headers
        int headerIndex = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].ToUpperInvariant();
            
            // Look for common column headers
            bool hasProductColumn = line.Contains("PRODUCT") || 
                                    line.Contains("DESCRIPTION") || 
                                    line.Contains("ITEM") ||
                                    line.Contains("DESC");
            
            bool hasCodeColumn = line.Contains("SKU") || 
                                line.Contains("UPC") || 
                                line.Contains("CODE") ||
                                line.Contains("ITEM #") ||
                                line.Contains("ITEM#");
            
            bool hasAmountColumn = line.Contains("AMOUNT") || 
                                  line.Contains("TOTAL") || 
                                  line.Contains("COST") ||
                                  line.Contains("FINAL COST") ||
                                  line.Contains("TOTAL COST");
            
            // If we find at least product and amount columns, we likely found the header
            if (hasProductColumn && (hasCodeColumn || hasAmountColumn))
            {
                headerIndex = i;
                break;
            }
        }
        
        if (headerIndex == -1)
        {
            // No clear header found, try fallback parsing
            return ParseLineItemsFallback(lines);
        }
        
        // Step 2: Parse data rows
        PurchaseInvoiceLine? currentItem = null;
        bool inItemSection = false;
        
        for (int i = headerIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            
            var upper = line.ToUpperInvariant();
            
            // Stop at footer indicators
            if (upper.Contains("SUBTOTAL") || 
                upper.Contains("GRAND TOTAL") || 
                upper.Contains("BALANCE DUE") ||
                upper.Contains("TOTAL DUE") ||
                upper.Contains("AMOUNT DUE") ||
                upper.Contains("THANK YOU") ||
                upper.Contains("PAGE") && upper.Contains("OF"))
            {
                break;
            }
            
            // Check if this is a new item line (has UPC/SKU - typically 8-14 digits)
            var upcMatch = Regex.Match(line, @"\b(\d{8,14})\b");
            
            if (upcMatch.Success)
            {
                // Save previous item if exists
                if (currentItem != null && !string.IsNullOrWhiteSpace(currentItem.ItemCode))
                {
                    items.Add(currentItem);
                }
                
                // Start new item
                currentItem = new PurchaseInvoiceLine
                {
                    ItemCode = upcMatch.Value
                };
                
                inItemSection = true;
                
                // Try to extract description from same line
                var descPattern = upcMatch.Value + @"\s+(.+?)(?:\s+\d+\s+\d+|\s+\$|\s*$)";
                var descMatch = Regex.Match(line, descPattern);
                if (descMatch.Success && descMatch.Groups.Count > 1)
                {
                    var desc = descMatch.Groups[1].Value.Trim();
                    desc = Regex.Replace(desc, @"\s+\d+\s*$", "");
                    currentItem.ProductName = desc;
                }
                
                // Try to extract quantities from the same line
                var qtyMatches = Regex.Matches(line, @"\b(\d+)\b");
                if (qtyMatches.Count >= 3)
                {
                    var qtyList = qtyMatches.Cast<Match>()
                        .Select(m => m.Value)
                        .Where(v => decimal.TryParse(v, out _))
                        .ToList();
                    
                    if (qtyList.Count >= 3)
                    {
                        int startIdx = qtyList.Count - 3;
                        TryParseDecimal(qtyList[startIdx], out var ordQty);
                        TryParseDecimal(qtyList[startIdx + 2], out var shipQty);
                        
                        currentItem.OrdQuantity = ordQty;
                        currentItem.ShipQuantity = shipQty;
                    }
                }
            }
            else if (currentItem != null && inItemSection)
            {
                // This might be a continuation of description or a money line
                var hasMoney = line.Contains("$") || Regex.IsMatch(line, @"\d+\.\d{2}");
                
                if (!hasMoney && !Regex.IsMatch(line, @"^\s*\d+\s*$"))
                {
                    // Likely description continuation
                    var cleanLine = line.Trim();
                    if (!string.IsNullOrWhiteSpace(cleanLine) && 
                        cleanLine.Length > 2 && 
                        !cleanLine.All(char.IsDigit))
                    {
                        currentItem.ProductName = (currentItem.ProductName + " " + cleanLine).Trim();
                    }
                }
            }
            
            // Extract money values from line
            if (currentItem != null)
            {
                var moneyMatches = Regex.Matches(line, @"\$\s*([0-9]{1,3}(?:,[0-9]{3})*(?:\.[0-9]{2})?)");
                
                if (moneyMatches.Count > 0)
                {
                    if (moneyMatches.Count >= 3)
                    {
                        currentItem.Price = ParseMoneyValue(moneyMatches[0].Groups[1].Value);
                        currentItem.Tax = ParseMoneyValue(moneyMatches[1].Groups[1].Value);
                        currentItem.Amount = ParseMoneyValue(moneyMatches[2].Groups[1].Value);
                    }
                    else if (moneyMatches.Count == 2)
                    {
                        currentItem.Price = ParseMoneyValue(moneyMatches[0].Groups[1].Value);
                        currentItem.Amount = ParseMoneyValue(moneyMatches[1].Groups[1].Value);
                    }
                    else if (moneyMatches.Count == 1)
                    {
                        if (!currentItem.Amount.HasValue)
                            currentItem.Amount = ParseMoneyValue(moneyMatches[0].Groups[1].Value);
                        else if (!currentItem.Price.HasValue)
                            currentItem.Price = ParseMoneyValue(moneyMatches[0].Groups[1].Value);
                    }
                }
                
                // Look for shipping quantity if not found yet
                if (currentItem.ShipQuantity == 0)
                {
                    var shipMatch = Regex.Match(line, @"\b(?:SHIP|OUT)\s*:?\s*(\d+)\b", RegexOptions.IgnoreCase);
                    if (shipMatch.Success)
                    {
                        if (TryParseDecimal(shipMatch.Groups[1].Value, out var shipQty))
                            currentItem.ShipQuantity = shipQty;
                    }
                }
                
                // Look for ordered quantity if not found yet
                if (currentItem.OrdQuantity == 0)
                {
                    var ordMatch = Regex.Match(line, @"\b(?:ORD|SO|ORDERED)\s*:?\s*(\d+)\b", RegexOptions.IgnoreCase);
                    if (ordMatch.Success)
                    {
                        if (TryParseDecimal(ordMatch.Groups[1].Value, out var ordQty))
                            currentItem.OrdQuantity = ordQty;
                    }
                }
                
                // Calculate unit cost and quantity if we have amounts
                if (currentItem.Amount.HasValue)
                {
                    var qty = currentItem.ShipQuantity > 0 ? currentItem.ShipQuantity : 
                             currentItem.OrdQuantity > 0 ? currentItem.OrdQuantity : 1m;
                    
                    currentItem.Quantity = qty;
                    
                    if (qty > 0)
                    {
                        currentItem.UnitCost = currentItem.Amount.Value / qty;
                    }
                    
                    if (!currentItem.Price.HasValue && qty > 0)
                    {
                        currentItem.Price = currentItem.Amount.Value / qty;
                    }
                }
            }
        }
        
        // Add last item
        if (currentItem != null && !string.IsNullOrWhiteSpace(currentItem.ItemCode))
        {
            items.Add(currentItem);
        }
        
        return items;
    }

    /// <summary>
    /// Fallback parser when no clear header is found
    /// </summary>
    private static List<PurchaseInvoiceLine> ParseLineItemsFallback(List<string> lines)
    {
        var items = new List<PurchaseInvoiceLine>();
        PurchaseInvoiceLine? current = null;
        
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            var upper = line.ToUpperInvariant();
            if (upper.Contains("TOTAL") || upper.Contains("BALANCE") || upper.Contains("THANK YOU"))
                break;
            
            // Look for UPC/SKU pattern
            var upcMatch = Regex.Match(line, @"\b(\d{8,14})\b");
            if (upcMatch.Success)
            {
                if (current != null)
                    items.Add(current);
                
                current = new PurchaseInvoiceLine { ItemCode = upcMatch.Value };
                
                // Try to get description
                var parts = line.Split(new[] { upcMatch.Value }, StringSplitOptions.None);
                if (parts.Length > 1)
                {
                    var desc = Regex.Replace(parts[1], @"\$.*|\d+\s*$", "").Trim();
                    current.ProductName = desc;
                }
            }
            else if (current != null && line.Contains("$"))
            {
                // Parse money
                var money = Regex.Matches(line, @"\$\s*([0-9,]+\.[0-9]{2})");
                if (money.Count > 0)
                {
                    current.Amount = ParseMoneyValue(money[money.Count - 1].Groups[1].Value);
                    if (money.Count > 1)
                        current.Price = ParseMoneyValue(money[0].Groups[1].Value);
                }
            }
        }
        
        if (current != null)
            items.Add(current);
        
        return items;
    }

    /// <summary>
    /// Helper to parse money string
    /// </summary>
    private static decimal? ParseMoneyValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        
        value = value.Replace(",", "").Replace("$", "").Trim();
        if (decimal.TryParse(value, out var result))
            return result;
        
        return null;
    }

    /// <summary>
    /// Enhanced invoice number detection with multiple patterns
    /// </summary>
    private static string? GuessInvoiceNumberEnhanced(string text)
    {
        // Try common patterns in order of specificity
        var patterns = new[]
        {
            (@"INVOICE\s*(?:NO\.?|NUMBER|#)\s*:?\s*([A-Z0-9\-]+)", 1.0),
            (@"INV\.?\s*(?:NO\.?|#)\s*:?\s*([A-Z0-9\-]+)", 0.9),
            (@"(?:SALES\s*ORDER|ORDER)\s*(?:NO\.?|#)\s*:?\s*([A-Z0-9\-]+)", 0.8),
            (@"(?:TRANSACTION|TRANS\.?)\s*(?:NO\.?|#)\s*:?\s*([A-Z0-9\-]+)", 0.7),
            (@"DOCUMENT\s*#?\s*:?\s*([A-Z0-9\-]+)", 0.6),
            (@"#\s*([0-9]{5,})", 0.5),
            (@"\b([A-Z]{1,3}[0-9]{5,})\b", 0.4)
        };
        
        string? bestMatch = null;
        double bestConfidence = 0;
        
        foreach (var (pattern, confidence) in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (match.Success && match.Groups.Count > 1 && confidence > bestConfidence)
            {
                var value = match.Groups[1].Value.Trim();
                if (value.Length >= 4 && value.Length <= 20)
                {
                    bestMatch = value;
                    bestConfidence = confidence;
                }
            }
        }
        
        return bestMatch;
    }

    /// <summary>
    /// Enhanced total detection with multiple patterns
    /// </summary>
    private static decimal? GuessTotalEnhanced(string text)
    {
        // Look for total in specific order of preference
        var totalPatterns = new[]
        {
            (@"(?:GRAND\s*TOTAL|INVOICE\s*TOTAL)\s*:?\s*\$?\s*([0-9,]+\.[0-9]{2})", 1.0),
            (@"(?:TOTAL\s*DUE|BALANCE\s*DUE|AMOUNT\s*DUE)\s*:?\s*\$?\s*([0-9,]+\.[0-9]{2})", 0.95),
            (@"(?:BALANCE|TOTAL)\s*:?\s*\$?\s*([0-9,]+\.[0-9]{2})", 0.7)
        };
        
        decimal? bestMatch = null;
        double bestConfidence = 0;
        
        foreach (var (pattern, confidence) in totalPatterns)
        {
            var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (matches.Count > 0)
            {
                // Take the last match (usually the final total)
                var match = matches[matches.Count - 1];
                if (match.Groups.Count > 1 && confidence > bestConfidence)
                {
                    var valueStr = match.Groups[1].Value.Replace(",", "");
                    if (decimal.TryParse(valueStr, out var value) && value > 0)
                    {
                        bestMatch = value;
                        bestConfidence = confidence;
                    }
                }
            }
        }
        
        return bestMatch;
    }

}
