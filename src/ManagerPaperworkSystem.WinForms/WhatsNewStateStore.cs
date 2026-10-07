using System.Text;

namespace ManagerPaperworkSystem.WinForms;

// A separate marker for each release preserves acknowledgements across upgrades,
// reinstalls and switching stores. The open stream prevents concurrent popups.
internal sealed class WhatsNewStateStore(string directory)
{
    public Acknowledgement? TryBegin(string version)
    {
        if (!Version.TryParse(version, out var parsed)) throw new ArgumentException("Invalid release version.", nameof(version));
        var normalized = parsed.Revision > 0 ? parsed.ToString(4) : $"{parsed.Major}.{parsed.Minor}.{Math.Max(0, parsed.Build)}";
        Directory.CreateDirectory(directory);
        FileStream stream;
        try { stream = new FileStream(Path.Combine(directory, normalized + ".seen"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            if (reader.ReadToEnd() == "acknowledged") { stream.Dispose(); return null; }
            return new Acknowledgement(stream);
        }
        catch { stream.Dispose(); throw; }
    }

    internal sealed class Acknowledgement(FileStream stream) : IDisposable
    {
        public void Complete()
        {
            stream.Position = 0;
            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes("acknowledged"));
            stream.Flush(flushToDisk: true);
        }
        public void Dispose() => stream.Dispose();
    }
}
