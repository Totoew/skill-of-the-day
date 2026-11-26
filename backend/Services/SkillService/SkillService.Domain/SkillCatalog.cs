using System.Text.Json.Serialization;

namespace SkillService.Domain;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillDefinition(string Code, string Name, string[] Synonyms, string[] Directions, string[] BasicFor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogDocument(int SchemaVersion, SkillDefinition[] Skills);

public sealed record CatalogSnapshot(long Version, int SchemaVersion, SkillDefinition[] Skills);
public sealed record ImportSummary(long Version, int Added, int Updated, int Unchanged, int TotalSkills);
