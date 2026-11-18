using System.Text.Json;

namespace SignalSwap.Accounts;

public sealed class AccountService
{
    const string SessionKey = "signalswap.session";

    readonly ApiClient api;

    public AccountService(ApiClient api) => this.api = api;

    public event Action? SessionChanged;

    public Session? Session { get; private set; }

    public string? Notice { get; private set; }

    public bool IsSignedIn => Session is { } session && session.ExpiresAt > DateTimeOffset.UtcNow;

    public async Task LoadAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(SessionKey);
            Session = json == null ? null : JsonSerializer.Deserialize(json, ApiJson.Default.Session);
        }
        catch (Exception)
        {
            SecureStorage.Default.Remove(SessionKey);
            Session = null;
        }

        if (Session is { } loaded && loaded.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Session = null;
            Notice = "Your session ended. Log in again.";
            SecureStorage.Default.Remove(SessionKey);
        }

        api.Token = Session?.Token;
    }

    public async Task LoginAsync(string login, string password) =>
        await SaveAsync(await api.LoginAsync(login.Trim(), password));

    public async Task RegisterAsync(string username, string email, string password) =>
        await SaveAsync(await api.RegisterAsync(username.Trim(), email.Trim(), password));

    public async Task ChangePasswordAsync(string currentPassword, string newPassword) =>
        await SaveAsync(await api.ChangePasswordAsync(currentPassword, newPassword), false);

    public async Task LogoutEverywhereAsync()
    {
        await api.LogoutEverywhereAsync();
        Clear("You were logged out on every device.");
    }

    public void SignOut() => Clear(null);

    public void Expire()
    {
        if (Session != null)
            Clear("Your session ended. Log in again.");
    }

    async Task SaveAsync(AuthResponse response, bool announce = true)
    {
        bool sameUser = Session?.UserId == response.UserId;
        Session = new Session(response.UserId, response.Username, response.Token, response.ExpiresAt);
        Notice = null;
        api.Token = Session.Token;
        await SecureStorage.Default.SetAsync(SessionKey, JsonSerializer.Serialize(Session, ApiJson.Default.Session));
        if (announce || !sameUser)
            SessionChanged?.Invoke();
    }

    void Clear(string? notice)
    {
        Session = null;
        Notice = notice;
        api.Token = null;
        SecureStorage.Default.Remove(SessionKey);
        SessionChanged?.Invoke();
    }
}
