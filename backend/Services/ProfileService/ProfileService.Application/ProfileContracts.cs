using System.Text.Json.Serialization;
using ProfileService.Domain;

namespace ProfileService.Application;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveProfileRequest(string? Direction, string? Level, string[]? Regions, string[]? Skills, long Version);

public interface IProfileRepository
{
    Task<UserProfile?> GetAsync(Guid userId, CancellationToken cancellation);
    Task<UserProfile?> SaveAsync(Guid userId, SaveProfileRequest request, CancellationToken cancellation);
    Task DeleteAsync(Guid userId, CancellationToken cancellation);
    Task DeleteAccountAsync(Guid userId, CancellationToken cancellation);
}

public sealed class DeletedProfileOwnerException : Exception { }

// Direction/region ownership stays with dictionary services; skill values come from SkillService.
public static class ProfileCatalog
{
    public static readonly string[] Directions = ["analyst", "csharp", "qa"];
    public static readonly string[] Levels = ["all", "intern", "junior", "middle", "senior"];
    public static readonly string[] Regions = ["Москва", "Санкт-Петербург", "Екатеринбург", "Владивосток", "Удалённая работа"];

    public static Dictionary<string, string[]> Validate(SaveProfileRequest request, IEnumerable<string> skillNames)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Direction is null || !Directions.Contains(request.Direction)) errors["direction"] = ["Выберите поддерживаемое направление."];
        if (request.Level is null || !Levels.Contains(request.Level)) errors["level"] = ["Выберите поддерживаемый уровень."];
        if (request.Regions is null || request.Regions.Length > Regions.Length || request.Regions.Any(region => !Regions.Contains(region)))
            errors["regions"] = ["Проверьте список регионов. Пустой список означает всю Россию."];
        var skills = skillNames.ToHashSet(StringComparer.Ordinal);
        if (request.Skills is null || request.Skills.Length > skills.Count || request.Skills.Any(skill => skill is null || !skills.Contains(skill)))
            errors["skills"] = ["Выберите навыки из справочника."];
        if (request.Version < 0 || request.Version == long.MaxValue) errors["version"] = ["Некорректная версия профиля."];
        return errors;
    }
}
