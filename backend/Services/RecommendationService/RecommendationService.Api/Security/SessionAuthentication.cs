using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace RecommendationService.Api.Security;

// Auth validates signature, expiry AND live session revocation. No shared signing key or Auth DB access.
public sealed class SessionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IHttpClientFactory clients)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(raw)) return AuthenticateResult.NoResult();
        if (raw.Length > 8192 || !AuthenticationHeaderValue.TryParse(raw, out var header)
            || !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(header.Parameter))
            return AuthenticateResult.Fail("Invalid bearer token.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "auth/me");
            request.Headers.Authorization = header;
            using var response = await clients.CreateClient("auth").SendAsync(request, Context.RequestAborted);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return AuthenticateResult.Fail("Session is invalid or revoked.");
            if (!response.IsSuccessStatusCode) return Unavailable();
            var user = await response.Content.ReadFromJsonAsync<AuthUser>(Context.RequestAborted);
            if (user is null || user.Id == Guid.Empty) return Unavailable();
            var claims = new[] { new Claim("sub", user.Id.ToString()) }
                .Concat((user.Roles ?? []).Select(role => new Claim(ClaimTypes.Role, role)));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { return Unavailable(); }
    }

    private AuthenticateResult Unavailable()
    {
        Context.Items["AuthUnavailable"] = true;
        return AuthenticateResult.NoResult();
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var unavailable = Context.Items.ContainsKey("AuthUnavailable");
        if (!unavailable) Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: unavailable ? 503 : 401,
            title: unavailable ? "Сервис авторизации временно недоступен." : "Войдите в учётную запись.").ExecuteAsync(Context);
    }

    private sealed record AuthUser(Guid Id, string[]? Roles);
}
