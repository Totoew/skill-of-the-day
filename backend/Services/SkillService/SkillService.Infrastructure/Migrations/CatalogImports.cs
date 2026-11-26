using FluentMigrator;

namespace SkillService.Infrastructure.Migrations;

[Migration(20261125010100)]
public sealed class CatalogImports : Migration
{
    public override void Up() => Execute.Sql("""
        ALTER TABLE skill.skills ADD COLUMN code varchar(64);
        UPDATE skill.skills SET code = 'legacy-' || id;
        ALTER TABLE skill.skills ALTER COLUMN code SET NOT NULL;
        CREATE UNIQUE INDEX ux_skill_code ON skill.skills(code);
        CREATE UNIQUE INDEX ux_skill_name ON skill.skills(lower(btrim(name)));
        CREATE UNIQUE INDEX ux_skill_synonym ON skill.skill_synonyms(lower(btrim(synonym)));
        CREATE TABLE skill.skill_directions (
            skill_id bigint NOT NULL REFERENCES skill.skills(id) ON DELETE CASCADE,
            direction varchar(32) NOT NULL CHECK (direction IN ('analyst', 'csharp', 'qa')),
            is_basic boolean NOT NULL DEFAULT false,
            PRIMARY KEY (skill_id, direction)
        );
        CREATE TABLE skill.catalog_state (id integer PRIMARY KEY CHECK(id = 1), version bigint NOT NULL CHECK(version >= 0));
        INSERT INTO skill.catalog_state(id, version) VALUES (1, 0);
        CREATE TABLE skill.catalog_imports (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            actor_id uuid,
            imported_at timestamptz NOT NULL DEFAULT now(),
            version bigint NOT NULL,
            added integer NOT NULL,
            updated integer NOT NULL,
            unchanged integer NOT NULL
        );
        """);
    public override void Down() => throw new NotSupportedException("Catalog imports require a forward migration or a database backup.");
}
