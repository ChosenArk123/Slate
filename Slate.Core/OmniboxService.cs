namespace Slate.Core;

public enum OmniboxSuggestionKind
{
    Search,
    OpenTab,
    Bookmark,
    History
}

public sealed record OmniboxSuggestion(
    OmniboxSuggestionKind Kind,
    string Title,
    string Subtitle,
    string Target,
    Guid? TabId = null
);

public static class OmniboxService
{
    public static List<OmniboxSuggestion> GetSuggestions(
        string? input,
        IEnumerable<BrowserTab>? openTabs,
        IEnumerable<HistoryEntry>? history,
        string? searchEngine,
        IEnumerable<BookmarkEntry>? bookmarks = null,
        int maxSuggestions = 6)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var query = input.Trim();
        var results = new List<OmniboxSuggestion>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var engine = string.IsNullOrWhiteSpace(searchEngine) ? "DuckDuckGo" : searchEngine;
        results.Add(new OmniboxSuggestion(
            OmniboxSuggestionKind.Search,
            $"Search with {engine}",
            query,
            query
        ));

        if (openTabs is not null)
        {
            foreach (var tab in openTabs)
            {
                if (tab.Url == Navigation.NewTab) continue;
                bool matchTitle = !string.IsNullOrEmpty(tab.Title) && tab.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool matchUrl = !string.IsNullOrEmpty(tab.Url) && tab.Url.Contains(query, StringComparison.OrdinalIgnoreCase);
                if ((matchTitle || matchUrl) && seenUrls.Add(tab.Url))
                {
                    results.Add(new OmniboxSuggestion(
                        OmniboxSuggestionKind.OpenTab,
                        string.IsNullOrWhiteSpace(tab.Title) ? Navigation.DisplayHost(tab.Url) : tab.Title,
                        tab.Url,
                        tab.Url,
                        tab.Id
                    ));
                    if (results.Count >= maxSuggestions) return results;
                }
            }
        }

        if (bookmarks is not null)
        {
            foreach (var bookmark in bookmarks)
            {
                if (bookmark.Url == Navigation.NewTab) continue;
                bool matchTitle = !string.IsNullOrEmpty(bookmark.Title) && bookmark.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool matchUrl = !string.IsNullOrEmpty(bookmark.Url) && bookmark.Url.Contains(query, StringComparison.OrdinalIgnoreCase);
                if ((matchTitle || matchUrl) && seenUrls.Add(bookmark.Url))
                {
                    results.Add(new OmniboxSuggestion(
                        OmniboxSuggestionKind.Bookmark,
                        string.IsNullOrWhiteSpace(bookmark.Title) ? Navigation.DisplayHost(bookmark.Url) : bookmark.Title,
                        bookmark.Url,
                        bookmark.Url
                    ));
                    if (results.Count >= maxSuggestions) return results;
                }
            }
        }

        if (history is not null)
        {
            foreach (var entry in history)
            {
                if (entry.Url == Navigation.NewTab) continue;
                bool matchTitle = !string.IsNullOrEmpty(entry.Title) && entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool matchUrl = !string.IsNullOrEmpty(entry.Url) && entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase);
                if ((matchTitle || matchUrl) && seenUrls.Add(entry.Url))
                {
                    results.Add(new OmniboxSuggestion(
                        OmniboxSuggestionKind.History,
                        string.IsNullOrWhiteSpace(entry.Title) ? Navigation.DisplayHost(entry.Url) : entry.Title,
                        entry.Url,
                        entry.Url
                    ));
                    if (results.Count >= maxSuggestions) return results;
                }
            }
        }

        return results;
    }
}
