namespace MetGameHDR.Watch;

public sealed class HdrController(IHdrSwitch hdr, Func<bool> turnOffOnExit, TimeSpan offDelay)
{
    bool active;
    bool weTurnedOn;
    DateTime? offStart;

    public bool GameActive => active;

    public void Update(bool anyGameRunning, DateTime now)
    {
        if (anyGameRunning)
        {
            offStart = null;
            if (!active) Start();
            return;
        }

        if (!active) return;
        offStart ??= now;
        if (now - offStart.Value >= offDelay) Finish();
    }

    void Start()
    {
        var state = hdr.IsEnabled();
        if (state is null) return;
        active = true;
        if (state == false)
        {
            // set first: if Set throws after switching some monitors, Finish must still turn them off
            weTurnedOn = true;
            hdr.Set(true);
        }
    }

    public void Finish()
    {
        if (weTurnedOn && turnOffOnExit()) hdr.Set(false);
        active = false;
        weTurnedOn = false;
        offStart = null;
    }
}
