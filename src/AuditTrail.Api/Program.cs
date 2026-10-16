using AuditTrail.Api;
using AuditTrail.Api.Auditing;
using AuditTrail.Application.Auditing;
using AuditTrail.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpAuditContextProvider>();
builder.Services.AddScoped<IAuditContextProvider>(sp => sp.GetRequiredService<HttpAuditContextProvider>());
builder.Services.AddScoped<IAuditAccessProvider>(sp => sp.GetRequiredService<HttpAuditContextProvider>());
builder.Services.AddAuditTrailInfrastructure(builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing."));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:Authority"];
    options.Audience = builder.Configuration["Authentication:Audience"];
    options.RequireHttpsMetadata = true;
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AuditReader", policy => policy.RequireAuthenticatedUser().RequireRole("AuditReader"));
    options.AddPolicy("AuditWriter", policy => policy.RequireAuthenticatedUser().RequireRole("AuditWriter"));
});
var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
public partial class Program;
