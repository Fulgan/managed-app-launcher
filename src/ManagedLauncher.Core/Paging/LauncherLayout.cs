namespace ManagedLauncher.Core.Paging;

public enum KeyRole { App, NextPage, Back, Manage, Empty }

/// <param name="AppIndex">Index into the app list for <see cref="KeyRole.App"/>; -1 otherwise.</param>
/// <param name="Page">Zero-based page shown.</param>
/// <param name="Pages">Total pages, at least 1.</param>
public readonly record struct KeyAssignment(KeyRole Role, int AppIndex, int Page, int Pages);

/// <summary>
/// Maps a key of the launcher profile to what it shows. The items are an optional Back key, the apps, and one
/// Manage key, laid out in key order page after page. When they don't fit on one page the last key of every page
/// is next-page instead. Back is the first item, so it sits on the first key of the first page only. Manage always
/// takes the last item key of the last page, so it sits on the last key or just left of next-page.
/// </summary>
/// <param name="KeyCount">Keys on the device.</param>
/// <param name="AppCount">Apps in the list shown.</param>
/// <param name="HasBack">Whether the first item is a Back key.</param>
public readonly record struct LauncherLayout(int KeyCount, int AppCount, bool HasBack = false)
{
    private int BackItems => HasBack ? 1 : 0;

    /// <summary>Back, apps and the Manage key.</summary>
    private int ItemCount => BackItems + AppCount + 1;

    public bool HasNextPage => ItemCount > KeyCount && KeyCount > 1;

    private int NextKey => KeyCount - 1;

    /// <summary>Item keys per page; the last page gives its final one to Manage.</summary>
    public int Capacity => Math.Max(0, HasNextPage ? KeyCount - 1 : KeyCount);

    public int PageCount => Capacity <= 0 || ItemCount <= Capacity ? 1 : (ItemCount + Capacity - 1) / Capacity;

    public int Clamp(int page) => Math.Clamp(page, 0, PageCount - 1);

    /// <summary>Wraps in both directions so a single key can cycle through every page.</summary>
    public int Step(int page, int delta) => ((Clamp(page) + delta) % PageCount + PageCount) % PageCount;

    public KeyAssignment Assign(int keyIndex, int page)
    {
        page = Clamp(page);
        if (keyIndex < 0 || keyIndex >= KeyCount)
            return new KeyAssignment(KeyRole.Empty, -1, page, PageCount);
        if (HasNextPage && keyIndex == NextKey)
            return new KeyAssignment(KeyRole.NextPage, -1, page, PageCount);
        if (page == PageCount - 1 && keyIndex == Capacity - 1)
            return new KeyAssignment(KeyRole.Manage, -1, page, PageCount);

        var item = page * Capacity + keyIndex;
        if (item < BackItems)
            return new KeyAssignment(KeyRole.Back, -1, page, PageCount);
        var app = item - BackItems;
        return app < AppCount
            ? new KeyAssignment(KeyRole.App, app, page, PageCount)
            : new KeyAssignment(KeyRole.Empty, -1, page, PageCount);
    }
}
