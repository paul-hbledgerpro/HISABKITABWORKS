using System.Security.Cryptography;
using System.Text;

namespace ManagerPaperworkSystem.WinForms;

internal static class VerifiedBackupRetention
{
    public static void Keep(string root, string databaseName, string verifiedSource)
    {
        var folder = Path.Combine(Path.GetFullPath(root), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databaseName))));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, $"HKVERIFIED_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.bak");
        var temporary = destination + ".partial";
        File.Copy(verifiedSource, temporary, false);
        using (var original = File.OpenRead(verifiedSource))
        using (var copy = File.OpenRead(temporary))
            if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(copy))) throw new IOException("The retained backup copy did not verify.");
        File.Move(temporary, destination);
        // Only this component's verified copies in this exact database folder are eligible.
        foreach (var old in new DirectoryInfo(folder).EnumerateFiles("HKVERIFIED_*.bak").OrderByDescending(x => x.CreationTimeUtc).Skip(7))
            try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
