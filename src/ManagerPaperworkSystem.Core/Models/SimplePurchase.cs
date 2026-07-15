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
