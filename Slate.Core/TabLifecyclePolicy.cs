namespace Slate.Core;

/// <summary>
/// Policy logic for inactive tab discard candidate evaluation, ordering, and retention.
/// Discard candidates are strictly prioritized in Least Recently Used (LRU) order,
/// while protecting active, audio-playing, downloading, prompting, loading, or recently used tabs.
/// </summary>
public static class TabLifecyclePolicy
{
    public static List<TId> SelectDiscardCandidates<TId>(
        IEnumerable<TId> tabIds,
        Func<TId, bool> isProtected,
        Func<TId, bool> hasActivePermissionPrompt,
        Func<TId, bool> hasActiveDownloads,
        Func<TId, bool> isPlayingAudio,
        Func<TId, bool> isLoading,
        Func<TId, DateTimeOffset> getLastAccessed,
        TimeSpan minInactiveDuration,
        int maxRetainedBackgroundViews,
        DateTimeOffset now)
    {
        var candidates = tabIds
            .Where(id => !isProtected(id) &&
                         !hasActivePermissionPrompt(id) &&
                         !hasActiveDownloads(id) &&
                         !isPlayingAudio(id) &&
                         !isLoading(id))
            .Select(id => (TabId: id, LastAccessed: getLastAccessed(id)))
            .Where(entry => (now - entry.LastAccessed) >= minInactiveDuration)
            .OrderBy(entry => entry.LastAccessed) // LRU: Oldest first
            .ToList();

        int toDiscardCount = Math.Max(0, candidates.Count - maxRetainedBackgroundViews);
        return candidates.Take(toDiscardCount).Select(c => c.TabId).ToList();
    }
}
