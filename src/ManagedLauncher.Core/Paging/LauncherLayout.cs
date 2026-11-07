namespace ManagedLauncher.Core.Paging;

public enum KeyRole { App, NextPage, PreviousPage, Back, Up, Manage, Empty }

/// <summary>What a list item is, for layout: an app, folder or link takes a key, a break only moves where the next item goes.</summary>
public enum ListItemKind { App, RowBreak, PageBreak }

/// <param name="AppIndex">Index into the app list for <see cref="KeyRole.App"/>; -1 otherwise.</param>
/// <param name="Page">Zero-based page shown.</param>
/// <param name="Pages">Total pages, at least 1.</param>
public readonly record struct KeyAssignment(KeyRole Role, int AppIndex, int Page, int Pages);

/// <summary>
/// Maps a key of the launcher profile to what it shows. The items are an optional Back or Up key, the list's items, and
/// one Manage key, placed in key order page after page:
/// <list type="bullet">
/// <item>an app takes the next key;</item>
/// <item>a row break moves to the start of the next row (a second one in a row leaves an empty row; one right
/// after an app that filled its row does nothing);</item>
/// <item>a page break moves to the next page, unless nothing was placed on the current page yet.</item>
/// </list>
/// When everything doesn't fit on one page, the last key of every page is next-page instead. Back (or Up) is the
/// first item, so it sits on the first key of the first page, or of every page when the list asks for it. Manage takes the last free key of the last page, so it
/// sits on the last key or just left of next-page.
/// </summary>
public sealed class LauncherLayout
{
    private readonly (KeyRole Role, int AppIndex)?[][] pages;
    private readonly KeyRole lead;
    private readonly bool leadOnEveryPage;

    /// <param name="keyCount">Keys on the device.</param>
    /// <param name="columns">Keys per row; row breaks move to the next multiple of it.</param>
    /// <param name="items">The list's items in order; an app's position in this list is its app index.</param>
    /// <param name="hasBack">Whether the first item is a Back key, or an Up key when <paramref name="lead"/> says so.</param>
    /// <param name="lead">Role of that first key: <see cref="KeyRole.Back"/> or <see cref="KeyRole.Up"/>.</param>
    /// <param name="navigation">How pages are turned once the items need more than one page.</param>
    /// <param name="leadOnEveryPage">Repeat the Back or Up key on the first key of every page.</param>
    public LauncherLayout(int keyCount, int columns, IReadOnlyList<ListItemKind> items, bool hasBack = false, KeyRole lead = KeyRole.Back,
        Config.Navigation navigation = Config.Navigation.Carousel, bool leadOnEveryPage = false)
    {
        this.lead = lead;
        this.leadOnEveryPage = leadOnEveryPage;
        KeyCount = Math.Max(0, keyCount);
        Columns = Math.Clamp(columns, 1, Math.Max(1, KeyCount));
        pages = Place(items, hasBack, KeyCount);
        if (pages.Length <= 1 || KeyCount <= 1)
            return;
        if (navigation == Config.Navigation.Arrows && KeyCount / Columns >= 2)
        {
            HasArrows = true;
            pages = PlaceArrows(items, hasBack);
            return;
        }
        HasNextPage = true;
        pages = Place(items, hasBack, KeyCount - 1);
    }

    /// <summary>A list of apps only, no breaks.</summary>
    public LauncherLayout(int keyCount, int appCount, bool hasBack = false)
        : this(keyCount, keyCount, Enumerable.Repeat(ListItemKind.App, Math.Max(0, appCount)).ToList(), hasBack)
    {
    }

    public int KeyCount { get; }

    public int Columns { get; }

    /// <summary>Carousel paging: the last key of every page turns to the next page, wrapping.</summary>
    public bool HasNextPage { get; }

    /// <summary>
    /// Arrow paging: previous on the first key of the last row, next on the last key, each left out where there is
    /// no page that way; the freed key takes items.
    /// </summary>
    public bool HasArrows { get; }

    /// <summary>The page one step away in an arrow layout, stopping at the first and last page.</summary>
    public int Advance(int page, int delta) => Clamp((int)Math.Clamp((long)Clamp(page) + delta, int.MinValue, int.MaxValue));

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

    /// <summary>
    /// Pages for arrow paging. Each page is first tried as the last one (no next key); when the rest does not fit,
    /// the page keeps its next key and the remaining items go on.
    /// </summary>
    private (KeyRole, int)?[][] PlaceArrows(IReadOnlyList<ListItemKind> items, bool hasBack)
    {
        var previousKey = KeyCount - Columns;
        var nextKey = KeyCount - 1;
        var result = new List<(KeyRole, int)?[]>();
        var (start, afterBreak) = (0, false);
        for (var number = 0; ; number++)
        {
            var reserved = number > 0 ? new HashSet<int> { previousKey } : [];
            var last = PlacePage(items, start, afterBreak, hasBack && (number == 0 || leadOnEveryPage), reserved, final: true);
            if (last.Done)
            {
                if (number > 0)
                    last.Page[previousKey] = (KeyRole.PreviousPage, -1);
                result.Add(last.Page);
                return result.ToArray();
            }
            reserved.Add(nextKey);
            var page = PlacePage(items, start, afterBreak, hasBack && (number == 0 || leadOnEveryPage), reserved, final: false);
            if (number > 0)
                page.Page[previousKey] = (KeyRole.PreviousPage, -1);
            page.Page[nextKey] = (KeyRole.NextPage, -1);
            result.Add(page.Page);
            (start, afterBreak) = page.Next == start && start < items.Count ? (start + 1, false) : (page.Next, page.AfterBreak);
        }
    }

    /// <summary>
    /// Fills one page from item <paramref name="start"/>, skipping <paramref name="reserved"/> keys. A final page also
    /// takes Manage on its last free key and is done only when everything fit.
    /// </summary>
    private ((KeyRole, int)?[] Page, int Next, bool AfterBreak, bool Done) PlacePage(
        IReadOnlyList<ListItemKind> items, int start, bool afterBreak, bool withLead, HashSet<int> reserved, bool final)
    {
        var page = new (KeyRole, int)?[KeyCount];
        var slot = 0;

        bool Put(KeyRole role, int index)
        {
            while (slot < KeyCount && reserved.Contains(slot))
                slot++;
            if (slot >= KeyCount)
                return false;
            page[slot++] = (role, index);
            return true;
        }

        if (withLead)
            Put(lead, -1);
        for (var i = start; i < items.Count; i++)
        {
            switch (items[i])
            {
                case ListItemKind.App:
                    if (!Put(KeyRole.App, i))
                        return (page, i, afterBreak, false);
                    afterBreak = false;
                    break;
                case ListItemKind.RowBreak:
                    var column = slot % Columns;
                    if (column != 0)
                        slot += Columns - column;
                    else if (afterBreak)
                        slot += Columns;
                    afterBreak = true;
                    if (slot >= KeyCount)
                        return (page, i + 1, true, false);
                    break;
                case ListItemKind.PageBreak:
                    afterBreak = true;
                    if (slot > 0)
                        return (page, i + 1, true, false);
                    break;
            }
        }
        if (!final)
            return (page, items.Count, afterBreak, false);
        var manage = Enumerable.Range(0, KeyCount).Reverse().FirstOrDefault(s => !reserved.Contains(s), -1);
        while (slot < KeyCount && reserved.Contains(slot))
            slot++;
        if (manage < 0 || slot > manage)
            return (page, items.Count, afterBreak, false);
        page[manage] = (KeyRole.Manage, -1);
        return (page, items.Count, afterBreak, true);
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
            if (hasBack && leadOnEveryPage)
                page[slot++] = (lead, -1);
        }

        void Put(KeyRole role, int index)
        {
            if (slot >= usable)
                NextPage();
            page[slot++] = (role, index);
        }

        if (hasBack)
            Put(lead, -1);
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
