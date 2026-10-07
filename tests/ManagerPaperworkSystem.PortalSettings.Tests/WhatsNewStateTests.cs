using ManagerPaperworkSystem.WinForms;
using Xunit;

namespace ManagerPaperworkSystem.PortalSettings.Tests;

public sealed class WhatsNewStateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "HK-WhatsNew-Test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public void Acknowledgement_survives_restart_and_version_normalization()
    {
        using (var first = new WhatsNewStateStore(directory).TryBegin("1.0.176"))
        { Assert.NotNull(first); first.Complete(); }
        Assert.Null(new WhatsNewStateStore(directory).TryBegin("1.0.176"));
        Assert.Null(new WhatsNewStateStore(directory).TryBegin("1.0.176.0"));
    }
    [Fact]
    public void New_release_gets_its_own_notice_and_old_release_stays_acknowledged()
    {
        using (var first = new WhatsNewStateStore(directory).TryBegin("1.0.176")) first!.Complete();
        using (var next = new WhatsNewStateStore(directory).TryBegin("1.0.177")) { Assert.NotNull(next); next.Complete(); }
        Assert.Null(new WhatsNewStateStore(directory).TryBegin("1.0.176"));
        Assert.Null(new WhatsNewStateStore(directory).TryBegin("1.0.177"));
    }
    [Fact]
    public void Concurrent_launch_cannot_display_a_second_notice()
    {
        using var first = new WhatsNewStateStore(directory).TryBegin("1.0.176");
        Assert.NotNull(first);
        Assert.Null(new WhatsNewStateStore(directory).TryBegin("1.0.176"));
    }
    [Fact]
    public void Interrupted_notice_is_available_on_next_launch()
    {
        new WhatsNewStateStore(directory).TryBegin("1.0.176")!.Dispose();
        using var next = new WhatsNewStateStore(directory).TryBegin("1.0.176");
        Assert.NotNull(next);
    }
    [Fact]
    public void Different_windows_profiles_have_independent_acknowledgements()
    {
        using (var first = new WhatsNewStateStore(Path.Combine(directory,"user1")).TryBegin("1.0.176")) first!.Complete();
        using var other = new WhatsNewStateStore(Path.Combine(directory,"user2")).TryBegin("1.0.176");
        Assert.NotNull(other);
    }
    [Theory]
    [InlineData("../other")]
    [InlineData("1.0/not-a-version")]
    public void Invalid_version_cannot_become_a_file_path(string version)
    {
        Assert.Throws<ArgumentException>(() => new WhatsNewStateStore(directory).TryBegin(version));
        Assert.False(Directory.Exists(directory));
    }
}
