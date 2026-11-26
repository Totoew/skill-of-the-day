using System.Text.Json;

namespace ProfileService.Api.Clients;

public sealed record CatalogSkill(string Code, string Name, string[] Directions, string[] BasicFor);
public sealed record SkillCatalog(int SchemaVersion, long Version, CatalogSkill[] Skills);
public sealed class CatalogUnavailableException : Exception { }

public sealed class SkillCatalogClient(HttpClient client)
{
    public async Task<SkillCatalog> GetAsync(CancellationToken cancellation)
    {
        try
        {
            using var response = await client.GetAsync("skills/catalog", cancellation);
            response.EnsureSuccessStatusCode();
            var catalog = await response.Content.ReadFromJsonAsync<SkillCatalog>(cancellation);
            if (catalog is null || catalog.SchemaVersion != 1 || catalog.Skills is null || catalog.Skills.Length > 10000 ||
                catalog.Skills.Any(skill => skill is null || string.IsNullOrEmpty(skill.Name) || skill.Directions is null))
                throw new CatalogUnavailableException();
            return catalog;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        { throw new CatalogUnavailableException(); }
    }
}
