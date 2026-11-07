namespace MetGameHUB.Watch;

public interface IHdrSwitch
{
    bool? IsEnabled();
    void Set(bool on);
}
