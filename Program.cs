using FastEndpoints;
using FastEndpoints.Security;

var builder = WebApplication.CreateBuilder(args);

// Add FastEndpoints
builder.Services.AddFastEndpoints();

// Add JWT Bearer Authentication
builder.Services
    .AddAuthenticationJwtBearer(options =>
    {
        options.SigningKey = builder.Configuration["Jwt:Secret"]!;
    })
    .AddAuthorization();

var app = builder.Build();

// Use authentication & authorization
app.UseAuthentication();
app.UseAuthorization();

// Use FastEndpoints
app.UseFastEndpoints();

app.Run();
