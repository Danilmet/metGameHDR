using MetGameHDR.Watch;
using Xunit;

namespace MetGameHDR.Tests.Watch;

public class HdrControllerTests
{
    sealed class FakeHdr : IHdrSwitch
    {
        public bool? State;
        public bool ThrowOnOn;
        public List<bool> Sets = [];
        public bool? IsEnabled() => State;
        public void Set(bool on) { Sets.Add(on); if (on && ThrowOnOn) throw new InvalidOperationException("second monitor"); State = on; }
    }

    static readonly DateTime T0 = new(2026, 1, 1);
    HdrController C(FakeHdr h, bool offOnExit = true) => new(h, () => offOnExit, TimeSpan.FromSeconds(3));

    [Fact] public void TurnsOnWhenGameStartsAndHdrWasOff() { var h = new FakeHdr { State = false }; C(h).Update(true, T0); Assert.Equal([true], h.Sets); }
    [Fact] public void DoesNotTouchHdrAlreadyOn() { var h = new FakeHdr { State = true }; var c = C(h); c.Update(true, T0); c.Update(false, T0); c.Update(false, T0.AddSeconds(4)); Assert.Empty(h.Sets); }
    [Fact] public void TurnsOffAfterDelay() { var h = new FakeHdr { State = false }; var c = C(h); c.Update(true, T0); c.Update(false, T0.AddSeconds(10)); c.Update(false, T0.AddSeconds(12)); Assert.Equal([true], h.Sets); c.Update(false, T0.AddSeconds(13)); Assert.Equal([true, false], h.Sets); }
    [Fact] public void RestartWithinDelayCancelsOff() { var h = new FakeHdr { State = false }; var c = C(h); c.Update(true, T0); c.Update(false, T0.AddSeconds(10)); c.Update(true, T0.AddSeconds(11)); c.Update(true, T0.AddSeconds(20)); Assert.Equal([true], h.Sets); }
    [Fact] public void RespectsTurnOffOnExitSetting() { var h = new FakeHdr { State = false }; var c = C(h, offOnExit: false); c.Update(true, T0); c.Update(false, T0.AddSeconds(10)); c.Update(false, T0.AddSeconds(14)); Assert.Equal([true], h.Sets); }
    [Fact] public void Finish_TurnsOffOnlyWhatWeTurnedOn() { var h = new FakeHdr { State = false }; var c = C(h); c.Update(true, T0); c.Finish(); Assert.Equal([true, false], h.Sets); var on = new FakeHdr { State = true }; var c2 = C(on); c2.Update(true, T0); c2.Finish(); Assert.Empty(on.Sets); }
    [Fact] public void Finish_TurnsOffAfterPartialFailureToTurnOn() { var h = new FakeHdr { State = false, ThrowOnOn = true }; var c = C(h); Assert.Throws<InvalidOperationException>(() => c.Update(true, T0)); c.Finish(); Assert.Equal([true, false], h.Sets); }
    [Fact] public void Update_DoesNothingWhenHdrUnsupported() { var h = new FakeHdr { State = null }; var c = C(h); c.Update(true, T0); c.Update(false, T0.AddSeconds(10)); c.Update(false, T0.AddSeconds(14)); Assert.Empty(h.Sets); }
}
