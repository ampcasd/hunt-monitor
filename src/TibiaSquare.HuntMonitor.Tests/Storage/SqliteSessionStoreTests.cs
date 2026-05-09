using TibiaSquare.HuntMonitor.Models;
using TibiaSquare.HuntMonitor.Storage;
using Xunit;

namespace TibiaSquare.HuntMonitor.Tests.Storage;

public class SqliteSessionStoreTests : IDisposable
{
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        var connection = DatabaseMigrator.CreateAndMigrate(":memory:");
        _store = new SqliteSessionStore(connection);
    }

    [Fact]
    public void CreateSession_And_GetUnfinished_ReturnsSessions()
    {
        var session = new HuntSession { CharacterName = "TestChar" };
        _store.CreateSession(session);

        var unfinished = _store.GetUnfinishedSessions();

        Assert.Single(unfinished);
        Assert.Equal("TestChar", unfinished[0].CharacterName);
        Assert.Equal(session.Id, unfinished[0].Id);
    }

    [Fact]
    public void EndSession_RemovesFromUnfinished()
    {
        var session = new HuntSession { CharacterName = "TestChar" };
        _store.CreateSession(session);
        _store.EndSession(session.Id, "AnalyserReset", 300);

        var unfinished = _store.GetUnfinishedSessions();

        Assert.Empty(unfinished);
    }

    [Fact]
    public void InsertSnapshot_And_GetLast_RoundTrips()
    {
        var session = new HuntSession { CharacterName = "TestChar" };
        _store.CreateSession(session);

        var snapshot = new HuntSnapshot
        {
            SessionTime = TimeSpan.FromSeconds(3600),
            RawXpGain = 1_000_000,
            XpGain = 800_000,
            RawXpPerHour = 1_000_000,
            XpPerHour = 800_000,
            Loot = 500_000,
            Supplies = -200_000,
            Balance = 300_000,
            Damage = 2_000_000,
            DamagePerHour = 2_000_000,
            Healing = 500_000,
            HealingPerHour = 500_000,
            KilledMonsters = [new("Dragon", 15), new("Dragon Lord", 3)],
        };

        _store.InsertSnapshot(session.Id, snapshot, session.StartedAtUtc);
        var loaded = _store.GetLastSnapshot(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(1_000_000, loaded.RawXpGain);
        Assert.Equal(800_000, loaded.XpGain);
        Assert.Equal(500_000, loaded.Loot);
        Assert.Equal(-200_000, loaded.Supplies);
        Assert.Equal(300_000, loaded.Balance);
        Assert.Equal(2, loaded.KilledMonsters.Count);
        Assert.Equal("Dragon", loaded.KilledMonsters[0].Name);
        Assert.Equal(15, loaded.KilledMonsters[0].Count);
        Assert.Equal("Dragon Lord", loaded.KilledMonsters[1].Name);
        Assert.Equal(3, loaded.KilledMonsters[1].Count);
    }

    [Fact]
    public void GetLastSnapshot_ReturnsLatest()
    {
        var session = new HuntSession { CharacterName = "TestChar" };
        _store.CreateSession(session);

        var first = new HuntSnapshot
        {
            SessionTime = TimeSpan.FromSeconds(60),
            XpGain = 100,
            KilledMonsters = [new("Rat", 5)],
        };
        var second = new HuntSnapshot
        {
            SessionTime = TimeSpan.FromSeconds(120),
            XpGain = 200,
            KilledMonsters = [new("Rat", 10)],
        };

        _store.InsertSnapshot(session.Id, first, session.StartedAtUtc);
        _store.InsertSnapshot(session.Id, second, session.StartedAtUtc);

        var loaded = _store.GetLastSnapshot(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(200, loaded.XpGain);
    }

    [Fact]
    public void GetLastSnapshot_NoSnapshots_ReturnsNull()
    {
        var session = new HuntSession { CharacterName = "TestChar" };
        _store.CreateSession(session);

        Assert.Null(_store.GetLastSnapshot(session.Id));
    }

    [Fact]
    public void InsertSnapshot_WithNullValues_RoundTrips()
    {
        var session = new HuntSession { CharacterName = "TestChar" };
        _store.CreateSession(session);

        var snapshot = new HuntSnapshot
        {
            SessionTime = TimeSpan.FromSeconds(30),
            KilledMonsters = [],
        };

        _store.InsertSnapshot(session.Id, snapshot, session.StartedAtUtc);
        var loaded = _store.GetLastSnapshot(session.Id);

        Assert.NotNull(loaded);
        Assert.Null(loaded.XpGain);
        Assert.Null(loaded.Loot);
        Assert.Null(loaded.Supplies);
        Assert.Empty(loaded.KilledMonsters);
    }

    public void Dispose()
    {
        _store.Dispose();
    }
}
