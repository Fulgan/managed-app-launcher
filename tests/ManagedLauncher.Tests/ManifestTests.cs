using System.Text.Json;
using ManagedLauncher.Core.Import;

namespace ManagedLauncher.Tests;

public class ManifestTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ManagedLauncher.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, .. parts]);
    }

    private static JsonElement Manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("plugin", "manifest.json")));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void AssemblyVersionMatchesPluginManifest()
    {
        var assembly = typeof(SlotPageBuilder).Assembly.GetName().Version!.ToString();

        Assert.Equal(Manifest().GetProperty("Version").GetString(), assembly);
    }

    [Fact]
    public void ManifestDeclaresIdsUsedByCode()
    {
        var manifest = Manifest();
        var actions = manifest.GetProperty("Actions").EnumerateArray().Select(a => a.GetProperty("UUID").GetString()).ToList();

        Assert.Equal(SlotPageBuilder.PluginUuid, manifest.GetProperty("UUID").GetString());
        Assert.Contains(SlotPageBuilder.SlotActionUuid, actions);
        Assert.Contains(ManagedLauncher.Plugin.Keys.KeyController.LaunchAction, actions);
        Assert.Contains(ManagedLauncher.Plugin.Keys.KeyController.OpenAction, actions);
        Assert.Contains(ManagedLauncher.Plugin.Keys.KeyController.ManageAction, actions);
        Assert.True(File.Exists(RepoFile("plugin", manifest.GetProperty("Icon").GetString() + ".png")));
    }

    [Fact]
    public void EveryManifestImageExistsInBothSizes()
    {
        var manifest = Manifest();
        var paths = new List<string> { manifest.GetProperty("Icon").GetString()!, manifest.GetProperty("CategoryIcon").GetString()! };
        foreach (var action in manifest.GetProperty("Actions").EnumerateArray())
        {
            paths.Add(action.GetProperty("Icon").GetString()!);
            paths.AddRange(action.GetProperty("States").EnumerateArray().Select(s => s.GetProperty("Image").GetString()!));
        }

        foreach (var path in paths)
        {
            Assert.True(File.Exists(RepoFile("plugin", path + ".png")), path);
            Assert.True(File.Exists(RepoFile("plugin", path + "@2x.png")), path + "@2x");
        }
    }

    [Fact]
    public void ShippedManageKeyMatchesRuntimeDrawing()
    {
        using var shipped = new System.Drawing.Bitmap(RepoFile("plugin", "imgs", "key-manage@2x.png"));
        using var stream = new MemoryStream(ManagedLauncher.Core.Icons.KeyRenderer.RenderManagePng());
        using var runtime = new System.Drawing.Bitmap(stream);

        Assert.Equal(shipped.Size, runtime.Size);
        for (var y = 0; y < shipped.Height; y += 3)
            for (var x = 0; x < shipped.Width; x += 3)
                Assert.Equal(shipped.GetPixel(x, y), runtime.GetPixel(x, y));
    }
}
