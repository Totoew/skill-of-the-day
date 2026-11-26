using System.Text;
using System.Text.RegularExpressions;

namespace RecommendationService.Domain;

// Pure calculation: no databases, HTTP clients or browser-specific state.
public static partial class PlanCalculator
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
    private static string Normalize(string value) => Spaces().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " ").ToUpperInvariant();

    public static RecommendationPlan Calculate(PlanProfile profile, SkillCatalog catalog, VacancySnapshot snapshot,
        string source, int? layer, CancellationToken cancellation = default)
    {
        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        var codes = catalog.Skills.ToDictionary(skill => skill.Code, skill => skill.Name, StringComparer.Ordinal);
        foreach (var skill in catalog.Skills)
            foreach (var term in skill.Synonyms.Prepend(skill.Name))
            {
                var key = Normalize(term);
                if (terms.TryGetValue(key, out var existing) && existing != skill.Name)
                    throw new InvalidDataException("Ambiguous skill catalogue.");
                terms[key] = skill.Name;
            }
        string? Canonical(string term) => codes.GetValueOrDefault(term) ?? terms.GetValueOrDefault(Normalize(term));
        var known = profile.Skills.Select(skill => Canonical(skill) ?? skill).ToHashSet(StringComparer.Ordinal);
        var bases = catalog.Skills.Where(skill => skill.BasicFor.Contains(profile.Direction)).Select(skill => skill.Name).ToArray();
        var missingBases = bases.Where(skill => !known.Contains(skill)).ToArray();
        var noSkills = !catalog.Skills.Any(skill => skill.Directions.Contains(profile.Direction) && known.Contains(skill.Name));
        var scope = new List<Vacancy>(); var excluded = 0;
        foreach (var job in snapshot.Vacancies)
        {
            cancellation.ThrowIfCancellationRequested();
            if (job.Direction != profile.Direction || (profile.Level != "all" && job.Level != profile.Level) ||
                (profile.Regions.Length > 0 && !profile.Regions.Contains(job.Region) && !(job.Remote && profile.Regions.Contains("Удалённая работа")))) continue;
            if (!job.StructureRecognized || job.Excluded || job.Required.Any(skill => Canonical(skill) is null)) { excluded++; continue; }
            string[] Resolve(string[] values) => values.Select(Canonical).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            scope.Add(job with { Required = Resolve(job.Required), Desired = Resolve(job.Desired), Mentioned = Resolve(job.Mentioned) });
        }
        if (scope.Count > 5000 || scope.SelectMany(job => job.Required).Distinct().Count() > 256)
            throw new InvalidDataException("Vacancy slice exceeds calculation limits (5000 vacancies / 256 required skills).");
        var layers = scope.GroupBy(job => Missing(job, known).Length).OrderBy(group => group.Key).Select(group => new DeficitLayer(group.Key, group.Count())).ToArray();
        var active = layer is { } selected && layers.Any(item => item.Layer == selected) ? layer : null;
        var jobs = scope.Where(job => active is null || Missing(job, known).Length == active).ToArray();
        var basicMetrics = missingBases.Select(skill => { cancellation.ThrowIfCancellationRequested(); return Metric(jobs, known, skill); }).ToArray();
        var steps = new List<PlanStep>();
        var context = new HashSet<string>(known.Concat(bases), StringComparer.Ordinal);
        var limit = jobs.SelectMany(job => job.Required).Distinct().Count();
        for (var index = 0; !noSkills && index < limit; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var missing = jobs.Select(job => Missing(job, context)).Where(values => values.Length > 0).ToArray();
            if (missing.Length == 0) break;
            var nearest = missing.Min(values => values.Length);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var bundles = missing.Where(values => values.Length == nearest)
                .Where(values => seen.Add(string.Join('\0', values.Order(StringComparer.Ordinal))))
                .Select((values, order) => { cancellation.ThrowIfCancellationRequested(); return new { Values = values, Order = order, Coverage = Covered(jobs, new HashSet<string>(context.Concat(values), StringComparer.Ordinal)) }; })
                .OrderByDescending(item => item.Coverage).ThenBy(item => item.Order).ToArray();
            var chosen = bundles[0].Values.Take(5).ToArray();
            var before = context.ToArray(); var beforeCoverage = Covered(jobs, context);
            context.UnionWith(chosen);
            var afterCoverage = Covered(jobs, context);
            var metrics = chosen.Select(skill => Metric(jobs, new HashSet<string>(before.Concat(chosen.Where(value => value != skill)), StringComparer.Ordinal), skill)).ToArray();
            steps.Add(new(index, chosen, before, beforeCoverage, afterCoverage, afterCoverage - beforeCoverage,
                Points(afterCoverage - beforeCoverage, jobs.Length), bundles[0].Values.Length > 5, nearest, Minimum(jobs, context), metrics));
        }
        return new(profile.Version, catalog.Version, source, snapshot.SnapshotId, snapshot.CollectedAt, 5,
            scope.Count, jobs.Length, active, layers, Covered(jobs, known), Minimum(jobs, known), bases,
            missingBases, noSkills, basicMetrics, steps.ToArray(), jobs.Select(job => new EvidenceVacancy(job.Id, job.Title, SafeUrl(job.Url), job.Required)).ToArray(), excluded);
    }

    private static string[] Missing(Vacancy job, HashSet<string> skills) => job.Required.Where(skill => !skills.Contains(skill)).ToArray();
    private static int Covered(Vacancy[] jobs, HashSet<string> skills) => jobs.Count(job => job.Required.All(skills.Contains));
    private static int? Minimum(Vacancy[] jobs, HashSet<string> skills) => jobs.Select(job => Missing(job, skills).Length).Where(value => value > 0).Cast<int?>().Min();
    private static double Points(int gain, int total) => total == 0 ? 0 : 100d * gain / total;
    private static SkillMetric Metric(Vacancy[] jobs, HashSet<string> before, string skill)
    {
        var after = new HashSet<string>(before, StringComparer.Ordinal) { skill };
        var ids = jobs.Where(job => !job.Required.All(before.Contains) && job.Required.All(after.Contains)).Select(job => job.Id).ToArray();
        var covered = Covered(jobs, before);
        var category = jobs.Any(job => job.Required.Contains(skill)) ? "required" : jobs.Any(job => job.Desired.Contains(skill)) ? "desired" : jobs.Any(job => job.Mentioned.Contains(skill)) ? "mentioned" : "required";
        return new(skill, category, covered, covered + ids.Length, ids.Length, Points(ids.Length, jobs.Length), ids);
    }
    private static string? SafeUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var value) && value.Scheme is "http" or "https" && string.IsNullOrEmpty(value.UserInfo) ? value.AbsoluteUri : null;
}
