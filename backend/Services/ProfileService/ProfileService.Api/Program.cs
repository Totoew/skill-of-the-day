using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi;
using Npgsql;
using ProfileService.Api.Security;
using ProfileService.Api.Clients;
using ProfileService.Application;
using ProfileService.Domain;
using ProfileService.Infrastructure;
using ProfileService.Infrastructure.Migrations;
using SkillDay.ServiceDefaults;

if (args.Contains("--healthcheck"))
{
    try { using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; Environment.ExitCode = (await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch { Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 32768);
builder.AddServiceTelemetry("profile-service");
var connection = new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString("ProfileDb")
    ?? throw new InvalidOperationException("ConnectionStrings:ProfileDb is required."));
if (builder.Configuration["Database:Password"] is { Length: > 0 } password) connection.Password = password;
var authAddress = builder.Configuration["Auth:Address"] ?? "http://auth-service:8080/";
if (!Uri.TryCreate(authAddress, UriKind.Absolute, out var authUri) || authUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(authUri.UserInfo))
    throw new InvalidOperationException("Auth:Address must be an HTTP(S) URL without credentials.");
var internalKey = builder.Configuration["Internal:Key"] ?? "";
if (internalKey.Length < 16 || (!builder.Environment.IsDevelopment() && internalKey == "local-development-only"))
    throw new InvalidOperationException("Internal:Key must be configured with a private service key.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection.ConnectionString));
builder.Services.AddScoped<IProfileRepository, ProfileRepository>();
builder.Services.AddFluentMigratorCore().ConfigureRunner(runner => runner.AddPostgres()
    .WithGlobalConnectionString(connection.ConnectionString).ScanIn(typeof(InitialCreateProfileScheme).Assembly).For.Migrations());
builder.Services.AddHttpClient("auth", client => { client.BaseAddress = authUri; client.Timeout = TimeSpan.FromSeconds(4); });
var skillsAddress = builder.Configuration["Skills:Address"] ?? "http://skill-service:8080/";
if (!Uri.TryCreate(skillsAddress, UriKind.Absolute, out var skillsUri) || skillsUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(skillsUri.UserInfo))
    throw new InvalidOperationException("Skills:Address must be an HTTP(S) URL without credentials.");
builder.Services.AddHttpClient<SkillCatalogClient>(client => { client.BaseAddress = skillsUri; client.Timeout = TimeSpan.FromSeconds(4); });
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("Session", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Навык дня — Profile Service", Version = "v1" });
    options.AddServer(new OpenApiServer { Url = "/api" });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] });
    options.DocInclusionPredicate((_, description) => description.RelativePath?.StartsWith("profile/") == true);
});
var app = builder.Build();
app.UseServiceRequestLogging();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = error => error is BadHttpRequestException bad ? bad.StatusCode : 500
});
app.Use(async (http, next) =>
{
    http.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (CatalogUnavailableException) { await Results.Problem(statusCode: 503, title: "Справочник навыков временно недоступен. Настройки не изменены.").ExecuteAsync(http); }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (NpgsqlDataSource db, CancellationToken cancellation) =>
{
    try { await using var conn = await db.OpenConnectionAsync(cancellation); await using var command = new NpgsqlCommand("SELECT 1 FROM profile.user_profiles LIMIT 0", conn); await command.ExecuteNonQueryAsync(cancellation); return Results.Ok(new { status = "ready" }); }
    catch (NpgsqlException) { return Results.StatusCode(503); }
});
var profiles = app.MapGroup("/profile").WithTags("Profile").RequireAuthorization();
profiles.MapGet("/me", async (ClaimsPrincipal user, IProfileRepository repository, CancellationToken cancellation) =>
    Results.Ok(await repository.GetAsync(UserId(user), cancellation) ?? UserProfile.Empty(UserId(user))))
    .WithSummary("Получить свой профиль; version=0 означает, что настройки ещё не сохранены.");
profiles.MapPut("/me", async (SaveProfileRequest request, ClaimsPrincipal user, IProfileRepository repository, SkillCatalogClient catalogClient, CancellationToken cancellation) =>
{
    var catalog = await catalogClient.GetAsync(cancellation);
    var errors = ProfileCatalog.Validate(request, catalog.Skills.Select(skill => skill.Name));
    if (errors.Count > 0) return Results.ValidationProblem(errors, title: "Проверьте настройки профиля.");
    try
    {
        var profile = await repository.SaveAsync(UserId(user), request, cancellation);
        return profile is null ? Results.Problem(statusCode: 409, title: "Профиль изменён на другом устройстве. Загрузите актуальные настройки и повторите изменение.") : Results.Ok(profile);
    }
    catch (DeletedProfileOwnerException) { return Results.Problem(statusCode: 401, title: "Учётная запись удалена."); }
}).WithSummary("Сохранить настройки с проверкой версии. ID пользователя определяется по сессии.");
profiles.MapDelete("/me", async (ClaimsPrincipal user, IProfileRepository repository, CancellationToken cancellation) =>
{ await repository.DeleteAsync(UserId(user), cancellation); return Results.NoContent(); }).WithSummary("Сбросить свой профиль.");
profiles.MapGet("/catalog", async (SkillCatalogClient catalogClient, CancellationToken cancellation) =>
{
    var catalog = await catalogClient.GetAsync(cancellation);
    return Results.Ok(new { version = catalog.Version, directions = ProfileCatalog.Directions.ToDictionary(code => code,
        code => catalog.Skills.Where(skill => skill.Directions.Contains(code)).Select(skill => skill.Name).ToArray()),
        levels = ProfileCatalog.Levels, regions = ProfileCatalog.Regions });
}).WithSummary("Допустимые значения профиля; навыки поступают из SkillService.");
app.MapDelete("/internal/profiles/{id:guid}", async (Guid id, HttpContext http, IProfileRepository repository, CancellationToken cancellation) =>
{
    var provided = http.Request.Headers["X-Service-Key"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(internalKey))))
        return Results.Unauthorized();
    await repository.DeleteAccountAsync(id, cancellation); return Results.NoContent();
}).ExcludeFromDescription();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger(options => options.RouteTemplate = "profile/openapi/{documentName}.json");
    app.UseSwaggerUI(options => { options.RoutePrefix = "profile/swagger"; options.SwaggerEndpoint("../openapi/v1.json", "Profile Service v1"); options.ConfigObject.PersistAuthorization = false; });
}
if (builder.Configuration.GetValue<bool>("Profile:AutoMigrate") || args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
}
if (!args.Contains("--migrate")) await app.RunAsync();
static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
public partial class Program;
