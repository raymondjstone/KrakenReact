using System.Text.Json;
using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class MissingCredentialsTests
{
    private static TradingStateService NewState() =>
        new(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));

    [Fact]
    public async Task NoSavedKeys_ThrowsAClearError_NotANullReference()
    {
        var db = new DbMethods(new InMemoryDbFactory($"nocreds-{Guid.NewGuid()}"), new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var kraken = new KrakenRestService(db, NewState(), new Mock<ILogger<KrakenRestService>>().Object);

        var ex = await Assert.ThrowsAsync<KrakenCredentialsMissingException>(() => kraken.AuthenticatedClient());
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public async Task SavedKeys_ProduceAClient()
    {
        var factory = new InMemoryDbFactory($"creds-{Guid.NewGuid()}");
        await using (var c = factory.CreateDbContext())
        {
            c.AppSettings.Add(new AppSettings { Key = "KrakenApiKey", Value = "k" });
            c.AppSettings.Add(new AppSettings { Key = "KrakenApiSecret", Value = "c2VjcmV0" });   // the client requires a base64 secret
            await c.SaveChangesAsync();
        }
        var db = new DbMethods(factory, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var kraken = new KrakenRestService(db, NewState(), new Mock<ILogger<KrakenRestService>>().Object);

        Assert.NotNull(await kraken.AuthenticatedClient());
    }

    // ── Health page ────────────────────────────────────────────────────────

    private static async Task<JsonElement> Check(KrakenDbContext db, string name)
    {
        var ok = Assert.IsType<OkObjectResult>(await new HealthController(db, NewState()).Get());
        var json = JsonSerializer.SerializeToElement(ok.Value);
        return json.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);
    }

    private static KrakenDbContext NewDb() =>
        new(new DbContextOptionsBuilder<KrakenDbContext>().UseInMemoryDatabase($"health-{Guid.NewGuid()}").Options);

    [Fact]
    public async Task Health_FlagsMissingKrakenKeys()
    {
        using var db = NewDb();
        var check = await Check(db, "Kraken API Keys");
        Assert.False(check.GetProperty("ok").GetBoolean());
        Assert.Contains("Settings", check.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Health_AcceptsSavedKrakenKeys()
    {
        using var db = NewDb();
        db.AppSettings.Add(new AppSettings { Key = "KrakenApiKey", Value = "k" });
        db.AppSettings.Add(new AppSettings { Key = "KrakenApiSecret", Value = "s" });
        await db.SaveChangesAsync();

        Assert.True((await Check(db, "Kraken API Keys")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Health_BlankKeysCountAsMissing()
    {
        using var db = NewDb();
        db.AppSettings.Add(new AppSettings { Key = "KrakenApiKey", Value = "k" });
        db.AppSettings.Add(new AppSettings { Key = "KrakenApiSecret", Value = "  " });
        await db.SaveChangesAsync();

        Assert.False((await Check(db, "Kraken API Keys")).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Health_PushoverIsOptional_ItNeverFailsTheCheck_ButSaysWhenItIsOff()
    {
        using var db = NewDb();
        var off = await Check(db, "Pushover");
        Assert.True(off.GetProperty("ok").GetBoolean());
        Assert.Contains("Not configured", off.GetProperty("detail").GetString());

        db.AppSettings.Add(new AppSettings { Key = "PushoverUserKey", Value = "u" });
        db.AppSettings.Add(new AppSettings { Key = "PushoverAppToken", Value = "t" });
        await db.SaveChangesAsync();
        Assert.Equal("Configured", (await Check(db, "Pushover")).GetProperty("detail").GetString());
    }
}
