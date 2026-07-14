namespace obxodka.Core.Helpers;

public class AchievementDef
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string Icon { get; set; }
    public required string Color { get; set; }
}

public static class AchievementHelper
{
    public static readonly List<AchievementDef> AllAchievements =
    [
        new AchievementDef { Id = "ach_first_blood", Name = "Теневой серфер", Description = "Потратил > 100 ГБ трафика", Icon = "surfing", Color = "text-[#00e5ff]" },
        new AchievementDef { Id = "ach_whale", Name = "Кибер-кит", Description = "Потратил > 1 ТБ трафика", Icon = "water_ec", Color = "text-[#8B5CF6]" },
        new AchievementDef { Id = "ach_ambassador", Name = "Амбассадор", Description = "Ваш промокод ввели 3+ раз", Icon = "campaign", Color = "text-[#a855f7]" },
        new AchievementDef { Id = "ach_influencer", Name = "Инфлюенсер", Description = "Ваш промокод ввели 10/10 раз", Icon = "stars", Color = "text-[#ffaa00]" },
        new AchievementDef { Id = "ach_veteran", Name = "Кибер-ветеран", Description = "Провел в сети > 5,000 часов", Icon = "military_tech", Color = "text-gray-400" },
        new AchievementDef { Id = "ach_legend", Name = "Легенда", Description = "Провел в сети > 10,000 часов", Icon = "diamond", Color = "text-[#ff0055]" },
        new AchievementDef { Id = "ach_reviewer", Name = "Голос народа", Description = "Оставил популярный отзыв (10+ лайков)", Icon = "record_voice_over", Color = "text-[#10b981]" },
        new AchievementDef { Id = "ach_sponsor", Name = "Спонсор", Description = "Пополнил баланс 5+ раз", Icon = "volunteer_activism", Color = "text-[#f43f5e]" },
        new AchievementDef { Id = "ach_hacker", Name = "Хакерок", Description = "Активировал секретную пасхалку", Icon = "terminal", Color = "text-[#22c55e]" },
        new AchievementDef { Id = "ach_multiplexer", Name = "Мультиплексор", Description = "Подключил 4/4 устройств", Icon = "devices_other", Color = "text-[#3b82f6]" }
    ];

    public static bool CheckAndGrantAchievements(User user, int orderCount = 0, int maxLikesOnReviews = 0)
    {
        var existingAchs = user.Achievements?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];
        var changed = false;

        void Grant(string id)
        {
            if (!existingAchs.Contains(id))
            {
                existingAchs.Add(id);
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

        if (user.Devices != null && user.Devices.Count >= 4)
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
