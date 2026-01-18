using FastEndpoints;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add FastEndpoints
builder.Services.AddFastEndpoints();

// Add Bearer Token Authentication
builder.Services
    .AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, BearerTokenAuthHandler>("Bearer", null);
builder.Services.AddAuthorization();

var app = builder.Build();

// Use authentication & authorization
app.UseAuthentication();
app.UseAuthorization();

// Use FastEndpoints
app.UseFastEndpoints();

app.Run();

/// <summary>
/// Simple Bearer token authentication handler.
/// Validates against the configured API key.
/// </summary>
public class BearerTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IConfiguration _config;

    public BearerTokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration config) : base(options, logger, encoder)
    {
        _config = config;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing or invalid Authorization header"));
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var expectedToken = _config["ApiKey"];

        if (string.IsNullOrEmpty(expectedToken))
        {
            return Task.FromResult(AuthenticateResult.Fail("ApiKey not configured"));
        }

        if (token != expectedToken)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid token"));
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "ApiUser") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
