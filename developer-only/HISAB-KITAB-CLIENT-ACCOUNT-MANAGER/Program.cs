namespace HisabKitabWorks.ClientAccountManager.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        if (HisabKitabWorks.DeveloperUpdates.DeveloperAutoUpdateService.InstallLatestIfAvailable(
                "HISAB KITAB WORKS Account Manager",
                "HISAB_KITAB_Account_Manager_Update_win-x64",
                "HISAB_KITAB_WORKS_Account_Manager_Setup_"))
            return;
        Application.Run(new MainForm());
    }
}
