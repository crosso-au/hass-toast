using System.Xml.Linq;
using HassToast.Agent.Toasts;
using Xunit;

namespace HassToast.Agent.Tests;

public sealed class ActivationArgumentsTests
{
    [Fact]
    public void Round_trips_simple_pairs()
    {
        var original = new Dictionary<string, string> { ["action"] = "ack", ["id"] = "42" };

        var decoded = ActivationArguments.Decode(ActivationArguments.Encode(original));

        Assert.Equal("ack", decoded["action"]);
        Assert.Equal("42", decoded["id"]);
    }

    [Theory]
    // Each of these would corrupt or forge a pair under the naive k=v;k=v convention.
    [InlineData("a=b&c=d")]
    [InlineData("value;with;semicolons")]
    [InlineData("equals=inside=value")]
    [InlineData("ampersand & more")]
    [InlineData("percent %20 encoded")]
    [InlineData("unicode caf\u00e9 \u00e7ay")]
    public void Round_trips_values_containing_separator_characters(string hostile)
    {
        var original = new Dictionary<string, string> { ["payload"] = hostile };

        var decoded = ActivationArguments.Decode(ActivationArguments.Encode(original));

        Assert.Equal(hostile, decoded["payload"]);
    }

    [Fact]
    public void A_crafted_value_cannot_invent_an_extra_pair()
    {
        // The whole point of encoding: this must stay one key, not become two.
        var original = new Dictionary<string, string> { ["action"] = "ack&admin=true" };

        var decoded = ActivationArguments.Decode(ActivationArguments.Encode(original));

        Assert.Single(decoded);
        Assert.Equal("ack&admin=true", decoded["action"]);
        Assert.False(decoded.ContainsKey("admin"));
    }

    [Fact]
    public void A_crafted_key_cannot_impersonate_the_reserved_identity_keys()
    {
        var original = new Dictionary<string, string> { ["x"] = "1&_tag=spoofed" };

        var decoded = ActivationArguments.Decode(
            ActivationArguments.Encode(ActivationArguments.WithIdentity(original, "real-tag", null)));

        Assert.Equal("real-tag", decoded[ActivationArguments.TagKey]);
    }

    [Fact]
    public void Identity_keys_are_added_only_when_present()
    {
        var withNeither = ActivationArguments.WithIdentity(null, null, null);
        Assert.Empty(withNeither);

        var withTag = ActivationArguments.WithIdentity(null, "t", null);
        Assert.Equal("t", withTag[ActivationArguments.TagKey]);
        Assert.False(withTag.ContainsKey(ActivationArguments.GroupKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_input_decodes_to_nothing(string? encoded)
    {
        Assert.Empty(ActivationArguments.Decode(encoded));
    }

    [Fact]
    public void A_bare_token_is_kept_as_an_empty_valued_key()
    {
        // Windows hands back reserved words like "dismiss" with no '='; losing them silently
        // would make an activation impossible to diagnose.
        var decoded = ActivationArguments.Decode("dismiss");

        Assert.True(decoded.ContainsKey("dismiss"));
        Assert.Equal("", decoded["dismiss"]);
    }

    [Fact]
    public void A_malformed_escape_sequence_does_not_throw()
    {
        var decoded = ActivationArguments.Decode("key=%ZZnot-valid");
        Assert.Single(decoded);
    }
}

public sealed class ToastActionXmlTests
{
    private static XDocument Build(ToastPayload payload, ResolvedImages? images = null)
        => ToastXmlWriter.Build(payload, images ?? new ResolvedImages());

    private static ToastPayload WithButtons(params ToastButton[] buttons)
        => new() { Visual = { Text = ["body"] }, Buttons = [.. buttons] };

    [Fact]
    public void Buttons_carry_encoded_arguments_and_the_toast_identity()
    {
        var payload = WithButtons(new ToastButton
        {
            Content = "Ack",
            Args = new Dictionary<string, string> { ["action"] = "ack" },
        });
        payload.Tag = "build-42";
        payload.Group = "ci";

        var action = Build(payload).Descendants("action").Single();
        var args = ActivationArguments.Decode(action.Attribute("arguments")!.Value);

        Assert.Equal("ack", args["action"]);
        Assert.Equal("build-42", args[ActivationArguments.TagKey]);
        Assert.Equal("ci", args[ActivationArguments.GroupKey]);
    }

    [Fact]
    public void Button_styles_require_the_toast_to_opt_in()
    {
        // hint-buttonStyle is ignored unless useButtonStyle is set on the toast element.
        var payload = WithButtons(new ToastButton { Content = "Go", Style = "Success" });

        var document = Build(payload);

        Assert.Equal("true", document.Root!.Attribute("useButtonStyle")!.Value);
        Assert.Equal("Success", document.Descendants("action").Single().Attribute("hint-buttonStyle")!.Value);
    }

    [Fact]
    public void Toasts_without_styled_buttons_do_not_set_useButtonStyle()
    {
        var document = Build(WithButtons(new ToastButton { Content = "Plain" }));
        Assert.Null(document.Root!.Attribute("useButtonStyle"));
    }

    [Theory]
    [InlineData("green", "Success")]
    [InlineData("RED", "Critical")]
    [InlineData("success", "Success")]
    public void Button_styles_are_normalised(string input, string expected)
    {
        Assert.Equal(expected, ToastXmlWriter.NormaliseButtonStyle(input));
    }

    [Fact]
    public void A_protocol_button_whose_target_was_rejected_is_dropped_entirely()
    {
        // The composer blanks a refused Uri. Emitting the button anyway would leave a control
        // that looks live but does nothing.
        var payload = WithButtons(
            new ToastButton { Content = "Blocked", Activation = "protocol", Uri = null },
            new ToastButton { Content = "Fine", Args = new Dictionary<string, string>() });

        var actions = Build(payload).Descendants("action").ToList();

        Assert.Single(actions);
        Assert.Equal("Fine", actions[0].Attribute("content")!.Value);
    }

    [Fact]
    public void Protocol_buttons_put_the_uri_in_arguments()
    {
        var payload = WithButtons(new ToastButton
        {
            Content = "Open", Activation = "protocol", Uri = "https://example.com/x",
        });

        var action = Build(payload).Descendants("action").Single();

        Assert.Equal("protocol", action.Attribute("activationType")!.Value);
        Assert.Equal("https://example.com/x", action.Attribute("arguments")!.Value);
    }

    [Fact]
    public void System_buttons_use_the_reserved_arguments()
    {
        var payload = WithButtons(
            new ToastButton { Type = "snooze", InputId = "snoozeFor" },
            new ToastButton { Type = "dismiss" });

        var actions = Build(payload).Descendants("action").ToList();

        Assert.Equal("system", actions[0].Attribute("activationType")!.Value);
        Assert.Equal("snooze", actions[0].Attribute("arguments")!.Value);
        Assert.Equal("snoozeFor", actions[0].Attribute("hint-inputId")!.Value);
        Assert.Equal("dismiss", actions[1].Attribute("arguments")!.Value);
    }

    [Fact]
    public void Buttons_and_context_menu_items_share_one_budget_of_five()
    {
        var payload = new ToastPayload { Visual = { Text = ["t"] } };
        for (var i = 0; i < 4; i++)
            payload.Buttons.Add(new ToastButton { Content = $"b{i}" });
        for (var i = 0; i < 4; i++)
            payload.ContextMenu.Add(new ToastButton { Content = $"c{i}" });

        var actions = Build(payload).Descendants("action").ToList();

        Assert.Equal(5, actions.Count);
        Assert.Equal(4, actions.Count(a => a.Attribute("placement") is null));
        Assert.Equal(1, actions.Count(a => a.Attribute("placement")?.Value == "contextMenu"));
    }

    [Fact]
    public void Inputs_are_capped_at_five()
    {
        var payload = new ToastPayload { Visual = { Text = ["t"] } };
        for (var i = 0; i < 8; i++)
            payload.Inputs.Add(new ToastInput { Id = $"in{i}", Type = "text" });

        Assert.Equal(5, Build(payload).Descendants("input").Count());
    }

    [Fact]
    public void Selection_inputs_are_capped_at_five_items()
    {
        var input = new ToastInput { Id = "pick", Type = "selection" };
        for (var i = 0; i < 9; i++)
            input.Items.Add(new ToastSelectionItem { Id = $"{i}", Content = $"Option {i}" });

        var payload = new ToastPayload { Visual = { Text = ["t"] }, Inputs = [input] };

        Assert.Equal(5, Build(payload).Descendants("selection").Count());
    }

    [Fact]
    public void A_text_input_emits_its_placeholder_and_a_selection_emits_its_options()
    {
        var payload = new ToastPayload
        {
            Visual = { Text = ["t"] },
            Inputs =
            [
                new ToastInput { Id = "reply", Type = "text", Placeholder = "Type here", Title = "Reply" },
                new ToastInput
                {
                    Id = "when", Type = "selection", Default = "10",
                    Items = [new ToastSelectionItem { Id = "10", Content = "10 minutes" }],
                },
            ],
        };

        var inputs = Build(payload).Descendants("input").ToList();

        Assert.Equal("text", inputs[0].Attribute("type")!.Value);
        Assert.Equal("Type here", inputs[0].Attribute("placeHolderContent")!.Value);
        Assert.Equal("Reply", inputs[0].Attribute("title")!.Value);

        Assert.Equal("selection", inputs[1].Attribute("type")!.Value);
        Assert.Equal("10", inputs[1].Attribute("defaultInput")!.Value);
        Assert.Equal("10 minutes", inputs[1].Element("selection")!.Attribute("content")!.Value);
    }

    [Fact]
    public void An_input_without_an_id_is_skipped()
    {
        // The id is how the typed value comes back; without one the input is unreadable.
        var payload = new ToastPayload
        {
            Visual = { Text = ["t"] },
            Inputs = [new ToastInput { Id = "", Type = "text" }],
        };

        Assert.Empty(Build(payload).Descendants("input"));
    }

    [Fact]
    public void Button_icons_are_all_or_nothing()
    {
        // If any button carries an icon Windows switches the whole toast to icon buttons and
        // the unillustrated ones render blank, so a partial set must be ignored entirely.
        var withIcon = new ToastButton { Content = "A", Icon = "https://example.com/a.png" };
        var without = new ToastButton { Content = "B" };
        var payload = WithButtons(withIcon, without);

        var images = new ResolvedImages();
        images.Add(withIcon, @"C:\cache\a.png");

        var actions = Build(payload, images).Descendants("action").ToList();

        Assert.All(actions, a => Assert.Null(a.Attribute("imageUri")));
    }

    [Fact]
    public void Button_icons_are_emitted_when_every_button_has_one()
    {
        var a = new ToastButton { Content = "A", Icon = "https://example.com/a.png" };
        var b = new ToastButton { Content = "B", Icon = "https://example.com/b.png" };
        var payload = WithButtons(a, b);

        var images = new ResolvedImages();
        images.Add(a, @"C:\cache\a.png");
        images.Add(b, @"C:\cache\b.png");

        var actions = Build(payload, images).Descendants("action").ToList();

        Assert.Equal(@"C:\cache\a.png", actions[0].Attribute("imageUri")!.Value);
        Assert.Equal(@"C:\cache\b.png", actions[1].Attribute("imageUri")!.Value);
    }

    [Fact]
    public void Hostile_button_content_is_escaped_like_everything_else()
    {
        var payload = WithButtons(new ToastButton
        {
            Content = "\"/><action content='pwn' activationType='protocol' arguments='file:///c:/x.exe'/><action content='",
        });

        var document = Build(payload);

        Assert.Single(document.Descendants("action"));
        Assert.DoesNotContain(document.Descendants("action"),
            a => a.Attribute("arguments")?.Value.StartsWith("file:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void A_header_is_emitted_with_its_identity()
    {
        var payload = new ToastPayload
        {
            Visual = { Text = ["t"] },
            Header = new ToastHeader { Id = "ci", Title = "Continuous integration" },
        };

        var header = Build(payload).Descendants("header").Single();

        Assert.Equal("ci", header.Attribute("id")!.Value);
        Assert.Equal("Continuous integration", header.Attribute("title")!.Value);
    }

    [Theory]
    [InlineData("reminder")]
    [InlineData("alarm")]
    [InlineData("incomingCall")]
    public void A_scenario_that_needs_a_button_gets_one(string scenario)
    {
        // Windows only honours these scenarios when the toast has at least one button, and
        // silently downgrades to an ordinary toast otherwise — so a payload asking to stay on
        // screen would quietly fade away instead.
        var payload = new ToastPayload { Scenario = scenario, Visual = { Text = ["body"] } };

        var document = Build(payload);

        Assert.Equal(scenario, document.Root!.Attribute("scenario")!.Value);

        var action = Assert.Single(document.Descendants("action"));
        Assert.Equal("system", action.Attribute("activationType")!.Value);
        Assert.Equal("dismiss", action.Attribute("arguments")!.Value);
    }

    [Fact]
    public void A_supplied_button_is_not_joined_by_an_added_one()
    {
        var payload = new ToastPayload
        {
            Scenario = "reminder",
            Visual = { Text = ["body"] },
            Buttons = [new ToastButton { Content = "Snooze me", Args = new Dictionary<string, string>() }],
        };

        var actions = Build(payload).Descendants("action").ToList();

        Assert.Single(actions);
        Assert.Equal("Snooze me", actions[0].Attribute("content")!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    [InlineData("urgent")]
    public void Scenarios_that_do_not_need_a_button_get_none(string? scenario)
    {
        var payload = new ToastPayload { Scenario = scenario, Visual = { Text = ["body"] } };

        Assert.Empty(Build(payload).Descendants("action"));
    }

    [Fact]
    public void A_tagged_toast_carries_its_identity_even_with_no_launch_configured()
    {
        // The body is always clickable. Without a launch attribute the click arrives with empty
        // arguments and Home Assistant cannot tell which toast it came from.
        var payload = new ToastPayload
        {
            Tag = "build-42",
            Group = "ci",
            Visual = { Text = ["body"] },
        };

        var launch = Build(payload).Root!.Attribute("launch");

        Assert.NotNull(launch);
        var args = ActivationArguments.Decode(launch!.Value);
        Assert.Equal("build-42", args[ActivationArguments.TagKey]);
        Assert.Equal("ci", args[ActivationArguments.GroupKey]);
    }

    [Fact]
    public void An_untagged_toast_with_no_launch_emits_no_launch_attribute()
    {
        var payload = new ToastPayload { Visual = { Text = ["body"] } };
        Assert.Null(Build(payload).Root!.Attribute("launch"));
    }

    [Fact]
    public void A_foreground_launch_encodes_arguments_and_identity()
    {
        var payload = new ToastPayload
        {
            Tag = "t1",
            Visual = { Text = ["t"] },
            Launch = new ToastLaunch
            {
                Type = "foreground",
                Args = new Dictionary<string, string> { ["screen"] = "camera" },
            },
        };

        var root = Build(payload).Root!;
        var args = ActivationArguments.Decode(root.Attribute("launch")!.Value);

        Assert.Equal("foreground", root.Attribute("activationType")!.Value);
        Assert.Equal("camera", args["screen"]);
        Assert.Equal("t1", args[ActivationArguments.TagKey]);
    }
}

public sealed class ToastRegistryTests
{
    [Fact]
    public void Sequence_numbers_increase_on_every_update()
    {
        // Windows ignores an update whose sequence is not newer than the one it holds.
        var registry = new ToastRegistry();

        Assert.Equal(1u, registry.Register("tag", "group"));
        Assert.Equal(2u, registry.NextSequence("tag", "group"));
        Assert.Equal(3u, registry.NextSequence("tag", "group"));
    }

    [Fact]
    public void An_unknown_tag_has_no_next_sequence()
    {
        Assert.Null(new ToastRegistry().NextSequence("never-seen", null));
    }

    [Fact]
    public void Adopting_a_tag_starts_high_enough_to_outrank_a_pre_restart_sequence()
    {
        // After a restart the counter is gone but Windows still holds the old sequence, so
        // resuming from 1 would be silently ignored.
        var registry = new ToastRegistry();

        var adopted = registry.Adopt("tag", null);

        Assert.True(adopted > 1000, $"adopted sequence {adopted} is too low to win");
        Assert.Equal(adopted + 1, registry.NextSequence("tag", null));
    }

    [Fact]
    public void Tag_and_group_together_form_the_identity()
    {
        var registry = new ToastRegistry();
        registry.Register("same", "group-a");
        registry.Register("same", "group-b");

        Assert.Equal(2, registry.Count);
        Assert.Equal(2u, registry.NextSequence("same", "group-a"));
        Assert.Equal(2u, registry.NextSequence("same", "group-b"));
    }

    [Fact]
    public void Forgetting_a_tag_removes_only_that_one()
    {
        var registry = new ToastRegistry();
        registry.Register("a", null);
        registry.Register("b", null);

        registry.Forget("a", null);

        Assert.Null(registry.NextSequence("a", null));
        Assert.NotNull(registry.NextSequence("b", null));
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var registry = new ToastRegistry();
        registry.Register("a", null);
        registry.Register("b", "g");

        registry.Clear();

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Concurrent_updates_never_hand_out_the_same_sequence_twice()
    {
        var registry = new ToastRegistry();
        registry.Register("tag", null);

        var seen = new System.Collections.Concurrent.ConcurrentBag<uint>();
        Parallel.For(0, 200, _ =>
        {
            if (registry.NextSequence("tag", null) is { } sequence) seen.Add(sequence);
        });

        Assert.Equal(200, seen.Count);
        Assert.Equal(200, seen.Distinct().Count());
    }
}
