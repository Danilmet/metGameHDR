using MetGameHUB.Advice;

namespace MetGameHUB.Tests;

public class UnrealScalabilityTests
{
    [Theory]
    [InlineData("0", "Low")]
    [InlineData("2", "High")]
    [InlineData("4", "Cinematic")]
    public void Describe_Levels(string raw, string label) =>
        Assert.Equal(new CurrentSetting("Тени", label, "ScalabilityGroups", "sg.ShadowQuality"),
            UnrealScalability.Describe("scalabilitygroups", "sg.ShadowQuality", raw));

    [Theory]
    [InlineData("abc")]
    [InlineData("7")]
    public void Describe_UnknownValues_Raw(string raw) =>
        Assert.Equal(raw, UnrealScalability.Describe("ScalabilityGroups", "sg.TextureQuality", raw)!.Current);

    [Fact]
    public void Describe_ResolutionAndForeignKeys()
    {
        Assert.Equal("100%", UnrealScalability.Describe("ScalabilityGroups", "sg.ResolutionQuality", "100.000000")!.Current);
        Assert.Equal("Масштаб разрешения", UnrealScalability.Describe("ScalabilityGroups", "sg.ResolutionQuality", "75")!.Name);
        Assert.Null(UnrealScalability.Describe("Other", "sg.ShadowQuality", "3"));
        Assert.Null(UnrealScalability.Describe(null, "sg.ShadowQuality", "3"));
        Assert.Null(UnrealScalability.Describe("ScalabilityGroups", "sg.Unknown", "3"));
    }

    [Fact]
    public void FromFile_TableOrder()
    {
        var text = "[ScalabilityGroups]\r\nsg.TextureQuality=3\r\nsg.ResolutionQuality=100.000000\r\nsg.ShadowQuality=1\r\n";
        Assert.Equal(["Масштаб разрешения", "Тени", "Текстуры"], UnrealScalability.FromFile(text).Select(s => s.Name));
    }

    [Fact]
    public void Merge_OverridesCurrentAndAppendsMissing()
    {
        var text = "[ScalabilityGroups]\nsg.ShadowQuality=1\nsg.TextureQuality=3\n";
        List<CurrentSetting> model = [new("Shadows", "Ultra", "ScalabilityGroups", "sg.ShadowQuality"), new("VSync", "On", "S", "bUseVSync")];
        var merged = UnrealScalability.Merge(model, text);
        Assert.Equal(new CurrentSetting("Shadows", "Medium", "ScalabilityGroups", "sg.ShadowQuality"), merged[0]);
        Assert.Equal("On", merged[1].Current);
        Assert.Equal(new CurrentSetting("Текстуры", "Epic", "ScalabilityGroups", "sg.TextureQuality"), merged[2]);
    }
}
