using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SignalSwap.Core;
using SignalSwap.Mapping;

namespace SignalSwap.Accounts;

public sealed class ApiException : Exception
{
    public ApiException(string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner) => Status = status;

    public HttpStatusCode? Status { get; }

    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;
}

public sealed class ApiClient
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    readonly AppSettings settings;
    HttpClient? http;
    string? httpBase;

    public ApiClient(AppSettings settings) => this.settings = settings;

    public string? Token { get; set; }

    public string ServerHost => Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var uri) ? uri.Authority : settings.ServerUrl;

    HttpClient Http
    {
        get
        {
            string baseUrl = settings.ServerUrl.TrimEnd('/') + "/";
            if (http == null || httpBase != baseUrl)
            {
                http?.Dispose();
                http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = Timeout };
                httpBase = baseUrl;
            }
            return http;
        }
    }

    public Task<AuthResponse> LoginAsync(string login, string password) =>
        SendAsync(HttpMethod.Post, "api/auth/login", new LoginRequest(login, password), ApiJson.Default.LoginRequest, ApiJson.Default.AuthResponse, false);

    public Task<AuthResponse> RegisterAsync(string username, string email, string password) =>
        SendAsync(HttpMethod.Post, "api/auth/register", new RegisterRequest(username, email, password), ApiJson.Default.RegisterRequest, ApiJson.Default.AuthResponse, false);

    public Task<AuthResponse> ChangePasswordAsync(string currentPassword, string newPassword) =>
        SendAsync(HttpMethod.Post, "api/auth/change-password", new ChangePasswordRequest(currentPassword, newPassword), ApiJson.Default.ChangePasswordRequest, ApiJson.Default.AuthResponse, true);

    public async Task LogoutEverywhereAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout-everywhere");
        using var response = await SendRawAsync(request, true);
    }

    public Task<UploadResult> UploadAsync(IReadOnlyList<SignalReading> readings) =>
        SendAsync(HttpMethod.Post, "api/readings", readings, ApiJson.Default.IReadOnlyListSignalReading, ApiJson.Default.UploadResult, true);

    public Task<List<NetworkSummary>> GetNetworksAsync() =>
        GetAsync("api/networks", ApiJson.Default.ListNetworkSummary);

    public Task<List<SignalReading>> GetReadingsAsync(string ssid, DateTimeOffset? since)
    {
        string path = "api/readings?ssid=" + Uri.EscapeDataString(ssid);
        if (since is { } value)
            path += "&since=" + Uri.EscapeDataString(value.UtcDateTime.ToString("o"));
        return GetAsync(path, ApiJson.Default.ListSignalReading);
    }

    public async Task<ClearResult> ClearAsync(string ssid)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "api/readings?ssid=" + Uri.EscapeDataString(ssid));
        using var response = await SendRawAsync(request, true);
        return await ReadAsync(response, ApiJson.Default.ClearResult);
    }

    public async Task CheckHealthAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/health");
        using var response = await SendRawAsync(request, false);
    }

    public static string Describe(Exception ex) => ex switch
    {
        ApiException api => api.Message,
        _ => ex.Message
    };

    async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> responseType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendRawAsync(request, true);
        return await ReadAsync(response, responseType);
    }

    async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        bool authorized)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, requestType)
        };
        using var response = await SendRawAsync(request, authorized);
        return await ReadAsync(response, responseType);
    }

    async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, bool authorized)
    {
        if (authorized)
        {
            if (Token is not { } token)
                throw new ApiException("Log in first.", HttpStatusCode.Unauthorized);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request);
        }
        catch (TaskCanceledException ex)
        {
            throw new ApiException($"{ServerHost} took too long to answer. Check that Tailscale is connected.", null, ex);
        }
        catch (Exception ex) when (ex is not ApiException)
        {
            throw new ApiException($"Could not reach {ServerHost}. Check that Tailscale is connected.", null, ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        string message = await ProblemMessageAsync(response);
        var status = response.StatusCode;
        response.Dispose();
        throw new ApiException(message, status);
    }

    static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type)
                ?? throw new ApiException("The server sent an empty answer.");
        }
        catch (JsonException ex)
        {
            throw new ApiException("The server sent an answer this app does not understand.", null, ex);
        }
    }

    static async Task<string> ProblemMessageAsync(HttpResponseMessage response)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(text) &&
                JsonSerializer.Deserialize(text, ApiJson.Default.ProblemResponse) is { } problem)
            {
                var first = problem.Errors?.Values.SelectMany(v => v).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                    return first;
                if (!string.IsNullOrWhiteSpace(problem.Title))
                    return problem.Title;
            }
        }
        catch (JsonException)
        {
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session ended. Log in again.",
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
            _ => $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}."
        };
    }
}
