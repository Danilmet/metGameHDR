using MetGameHUB.Advice;
using MetGameHUB.Ui;

namespace MetGameHUB.Tests.Ui;

public class AdviceTableTests
{
    static Advice.Advice Adv(params SettingAdvice[] s) => new(new AdviceResult("", s.ToList(), null), DateTime.UtcNow);

    [Fact]
    public void Build_NoAdvice_RowsFromCurrentWithDash()
    {
        var rows = AdviceTable.Build(null, [new("Тени", "Medium", "ScalabilityGroups", "sg.ShadowQuality")]);
        Assert.Equal([new AdviceRowVm("Тени", "Medium", "—", "", true)], rows);
    }

    [Fact]
    public void Build_Advice_CurrentByKeyCaseInsensitive()
    {
        var rows = AdviceTable.Build(Adv(new SettingAdvice("Shadows", "Old", "Low", "S", "k", "0", "r")), [new("x", "New", "s", "K")]);
        Assert.Equal("New", rows[0].Current);
    }

    [Fact]
    public void Build_Advice_NoMatchOrNoCurrent_UsesAdvice()
    {
        var advice = Adv(new SettingAdvice("A", "Old", "Low", "S", "k", "0", "r"), new SettingAdvice("B", null, "Low", null, null, null, "r"));
        Assert.Equal("Old", AdviceTable.Build(advice, null)[0].Current);
        var rows = AdviceTable.Build(advice, [new("x", "New", "S", "other")]);
        Assert.Equal("Old", rows[0].Current);
        Assert.Equal("—", rows[1].Current);
    }
}
