namespace ManagedLauncher.Cli;

/// <summary>Positional arguments plus <c>--name value</c> options and bare <c>--flag</c> switches.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    /// <param name="flags">Option names that take no value.</param>
    public CommandLine(IEnumerable<string> args, params string[] flags)
    {
        var flagSet = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var a = e.Current;
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                var name = a[2..];
                if (flagSet.Contains(name))
                    options[name] = null;
                else
                    options[name] = e.MoveNext() ? e.Current : throw new UsageException($"--{name} needs a value");
            }
            else
            {
                Positional.Add(a);
            }
        }
    }

    public bool Has(string name) => options.ContainsKey(name);

    public string? Get(string name) => options.GetValueOrDefault(name);

    public int? GetInt(string name) =>
        Get(name) is { } v ? int.TryParse(v, out var i) ? i : throw new UsageException($"--{name} must be a number") : null;

    public T? GetEnum<T>(string name) where T : struct, Enum =>
        Get(name) is { } v
            ? Enum.TryParse<T>(v, ignoreCase: true, out var e) ? e : throw new UsageException($"--{name}: one of {string.Join('|', Enum.GetNames<T>()).ToLowerInvariant()}")
            : null;

    public bool? GetBool(string name) =>
        !Has(name) ? null
        : Get(name) is null ? true
        : bool.TryParse(Get(name), out var b) ? b : throw new UsageException($"--{name} must be true or false");

    public string Arg(int index, string what) =>
        index < Positional.Count ? Positional[index] : throw new UsageException($"missing {what}");
}

internal sealed class UsageException(string message) : Exception(message);
