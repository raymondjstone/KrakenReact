using System.Reflection;
using Hangfire;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class SettingsControllerTests : IDisposable
{
    private readonly KrakenDbContext _db;
    private readonly TradingStateService _state;
    private readonly Mock<IRecurringJobManager> _jobs = new();

    public SettingsControllerTests()
    {
        _db = new KrakenDbContext(new DbContextOptionsBuilder<KrakenDbContext>()
            .UseInMemoryDatabase($"settings-{Guid.NewGuid()}").Options);

        var dlog = new Mock<ILogger<DelistedPriceService>>();
        _state = new TradingStateService(new DelistedPriceService(dlog.Object));
    }
    public void Dispose() => _db.Dispose();

    private SettingsController NewCtrl()
    {
        // Neither collaborator touches the database during construction or in the paths these tests exercise
        var dbMethods = new DbMethods(new Mock<IDbContextFactory<KrakenDbContext>>().Object,
            new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var kraken = new KrakenRestService(dbMethods, _state, new Mock<ILogger<KrakenRestService>>().Object);
        return new(_db, _state, _jobs.Object, new Mock<ILogger<SettingsController>>().Object, kraken, dbMethods);
    }

    // ── MaskSecret (via reflection) ─────────────────────────────────────────

    private static string MaskSecret(string secret)
    {
        var mi = typeof(SettingsController).GetMethod("MaskSecret", BindingFlags.NonPublic | BindingFlags.Static);
        return (string)mi!.Invoke(null, [secret])!;
    }

    [Theory]
    [InlineData("", "***")]
    [InlineData("abc", "***")]
    [InlineData("1234567", "***")]
    public void MaskSecret_ShortOrEmpty_ReturnsTripleStar(string secret, string expected)
    {
        Assert.Equal(expected, MaskSecret(secret));
    }

    [Fact]
    public void MaskSecret_LongSecret_KeepsFirstAndLastFour()
    {
        // "ABCDsecretXYZW" → "ABCD***XYZW"
        var result = MaskSecret("ABCDsecretXYZW");
        Assert.Equal("ABCD***XYZW", result);
    }

    [Fact]
    public void MaskSecret_EightChars_StillMasksMiddle()
    {
        // Length is 8 → first 4 + *** + last 4 → "ABCD***EFGH" — but [..4] is "ABCD" and [^4..] is "EFGH"
        var result = MaskSecret("ABCDEFGH");
        Assert.Equal("ABCD***EFGH", result);
    }

    // ── pinned-pairs endpoints ──────────────────────────────────────────────

    [Fact]
    public async Task GetPinnedPairs_NoSetting_ReturnsDefault()
    {
        var ok = Assert.IsType<OkObjectResult>(await NewCtrl().GetPinnedPairs());
        var list = Assert.IsAssignableFrom<List<string>>(ok.Value);
        Assert.Equal(new[] { "XBT/USD", "ETH/USD", "SOL/USD" }, list);
    }

    [Fact]
    public async Task SavePinnedPairs_NewSetting_PersistsAndRoundTrips()
    {
        var ctrl = NewCtrl();
        var input = new List<string> { "BTC/USD", "DOT/USD" };
        Assert.IsType<OkResult>(await ctrl.SavePinnedPairs(input));

        var ok = Assert.IsType<OkObjectResult>(await ctrl.GetPinnedPairs());
        var list = Assert.IsAssignableFrom<List<string>>(ok.Value);
        Assert.Equal(input, list);
    }

    [Fact]
    public async Task SavePinnedPairs_DropsEntriesThatWouldCorruptTheStoredList()
    {
        var ctrl = NewCtrl();
        // "A/USD,B/USD" would come back as two entries; blanks, overlong names and odd characters are not pair names
        var messy = new List<string> { "BTC/USD", "  eth/usd ", "A/USD,B/USD", "", "   ", new string('X', 60), "<script>", "BTC/USD", "SOL.F/USD" };
        Assert.IsType<OkResult>(await ctrl.SavePinnedPairs(messy));

        var saved = Assert.IsAssignableFrom<List<string>>(Assert.IsType<OkObjectResult>(await ctrl.GetPinnedPairs()).Value);
        Assert.Equal(new[] { "BTC/USD", "eth/usd", "SOL.F/USD" }, saved);   // trimmed, de-duplicated, only sane names
    }

    [Fact]
    public async Task SavePinnedPairs_IsCappedAtFifty()
    {
        var ctrl = NewCtrl();
        await ctrl.SavePinnedPairs(Enumerable.Range(1, 500).Select(i => $"C{i}/USD").ToList());

        var saved = Assert.IsAssignableFrom<List<string>>(Assert.IsType<OkObjectResult>(await ctrl.GetPinnedPairs()).Value);
        Assert.Equal(50, saved.Count);
    }

    [Fact]
    public async Task SavePinnedPairs_ANullBody_SavesAnEmptyList()
    {
        var ctrl = NewCtrl();
        Assert.IsType<OkResult>(await ctrl.SavePinnedPairs(null!));
    }

    [Fact]
    public async Task SavePinnedPairs_UpdatesExistingSetting()
    {
        _db.AppSettings.Add(new AppSettings { Key = "PinnedPairs", Value = "OLD/USD" });
        await _db.SaveChangesAsync();

        var ctrl = NewCtrl();
        await ctrl.SavePinnedPairs(new List<string> { "NEW/USD", "NEW2/USD" });

        var stored = await _db.AppSettings.FirstAsync(s => s.Key == "PinnedPairs");
        Assert.Equal("NEW/USD,NEW2/USD", stored.Value);
    }

    [Fact]
    public async Task SavePinnedPairs_EmptyList_PersistsEmptyValue()
    {
        var ctrl = NewCtrl();
        await ctrl.SavePinnedPairs(new List<string>());

        var stored = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "PinnedPairs");
        Assert.NotNull(stored);
        Assert.Equal("", stored!.Value);

        // GetPinnedPairs then returns the empty list (NOT the default seed) because the setting exists.
        var ok = Assert.IsType<OkObjectResult>(await ctrl.GetPinnedPairs());
        var list = Assert.IsAssignableFrom<List<string>>(ok.Value);
        Assert.Empty(list);
    }

    // ── AppSettings model default ───────────────────────────────────────────

    [Fact]
    public void AppSettings_Defaults()
    {
        var s = new AppSettings();
        Assert.Equal("", s.Key);
        Assert.Equal("", s.Value);
        Assert.Null(s.Description);
    }
}
