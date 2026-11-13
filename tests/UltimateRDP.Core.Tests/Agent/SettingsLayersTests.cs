using System.Text;
using System.Text.Json.Nodes;
using UltimateRDP.Core.Agent;

namespace UltimateRDP.Core.Tests.Agent;

public class SettingsLayersTests
{
    private static JsonObject Parse(string json) =>
        SettingsLayers.ParseObject(Encoding.UTF8.GetBytes(json), out var error) ?? throw new InvalidOperationException(error);

    [Fact]
    public void The_user_overrides_the_machine_key_by_key()
    {
        var machine = Parse("""{ "mapClientDrives": "auto", "fastTransfer": "never", "logLevel": "info" }""");
        var user = Parse("""{ "fastTransfer": "always", "stageFiles": true }""");

        var merged = SettingsLayers.Merge(machine, user);

        Assert.Equal("auto", (string?)merged["mapClientDrives"]);
        Assert.Equal("always", (string?)merged["fastTransfer"]);
        Assert.Equal("info", (string?)merged["logLevel"]);
        Assert.True((bool?)merged["stageFiles"]);
        Assert.Equal(4, merged.Count);
    }

    [Fact]
    public void Keys_are_compared_without_case()
    {
        var merged = SettingsLayers.Merge(Parse("""{ "TrayIcon": "never" }"""), Parse("""{ "trayicon": "auto" }"""));

        Assert.Single(merged);
        Assert.Equal("auto", (string?)merged["trayIcon"]);
    }

    [Fact]
    public void A_null_of_the_user_leaves_the_machine_value()
    {
        var merged = SettingsLayers.Merge(Parse("""{ "mapClientDrives": "auto" }"""), Parse("""{ "mapClientDrives": null }"""));

        Assert.Equal("auto", (string?)merged["mapClientDrives"]);
    }

    [Fact]
    public void Without_a_machine_file_the_user_file_is_the_settings()
    {
        var merged = SettingsLayers.Merge(null, Parse("""{ "directConnections": 4 }"""));

        Assert.Equal(4, (int?)merged["directConnections"]);
        Assert.Single(merged);
    }

    [Fact]
    public void Without_a_user_file_the_machine_file_is_the_settings()
    {
        var merged = SettingsLayers.Merge(Parse("""{ "directConnections": 4 }"""), null);

        Assert.Equal(4, (int?)merged["directConnections"]);
    }

    [Fact]
    public void The_merge_copies_and_changes_neither_file()
    {
        var machine = Parse("""{ "directPortRange": "3400-3449" }""");
        var user = Parse("""{ "stageFiles": false }""");

        var merged = SettingsLayers.Merge(machine, user);
        merged["directPortRange"] = "4000";

        Assert.Equal("3400-3449", (string?)machine["directPortRange"]);
        Assert.Single(user);
    }

    [Fact]
    public void The_machine_keys_in_force_are_the_ones_the_user_does_not_set()
    {
        var machine = Parse("""{ "mapClientDrives": "auto", "fastTransfer": "never", "logLevel": "debug" }""");
        var user = Parse("""{ "FASTTRANSFER": "auto", "logLevel": null }""");

        Assert.Equal(["mapClientDrives", "logLevel"], SettingsLayers.MachineKeysInForce(machine, user));
    }

    [Fact]
    public void Setting_a_value_keeps_the_other_keys_and_one_spelling()
    {
        var user = Parse("""{ "StageFiles": true, "directConnections": 4 }""");

        SettingsLayers.SetUserValue(user, "stageFiles", JsonValue.Create(false));

        Assert.Equal(2, user.Count);
        Assert.False((bool?)user["stageFiles"]);
        Assert.Contains("stageFiles", user.Select(pair => pair.Key));
        Assert.DoesNotContain("StageFiles", user.Select(pair => pair.Key));
        Assert.Equal(4, (int?)user["directConnections"]);
    }

    [Fact]
    public void A_file_written_by_hand_is_read()
    {
        var bytes = (byte[])[0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""
            {
              // turned on for everybody
              "mapClientDrives": "auto",
            }
            """)];

        var value = SettingsLayers.ParseObject(bytes, out var error);

        Assert.Null(error);
        Assert.Equal("auto", (string?)value!["mapClientDrives"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n")]
    public void An_empty_file_is_an_empty_object(string content)
    {
        var value = SettingsLayers.ParseObject(Encoding.UTF8.GetBytes(content), out var error);

        Assert.Null(error);
        Assert.Empty(value!);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"auto\"")]
    [InlineData("{ \"a\": ")]
    [InlineData("{ \"trayIcon\": \"auto\", \"TrayIcon\": \"never\" }")]
    public void What_is_not_one_object_is_refused_with_a_reason(string content)
    {
        Assert.Null(SettingsLayers.ParseObject(Encoding.UTF8.GetBytes(content), out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
