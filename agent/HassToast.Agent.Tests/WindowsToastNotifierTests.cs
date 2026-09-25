using HassToast.Agent.Toasts;
using Xunit;

namespace HassToast.Agent.Tests;

/// <summary>
/// The group a toast is given in Windows. Every toast must have one: Windows offers an
/// unpackaged app no way to remove a toast without a group, so a missing group would make
/// hass_toast.remove silently do nothing.
/// </summary>
public sealed class WindowsToastNotifierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_toast_sent_without_a_group_is_given_the_default_one(string? group)
    {
        Assert.Equal(WindowsToastNotifier.DefaultGroup, WindowsToastNotifier.GroupFor(group));
    }

    [Fact]
    public void A_toast_sent_with_a_group_keeps_it()
    {
        Assert.Equal("security", WindowsToastNotifier.GroupFor("security"));
    }

    [Fact]
    public void A_group_is_capped_at_the_64_characters_Windows_accepts()
    {
        var group = WindowsToastNotifier.GroupFor(new string('g', 80));

        Assert.Equal(64, group.Length);
    }

    [Fact]
    public void The_default_group_fits_within_what_Windows_accepts()
    {
        Assert.InRange(WindowsToastNotifier.DefaultGroup.Length, 1, 64);
    }
}
