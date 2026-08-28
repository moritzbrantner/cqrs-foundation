using System.Text;
using CqrsFoundation.Api;
using CqrsFoundation.Auth;
using CqrsFoundation.Infrastructure;
using Marten;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration is required.");

if (Encoding.UTF8.GetByteCount(jwtOptions.Key) < 32)
{
    throw new InvalidOperationException("Jwt:Key must be at least 32 bytes.");
}

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPasswordHasher<Credential>, PasswordHasher<Credential>>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services
    .AddMarten(options => Persistence.Configure(options, connectionString))
    .UseLightweightSessions();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapFoundationEndpoints();

app.Run();

public partial class Program;
