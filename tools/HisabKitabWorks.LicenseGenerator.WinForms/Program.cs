namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length >= 2 && args[0].Equals("--validate-request", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var request = System.Text.Json.JsonSerializer.Deserialize<DeviceLicenseRequestV2>(File.ReadAllText(args[1]))
                    ?? throw new InvalidOperationException("Request is empty.");
                DeviceRequestValidator.Validate(request);
                return 0;
            }
            catch
            {
                return 2;
            }
        }
        Application.Run(new MainForm());
        return 0;
    }
}
