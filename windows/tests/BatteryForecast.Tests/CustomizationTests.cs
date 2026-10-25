using BatteryForecast.Core;
using BatteryForecast.Core.Battery;
using BatteryForecast.Core.Forecasting;

namespace BatteryForecast.Tests;

public class CustomizationTests
{
    static readonly DateTimeOffset Now = new(2026,10,25,12,0,0,TimeSpan.Zero);
    static BatterySample Charge(int minute, double level, int design = 60000) =>
        new(Now.AddMinutes(minute), ChargeState.Charging, level, true, FullChargeMWh: 56000, DesignMWh: design);
    static List<BatterySample> Session(int minute, double start, double end, int length = 30) =>
        [.. Enumerable.Range(0,length+1).Select(i => Charge(minute+i,start+(end-start)*i/length))];

    [Theory]
    [InlineData("Ctrl+Alt+B", true)]
    [InlineData("alt+shift+F5", true)]
    [InlineData("", true)]
    [InlineData("Shift+B", false)]
    [InlineData("Ctrl+Ctrl+B", false)]
    [InlineData("Ctrl+F12", false)]
    [InlineData("Ctrl+anything", false)]
    public void ShortcutValidation(string text, bool valid) => Assert.Equal(valid, HotkeySpec.TryParse(text,out _));

    [Theory]
    [InlineData("ctrl+alt+b", "Ctrl+Alt+B")]
    [InlineData("Win+Shift+F9", "Shift+Win+F9")]
    [InlineData("Alt+Ctrl+space", "Ctrl+Alt+Space")]
    [InlineData("Ctrl+Win+NumPad5", "Ctrl+Win+NumPad5")]
    [InlineData("", "")]
    public void ShortcutTextIsCanonical(string text, string canonical)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec));
        Assert.Equal(canonical, spec.ToString());
        Assert.True(HotkeySpec.TryParse(spec.ToString(), out var again));
        Assert.Equal(spec, again);
    }

    [Fact]
    public void PanelSizeAndHoverDefaultsAndLimits()
    {
        var d = new Settings().Normalized();
        Assert.True(d.OpenOnHover);
        Assert.Equal(Settings.DefaultPanelWidth, d.PanelWidth);
        Assert.Equal(1.0, d.PanelScale);
        var big = new Settings { PanelWidth = 5000, PanelScale = 9, PanelHotkey = "alt+ctrl+b" }.Normalized();
        Assert.Equal(Settings.MaxPanelWidth, big.PanelWidth);
        Assert.Equal(Settings.MaxPanelScale, big.PanelScale);
        Assert.Equal("Ctrl+Alt+B", big.PanelHotkey);
        var nan = new Settings { PanelWidth = double.NaN, PanelScale = double.PositiveInfinity }.Normalized();
        Assert.Equal(Settings.DefaultPanelWidth, nan.PanelWidth);
        Assert.Equal(1.0, nan.PanelScale);
    }

    [Theory]
    [InlineData(HotkeySpec.Ctrl | HotkeySpec.Alt, 0x42u, true)]        // Ctrl+Alt+B
    [InlineData(HotkeySpec.Win, 0x31u, true)]                           // Win+1
    [InlineData(HotkeySpec.Shift, 0x42u, false)]                        // il solo Shift non basta
    [InlineData(HotkeySpec.Ctrl, 0x11u, false)]                         // tasto modificatore da solo
    [InlineData(HotkeySpec.Ctrl, 0x7Bu, false)]                         // F12 riservato
    [InlineData(HotkeySpec.Ctrl | 0x10u, 0x42u, false)]                 // bit sconosciuto
    public void ShortcutFromPressedKeys(uint mods, uint vk, bool valid) => Assert.Equal(valid, HotkeySpec.TryCreate(mods, vk, out _));

    [Fact]
    public void QuietHoursAcrossMidnightAndBoundaries()
    {
        var settings = new Settings { QuietHoursEnabled = true, QuietStart = new(22,0), QuietEnd = new(8,0) };
        Assert.True(settings.IsQuietAt(Now.AddHours(10),TimeZoneInfo.Utc));
        Assert.True(settings.IsQuietAt(Now.AddHours(14),TimeZoneInfo.Utc));
        Assert.False(settings.IsQuietAt(Now.AddHours(-4),TimeZoneInfo.Utc));
        Assert.False((settings with { QuietStart = new(8,0) }).IsQuietAt(Now,TimeZoneInfo.Utc));
    }

    [Fact]
    public void CustomSettingsSurviveSaveAndLoad()
    {
        var file = Path.GetTempFileName();
        try
        {
            var settings = new Settings { TargetTime = new(1,17), AdditionalTargets = [new(5,43),new(9,12)],
                ChargeTargetPct = 87, PanelHotkey = "Ctrl+Shift+F6", QuietHoursEnabled = true, QuietStart = new(23,7),
                QuietEnd = new(7,31), ShowChart = false, KeepPanelOpen = true };
            settings.Save(file);
            var loaded = Settings.Load(file);
            Assert.Equal(settings.TargetTime,loaded.TargetTime);
            Assert.Equal(settings.AdditionalTargets,loaded.AdditionalTargets);
            Assert.Equal(87,loaded.ChargeTargetPct);
            Assert.Equal(settings.PanelHotkey,loaded.PanelHotkey);
            Assert.Equal(settings.QuietStart,loaded.QuietStart);
            Assert.False(loaded.ShowChart); Assert.True(loaded.KeepPanelOpen);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ShortRecentChargeProjectionBelowEighty()
    {
        var data = Session(-10,60,65,10);
        var result = ChargeRate.Estimate(data,data[^1],75);
        Assert.Equal(ChargeEstimateKind.RecentRate,result.Kind);
        Assert.Equal(Now.AddMinutes(20),result.TargetAt);
    }

    [Fact]
    public void HighTargetNeedsObservedTaperAndTwoSessions()
    {
        var data = Session(-10,80,85,10);
        Assert.Equal(ChargeEstimateKind.Collecting,ChargeRate.Estimate(data,data[^1],100).Kind);
        var past = Session(-3000,80,100,60).Concat(Session(-1500,80,100,90)).Concat(data).ToList();
        var result = ChargeRate.Estimate(past,data[^1],100);
        Assert.Equal(ChargeEstimateKind.PastSessions,result.Kind);
        Assert.Equal(2,result.Sessions);
        Assert.Equal(Now.AddMinutes(67.5),result.TargetAt);
    }

    [Fact]
    public void PauseUnknownOrChangedBatteryDoesNotProduceChargingEta()
    {
        var data = Session(-10,60,65,10);
        Assert.Equal(ChargeEstimateKind.Collecting,ChargeRate.Estimate(data,data[^1] with { OnAc = false },75).Kind);
        Assert.Equal(ChargeEstimateKind.Collecting,ChargeRate.Estimate(data,data[^1] with { State = ChargeState.Idle },75).Kind);
        Assert.Equal(ChargeEstimateKind.Collecting,ChargeRate.Estimate(data,data[^1] with { DesignMWh = 70000 },75).Kind);
        data.RemoveRange(3,6);
        Assert.Equal(ChargeEstimateKind.Collecting,ChargeRate.Estimate(data,data[^1],75).Kind);
    }

    [Fact]
    public void TinyRiseOrLongProjectionIsInsufficient()
    {
        var data = Session(-10,60,61,10);
        Assert.Equal(ChargeEstimateKind.Collecting,ChargeRate.Estimate(data,data[^1],75).Kind);
        data = Session(-10,40,45,10);
        Assert.Equal(ChargeEstimateKind.Collecting,ChargeRate.Estimate(data,data[^1],80).Kind);
        Assert.Equal(ChargeEstimateKind.AlreadyReached,ChargeRate.Estimate(data,data[^1] with { LevelPct=80 },80).Kind);
    }
}
