using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.OpenApi;
using RecommendationService.Api.Security;
using RecommendationService.Application;
using RecommendationService.Infrastructure;
using SkillDay.ServiceDefaults;

if (args.Contains("--healthcheck"))
{
    try { using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; Environment.ExitCode = (await client.GetAsync("http://127.0.0.1:8080/health/ready")).IsSuccessStatusCode ? 0 : 1; }
    catch { Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 1024);
builder.AddServiceTelemetry("recommendation-service");
builder.Services.AddScoped<Recommendation>();
void ConfigureClient(HttpClient client, string key, string? fallback = null)
{
    var address = builder.Configuration[key] ?? fallback;
    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        throw new InvalidOperationException($"{key} must be an HTTP(S) base URL without credentials, query or fragment.");
    client.BaseAddress = uri;
    client.Timeout = TimeSpan.FromSeconds(8);
    client.MaxResponseContentBufferSize = 16_000_000;
}
builder.Services.AddHttpClient("auth", client => ConfigureClient(client, "Auth:Address", "http://auth-service:8080/"));
builder.Services.AddHttpClient<IProfileSource, ProfileSource>(client => ConfigureClient(client, "Profile:Address", "http://profile-service:8080/"));
builder.Services.AddHttpClient<ISkillSource, SkillSource>(client => ConfigureClient(client, "Skills:Address", "http://skill-service:8080/"));
var provider = builder.Configuration["Vacancies:Provider"] ?? "Http";
if (provider == "Demo")
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("The Demo vacancy source is only allowed in Development.");
    builder.Services.AddSingleton<IVacancySource, DemoVacancySource>();
}
else if (provider == "Http") builder.Services.AddHttpClient<IVacancySource, HttpVacancySource>(client => ConfigureClient(client, "Vacancies:Address"));
else throw new InvalidOperationException("Vacancies:Provider must be Demo or Http.");
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("Session", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Навык дня — Recommendation Service", Version = "v1" });
    options.AddServer(new OpenApiServer { Url = "/api" });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] });
    options.DocInclusionPredicate((_, description) => description.RelativePath?.StartsWith("recommendations/") == true);
});
var app = builder.Build();
// Validate configured clients and the source at startup, rather than on the first customer request.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<IProfileSource>();
    scope.ServiceProvider.GetRequiredService<ISkillSource>();
    scope.ServiceProvider.GetRequiredService<IVacancySource>();
    scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("auth");
}
app.UseServiceRequestLogging();
app.UseExceptionHandler(new ExceptionHandlerOptions { StatusCodeSelector = error => error is BadHttpRequestException bad ? bad.StatusCode : 500 });
app.Use(async (http, next) =>
{
    http.Response.Headers.CacheControl = "no-store";
    try { await next(); }
    catch (SessionRejectedException) { await Results.Problem(statusCode: 401, title: "Сессия завершена. Войдите снова.").ExecuteAsync(http); }
    catch (StaleProfileException) { await Results.Problem(statusCode: 409, title: "Профиль изменён на другом устройстве. Загрузите актуальный профиль и повторите расчёт.").ExecuteAsync(http); }
    catch (DependencyUnavailableException error) { await Results.Problem(statusCode: 503, title: error.Message).ExecuteAsync(http); }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
// Liveness/readiness of the process; unavailable dependencies fail calculation with 503, never a fake empty result.
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", source = provider }));
var recommendations = app.MapGroup("/recommendations").WithTags("Recommendations").RequireAuthorization();
recommendations.MapGet("/plan", Calculate).Produces<RecommendationService.Domain.RecommendationPlan>()
    .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409).ProducesProblem(503).ProducesProblem(504)
    .WithSummary("План своего профиля: охват, слои, базовые навыки, шаги и вакансии-основания.");
recommendations.MapPost("/recalculate", Calculate).Produces<RecommendationService.Domain.RecommendationPlan>()
    .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409).ProducesProblem(503).ProducesProblem(504)
    .WithSummary("Повторно рассчитать план по актуальному профилю, справочнику и срезу. Тело не требуется.");
if (app.Environment.IsDevelopment())
{
    app.UseSwagger(options => options.RouteTemplate = "recommendations/openapi/{documentName}.json");
    app.UseSwaggerUI(options => { options.RoutePrefix = "recommendations/swagger"; options.SwaggerEndpoint("../openapi/v1.json", "Recommendation Service v1"); options.ConfigObject.PersistAuthorization = false; });
}
await app.RunAsync();

static async Task<IResult> Calculate(HttpContext http, long? profileVersion, int? layer, Recommendation recommendation)
{
    if (http.Request.Query.Any(pair => pair.Key is not ("profileVersion" or "layer") || pair.Value.Count != 1) ||
        http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding") || profileVersion < 0 || layer < 0 || layer > 256)
        return Results.Problem(statusCode: 400, title: "Допустимы только параметры profileVersion и layer (0–256). Профиль и вакансии определяются сервисом; тело запроса не принимается.");
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    try
    {
        return Results.Ok(await recommendation.CalculateAsync(http.Request.Headers.Authorization.ToString(),
            Guid.Parse(http.User.FindFirstValue("sub")!), profileVersion, layer, timeout.Token));
    }
    catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
    { return Results.Problem(statusCode: 504, title: "Расчёт занял слишком много времени. Повторите позже."); }
}
public partial class Program;
