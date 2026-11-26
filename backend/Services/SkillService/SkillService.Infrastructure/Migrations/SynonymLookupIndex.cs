using FluentMigrator;

namespace SkillService.Infrastructure.Migrations;

[Migration(20261125010200)]
public sealed class SynonymLookupIndex : Migration
{
    public override void Up() => Execute.Sql("CREATE INDEX ix_skill_synonyms_skill_id ON skill.skill_synonyms(skill_id);");
    public override void Down() => Execute.Sql("DROP INDEX skill.ix_skill_synonyms_skill_id;");
}
