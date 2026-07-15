namespace HisabKitabWorks.LicenseGenerator.WinForms;

internal sealed class OfflineLicenseFile
{
    public int Version { get; set; }
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}

internal sealed class OfflineLicensePayload
{
    public string LicenseKey { get; set; } = "";
    public string BusinessName { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int MaxStores { get; set; }
    public int MaxUsers { get; set; }
    public string ExpiresUtc { get; set; } = "";
    public string IssuedUtc { get; set; } = "";
}
