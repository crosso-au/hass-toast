using HassToast.Agent.Setup;
using Xunit;

namespace HassToast.Agent.Tests;

/// <summary>
/// The uninstall inventory.
/// <para>
/// The reason this type exists at all is that install, cleanup and diagnosis each used to carry
/// their own idea of what the product leaves behind, and the three drifted: <c>--reset</c> ended
/// up removing two of nine artifacts, and the ones it missed — the log folder, the generated
/// icon, the image cache, a renamed shortcut — were missed precisely because no single list
/// existed to check against. These tests are what keep that from happening again.
/// </para>
/// </summary>
public sealed class InstallFootprintTests
{
    [Fact]
    public void Every_artifact_the_agent_creates_is_in_the_inventory()
    {
        var locations = InstallFootprint.All()
            .Select(i => i.Location.ToLowerInvariant())
            .ToList();

        // Named individually rather than counted, so that adding an artifact without registering
        // it fails here with the name of the thing that was forgotten.
        Assert.Contains(locations, l => l.Contains("config.json"));
        Assert.Contains(locations, l => l.Contains("secrets.dat"));
        Assert.Contains(locations, l => l.Contains("icon.png"));
        Assert.Contains(locations, l => l.Contains("logs"));
        Assert.Contains(locations, l => l.Contains("images"));
        Assert.Contains(locations, l => l.Contains(".lnk"));
        Assert.Contains(locations, l => l.Contains("clsid"));
        Assert.Contains(locations, l => l.Contains(@"currentversion\run"));
        Assert.Contains(locations, l => l.Contains("startupapproved"));
        Assert.Contains(locations, l => l.Contains(@"notifications\settings"));
        Assert.Contains(locations, l => l.Contains("uninstall"));
        Assert.Contains(locations, l => l.Contains(@"programs\hasstoast"));
    }

    [Fact]
    public void Keeping_the_logs_removes_them_from_the_inventory()
    {
        var withLogs = InstallFootprint.All(keepLogs: false);
        var withoutLogs = InstallFootprint.All(keepLogs: true);

        Assert.Contains(withLogs, i => i.Name == "Logs");
        Assert.DoesNotContain(withoutLogs, i => i.Name == "Logs");

        // The configuration folder stays listed either way. With logs kept it simply will not
        // empty, and reporting that honestly beats deleting what was asked to be kept.
        Assert.Contains(withoutLogs, i => i.Name == "Configuration folder");
    }

    [Fact]
    public void Every_entry_is_named_and_located()
    {
        foreach (var item in InstallFootprint.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name), "an inventory entry has no name");
            Assert.False(string.IsNullOrWhiteSpace(item.Location), $"'{item.Name}' has no location");
        }

        var names = InstallFootprint.All().Select(i => i.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Probing_never_throws_even_where_the_path_cannot_be_read()
    {
        // The audit runs after files and registry keys have been deleted out from under it, so
        // a probe that throws would turn a successful uninstall into a crash on the last page.
        foreach (var item in InstallFootprint.All())
        {
            var exception = Record.Exception(() => item.Exists());
            Assert.Null(exception);
        }
    }

    [Fact]
    public void An_unreadable_artifact_is_reported_as_present_rather_than_absent()
    {
        var item = new FootprintItem(
            "Unreadable", "nowhere", FootprintKind.File,
            Probe: () => throw new UnauthorizedAccessException("denied"),
            Remove: () => { });

        // "I could not tell" and "it is gone" are different answers, and only one of them may
        // let the uninstaller claim the machine is clean.
        Assert.True(item.Exists());
    }

    [Fact]
    public void Removal_reports_failure_instead_of_throwing()
    {
        var item = new FootprintItem(
            "Stubborn", "nowhere", FootprintKind.File,
            Probe: () => true,
            Remove: () => throw new IOException("file in use"));

        var failure = item.TryRemove();

        // One artifact that will not go must not stop the other twelve being removed.
        Assert.NotNull(failure);
        Assert.Contains("file in use", failure);
    }

    [Fact]
    public void Removing_something_already_gone_succeeds_quietly()
    {
        // Uninstall runs the whole inventory whatever state the machine is in, including after a
        // partly completed earlier attempt, so every removal has to be idempotent.
        //
        // Deliberately against synthetic paths rather than the real inventory: running the real
        // removals here would reach into the registry and LOCALAPPDATA of whichever machine is
        // running the tests, which is a great deal to risk for one assertion.
        var missingFile = Path.Combine(Path.GetTempPath(), $"hasstoast-absent-{Guid.NewGuid():N}.tmp");
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"hasstoast-absent-{Guid.NewGuid():N}");

        var items = new[]
        {
            new FootprintItem("Missing file", missingFile, FootprintKind.File,
                () => File.Exists(missingFile),
                () => { if (File.Exists(missingFile)) File.Delete(missingFile); }),

            new FootprintItem("Missing directory", missingDirectory, FootprintKind.Directory,
                () => Directory.Exists(missingDirectory),
                () => { if (Directory.Exists(missingDirectory)) Directory.Delete(missingDirectory, true); }),
        };

        foreach (var item in items)
        {
            Assert.False(item.Exists());
            Assert.Null(item.TryRemove());
        }
    }
}
