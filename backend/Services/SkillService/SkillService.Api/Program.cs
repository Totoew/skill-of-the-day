using System.Security.Claims;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi;
using Npgsql;
using SkillDay.ServiceDefaults;
using SkillService.Api.Security;
using SkillService.Application;
using SkillService.Domain;
using SkillService.Infrastructure;
using SkillService.Infrastructure.Migrations;

if (args.Contains("--healthcheck"))
{
    try { using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; Environment.ExitCode = (await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch { Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 1_100_000);
builder.AddServiceTelemetry("skill-service");
var connection = new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString("SkillDb")
    ?? throw new InvalidOperationException("ConnectionStrings:SkillDb is required."));
if (builder.Configuration["Database:Password"] is { Length: > 0 } password) connection.Password = password;
var authAddress = builder.Configuration["Auth:Address"] ?? "http://auth-service:8080/";
if (!Uri.TryCreate(authAddress, UriKind.Absolute, out var authUri) || authUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(authUri.UserInfo))
    throw new InvalidOperationException("Auth:Address must be an HTTP(S) URL without credentials.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection.ConnectionString));
builder.Services.AddScoped<ICatalogRepository, CatalogRepository>();
builder.Services.AddFluentMigratorCore().ConfigureRunner(runner => runner.AddPostgres()
    .WithGlobalConnectionString(connection.ConnectionString).ScanIn(typeof(InitialCreateSkillScheme).Assembly).For.Migrations());
builder.Services.AddHttpClient("auth", client => { client.BaseAddress = authUri; client.Timeout = TimeSpan.FromSeconds(4); });
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("Session", _ => { });
builder.Services.AddAuthorizationBuilder().AddPolicy("Admin", policy => policy.RequireRole("Admin"));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Навык дня — Skill Service", Version = "v1" });
    options.AddServer(new OpenApiServer { Url = "/api" });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    options.DocInclusionPredicate((_, description) => description.RelativePath?.StartsWith("skills/") == true);
    options.DocumentFilter<AdminSecurity>();
});
var app = builder.Build();
app.UseServiceRequestLogging();
app.UseExceptionHandler(new ExceptionHandlerOptions { StatusCodeSelector = error => error is BadHttpRequestException bad ? bad.StatusCode : 500 });
app.Use(async (http, next) =>
{
    http.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (CatalogValidationException error) { await Results.ValidationProblem(error.Errors, title: "Проверьте JSON-файл справочника.").ExecuteAsync(http); }
    catch (CatalogConflictException error) { await Results.Problem(statusCode: 409, title: error.Message).ExecuteAsync(http); }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (NpgsqlDataSource db, CancellationToken cancellation) =>
{
    try { await using var conn = await db.OpenConnectionAsync(cancellation); await using var command = new NpgsqlCommand("SELECT version FROM skill.catalog_state WHERE id=1", conn); await command.ExecuteScalarAsync(cancellation); return Results.Ok(new { status = "ready" }); }
    catch (NpgsqlException) { return Results.StatusCode(503); }
});
var skills = app.MapGroup("/skills").WithTags("Skills");
skills.MapGet("/catalog", async (ICatalogRepository repository, CancellationToken cancellation) => Results.Ok(await repository.GetAsync(cancellation)))
    .WithSummary("Справочник навыков, синонимов и базовых навыков по направлениям.");
var admin = skills.MapGroup("/admin").RequireAuthorization("Admin");
admin.MapGet("/template", () => Results.Json(CatalogImport.Example()))
    .WithSummary("Пример JSON: стартовый демонстрационный справочник, не утверждённая таксономия.");
admin.MapGet("/export", async (ICatalogRepository repository, CancellationToken cancellation) =>
{
    var catalog = await repository.GetAsync(cancellation);
    return Results.Json(new CatalogDocument(1, catalog.Skills));
}).WithSummary("Выгрузить текущий справочник в формате импорта.");
admin.MapPost("/import/preview", async (CatalogDocument document, ICatalogRepository repository, CancellationToken cancellation) =>
    Results.Ok(await repository.ImportAsync(document, null, null, true, cancellation)))
    .WithSummary("Проверить JSON и конфликты без изменения БД. Возвращает версию для импорта.");
admin.MapPost("/import", async (ImportRequest request, ClaimsPrincipal user, ICatalogRepository repository, CancellationToken cancellation) =>
{
    if (request.Version < 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["Некорректная версия справочника."] });
    return Results.Ok(await repository.ImportAsync(request.Catalog, request.Version, Guid.Parse(user.FindFirstValue("sub")!), false, cancellation));
}).WithSummary("Атомарно добавить/обновить записи после проверки версии. Отсутствующие записи сохраняются.");
if (app.Environment.IsDevelopment())
{
    app.UseSwagger(options => options.RouteTemplate = "skills/openapi/{documentName}.json");
    app.UseSwaggerUI(options => { options.RoutePrefix = "skills/swagger"; options.SwaggerEndpoint("../openapi/v1.json", "Skill Service v1"); options.ConfigObject.PersistAuthorization = false; });
}
if (builder.Configuration.GetValue<bool>("Skill:AutoMigrate") || args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
}
if (builder.Configuration.GetValue<bool>("Skill:SeedDemoCatalog"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ICatalogRepository>().SeedIfEmptyAsync(CancellationToken.None);
}
if (!args.Contains("--migrate")) await app.RunAsync();

public sealed class AdminSecurity : Swashbuckle.AspNetCore.SwaggerGen.IDocumentFilter
{
    public void Apply(OpenApiDocument document, Swashbuckle.AspNetCore.SwaggerGen.DocumentFilterContext context)
    {
        foreach (var path in document.Paths.Where(path => path.Key.StartsWith("/skills/admin/")))
            foreach (var operation in path.Value.Operations!.Values)
                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
    }
}
public partial class Program;
