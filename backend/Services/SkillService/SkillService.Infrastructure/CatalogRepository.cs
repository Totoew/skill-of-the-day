using System.Text.Json;
using Dapper;
using Npgsql;
using SkillService.Application;
using SkillService.Domain;

namespace SkillService.Infrastructure;

public sealed class CatalogRepository(NpgsqlDataSource database) : ICatalogRepository
{
    private const string SnapshotSql = """
        SELECT jsonb_build_object('version', c.version, 'schemaVersion', 1, 'skills',
          COALESCE((SELECT jsonb_agg(jsonb_build_object(
            'code', s.code, 'name', s.name,
            'synonyms', COALESCE((SELECT jsonb_agg(synonym ORDER BY synonym) FROM skill.skill_synonyms WHERE skill_id=s.id), '[]'::jsonb),
            'directions', COALESCE((SELECT jsonb_agg(direction ORDER BY direction) FROM skill.skill_directions WHERE skill_id=s.id), '[]'::jsonb),
            'basicFor', COALESCE((SELECT jsonb_agg(direction ORDER BY direction) FROM skill.skill_directions WHERE skill_id=s.id AND is_basic), '[]'::jsonb)
          ) ORDER BY s.id) FROM skill.skills s), '[]'::jsonb))::text FROM skill.catalog_state c WHERE c.id=1
        """;
    private static async Task<CatalogSnapshot> Read(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellation) =>
        JsonSerializer.Deserialize<CatalogSnapshot>(await connection.QuerySingleAsync<string>(new(SnapshotSql, transaction: transaction, cancellationToken: cancellation)), CatalogImport.Json)!;
    public async Task<CatalogSnapshot> GetAsync(CancellationToken cancellation)
    {
        await using var connection = await database.OpenConnectionAsync(cancellation);
        return await Read(connection, null, cancellation);
    }

    public async Task<ImportSummary> ImportAsync(CatalogDocument document, long? expectedVersion, Guid? actor, bool preview, CancellationToken cancellation)
    {
        CatalogImport.Validate(document);
        await using var connection = await database.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        // Serialize previews/imports across all service instances; no partial catalogue can become visible.
        await connection.ExecuteAsync(new("SELECT pg_advisory_xact_lock(72619112501)", transaction: transaction, cancellationToken: cancellation));
        var current = await Read(connection, transaction, cancellation);
        if (expectedVersion is { } version && current.Version != version)
            throw new CatalogConflictException("Справочник изменён другим администратором. Проверьте файл повторно перед импортом.");
        var plan = CatalogImport.Plan(current, document);
        if (preview || plan.Added + plan.Updated == 0) return plan;
        // Remove aliases for the whole incoming fragment first: one atomic import may transfer an alias between skills.
        await connection.ExecuteAsync(new("""
            DELETE FROM skill.skill_synonyms WHERE skill_id IN (SELECT id FROM skill.skills WHERE code=ANY(@Codes));
            DELETE FROM skill.skill_directions WHERE skill_id IN (SELECT id FROM skill.skills WHERE code=ANY(@Codes));
            """, new { Codes = document.Skills.Select(item => item.Code).ToArray() }, transaction, cancellationToken: cancellation));
        foreach (var item in document.Skills)
        {
            var id = await connection.QuerySingleAsync<long>(new("""
                INSERT INTO skill.skills(code, name) VALUES (@Code, @Name)
                ON CONFLICT(code) DO UPDATE SET name=EXCLUDED.name RETURNING id
                """, item, transaction, cancellationToken: cancellation));
            // All values are parameters, including names, synonyms and direction codes.
            await connection.ExecuteAsync(new("INSERT INTO skill.skill_synonyms(skill_id, synonym) SELECT @Id, unnest(@Synonyms::text[])", new { Id = id, item.Synonyms }, transaction, cancellationToken: cancellation));
            await connection.ExecuteAsync(new("""
                INSERT INTO skill.skill_directions(skill_id, direction, is_basic)
                SELECT @Id, value, value=ANY(@BasicFor::text[]) FROM unnest(@Directions::text[]) value
                """, new { Id = id, item.Directions, item.BasicFor }, transaction, cancellationToken: cancellation));
        }
        var next = plan with { Version = checked(current.Version + 1) };
        await connection.ExecuteAsync(new("""
            UPDATE skill.catalog_state SET version=@Version WHERE id=1;
            INSERT INTO skill.catalog_imports(actor_id, version, added, updated, unchanged) VALUES (@Actor, @Version, @Added, @Updated, @Unchanged)
            """, new { Actor = actor, next.Version, next.Added, next.Updated, next.Unchanged }, transaction, cancellationToken: cancellation));
        await transaction.CommitAsync(cancellation);
        return next;
    }

    public async Task SeedIfEmptyAsync(CancellationToken cancellation)
    {
        var current = await GetAsync(cancellation);
        if (current.Version != 0 || current.Skills.Length != 0) return;
        try { await ImportAsync(CatalogImport.Example(), 0, null, false, cancellation); }
        catch (CatalogConflictException) { /* Another instance has already seeded the catalogue. */ }
    }
}
