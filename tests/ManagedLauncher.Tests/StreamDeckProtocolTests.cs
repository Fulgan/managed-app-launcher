using System.Text.Json;
using System.Text.Json.Nodes;
using ManagedLauncher.Plugin.StreamDeck;

namespace ManagedLauncher.Tests;

public class StreamDeckProtocolTests
{
    [Fact]
    public void ParsesRegistrationArguments()
    {
        var info = RegistrationInfo.Parse(["-port", "28196", "-pluginUUID", "abc", "-registerEvent", "registerPlugin", "-info", "{}"]);

        Assert.Equal(28196, info.Port);
        Assert.Equal("abc", info.PluginUuid);
        Assert.Equal("registerPlugin", info.RegisterEvent);
    }

    [Fact]
    public void ParsesKeyDownWithSettings()
    {
        const string json = """
            {"action":"com.fulgan.managedapplauncher.slot","event":"keyDown","context":"ctx","device":"dev",
             "payload":{"settings":{"slot":3},"coordinates":{"column":3,"row":0},"isInMultiAction":false}}
            """;

        var ev = StreamDeckEvent.Parse(json);

        Assert.Equal("keyDown", ev.Event);
        Assert.Equal("ctx", ev.Context);
        Assert.Equal(3, ev.Settings!.Value.GetProperty("slot").GetInt32());
        Assert.False(ev.IsInMultiAction);
    }

    [Fact]
    public void SetImageUsesPngDataUri()
    {
        var msg = JsonNode.Parse(Messages.SetImage("ctx", "AAAA"))!;

        Assert.Equal("setImage", (string?)msg["event"]);
        Assert.Equal("ctx", (string?)msg["context"]);
        Assert.Equal("data:image/png;base64,AAAA", (string?)msg["payload"]!["image"]);
        Assert.Equal(0, (int)msg["payload"]!["target"]!);
    }

    [Fact]
    public void SetTitleAndFeedbackShapes()
    {
        var title = JsonNode.Parse(Messages.SetTitle("ctx", "KeePass"))!;
        var ok = JsonNode.Parse(Messages.ShowOk("ctx"))!;
        var alert = JsonNode.Parse(Messages.ShowAlert("ctx"))!;

        Assert.Equal("KeePass", (string?)title["payload"]!["title"]);
        Assert.Equal("showOk", (string?)ok["event"]);
        Assert.Equal("showAlert", (string?)alert["event"]);
        Assert.Equal("ctx", (string?)alert["context"]);
    }

    [Fact]
    public void NullTitleOmitsTitleSoUserTitleShows()
    {
        var msg = JsonNode.Parse(Messages.SetTitle("ctx", null))!;

        Assert.False(msg["payload"]!.AsObject().ContainsKey("title"));
    }

    [Fact]
    public void RegisterMessage()
    {
        using var doc = JsonDocument.Parse(Messages.Register("registerPlugin", "uuid-1"));

        Assert.Equal("registerPlugin", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("uuid-1", doc.RootElement.GetProperty("uuid").GetString());
    }
}
