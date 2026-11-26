using Microsoft.EntityFrameworkCore;

namespace AuthService.Data;

public sealed class ProfileCleanupWorker(IServiceScopeFactory scopes, IHttpClientFactory clients,
    IConfiguration configuration, ILogger<ProfileCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Standalone Auth installations can leave delivery disabled until ProfileService is connected.
        if (string.IsNullOrEmpty(configuration["ProfileCleanup:Address"])) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                var tasks = await db.AccountDeletions.FromSqlRaw("""
                    SELECT * FROM auth."AccountDeletions" WHERE "CompletedAt" IS NULL AND "NextAttemptAt" <= now()
                    ORDER BY "CreatedAt" LIMIT 10 FOR UPDATE SKIP LOCKED
                    """).ToListAsync(stoppingToken);
                foreach (var task in tasks)
                {
                    task.Attempts++;
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Delete, $"internal/profiles/{task.UserId}");
                        request.Headers.Add("X-Service-Key", configuration["ProfileCleanup:Key"]);
                        using var response = await clients.CreateClient("profile-cleanup").SendAsync(request, stoppingToken);
                        response.EnsureSuccessStatusCode();
                        task.CompletedAt = DateTimeOffset.UtcNow;
                    }
                    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
                    {
                        if (stoppingToken.IsCancellationRequested) throw;
                        task.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, Math.Pow(2, Math.Min(task.Attempts, 8)) * 2));
                        logger.LogWarning("Profile cleanup {TaskId} will be retried ({Type})", task.Id, error.GetType().Name);
                    }
                }
                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Profile cleanup queue unavailable ({Type})", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
