using ManagerPaperworkSystem.Core.Models;

namespace ManagerPaperworkSystem.Core.Services;

public interface IAppPaths
{
    string AppDataDirectory { get; }
    string DatabasePath { get; }
    string BackupsDirectory { get; }
}

public interface ISettingsService
{
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
    Task<AppSettings> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
}

public interface IAuthService
{
    Task<bool> HasAnyUsersAsync(CancellationToken ct = default);
    Task<UserAccount> CreateUserAsync(string firstName, string lastName, UserRole role, string username, string password, string securityQuestion, string securityAnswer, string email = "", CancellationToken ct = default);
    Task<UserAccount?> AuthenticateAsync(string username, string password, CancellationToken ct = default);
    
    Task<UserAccount?> GetUserByUsernameAsync(string username, CancellationToken ct = default);
    Task<bool> VerifyAdminCredentialsAsync(string username, string password, CancellationToken ct = default);
    Task ChangePasswordAsync(int userId, string newPassword, CancellationToken ct = default);
    Task<string?> GetSecurityQuestionAsync(string username, CancellationToken ct = default);
    Task ResetPasswordWithSecurityAnswerAsync(string username, string securityAnswer, string newPassword, CancellationToken ct = default);
    Task<IReadOnlyList<UserAccount>> GetUsersAsync(CancellationToken ct = default);
    Task SetUserActiveAsync(int userId, bool isActive, CancellationToken ct = default);
}

public interface IReportService
{
    // Shift Log Reports
    Task GenerateShiftLogPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    Task GenerateShiftLogDetailPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    Task GenerateShiftLogSummaryPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    
    // Cash On Hand Reports
    Task GenerateCashOnHandPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    Task GenerateCashOnHandDetailPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    Task GenerateCashOnHandSummaryPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    
    // Check Payouts Reports
    Task GenerateCheckPayoutsPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    Task GenerateCheckPayoutsDetailPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    Task GenerateCheckPayoutsSummaryPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    
    // Sales Summary
    Task GenerateSalesSummaryByDatePdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    
    // Profit & Loss Report
    Task GenerateProfitLossPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);

    // Payroll Report
    Task GeneratePayrollPdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);

    // Combined report packet
    Task GenerateAllReportsBundlePdfAsync(DateOnly from, DateOnly to, string outputPdfPath, CancellationToken ct = default);
    
    // Get P&L data for UI display
    Task<ProfitLossData> GetProfitLossDataAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}
