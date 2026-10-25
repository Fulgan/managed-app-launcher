namespace ManagedLauncher.Tests;

internal sealed class TempDir : IDisposable
{
    /// <summary>Hex-only name: no <c>.</c> or <c>_</c>, so it never looks like a version folder to the code under test.</summary>
    public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "malaunch-tests", Guid.NewGuid().ToString("N"))).FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string MakeDir(params string[] parts)
    {
        var dir = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string Touch(params string[] parts)
    {
        var file = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllBytes(file, []);
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
