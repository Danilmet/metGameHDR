namespace MetGameHUB.Advice;

public enum GoalKind { AtLeast, Target, Unlimited }

public static class GoalTargets
{
    public static (int? Fps, GoalKind Kind) For(GoalStep step, int hz)
    {
        if (hz <= 0) hz = 60;
        int pretty = Math.Min(60, hz);
        int smooth = hz;
        // Balanced snaps to a multiple of 10 and stays inside [pretty, smooth] for odd refresh rates.
        return step switch
        {
            GoalStep.MaxQuality => (30, GoalKind.AtLeast),
            GoalStep.Pretty => (pretty, GoalKind.Target),
            GoalStep.Balanced => (Math.Clamp((int)Math.Round((pretty + smooth) / 20.0, MidpointRounding.AwayFromZero) * 10, pretty, smooth), GoalKind.Target),
            GoalStep.Smooth => (smooth, GoalKind.Target),
            _ => (null, GoalKind.Unlimited),
        };
    }

    public static string Mode(GoalStep step) => step switch
    {
        GoalStep.MaxQuality => "max_quality",
        GoalStep.Pretty => "quality",
        GoalStep.Balanced => "balanced",
        GoalStep.Smooth => "smooth",
        _ => "max_fps",
    };

    public static string Label(GoalStep step, int hz, bool isDefault)
    {
        var (fps, kind) = For(step, hz);
        string name = step switch
        {
            GoalStep.MaxQuality => "Макс. качество",
            GoalStep.Pretty => "Красиво",
            GoalStep.Balanced => "Баланс",
            GoalStep.Smooth => "Плавно",
            _ => "Макс. FPS",
        };
        string text = kind switch
        {
            GoalKind.AtLeast => $"{name} · не ниже {fps} FPS",
            GoalKind.Target => $"{name} · цель ~{fps} FPS",
            _ => $"{name} · без ограничения",
        };
        return isDefault ? text + " (по умолчанию)" : text;
    }
}
