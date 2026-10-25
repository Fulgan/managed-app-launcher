namespace ManagedLauncher.Core.Paging;

public enum KeyRole { App, NextPage, Back, Manage, Empty }

/// <summary>What a list item is, for layout: an app takes a key, a break only moves where the next item goes.</summary>
public enum ListItemKind { App, RowBreak, PageBreak }

/// <param name="AppIndex">Index into the app list for <see cref="KeyRole.App"/>; -1 otherwise.</param>
/// <param name="Page">Zero-based page shown.</param>
/// <param name="Pages">Total pages, at least 1.</param>
public readonly record struct KeyAssignment(KeyRole Role, int AppIndex, int Page, int Pages);

/// <summary>
/// Maps a key of the launcher profile to what it shows. The items are an optional Back key, the list's items, and
/// one Manage key, placed in key order page after page:
/// <list type="bullet">
/// <item>an app takes the next key;</item>
/// <item>a row break moves to the start of the next row (a second one in a row leaves an empty row; one right
/// after an app that filled its row does nothing);</item>
/// <item>a page break moves to the next page, unless nothing was placed on the current page yet.</item>
/// </list>
/// When everything doesn't fit on one page, the last key of every page is next-page instead. Back is the first
/// item, so it sits on the first key of the first page only. Manage takes the last free key of the last page, so it
/// sits on the last key or just left of next-page.
/// </summary>
public sealed class LauncherLayout
{
    private readonly (KeyRole Role, int AppIndex)?[][] pages;

    /// <param name="keyCount">Keys on the device.</param>
    /// <param name="columns">Keys per row; row breaks move to the next multiple of it.</param>
    /// <param name="items">The list's items in order; an app's position in this list is its app index.</param>
    /// <param name="hasBack">Whether the first item is a Back key.</param>
    public LauncherLayout(int keyCount, int columns, IReadOnlyList<ListItemKind> items, bool hasBack = false)
    {
        KeyCount = Math.Max(0, keyCount);
        Columns = Math.Clamp(columns, 1, Math.Max(1, KeyCount));
        pages = Place(items, hasBack, KeyCount);
        if (pages.Length > 1 && KeyCount > 1)
        {
            HasNextPage = true;
            pages = Place(items, hasBack, KeyCount - 1);
        }
    }

    /// <summary>A list of apps only, no breaks.</summary>
    public LauncherLayout(int keyCount, int appCount, bool hasBack = false)
        : this(keyCount, keyCount, Enumerable.Repeat(ListItemKind.App, Math.Max(0, appCount)).ToList(), hasBack)
    {
    }

    public int KeyCount { get; }

    public int Columns { get; }

    public bool HasNextPage { get; }

    public int PageCount => pages.Length;

    public int Clamp(int page) => Math.Clamp(page, 0, PageCount - 1);

    /// <summary>Wraps in both directions so a single key can cycle through every page.</summary>
    public int Step(int page, int delta) => ((Clamp(page) + delta) % PageCount + PageCount) % PageCount;

    public KeyAssignment Assign(int keyIndex, int page)
    {
        page = Clamp(page);
        if (keyIndex < 0 || keyIndex >= KeyCount)
            return new KeyAssignment(KeyRole.Empty, -1, page, PageCount);
        if (HasNextPage && keyIndex == KeyCount - 1)
            return new KeyAssignment(KeyRole.NextPage, -1, page, PageCount);
        return pages[page][keyIndex] is { } placed
            ? new KeyAssignment(placed.Role, placed.AppIndex, page, PageCount)
            : new KeyAssignment(KeyRole.Empty, -1, page, PageCount);
    }

    /// <param name="usable">Keys per page that take items: all of them, or all but the next-page key.</param>
    private (KeyRole, int)?[][] Place(IReadOnlyList<ListItemKind> items, bool hasBack, int usable)
    {
        var result = new List<(KeyRole, int)?[]>();
        if (usable <= 0)
            return [new (KeyRole, int)?[KeyCount]];

        var page = new (KeyRole, int)?[KeyCount];
        var slot = 0;
        var afterBreak = false;

        void NextPage()
        {
            result.Add(page);
            page = new (KeyRole, int)?[KeyCount];
            slot = 0;
        }

        void Put(KeyRole role, int index)
        {
            if (slot >= usable)
                NextPage();
            page[slot++] = (role, index);
        }

        if (hasBack)
            Put(KeyRole.Back, -1);
        for (var i = 0; i < items.Count; i++)
        {
            switch (items[i])
            {
                case ListItemKind.App:
                    Put(KeyRole.App, i);
                    afterBreak = false;
                    break;
                case ListItemKind.RowBreak:
                    var column = slot % Columns;
                    if (column != 0)
                        slot += Columns - column;
                    else if (afterBreak)
                        slot += Columns;
                    if (slot >= usable)
                        NextPage();
                    afterBreak = true;
                    break;
                case ListItemKind.PageBreak:
                    if (slot > 0)
                        NextPage();
                    afterBreak = true;
                    break;
            }
        }

        var last = usable - 1;
        if (slot > last)
            NextPage();
        page[last] = (KeyRole.Manage, -1);
        result.Add(page);
        return result.ToArray();
    }
}
