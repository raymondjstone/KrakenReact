using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class PagedFetchTests
{
    /// <summary>A fake listing of `total` records that serves pages of 50 by offset, like Kraken.</summary>
    private sealed class FakeListing
    {
        private readonly int _total;
        public readonly List<int> Offsets = new();
        public FakeListing(int total) => _total = total;

        public Task<PageResult<string>> Page(int offset)
        {
            Offsets.Add(offset);
            var items = Enumerable.Range(offset, Math.Max(0, Math.Min(50, _total - offset))).ToDictionary(i => "id" + i, i => "v" + i);
            return Task.FromResult(new PageResult<string>(true, items));
        }
    }

    private static Task<bool> NeverRetry(object? error, int attempt) => Task.FromResult(false);
    private static Task<bool> AlwaysRetry(object? error, int attempt) => Task.FromResult(true);

    [Fact]
    public async Task ReadsEveryPage_UntilAShortOneEndsTheListing()
    {
        var listing = new FakeListing(120);

        var (ok, data) = await PagedFetch.FetchAllAsync<string>(listing.Page, NeverRetry);

        Assert.True(ok);
        Assert.Equal(120, data.Count);
        Assert.Equal(new[] { 0, 50, 100 }, listing.Offsets);   // the offset is the number of records already held
    }

    [Fact]
    public async Task AnExactMultipleOfThePageSize_NeedsOneMoreEmptyRequestToKnowItIsDone()
    {
        var listing = new FakeListing(100);

        var (_, data) = await PagedFetch.FetchAllAsync<string>(listing.Page, NeverRetry);

        Assert.Equal(100, data.Count);
        Assert.Equal(new[] { 0, 50, 100 }, listing.Offsets);
    }

    [Fact]
    public async Task AnEmptyListing_IsFineAndEmpty()
    {
        var (ok, data) = await PagedFetch.FetchAllAsync<string>(new FakeListing(0).Page, NeverRetry);
        Assert.True(ok);
        Assert.Empty(data);
    }

    [Fact]
    public async Task TheRecordCap_StopsAnEndlessListing()
    {
        var listing = new FakeListing(1_000_000);

        var (ok, data) = await PagedFetch.FetchAllAsync<string>(listing.Page, NeverRetry, maxRecords: 120);

        Assert.True(ok);
        Assert.InRange(data.Count, 120, 170);      // stops at the first page boundary at or past the cap
        Assert.True(listing.Offsets.Count <= 4);
    }

    [Fact]
    public async Task AFullPageThatRepeatsWhatWeAlreadyHave_EndsTheLoop_InsteadOfSpinning()
    {
        // The offset is the count of distinct records held. An exchange that keeps answering with the same 50 records would
        // leave it stuck at 50 and the old loops would request that page for ever.
        var calls = 0;
        Task<PageResult<string>> Repeats(int offset)
        {
            calls++;
            return Task.FromResult(new PageResult<string>(true, Enumerable.Range(0, 50).ToDictionary(i => "same" + i, i => "v")));
        }

        var (ok, data) = await PagedFetch.FetchAllAsync<string>(Repeats, NeverRetry);

        Assert.True(ok);
        Assert.Equal(50, data.Count);
        Assert.Equal(2, calls);                      // the second call brought nothing new, so it stopped
    }

    [Fact]
    public async Task AFailedPage_IsRetriedAtTheSameOffset_ThenTheReadContinues()
    {
        var listing = new FakeListing(120);
        var failNext = true;
        Task<PageResult<string>> Flaky(int offset)
        {
            if (offset == 50 && failNext) { failNext = false; return Task.FromResult(new PageResult<string>(false, null, "EAPI:Rate limit exceeded")); }
            return listing.Page(offset);
        }

        var (ok, data) = await PagedFetch.FetchAllAsync<string>(Flaky, AlwaysRetry);

        Assert.True(ok);
        Assert.Equal(120, data.Count);
        Assert.Equal(new[] { 0, 50, 100 }, listing.Offsets);   // the failed attempt at 50 was retried, not skipped
    }

    [Fact]
    public async Task WhenTheCallerGivesUp_TheReadIsReportedAsFailed()
    {
        Task<PageResult<string>> Broken(int offset) => Task.FromResult(new PageResult<string>(false, null, "boom"));

        var (ok, _) = await PagedFetch.FetchAllAsync<string>(Broken, NeverRetry);

        Assert.False(ok);   // callers then fall back to what they already have rather than trusting partial data
    }

    [Fact]
    public async Task TheRetryCallbackSeesConsecutiveFailures_AndItsCounterResetsAfterASuccess()
    {
        var listing = new FakeListing(120);
        var script = new Queue<bool>(new[] { false, false, true, false, true, true });   // fail, fail, ok, fail, ok, ok
        Task<PageResult<string>> Scripted(int offset) =>
            script.Dequeue() ? listing.Page(offset) : Task.FromResult(new PageResult<string>(false, null, "err"));

        var attempts = new List<int>();
        var (ok, _) = await PagedFetch.FetchAllAsync<string>(Scripted, (e, attempt) => { attempts.Add(attempt); return Task.FromResult(true); });

        Assert.True(ok);
        Assert.Equal(new[] { 0, 1, 0 }, attempts);   // two in a row, then a success resets it, then a fresh first failure
    }

    [Fact]
    public async Task TheErrorObjectIsPassedToTheRetryCallback()
    {
        object? seen = null;
        Task<PageResult<string>> Broken(int offset) => Task.FromResult(new PageResult<string>(false, null, "the-error"));

        await PagedFetch.FetchAllAsync<string>(Broken, (e, a) => { seen = e; return Task.FromResult(false); });

        Assert.Equal("the-error", seen);
    }

    [Fact]
    public async Task ARecordAppearingOnTwoPages_IsKeptOnce_WithTheLaterValue()
    {
        var pages = new Queue<Dictionary<string, string>>(new[]
        {
            Enumerable.Range(0, 50).ToDictionary(i => "id" + i, i => "old"),
            new Dictionary<string, string> { ["id10"] = "new", ["idNEW"] = "x" },   // short page: overlaps id10, adds idNEW
        });

        var (_, data) = await PagedFetch.FetchAllAsync<string>(o => Task.FromResult(new PageResult<string>(true, pages.Dequeue())), NeverRetry);

        Assert.Equal(51, data.Count);
        Assert.Equal("new", data["id10"]);
    }
}
