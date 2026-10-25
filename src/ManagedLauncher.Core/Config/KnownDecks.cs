using System.Text.Json;

namespace ManagedLauncher.Core.Config;

/// <param name="Id">Stream Deck device id, the key used in <see cref="AppConfig.Decks"/>.</param>
public sealed record KnownDeck(string Id, string Name, int Columns, int Rows);

/// <summary>
/// Decks the plugin has seen, so the manager can offer them while the plugin is the only one talking to
/// Stream Deck. Entries are never removed: a deck that is unplugged keeps its list.
/// </summary>
public static class KnownDecks
{
    /// <summary>Missing or unreadable file yields no decks.</summary>
    public static IReadOnlyList<KnownDeck> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return (JsonSerializer.Deserialize<List<KnownDeck>>(stream, ConfigStore.JsonOptions) ?? [])
                .Where(d => d is not null && !string.IsNullOrEmpty(d.Id))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Adds or replaces the deck by id; writes only when something changed. Returns whether it wrote.</summary>
    public static bool Upsert(string path, KnownDeck deck)
    {
        var decks = Load(path).ToList();
        var index = decks.FindIndex(d => d.Id == deck.Id);
        if (index >= 0 && decks[index] == deck)
            return false;
        if (index >= 0)
            decks[index] = deck;
        else
            decks.Add(deck);

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(decks, ConfigStore.JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        return true;
    }
}
