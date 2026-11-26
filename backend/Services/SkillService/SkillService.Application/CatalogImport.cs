using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SkillService.Domain;

namespace SkillService.Application;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportRequest(CatalogDocument Catalog, long Version);

public interface ICatalogRepository
{
    Task<CatalogSnapshot> GetAsync(CancellationToken cancellation);
    Task<ImportSummary> ImportAsync(CatalogDocument document, long? expectedVersion, Guid? actor, bool preview, CancellationToken cancellation);
    Task SeedIfEmptyAsync(CancellationToken cancellation);
}

public sealed class CatalogValidationException(Dictionary<string, string[]> errors) : Exception("Некорректный справочник.")
{ public Dictionary<string, string[]> Errors { get; } = errors; }
public sealed class CatalogConflictException(string message) : Exception(message);

public static partial class CatalogImport
{
    public static readonly string[] DirectionCodes = ["analyst", "csharp", "qa"];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static CatalogDocument Example()
    {
        using var stream = typeof(CatalogImport).Assembly.GetManifestResourceStream("catalog.example.json")!;
        return JsonSerializer.Deserialize<CatalogDocument>(stream, Json)!;
    }

    public static string Normalize(string value) => Whitespace().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ").ToUpperInvariant();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$")]
    private static partial Regex CodePattern();

    public static void Validate(CatalogDocument? document)
    {
        var errors = new Dictionary<string, string[]>();
        void Error(string key, string message) { if (errors.Count < 50) errors.TryAdd(key, [message]); }
        if (document is null) throw new CatalogValidationException(new() { ["catalog"] = ["Передайте объект справочника."] });
        if (document.SchemaVersion != 1) Error("schemaVersion", "Поддерживается только schemaVersion: 1.");
        if (document.Skills is not { Length: >= 1 and <= 2000 })
            Error("skills", "Файл должен содержать от 1 до 2000 навыков.");
        else
        {
            var codes = new HashSet<string>(StringComparer.Ordinal);
            var terms = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < document.Skills.Length; i++)
            {
                var item = document.Skills[i]; var key = $"skills[{i}]";
                if (item is null) { Error(key, "Ожидается объект навыка."); continue; }
                if (item.Code is null || !CodePattern().IsMatch(item.Code) || !codes.Add(item.Code))
                    Error(key + ".code", "Уникальный код: строчные латинские буквы, цифры и дефис, до 64 символов; первый символ — буква.");
                bool ValidTerm(string? term) => term is { Length: >= 1 and <= 255 } && term == term.Trim() && !term.Any(char.IsControl) && !string.IsNullOrWhiteSpace(term);
                if (!ValidTerm(item.Name)) Error(key + ".name", "Название: от 1 до 255 символов, без крайних пробелов и управляющих символов.");
                else if (!terms.Add(Normalize(item.Name))) Error(key + ".name", "Название или синоним повторяется в файле.");
                if (item.Synonyms is null || item.Synonyms.Length > 50) Error(key + ".synonyms", "Нужен массив synonyms, не более 50 значений.");
                else foreach (var term in item.Synonyms)
                {
                    if (!ValidTerm(term)) Error(key + ".synonyms", "Синонимы: непустые строки до 255 символов, без крайних пробелов и управляющих символов.");
                    else if (!terms.Add(Normalize(term))) Error(key + ".synonyms", "Название или синоним повторяется в файле.");
                }
                if (item.Directions is not { Length: >= 1 and <= 3 } || item.Directions.Any(value => !DirectionCodes.Contains(value)) || item.Directions.Distinct().Count() != item.Directions.Length)
                    Error(key + ".directions", "Укажите уникальные направления: analyst, csharp, qa; хотя бы одно.");
                if (item.BasicFor is null || item.BasicFor.Length > 3 || item.BasicFor.Any(value => item.Directions is null || !item.Directions.Contains(value)) || item.BasicFor.Distinct().Count() != item.BasicFor.Length)
                    Error(key + ".basicFor", "Массив basicFor должен содержать только уникальные значения из directions; [] означает специализированный навык.");
            }
        }
        if (errors.Count > 0) throw new CatalogValidationException(errors);
    }

    public static ImportSummary Plan(CatalogSnapshot current, CatalogDocument incoming)
    {
        // Validate the resulting catalogue, not only the incoming fragment. This catches collisions with existing aliases.
        var existing = current.Skills.ToDictionary(item => item.Code, StringComparer.Ordinal);
        foreach (var item in incoming.Skills)
        {
            if (existing.TryGetValue(item.Code, out var old) && old.Name != item.Name)
                throw new CatalogConflictException($"Код «{item.Code}» уже принадлежит навыку «{old.Name}». Переименование требует отдельной миграции профилей; сохраните прежнее название.");
        }
        var merged = new Dictionary<string, SkillDefinition>(existing, StringComparer.Ordinal);
        foreach (var item in incoming.Skills) merged[item.Code] = item;
        if (merged.Count > 10000) throw new CatalogConflictException("Справочник не может содержать более 10 000 навыков.");
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in merged.Values)
            foreach (var term in item.Synonyms.Prepend(item.Name))
                if (!owners.TryAdd(Normalize(term), item.Code))
                    throw new CatalogConflictException($"Название или синоним «{term}» уже используется. Устраните дублирование.");
        static bool Equal(SkillDefinition a, SkillDefinition b) => a.Name == b.Name &&
            a.Synonyms.Order(StringComparer.Ordinal).SequenceEqual(b.Synonyms.Order(StringComparer.Ordinal)) &&
            a.Directions.Order().SequenceEqual(b.Directions.Order()) && a.BasicFor.Order().SequenceEqual(b.BasicFor.Order());
        var added = incoming.Skills.Count(item => !existing.ContainsKey(item.Code));
        var unchanged = incoming.Skills.Count(item => existing.TryGetValue(item.Code, out var old) && Equal(old, item));
        return new(current.Version, added, incoming.Skills.Length - added - unchanged, unchanged, merged.Count);
    }
}
