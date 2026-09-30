namespace KrakenReact.Server.Services;

/// <summary>One page of a paged Kraken listing, or the error that prevented it.</summary>
public sealed record PageResult<T>(bool Success, IReadOnlyDictionary<string, T>? Items, object? Error = null);

/// <summary>
/// Reads a Kraken listing that is served a page at a time (50 records, addressed by offset). The trades, ledger and closed-
/// orders syncs each carried their own copy of this loop; it lives here once so it can be tested without a live exchange.
/// </summary>
public static class PagedFetch
{
    /// <summary>Records Kraken returns per page; a shorter page means the listing is exhausted.</summary>
    public const int PageSize = 50;

    /// <summary>Upper bound on records read in one sync.</summary>
    public const int DefaultMaxRecords = 3501;

    /// <summary>
    /// Reads pages until one comes back short, the cap is reached, or a page brings nothing new.
    /// The last stop matters: the offset is the number of DISTINCT records held so far, so if the exchange ever answers with a
    /// page whose records are all already known, the offset never advances and the loop would request the same page forever
    /// (exactly how the open-orders sync used to spin once 50 orders were open).
    /// </summary>
    /// <param name="fetchPage">Fetches the page starting at the given offset.</param>
    /// <param name="shouldRetry">Given a failed page's error and how many consecutive failures there have been, waits if
    /// appropriate and says whether to try again. Returning false abandons the whole read.</param>
    /// <returns>Ok=false if a page failed and the caller declined to retry; the partial data is then not to be trusted.</returns>
    public static async Task<(bool Ok, Dictionary<string, T> Data)> FetchAllAsync<T>(
        Func<int, Task<PageResult<T>>> fetchPage,
        Func<object?, int, Task<bool>> shouldRetry,
        int maxRecords = DefaultMaxRecords,
        int pageSize = PageSize)
    {
        var data = new Dictionary<string, T>(Math.Min(maxRecords, 4096));
        var lastPageCount = pageSize; // enter the loop
        var failures = 0;

        while (lastPageCount >= pageSize && data.Count < maxRecords)
        {
            var page = await fetchPage(data.Count);

            if (!page.Success || page.Items == null)
            {
                if (!await shouldRetry(page.Error, failures++)) return (false, data);
                lastPageCount = pageSize; // try the same offset again
                continue;
            }

            failures = 0;
            lastPageCount = page.Items.Count;

            var before = data.Count;
            foreach (var kvp in page.Items) data[kvp.Key] = kvp.Value;
            if (data.Count == before) break; // nothing new: asking again would return the same thing
        }

        return (true, data);
    }
}
