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
    public void ManifestMeetsMarketplaceFloor()
    {
        var manifest = Manifest();

        Assert.Equal(3, manifest.GetProperty("SDKVersion").GetInt32());
        Assert.True(Version.Parse(manifest.GetProperty("Software").GetProperty("MinimumVersion").GetString()!) >= new Version(6, 9));
    }

    [Fact]
    public void NoShippedCodeReadsThePluginManifest()
    {
        var sources = Directory.EnumerateFiles(RepoFile("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        foreach (var file in sources)
            Assert.DoesNotContain("BaseDirectory, \"manifest.json\"", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("plugin", 256)]
    [InlineData("category", 28)]
    [InlineData("action-open", 20)]
    [InlineData("action-manage", 20)]
    [InlineData("action-slot", 20)]
    [InlineData("action-launch", 20)]
    [InlineData("key-open", 72)]
    [InlineData("key-manage", 72)]
    [InlineData("key-slot", 72)]
    [InlineData("key-launch", 72)]
    public void ShippedImagesHaveMarketplaceSizes(string name, int size)
    {
        using var normal = new System.Drawing.Bitmap(RepoFile("plugin", "imgs", name + ".png"));
        using var high = new System.Drawing.Bitmap(RepoFile("plugin", "imgs", name + "@2x.png"));

        Assert.Equal(new System.Drawing.Size(size, size), normal.Size);
        Assert.Equal(new System.Drawing.Size(2 * size, 2 * size), high.Size);
    }

    [Theory]
    [InlineData("app-icon.png", 288, 288)]
    [InlineData("thumbnail.png", 1920, 960)]
    public void ListingArtHasMarketplaceSizes(string file, int width, int height)
    {
        using var image = new System.Drawing.Bitmap(RepoFile("marketplace", file));

        Assert.Equal(new System.Drawing.Size(width, height), image.Size);
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
    public void PropertyInspectorsAcceptConnectBeforePiJsLoads()
    {
        const string stub = "<script>window.connectElgatoStreamDeckSocket = (...args) => { window.earlyConnect = args; };</script>";
        var pages = Manifest().GetProperty("Actions").EnumerateArray()
            .Where(a => a.TryGetProperty("PropertyInspectorPath", out _))
            .Select(a => a.GetProperty("PropertyInspectorPath").GetString()!)
            .ToList();

        Assert.NotEmpty(pages);
        foreach (var page in pages)
        {
            var html = File.ReadAllText(RepoFile("plugin", page));
            var head = html.IndexOf("<head>", StringComparison.Ordinal);
            Assert.True(head >= 0, page);
            Assert.Equal(html.IndexOf("<script", head, StringComparison.Ordinal), html.IndexOf(stub, StringComparison.Ordinal));
            Assert.True(html.IndexOf(stub, StringComparison.Ordinal) < html.IndexOf("<link", StringComparison.Ordinal), page);
        }
        Assert.EndsWith("if (window.earlyConnect) connectElgatoStreamDeckSocket(...window.earlyConnect);", File.ReadAllText(RepoFile("plugin", "pi", "pi.js")).TrimEnd());
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
