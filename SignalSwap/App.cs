using SignalSwap.Core;
using SignalSwap.Ui;

namespace SignalSwap;

public class App : Application
{
    public App()
    {
        UserAppTheme = AppTheme.Light;
        Resources = Theme.CreateResources();
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        AppServices.Engine.AppInForeground = false;
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppServices.Engine.AppInForeground = true;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new AppShell());
}
