using Dapper;
using Npgsql;
using ProfileService.Application;
using ProfileService.Domain;

namespace ProfileService.Infrastructure;

public sealed class ProfileRepository(NpgsqlDataSource source) : IProfileRepository
{
    public async Task<UserProfile?> GetAsync(Guid userId, CancellationToken cancellation)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        return await Read(connection, userId, null, cancellation);
    }

    public async Task<UserProfile?> SaveAsync(Guid userId, SaveProfileRequest request, CancellationToken cancellation)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await Lock(connection, userId, transaction, cancellation);
        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS (SELECT 1 FROM profile.deleted_accounts WHERE user_id = @UserId)", new { userId }, transaction, cancellationToken: cancellation)))
            throw new DeletedProfileOwnerException();
        var regions = request.Regions!.Distinct(StringComparer.Ordinal).ToArray();
        var skills = request.Skills!.Distinct(StringComparer.Ordinal).ToArray();
        var parameters = new { userId, request.Direction, request.Level, request.Version,
            RemoteWork = regions.Contains("Удалённая работа"),
            Regions = regions.Where(region => region != "Удалённая работа").ToArray(), Skills = skills };
        var sql = request.Version == 0
            ? """
              INSERT INTO profile.user_profiles (user_id, direction, level, remote_work, version)
              VALUES (@UserId, @Direction, @Level, @RemoteWork, 1)
              ON CONFLICT (user_id) DO NOTHING RETURNING version
              """
            : """
              UPDATE profile.user_profiles SET direction = @Direction, level = @Level,
                  remote_work = @RemoteWork, version = version + 1, updated_at = now()
              WHERE user_id = @UserId AND version = @Version RETURNING version
              """;
        var version = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellation));
        if (version is null) return null;
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM profile.user_skills WHERE user_id = @UserId;
            DELETE FROM profile.user_regions WHERE user_id = @UserId;
            INSERT INTO profile.user_skills (user_id, skill_name, position)
                SELECT @UserId, value, ordinality::integer FROM unnest(@Skills::text[]) WITH ORDINALITY AS items(value, ordinality);
            INSERT INTO profile.user_regions (user_id, region, position)
                SELECT @UserId, value, ordinality::integer FROM unnest(@Regions::text[]) WITH ORDINALITY AS items(value, ordinality);
            """, parameters, transaction, cancellationToken: cancellation));
        var profile = await Read(connection, userId, transaction, cancellation);
        await transaction.CommitAsync(cancellation);
        return profile;
    }

    public async Task DeleteAsync(Guid userId, CancellationToken cancellation)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM profile.user_profiles WHERE user_id = @UserId", new { userId }, cancellationToken: cancellation));
    }

    public async Task DeleteAccountAsync(Guid userId, CancellationToken cancellation)
    {
        await using var connection = await source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await Lock(connection, userId, transaction, cancellation);
        // Serialize with saves: a request authorized just before deletion cannot recreate the account's profile.
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO profile.deleted_accounts(user_id) VALUES (@UserId) ON CONFLICT DO NOTHING;
            DELETE FROM profile.user_profiles WHERE user_id = @UserId;
            """, new { userId }, transaction, cancellationToken: cancellation));
        await transaction.CommitAsync(cancellation);
    }

    private static Task Lock(NpgsqlConnection connection, Guid userId, NpgsqlTransaction transaction, CancellationToken cancellation) =>
        connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@UserId::text, 0))", new { userId }, transaction, cancellationToken: cancellation));

    private static async Task<UserProfile?> Read(NpgsqlConnection connection, Guid userId, NpgsqlTransaction? transaction, CancellationToken cancellation)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition("""
            SELECT p.user_id AS UserId, p.direction, p.level, p.version, p.updated_at AS UpdatedAt,
                ARRAY(SELECT r.region FROM profile.user_regions r WHERE r.user_id = p.user_id ORDER BY r.position)
                    || CASE WHEN p.remote_work THEN ARRAY['Удалённая работа'] ELSE ARRAY[]::text[] END AS Regions,
                ARRAY(SELECT s.skill_name FROM profile.user_skills s WHERE s.user_id = p.user_id ORDER BY s.position) AS Skills
            FROM profile.user_profiles p WHERE p.user_id = @UserId
            """, new { userId }, transaction, cancellationToken: cancellation));
        return row is null ? null : new(row.UserId, row.Direction, row.Level, row.Regions, row.Skills, row.Version, row.UpdatedAt);
    }

    private sealed class ProfileRow
    {
        public Guid UserId { get; set; }
        public string Direction { get; set; } = "";
        public string Level { get; set; } = "";
        public string[] Regions { get; set; } = [];
        public string[] Skills { get; set; } = [];
        public long Version { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
