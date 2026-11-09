using MetGameHUB.Advice;

namespace MetGameHUB.Tests;

public class GoalTargetsTests
{
    [Theory]
    [InlineData(60, 60, 60, 60)]
    [InlineData(144, 60, 100, 144)]
    [InlineData(165, 60, 110, 165)]
    [InlineData(240, 60, 150, 240)]
    [InlineData(0, 60, 60, 60)]
    public void For_TargetsFollowRefreshRate(int hz, int pretty, int balanced, int smooth)
    {
        Assert.Equal(((int?)30, GoalKind.AtLeast), GoalTargets.For(GoalStep.MaxQuality, hz));
        Assert.Equal(((int?)pretty, GoalKind.Target), GoalTargets.For(GoalStep.Pretty, hz));
        Assert.Equal(((int?)balanced, GoalKind.Target), GoalTargets.For(GoalStep.Balanced, hz));
        Assert.Equal(((int?)smooth, GoalKind.Target), GoalTargets.For(GoalStep.Smooth, hz));
        Assert.Equal(((int?)null, GoalKind.Unlimited), GoalTargets.For(GoalStep.MaxFps, hz));
    }

    [Theory]
    [InlineData(59, 59)]
    [InlineData(75, 70)]
    [InlineData(30, 30)]
    public void For_OddRefreshRates_BalancedStaysBetween(int hz, int balanced) =>
        Assert.Equal(balanced, GoalTargets.For(GoalStep.Balanced, hz).Fps);

    [Fact]
    public void Label_PerKindAndDefaultSuffix()
    {
        Assert.Equal("Баланс · цель ~110 FPS", GoalTargets.Label(GoalStep.Balanced, 165, false));
        Assert.Equal("Макс. качество · не ниже 30 FPS (по умолчанию)", GoalTargets.Label(GoalStep.MaxQuality, 165, true));
        Assert.Equal("Макс. FPS · без ограничения", GoalTargets.Label(GoalStep.MaxFps, 165, false));
    }

    [Fact]
    public void Mode_MapsSteps() =>
        Assert.Equal(["max_quality", "quality", "balanced", "smooth", "max_fps"], Enum.GetValues<GoalStep>().Select(GoalTargets.Mode));
}
