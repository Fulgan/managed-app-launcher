namespace ManagedLauncher.Core;

/// <summary>Append-only text log that rolls over to <c>.1</c> once it passes <see cref="MaxBytes"/>.</summary>
public sealed class FileLog(string path)
{
    public const long MaxBytes = 1024 * 1024;
    private readonly Lock gate = new();

    public string Path { get; } = path;

    public void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, line);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
