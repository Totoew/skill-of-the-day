using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using RecommendationService.Application;
using RecommendationService.Domain;

namespace RecommendationService.Infrastructure;

public static class SourceValidation
{
    private static readonly string[] Directions = ["analyst", "csharp", "qa"];
    private static readonly string[] Levels = ["all", "intern", "junior", "middle", "senior"];
    public static bool Profile(PlanProfile? profile, Guid userId) => profile is not null && profile.UserId == userId && profile.Version >= 0 &&
        Directions.Contains(profile.Direction) && Levels.Contains(profile.Level) && profile.Skills is { Length: <= 10000 } && profile.Skills.All(Term) &&
        profile.Regions is { Length: <= 100 } && profile.Regions.All(Term);
    private static bool Term(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255;
    public static bool Catalog(SkillCatalog? catalog) => catalog is { SchemaVersion: 1, Version: >= 0, Skills.Length: <= 10000 } &&
        catalog.Skills.All(skill => skill is not null && Term(skill.Code) && Term(skill.Name) && skill.Synonyms is { Length: <= 50 } && skill.Synonyms.All(Term) &&
            skill.Directions is not null && skill.BasicFor is not null && skill.Directions.All(Directions.Contains) && skill.BasicFor.All(skill.Directions.Contains)) &&
        catalog.Skills.Select(skill => skill.Code).Distinct().Count() == catalog.Skills.Length;
    public static bool Snapshot(VacancySnapshot? snapshot) => snapshot is { SchemaVersion: 1, Vacancies.Length: <= 10000 } &&
        Term(snapshot.SnapshotId) && snapshot.CollectedAt > DateTimeOffset.UnixEpoch &&
        snapshot.Vacancies.All(job => job is not null && Term(job.Id) && !string.IsNullOrWhiteSpace(job.Title) && job.Title.Length <= 500 &&
            Directions.Contains(job.Direction) && Levels.Contains(job.Level) && job.Level != "all" && Term(job.Region) &&
            job.Required is { Length: <= 64 } && job.Required.All(Term) && job.Desired is { Length: <= 64 } && job.Desired.All(Term) &&
            job.Mentioned is { Length: <= 64 } && job.Mentioned.All(Term) && (job.Url is null || job.Url.Length <= 2048)) &&
        snapshot.Vacancies.Select(job => job.Id).Distinct().Count() == snapshot.Vacancies.Length;
}

internal static class Upstream
{
    public static async Task<T> Read<T>(HttpClient client, string path, string? bearer, string label, Func<T?, bool> valid, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (bearer is not null) request.Headers.Authorization = AuthenticationHeaderValue.Parse(bearer);
            using var response = await client.SendAsync(request, cancellation);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && bearer is not null)
                throw new SessionRejectedException();
            response.EnsureSuccessStatusCode();
            var value = await response.Content.ReadFromJsonAsync<T>(cancellation);
            if (!valid(value)) throw new DependencyUnavailableException($"{label}: некорректный ответ. Расчёт не выполнен.");
            return value!;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new DependencyUnavailableException($"{label} временно недоступен. Повторите расчёт позже.");
        }
    }
}

public sealed class ProfileSource(HttpClient client) : IProfileSource
{
    public Task<PlanProfile> GetAsync(string bearer, Guid userId, CancellationToken cancellation) =>
        Upstream.Read<PlanProfile>(client, "profile/me", bearer, "Сервис профиля", profile => SourceValidation.Profile(profile, userId), cancellation);
}
public sealed class SkillSource(HttpClient client) : ISkillSource
{
    public Task<SkillCatalog> GetAsync(CancellationToken cancellation) =>
        Upstream.Read<SkillCatalog>(client, "skills/catalog", null, "Справочник навыков", SourceValidation.Catalog, cancellation);
}
public sealed class HttpVacancySource(HttpClient client) : IVacancySource
{
    public string Kind => "vacancy-service";
    public Task<VacancySnapshot> GetAsync(string direction, CancellationToken cancellation) => Upstream.Read<VacancySnapshot>(client,
        "vacancies/snapshots/latest?direction=" + Uri.EscapeDataString(direction), null, "Источник вакансий", SourceValidation.Snapshot, cancellation);
}
public sealed class DemoVacancySource : IVacancySource
{
    private readonly VacancySnapshot snapshot;
    public string Kind => "demo";
    public DemoVacancySource()
    {
        using var stream = typeof(DemoVacancySource).Assembly.GetManifestResourceStream("vacancies.demo.json")!;
        snapshot = JsonSerializer.Deserialize<VacancySnapshot>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (!SourceValidation.Snapshot(snapshot)) throw new InvalidOperationException("Invalid bundled demonstration snapshot.");
    }
    public Task<VacancySnapshot> GetAsync(string direction, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(snapshot);
    }
}
