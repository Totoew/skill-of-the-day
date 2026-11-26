namespace RecommendationService.Domain;

public sealed record PlanProfile(Guid UserId, string Direction, string Level, string[] Regions, string[] Skills, long Version);
public sealed record CatalogSkill(string Code, string Name, string[] Synonyms, string[] Directions, string[] BasicFor);
public sealed record SkillCatalog(int SchemaVersion, long Version, CatalogSkill[] Skills);
public sealed record Vacancy(string Id, string Direction, string Title, string Level, string Region, bool Remote,
    string[] Required, string[] Desired, string[] Mentioned, string? Url, bool StructureRecognized = true, bool Excluded = false);
public sealed record VacancySnapshot(int SchemaVersion, string SnapshotId, DateTimeOffset CollectedAt, Vacancy[] Vacancies);
public sealed record EvidenceVacancy(string Id, string Title, string? Url, string[] Required);
public sealed record DeficitLayer(int Layer, int Count);
public sealed record SkillMetric(string Skill, string Category, int Before, int After, int Gain, double Points, string[] BasisIds);
public sealed record PlanStep(int Index, string[] Skills, string[] Before, int BeforeCoverage, int AfterCoverage,
    int Gain, double PercentagePoints, bool Partial, int MinimumBefore, int? MinimumAfter, SkillMetric[] Metrics);
public sealed record RecommendationPlan(long ProfileVersion, long CatalogVersion, string Source, string SnapshotId,
    DateTimeOffset SnapshotDate, int UpdateIntervalDays, int ScopeCount, int Total, int? SelectedLayer,
    DeficitLayer[] Layers, int Covered, int? MinimumDeficit, string[] BasicSkills, string[] RemainingBasics,
    bool NoSkills, SkillMetric[] Basics, PlanStep[] Steps, EvidenceVacancy[] Jobs, int ExcludedVacancies);
