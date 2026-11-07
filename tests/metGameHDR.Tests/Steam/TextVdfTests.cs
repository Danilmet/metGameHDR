using MetGameHDR.Steam;

namespace MetGameHDR.Tests.Steam;

public class TextVdfTests
{
    [Fact] public void Parse_NestedObjectsAndEscapes() {
        var text = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"1005300\"\t\t\"2380882867\"\n\t\t}\n\t}\n}\n";
        var root = (Dictionary<string, object>)TextVdf.Parse(text)["libraryfolders"];
        var zero = (Dictionary<string, object>)root["0"];
        Assert.Equal(@"C:\Program Files (x86)\Steam", zero["path"]);
        Assert.Equal("2380882867", ((Dictionary<string, object>)zero["apps"])["1005300"]);
    }

    [Fact] public void Parse_CommentsCrlfAndEscapedQuote() {
        var text = "// header\r\n\"a\"\r\n{\r\n\t\"k\" \"say \\\"hi\\\"\" // tail\r\n\t\"k\" \"last\"\r\n}\r\n";
        var a = (Dictionary<string, object>)TextVdf.Parse(text)["a"];
        Assert.Equal("last", a["k"]);
        Assert.Equal("say \"hi\"", TextVdf.Parse("\"x\" \"say \\\"hi\\\"\"")["x"]);
    }
}
