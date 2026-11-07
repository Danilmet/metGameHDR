using System.Text;
using MetGameHDR.Advice;

namespace MetGameHDR.Tests;

public class IniEditorTests
{
    private static ConfigEdit[] E(string? section, string key, string value) => [new(section, key, value)];

    [Fact]
    public void Apply_ExistingKey_ReplacesValueOnly()
    {
        var text = "[ScalabilityGroups]\r\nsg.ShadowQuality=3\r\nsg.TextureQuality=3\r\n";
        var result = IniEditor.Apply(text, E("ScalabilityGroups", "sg.ShadowQuality", "1"));
        Assert.Equal("[ScalabilityGroups]\r\nsg.ShadowQuality=1\r\nsg.TextureQuality=3\r\n", result);
    }

    [Fact]
    public void Apply_SpaceSeparator_KeepsSeparator()
    {
        var result = IniEditor.Apply("seta r_shadows \"1\"\nseta r_fov \"90\"\n", E(null, "r_shadows", "\"0\""));
        Assert.Equal("seta r_shadows \"0\"\nseta r_fov \"90\"\n", result);
        result = IniEditor.Apply("r_shadows  \"1\"\n", E(null, "r_shadows", "\"0\""));
        Assert.Equal("r_shadows  \"0\"\n", result);
    }

    [Fact]
    public void Apply_MissingKey_AppendsToSection()
    {
        var text = "[A]\na=1\n\n[B]\nb=2\n";
        var result = IniEditor.Apply(text, E("A", "c", "3"));
        Assert.Equal("[A]\na=1\nc=3\n\n[B]\nb=2\n", result);
    }

    [Fact]
    public void Apply_MissingSection_AppendsSection()
    {
        var result = IniEditor.Apply("[A]\r\na=1\r\n", E("New", "k", "v"));
        Assert.Equal("[A]\r\na=1\r\n\r\n[New]\r\nk=v\r\n", result);
    }

    [Fact]
    public void Apply_PlusPrefixedLine_IsNotMatched()
    {
        var text = "[A]\n+CustomSettings=old\n";
        var result = IniEditor.Apply(text, E("A", "CustomSettings", "new"));
        Assert.Equal("[A]\n+CustomSettings=old\nCustomSettings=new\n", result);
    }

    [Fact]
    public void Apply_PreservesCrlfAndComments()
    {
        var text = "; head\r\n[A]\r\n# note\r\na = 1\r\n;tail\r\n";
        var result = IniEditor.Apply(text, E("A", "a", "2"));
        Assert.Equal("; head\r\n[A]\r\n# note\r\na = 2\r\n;tail\r\n", result);
    }

    [Fact]
    public void ApplyBytes_Utf16Bom_RoundTrips()
    {
        var file = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[A]\r\na=1\r\nb=2\r\n")).ToArray();
        var result = IniEditor.ApplyBytes(file, E("A", "a", "9"));
        var expected = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[A]\r\na=9\r\nb=2\r\n")).ToArray();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ApplyBytes_Utf8Bom_Kept()
    {
        var file = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a=1\n")).ToArray();
        var result = IniEditor.ApplyBytes(file, E(null, "a", "2"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a=2\n")).ToArray(), result);
        Assert.Equal(Encoding.UTF8.GetBytes("a=2\n"), IniEditor.ApplyBytes(Encoding.UTF8.GetBytes("a=1\n"), E(null, "a", "2")));
    }

    [Fact]
    public void ApplyBytes_AnsiWithoutBom_PreservesOtherBytes()
    {
        byte[] Ansi(string tail) => [.. "n="u8, 0xC4, 0xE0, .. Encoding.ASCII.GetBytes(tail)];

        var result = IniEditor.ApplyBytes(Ansi("\r\na=1\r\n"), E(null, "a", "2"));

        Assert.Equal(Ansi("\r\na=2\r\n"), result);
    }

    [Fact]
    public void Apply_EmptySection_IsTreatedAsNull()
    {
        Assert.Equal("r_shadows=0\n", IniEditor.Apply("r_shadows=1\n", E("", "r_shadows", "0")));
        Assert.Equal("r_shadows=0\n", IniEditor.Apply("r_shadows=1\n", E("  ", "r_shadows", "0")));
        Assert.Equal("1", IniEditor.CurrentValue("r_shadows=1\n", "", "r_shadows"));
    }

    [Fact]
    public void Apply_SlashCommentLine_IsNotMatched()
    {
        var result = IniEditor.Apply("// r_shadows 1\n", E(null, "r_shadows", "0"));
        Assert.Equal("// r_shadows 1\nr_shadows=0\n", result);
    }

    [Fact]
    public void CurrentValue_FirstMatchInSection()
    {
        var text = "a=0\n[S]\nk = first\nk=second\n[T]\nk=other\n";
        Assert.Equal("first", IniEditor.CurrentValue(text, "s", "K"));
        Assert.Equal("0", IniEditor.CurrentValue(text, null, "a"));
        Assert.Null(IniEditor.CurrentValue(text, "S", "zzz"));
        Assert.Equal("\"1\"", IniEditor.CurrentValue("seta r_shadows \"1\"\n", null, "r_shadows"));
        Assert.Equal("\"a b\"", IniEditor.CurrentValue("seta r_x \"a b\"\n", null, "r_x"));
    }

    [Fact]
    public void Apply_JsonLine_ReplacesValueKeepsIndentAndComma()
    {
        var text = "{\n\t\"m_eVSync\": 1,\n\t\"x\": 2\n}\n";
        Assert.Equal("{\n\t\"m_eVSync\": 0,\n\t\"x\": 2\n}\n", IniEditor.Apply(text, E(null, "m_eVSync", "0")));
    }

    [Fact]
    public void Apply_JsonLine_QuotedValueStaysQuoted()
    {
        var text = "{\n\t\"m_sPreset\": \"high\",\n\t\"x\": 2\n}\n";
        Assert.Equal("{\n\t\"m_sPreset\": \"low\",\n\t\"x\": 2\n}\n", IniEditor.Apply(text, E(null, "m_sPreset", "low")));
    }

    [Fact]
    public void Apply_JsonLine_LastLineWithoutComma()
    {
        var text = "{\r\n\t\"a\": 1,\r\n\t\"b\": true\r\n}";
        Assert.Equal("{\r\n\t\"a\": 1,\r\n\t\"b\": false\r\n}", IniEditor.Apply(text, E("S", "b", "false")));
    }

    [Fact]
    public void CurrentValue_JsonLine_ReturnsRawValue()
    {
        Assert.Equal("360.0", IniEditor.CurrentValue("{\n\t\"m_maxFrameRate\": 360.0,\n}\n", null, "m_maxFrameRate"));
    }

    [Fact]
    public void Apply_JsonLine_MissingKey_IsNotAppended()
    {
        var text = "{\n\t\"a\": 1\n}\n";
        Assert.Equal(text, IniEditor.Apply(text, E(null, "zzz", "1")));
    }
}