using Microsoft.Maui.Controls.Shapes;
using SignalSwap.Accounts;
using SignalSwap.Core;
using SignalSwap.Mapping;
using SignalSwap.Ui;

namespace SignalSwap.Pages;

public class AccountPage : ContentPage
{
    readonly AccountService account = AppServices.Account;
    readonly SyncService sync = AppServices.Sync;
    readonly ReadingStore store = AppServices.Store;
    readonly ApiClient api = AppServices.Api;
    readonly SignalEngine engine = AppServices.Engine;

    readonly VerticalStackLayout signedOutView = new() { Spacing = 12 };
    readonly VerticalStackLayout signedInView = new() { Spacing = 12 };

    readonly Button loginTab = new();
    readonly Button registerTab = new();
    readonly Entry usernameEntry = new() { Placeholder = "Username", Keyboard = Keyboard.Plain };
    readonly Entry emailEntry = new() { Placeholder = "Email", Keyboard = Keyboard.Email };
    readonly Entry loginEntry = new() { Placeholder = "Username or email", Keyboard = Keyboard.Email };
    readonly Entry passwordEntry = new() { Placeholder = "Password", IsPassword = true };
    readonly Entry confirmEntry = new() { Placeholder = "Confirm password", IsPassword = true };
    readonly Button submitButton = Theme.PrimaryButton("Log in");
    readonly Label formError = new() { FontSize = 13, TextColor = Theme.Danger, IsVisible = false };
    readonly Label notice = new() { FontSize = 13, TextColor = Theme.PrimaryDark, IsVisible = false };
    readonly Label serverLabel = Theme.Caption();

    readonly Label avatarLetter = new() { FontFamily = Theme.Semibold, FontSize = 22, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    readonly Label nameLabel = Theme.Heading();
    readonly Label sessionLabel = Theme.Caption();
    readonly Label pendingLabel = new();
    readonly Label uploadLabel = new();
    readonly Label downloadLabel = new();
    readonly Label syncError = new() { FontSize = 13, TextColor = Theme.Danger, IsVisible = false };
    readonly Button syncButton = Theme.SecondaryButton("Sync now");
    readonly VerticalStackLayout networkList = new() { Spacing = 10 };
    readonly Label networksEmpty = Theme.Caption("No maps yet. Mapping starts automatically when you are on Wi-Fi.");
    readonly Entry currentPasswordEntry = new() { Placeholder = "Current password", IsPassword = true };
    readonly Entry newPasswordEntry = new() { Placeholder = "New password (8 or more characters)", IsPassword = true };
    readonly Button changePasswordButton = Theme.SecondaryButton("Change password");
    readonly Label passwordMessage = new() { FontSize = 13, IsVisible = false };
    readonly Button logoutButton = Theme.SecondaryButton("Log out");
    readonly Button logoutEverywhereButton = Theme.DangerButton("Log out on every device");

    bool registering;
    bool listening;

    public AccountPage()
    {
        Title = "Account";

        loginTab.Clicked += (_, _) => SetMode(false);
        registerTab.Clicked += (_, _) => SetMode(true);
        submitButton.Clicked += async (_, _) => await SubmitAsync();
        passwordEntry.Completed += async (_, _) => { if (!registering) await SubmitAsync(); };
        confirmEntry.Completed += async (_, _) => await SubmitAsync();
        syncButton.Clicked += async (_, _) => await SyncAsync();
        changePasswordButton.Clicked += async (_, _) => await ChangePasswordAsync();
        logoutButton.Clicked += async (_, _) => await LogoutAsync();
        logoutEverywhereButton.Clicked += async (_, _) => await LogoutEverywhereAsync();

        BuildSignedOut();
        BuildSignedIn();
        SetMode(false);

        Content = Theme.PageBody(notice, signedOutView, signedInView);
        Unloaded += (_, _) => Listen(false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Listen(true);
        Refresh();
        try
        {
            await sync.RefreshMapsAsync();
        }
        catch (Exception)
        {
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Listen(false);
    }

    void Listen(bool on)
    {
        if (on == listening)
            return;
        listening = on;
        if (on)
        {
            account.SessionChanged += Refresh;
            engine.StoreReloaded += Refresh;
            sync.Changed += Refresh;
            sync.NetworkCleared += OnNetworkCleared;
        }
        else
        {
            account.SessionChanged -= Refresh;
            engine.StoreReloaded -= Refresh;
            sync.Changed -= Refresh;
            sync.NetworkCleared -= OnNetworkCleared;
        }
    }

    void OnNetworkCleared(string ssid) => Refresh();

    void BuildSignedOut()
    {
        var tabs = new Grid
        {
            ColumnSpacing = 6,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }
        };
        tabs.Add(loginTab, 0, 0);
        tabs.Add(registerTab, 1, 0);

        var form = Theme.Card(
            Theme.Heading("Your SignalSwap account"),
            Theme.Caption("Your dead zone maps are saved to your account on your SignalSwap server, one map per Wi-Fi network."),
            tabs,
            loginEntry,
            usernameEntry,
            emailEntry,
            passwordEntry,
            confirmEntry,
            formError,
            submitButton,
            serverLabel);

        signedOutView.Children.Add(form);
    }

    void BuildSignedIn()
    {
        var avatar = new Border
        {
            BackgroundColor = Theme.Primary,
            StrokeThickness = 0,
            WidthRequest = 52,
            HeightRequest = 52,
            StrokeShape = new RoundRectangle { CornerRadius = 26 },
            Content = avatarLetter
        };
        var header = new Grid
        {
            ColumnSpacing = 14,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }
        };
        header.Add(avatar, 0, 0);
        header.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 2,
            Children = { nameLabel, sessionLabel }
        }, 1, 0);

        var profile = Theme.Card(header);

        var syncCard = Theme.Card(
            Theme.SectionTitle("Sync"),
            Theme.KeyValue("Waiting to upload", pendingLabel),
            Theme.KeyValue("Last upload", uploadLabel),
            Theme.KeyValue("Last download", downloadLabel),
            syncError,
            syncButton);

        var networksCard = Theme.Card(
            Theme.SectionTitle("Networks in your account"),
            Theme.Caption("Clearing a network deletes its map and dead zones on this phone and on the server. Mapping starts over the next time you join it."),
            networkList,
            networksEmpty);

        var security = Theme.Card(
            Theme.SectionTitle("Security"),
            currentPasswordEntry,
            newPasswordEntry,
            passwordMessage,
            changePasswordButton,
            new BoxView { HeightRequest = 1, Color = Theme.Outline, Margin = new Thickness(0, 6) },
            logoutButton,
            logoutEverywhereButton,
            Theme.Caption("Changing your password or logging out on every device ends all other sessions."));

        signedInView.Children.Add(profile);
        signedInView.Children.Add(syncCard);
        signedInView.Children.Add(networksCard);
        signedInView.Children.Add(security);
    }

    void SetMode(bool register)
    {
        registering = register;
        StyleTab(loginTab, "Log in", !register);
        StyleTab(registerTab, "Create account", register);
        loginEntry.IsVisible = !register;
        usernameEntry.IsVisible = register;
        emailEntry.IsVisible = register;
        confirmEntry.IsVisible = register;
        passwordEntry.Placeholder = register ? "Password (8 or more characters)" : "Password";
        submitButton.Text = register ? "Create account" : "Log in";
        formError.IsVisible = false;
    }

    static void StyleTab(Button tab, string text, bool selected)
    {
        tab.Text = text;
        tab.BackgroundColor = selected ? Theme.Primary : Theme.PrimaryTint;
        tab.TextColor = selected ? Colors.White : Theme.PrimaryDark;
    }

    void Refresh()
    {
        bool signedIn = account.IsSignedIn;
        signedOutView.IsVisible = !signedIn;
        signedInView.IsVisible = signedIn;
        notice.Text = account.Notice ?? "";
        notice.IsVisible = !signedIn && !string.IsNullOrEmpty(account.Notice);
        serverLabel.Text = $"Server: {api.ServerHost}. Your phone needs Tailscale connected to reach it. You can change the server in Settings.";

        if (!signedIn || account.Session is not { } session)
            return;

        avatarLetter.Text = session.Username.Length > 0 ? session.Username[..1].ToUpperInvariant() : "?";
        nameLabel.Text = session.Username;
        sessionLabel.Text = $"Signed in until {session.ExpiresAt.ToLocalTime():MMM d}";

        pendingLabel.Text = store.PendingCount == 0 ? "Nothing" : $"{store.PendingCount} reading(s)";
        uploadLabel.Text = Ago(sync.LastUpload);
        downloadLabel.Text = Ago(sync.LastDownload);
        var problem = sync.LastError ?? sync.LastRejection;
        syncError.Text = problem ?? "";
        syncError.IsVisible = problem != null;
        syncButton.IsEnabled = !sync.Busy;
        syncButton.Text = sync.Busy ? "Syncing..." : "Sync now";

        ShowNetworks();
    }

    void ShowNetworks()
    {
        networkList.Children.Clear();
        var names = store.Networks.ToList();
        networksEmpty.IsVisible = names.Count == 0;

        foreach (var ssid in names)
        {
            var readings = store.ForNetwork(ssid).ToList();
            int floors = readings.Select(r => r.Floor ?? MappingEngine.DefaultFloor).Distinct().Count();
            var last = readings.Max(r => r.RecordedAt);

            var clear = Theme.DangerButton("Clear");
            clear.MinimumWidthRequest = 84;
            clear.VerticalOptions = LayoutOptions.Center;
            clear.Clicked += async (_, _) => await ClearAsync(ssid, clear);

            var row = new Grid
            {
                ColumnSpacing = 12,
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
            };
            row.Add(new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label { Text = ssid, FontFamily = Theme.Semibold, FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation },
                    Theme.Caption($"{readings.Count} readings · {floors} floor(s) · last mapped {last.ToLocalTime():MMM d, h:mm tt}")
                }
            }, 0, 0);
            row.Add(clear, 1, 0);
            networkList.Children.Add(row);
        }
    }

    async Task SubmitAsync()
    {
        formError.IsVisible = false;
        string password = passwordEntry.Text ?? "";

        if (registering)
        {
            string username = usernameEntry.Text?.Trim() ?? "";
            string email = emailEntry.Text?.Trim() ?? "";
            if (username.Length < 3 || username.Contains('@'))
            {
                ShowFormError("Use 3 to 50 characters for the username, without an @.");
                return;
            }
            if (!email.Contains('@'))
            {
                ShowFormError("Enter a valid email address.");
                return;
            }
            if (password.Length < 8)
            {
                ShowFormError("Use at least 8 characters for the password.");
                return;
            }
            if (password != confirmEntry.Text)
            {
                ShowFormError("The passwords do not match.");
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(loginEntry.Text) || password.Length == 0)
        {
            ShowFormError("Enter your username or email and your password.");
            return;
        }

        submitButton.IsEnabled = false;
        submitButton.Text = registering ? "Creating account..." : "Logging in...";
        try
        {
            if (registering)
                await account.RegisterAsync(usernameEntry.Text ?? "", emailEntry.Text ?? "", password);
            else
                await account.LoginAsync(loginEntry.Text ?? "", password);

            passwordEntry.Text = "";
            confirmEntry.Text = "";
        }
        catch (Exception ex)
        {
            ShowFormError(ApiClient.Describe(ex));
        }
        finally
        {
            submitButton.IsEnabled = true;
            submitButton.Text = registering ? "Create account" : "Log in";
            Refresh();
        }
    }

    void ShowFormError(string message)
    {
        formError.Text = message;
        formError.IsVisible = true;
    }

    async Task SyncAsync()
    {
        try
        {
            await sync.SyncNowAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Sync failed", ApiClient.Describe(ex), "OK");
        }
        Refresh();
    }

    async Task ClearAsync(string ssid, Button button)
    {
        bool confirmed = await DisplayAlertAsync(
            "Clear map",
            $"Delete every reading and dead zone for {ssid} on every floor, on this phone and in your account? If you are connected to it, recording on it starts again the next time you join it.",
            "Delete",
            "Cancel");
        if (!confirmed)
            return;

        button.IsEnabled = false;
        try
        {
            await sync.ClearNetworkAsync(ssid);
        }
        catch (Exception ex)
        {
            button.IsEnabled = true;
            await DisplayAlertAsync("Could not clear the map", $"{ApiClient.Describe(ex)} Nothing was deleted.", "OK");
        }
    }

    async Task ChangePasswordAsync()
    {
        string current = currentPasswordEntry.Text ?? "";
        string replacement = newPasswordEntry.Text ?? "";
        if (current.Length == 0 || replacement.Length < 8)
        {
            ShowPasswordMessage("Enter your current password and a new one with at least 8 characters.", true);
            return;
        }

        changePasswordButton.IsEnabled = false;
        try
        {
            await account.ChangePasswordAsync(current, replacement);
            currentPasswordEntry.Text = "";
            newPasswordEntry.Text = "";
            ShowPasswordMessage("Password changed. Other devices were logged out.", false);
        }
        catch (Exception ex)
        {
            ShowPasswordMessage(ApiClient.Describe(ex), true);
        }
        finally
        {
            changePasswordButton.IsEnabled = true;
        }
    }

    void ShowPasswordMessage(string message, bool error)
    {
        passwordMessage.Text = message;
        passwordMessage.TextColor = error ? Theme.Danger : Theme.PrimaryDark;
        passwordMessage.IsVisible = true;
    }

    async Task LogoutAsync()
    {
        string extra = store.PendingCount > 0
            ? $" {store.PendingCount} reading(s) have not uploaded yet. They stay on this phone and upload when you log back in."
            : "";
        if (!await DisplayAlertAsync("Log out", "Mapping stops until you log in again." + extra, "Log out", "Cancel"))
            return;
        account.SignOut();
    }

    async Task LogoutEverywhereAsync()
    {
        if (!await DisplayAlertAsync("Log out on every device", "This ends every SignalSwap session for your account, including this one.", "Log out", "Cancel"))
            return;

        logoutEverywhereButton.IsEnabled = false;
        try
        {
            await account.LogoutEverywhereAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not log out everywhere", ApiClient.Describe(ex), "OK");
        }
        finally
        {
            logoutEverywhereButton.IsEnabled = true;
        }
    }

    static string Ago(DateTimeOffset? when)
    {
        if (when is not { } value)
            return "Not yet";
        var span = DateTimeOffset.UtcNow - value;
        if (span < TimeSpan.FromMinutes(1))
            return "Just now";
        if (span < TimeSpan.FromHours(1))
            return $"{(int)span.TotalMinutes} min ago";
        return value.ToLocalTime().ToString("MMM d, h:mm tt");
    }
}
