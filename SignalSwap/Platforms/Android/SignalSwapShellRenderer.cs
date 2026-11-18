using Google.Android.Material.BottomNavigation;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;

namespace SignalSwap;

public class SignalSwapShellRenderer : ShellRenderer
{
    protected override IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
        new EqualTabsAppearanceTracker(this, shellItem);
}

public class EqualTabsAppearanceTracker : ShellBottomNavViewAppearanceTracker
{
    const int AlwaysShowLabels = 1;

    public EqualTabsAppearanceTracker(IShellContext shellContext, ShellItem shellItem)
        : base(shellContext, shellItem)
    {
    }

    public override void SetAppearance(BottomNavigationView bottomView, IShellAppearanceElement appearance)
    {
        base.SetAppearance(bottomView, appearance);
        MakeEqual(bottomView);
    }

    public override void ResetAppearance(BottomNavigationView bottomView)
    {
        base.ResetAppearance(bottomView);
        MakeEqual(bottomView);
    }

    static void MakeEqual(BottomNavigationView bottomView)
    {
        bottomView.LabelVisibilityMode = AlwaysShowLabels;
        bottomView.ItemHorizontalTranslationEnabled = false;
    }
}
