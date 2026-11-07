using System.Text;
using MetGameHUB.Steam;

namespace MetGameHUB.Tests.Steam;

public class BinaryVdfTests
{
    static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

    public static byte[] Fixture() => [
      0x00, ..U8("shortcuts"), 0x00,
        0x00, ..U8("0"), 0x00,
          0x02, ..U8("appid"), 0x00, 0x14, 0xCA, 0x04, 0xE4,
          0x01, ..U8("AppName"), 0x00, ..U8("Game"), 0x00,
          0x01, ..U8("Exe"), 0x00, ..U8("\"C:\\G\\g.exe\""), 0x00,
          0x01, ..U8("StartDir"), 0x00, ..U8("C:\\G"), 0x00,
          0x02, ..U8("LastPlayTime"), 0x00, 0x01, 0x02, 0x03, 0x04,
          0x00, ..U8("tags"), 0x00, 0x01, ..U8("0"), 0x00, ..U8("favorite"), 0x00, 0x08,
        0x08,
      0x08, 0x08 ];

    [Fact]
    public void Roundtrip_PreservesUnknownFields() =>
        Assert.Equal(Fixture(), BinaryVdf.Write(BinaryVdf.Read(Fixture())));

    [Fact]
    public void Read_ExposesTypedValues()
    {
        var root = BinaryVdf.Read(Fixture());
        var entry = (VdfObject)((VdfObject)root["shortcuts"]!)["0"]!;
        Assert.Equal(unchecked((int)0xE404CA14), entry["appid"]);
        Assert.Equal("Game", entry["appname"]);
        Assert.Equal("favorite", ((VdfObject)entry["tags"]!)["0"]);
    }

    [Fact]
    public void Indexer_SetReplacesInPlaceOrAppends()
    {
        var o = new VdfObject { new("A", 1), new("B", 2) };
        o["a"] = 5;
        o["C"] = 3;
        Assert.Equal([new("A", 5), new("B", 2), new("C", 3)], o.ToArray());
    }

    [Fact]
    public void Read_UnknownTypeThrows() =>
        Assert.Throws<InvalidDataException>(() => BinaryVdf.Read([0x07, 0x41, 0x00, 0x08]));
}
