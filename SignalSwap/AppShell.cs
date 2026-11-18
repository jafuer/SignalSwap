using SignalSwap.Pages;
using SignalSwap.Ui;
using SignalSwap.Core;

namespace SignalSwap;

public class AppShell : Shell
{
    public const string SignalRoute = "signal";
    public const string MapRoute = "map";
    public const string AccountRoute = "account";
    public const string SettingsRoute = "settings";

    bool started;

    public AppShell()
    {
        FlyoutBehavior = FlyoutBehavior.Disabled;
        SetBackgroundColor(this, Theme.Surface);
        SetForegroundColor(this, Theme.PrimaryDark);
        SetTitleColor(this, Theme.Text);
        SetTabBarBackgroundColor(this, Theme.Surface);
        SetTabBarForegroundColor(this, Theme.Primary);
        SetTabBarTitleColor(this, Theme.PrimaryDark);
        SetTabBarUnselectedColor(this, Theme.Faint);

        var bar = new TabBar();
        bar.Items.Add(CreateTab("Signal", "tab_signal.png", SignalRoute, () => new SignalPage()));
        bar.Items.Add(CreateTab("Deadzone Map", "tab_map.png", MapRoute, () => new DeadzonePage()));
        bar.Items.Add(CreateTab("Account", "tab_account.png", AccountRoute, () => new AccountPage()));
        bar.Items.Add(CreateTab("Settings", "tab_settings.png", SettingsRoute, () => new SettingsPage()));
        Items.Add(bar);
    }

    public static Task OpenTabAsync(string route) => Current?.GoToAsync("//" + route) ?? Task.CompletedTask;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (started)
            return;
        started = true;
        await Startup.RunAsync();
    }

    static Tab CreateTab(string title, string icon, string route, Func<Page> create)
    {
        var tab = new Tab { Title = title, Icon = icon, Route = route };
        tab.Items.Add(new ShellContent
        {
            Title = title,
            Route = route + "-page",
            ContentTemplate = new DataTemplate(() => create())
        });
        return tab;
    }
}
