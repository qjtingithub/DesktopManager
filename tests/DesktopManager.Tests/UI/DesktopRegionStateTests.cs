using DesktopManager.UI;

namespace DesktopManager.Tests.UI;

public sealed class DesktopRegionStateTests
{
    [Fact]
    public void StateUsesSafeDefaultsAndNormalizesName()
    {
        DesktopRegionState state = new("  学习资料  ", backgroundOpacity: 0.4);

        Assert.Equal("学习资料", state.Name);
        Assert.Equal(0.4, state.BackgroundOpacity);
        Assert.Equal(DesktopRegionMode.Editing, state.Mode);
    }

    [Fact]
    public void OpacityIsClampedAndBlankNameGetsFallback()
    {
        DesktopRegionState state = new(" ", backgroundOpacity: 2);

        state.SetName(null);
        state.SetBackgroundOpacity(-1);

        Assert.Equal("新分区", state.Name);
        Assert.Equal(DesktopRegionState.MaxBackgroundOpacity, new DesktopRegionState(backgroundOpacity: 2).BackgroundOpacity);
        Assert.Equal(DesktopRegionState.MinBackgroundOpacity, state.BackgroundOpacity);
    }

    [Fact]
    public void ToggleModeChangesOnlyBetweenEditingAndLocked()
    {
        DesktopRegionState state = new();

        state.ToggleMode();
        Assert.Equal(DesktopRegionMode.Locked, state.Mode);

        state.ToggleMode();
        Assert.Equal(DesktopRegionMode.Editing, state.Mode);
    }
}
