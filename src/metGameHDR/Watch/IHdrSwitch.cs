namespace MetGameHDR.Watch;

public interface IHdrSwitch
{
    bool? IsEnabled();
    void Set(bool on);
}
