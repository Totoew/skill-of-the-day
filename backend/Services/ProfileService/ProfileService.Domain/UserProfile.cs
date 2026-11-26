namespace ProfileService.Domain;

public sealed record UserProfile(Guid UserId, string Direction, string Level, string[] Regions,
    string[] Skills, long Version, DateTimeOffset? UpdatedAt)
{
    public static UserProfile Empty(Guid userId) => new(userId, "analyst", "all",
        ["Москва", "Удалённая работа"], [], 0, null);
}
