using RecommendationService.Domain;

namespace RecommendationService.Application;

public interface IProfileSource { Task<PlanProfile> GetAsync(string bearer, Guid userId, CancellationToken cancellation); }
public interface ISkillSource { Task<SkillCatalog> GetAsync(CancellationToken cancellation); }
public interface IVacancySource
{
    string Kind { get; }
    Task<VacancySnapshot> GetAsync(string direction, CancellationToken cancellation);
}
public sealed class DependencyUnavailableException(string message) : Exception(message);
public sealed class SessionRejectedException : Exception { }
public sealed class StaleProfileException : Exception { }

public sealed class Recommendation(IProfileSource profiles, ISkillSource skills, IVacancySource vacancies)
{
    public async Task<RecommendationPlan> CalculateAsync(string bearer, Guid userId, long? expectedVersion, int? layer, CancellationToken cancellation)
    {
        var profileTask = profiles.GetAsync(bearer, userId, cancellation);
        var catalogTask = skills.GetAsync(cancellation);
        await Task.WhenAll(profileTask, catalogTask);
        var profile = await profileTask;
        if (expectedVersion is { } version && version != profile.Version) throw new StaleProfileException();
        var catalog = await catalogTask;
        var snapshot = await vacancies.GetAsync(profile.Direction, cancellation);
        try { return PlanCalculator.Calculate(profile, catalog, snapshot, vacancies.Kind, layer, cancellation); }
        catch (InvalidDataException) { throw new DependencyUnavailableException("Источник данных вернул некорректный или слишком большой срез. Расчёт не выполнен."); }
    }
}
