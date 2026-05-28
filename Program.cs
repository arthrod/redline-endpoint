using FastEndpoints;
using FastEndpoints.Security;

var builder = WebApplication.CreateBuilder(args);

// Add FastEndpoints
builder.Services.AddFastEndpoints();

// Add JWT Bearer Authentication
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    throw new InvalidOperationException("Missing required configuration: jwt_secret (Jwt:Secret / JWT_SECRET).");
}

builder.Services
    .AddAuthenticationJwtBearer(options =>
    {
        options.SigningKey = jwtSecret;
    })
    .AddAuthorization();

var app = builder.Build();

// Use authentication & authorization
app.UseAuthentication();
app.UseAuthorization();

// Use FastEndpoints
app.UseFastEndpoints();

app.Run();
