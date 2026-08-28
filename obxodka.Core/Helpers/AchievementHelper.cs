namespace obxodka.Core.Helpers;

public static class AchievementHelper
{
    public static readonly IReadOnlyList<AchievementDef> AllAchievements =
    [
        new("ach_first_blood", "Теневой серфер", "Потратил > 100 ГБ трафика", "surfing", "text-[#00e5ff]"),
        new("ach_whale", "Кибер-кит", "Потратил > 1 ТБ трафика", "water_ec", "text-[#8B5CF6]"),
        new("ach_ambassador", "Амбассадор", "Ваш промокод ввели 3+ раз", "campaign", "text-[#a855f7]"),
        new("ach_influencer", "Инфлюенсер", "Ваш промокод ввели 10/10 раз", "stars", "text-[#ffaa00]"),
        new("ach_veteran", "Кибер-ветеран", "Провел в сети > 5,000 часов", "military_tech", "text-gray-400"),
        new("ach_legend", "Легенда", "Провел в сети > 10,000 часов", "diamond", "text-[#ff0055]"),
        new("ach_reviewer", "Голос народа", "Оставил популярный отзыв (10+ лайков)", "record_voice_over", "text-[#10b981]"),
        new("ach_sponsor", "Спонсор", "Пополнил баланс 5+ раз", "volunteer_activism", "text-[#f43f5e]"),
        new("ach_hacker", "Хакерок", "Активировал секретную пасхалку", "terminal", "text-[#22c55e]"),
        new("ach_multiplexer", "Мультиплексор", "Подключил 4/4 устройств", "devices_other", "text-[#3b82f6]")
    ];

    public static readonly FrozenDictionary<string, AchievementDef> AchievementsById =
        AllAchievements.ToFrozenDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

    public static bool CheckAndGrantAchievements(User user, int orderCount = 0, int maxLikesOnReviews = 0)
    {
        var existingAchs = user.Achievements?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var changed = false;

        void Grant(string id)
        {
            if (existingAchs.Add(id))
            {
                changed = true;
            }
        }

        var gbUsed = user.TotalBytesUsed / (1024.0 * 1024.0 * 1024.0);
        if (gbUsed >= 100)
        {
            Grant("ach_first_blood");
        }

        if (gbUsed >= 1024)
        {
            Grant("ach_whale");
        }

        if (user.OwnCodeActivatedCount >= 3)
        {
            Grant("ach_ambassador");
        }

        if (user.OwnCodeActivatedCount >= 10)
        {
            Grant("ach_influencer");
        }

        var hoursUsed = user.TotalSecondsUsed / 3600;
        if (hoursUsed >= 5000)
        {
            Grant("ach_veteran");
        }

        if (hoursUsed >= 10000)
        {
            Grant("ach_legend");
        }

        if (maxLikesOnReviews >= 10)
        {
            Grant("ach_reviewer");
        }

        if (orderCount >= 5)
        {
            Grant("ach_sponsor");
        }

        if (user.Devices is { Count: >= 4 })
        {
            Grant("ach_multiplexer");
        }

        if (changed)
        {
            user.Achievements = string.Join(",", existingAchs);
        }

        return changed;
    }
}
