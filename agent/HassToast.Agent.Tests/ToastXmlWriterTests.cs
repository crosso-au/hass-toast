using System.Xml.Linq;
using HassToast.Agent.Toasts;
using Xunit;

namespace HassToast.Agent.Tests;

public sealed class ToastXmlWriterTests
{
    private static string Xml(ToastPayload payload, ResolvedImages? images = null)
        => ToastXmlWriter.Build(payload, images ?? new ResolvedImages())
            .ToString(SaveOptions.DisableFormatting);

    private static ToastPayload TextPayload(params string[] lines)
        => new() { Visual = { Text = [.. lines] } };

    // ---------------------------------------------------------------- injection safety

    [Theory]
    [InlineData("</text><action content='pwn' arguments='x' activationType='protocol'/><text>")]
    [InlineData("<toast useButtonStyle='true'><visual>")]
    [InlineData("\" onclick=\"evil()")]
    [InlineData("]]><!--")]
    [InlineData("& < > \" '")]
    public void Hostile_text_is_escaped_rather_than_becoming_markup(string hostile)
    {
        var document = ToastXmlWriter.Build(TextPayload(hostile), new ResolvedImages());

        // The decisive check: reparse and confirm the value survived as character data. If any
        // of it had become markup, the round trip would not return the original string.
        var text = document.Descendants("text").First();
        Assert.Equal(hostile, text.Value);

        // And the binding gained no smuggled elements.
        var binding = document.Descendants("binding").First();
        Assert.Single(binding.Elements());
        Assert.Empty(document.Descendants("action"));
    }

    [Fact]
    public void Hostile_text_does_not_increase_the_element_count()
    {
        var benign = ToastXmlWriter.Build(TextPayload("hello"), new ResolvedImages());
        var hostile = ToastXmlWriter.Build(
            TextPayload("</text><image src='x'/><text>"), new ResolvedImages());

        Assert.Equal(
            benign.Descendants().Count(),
            hostile.Descendants().Count());
    }

    [Fact]
    public void Hostile_attribute_values_are_escaped()
    {
        var payload = new ToastPayload
        {
            Visual =
            {
                Text = ["title"],
                Progress = new ToastProgress
                {
                    Status = "\"/><action content='pwn'/><progress status=\"",
                    Value = 0.5,
                },
            },
        };

        var document = ToastXmlWriter.Build(payload, new ResolvedImages());

        Assert.Empty(document.Descendants("action"));
        Assert.Single(document.Descendants("progress"));
        Assert.Equal("\"/><action content='pwn'/><progress status=\"",
            document.Descendants("progress").First().Attribute("status")!.Value);
    }

    [Fact]
    public void Generated_xml_always_reparses()
    {
        // Whatever goes in, the output must remain well-formed — Windows silently drops a
        // malformed payload, which is the worst possible failure mode.
        var payload = new ToastPayload
        {
            Visual =
            {
                Text = ["<a>", "b & c", "]]>"],
                Attribution = "<!--x-->",
                Progress = new ToastProgress { Status = "<status>", Title = "&amp;" },
                Groups =
                [
                    new ToastGroup
                    {
                        Columns =
                        [
                            new ToastColumn
                            {
                                Children = [new ToastColumnChild { Text = "</subgroup>", Style = "title" }],
                            },
                        ],
                    },
                ],
            },
        };

        var xml = Xml(payload);
        var reparsed = XDocument.Parse(xml); // throws if malformed

        Assert.Equal("toast", reparsed.Root!.Name.LocalName);
    }

    // ---------------------------------------------------------------- structure

    [Fact]
    public void Emits_a_toast_generic_binding()
    {
        var document = ToastXmlWriter.Build(TextPayload("hi"), new ResolvedImages());

        Assert.Equal("toast", document.Root!.Name.LocalName);
        Assert.Equal("ToastGeneric", document.Descendants("binding").First().Attribute("template")!.Value);
    }

    [Fact]
    public void Caps_top_level_text_at_three_lines()
    {
        // Windows drops the rest anyway; emitting them invites confusing partial rendering.
        var document = ToastXmlWriter.Build(
            TextPayload("one", "two", "three", "four", "five"), new ResolvedImages());

        var lines = document.Descendants("binding").First()
            .Elements("text").Where(e => e.Attribute("placement") is null).ToList();

        Assert.Equal(3, lines.Count);
        Assert.Equal(["one", "two", "three"], lines.Select(l => l.Value));
    }

    [Fact]
    public void Blank_lines_are_skipped_without_consuming_the_budget()
    {
        var document = ToastXmlWriter.Build(
            TextPayload("one", "   ", "", "two"), new ResolvedImages());

        var lines = document.Descendants("binding").First()
            .Elements("text").Where(e => e.Attribute("placement") is null).ToList();

        Assert.Equal(["one", "two"], lines.Select(l => l.Value));
    }

    [Fact]
    public void Text_elements_come_before_everything_else_in_the_binding()
    {
        // Windows pulls stray text to the top or drops it, so ordering is not cosmetic.
        var payload = new ToastPayload
        {
            Visual =
            {
                Text = ["a", "b"],
                Attribution = "src",
                Progress = new ToastProgress { Status = "working" },
                Groups = [new ToastGroup { Columns = [new ToastColumn { Children = [new ToastColumnChild { Text = "x" }] }] }],
            },
        };

        var binding = ToastXmlWriter.Build(payload, new ResolvedImages()).Descendants("binding").First();
        var names = binding.Elements().Select(e => e.Name.LocalName).ToList();

        var lastText = names.LastIndexOf("text");
        var firstNonText = names.FindIndex(n => n != "text");

        Assert.True(firstNonText == -1 || lastText < firstNonText,
            $"text elements are not contiguous at the start: {string.Join(",", names)}");
    }

    [Fact]
    public void Attribution_is_a_text_element_with_the_attribution_placement()
    {
        var payload = TextPayload("body");
        payload.Visual.Attribution = "Jenkins";

        var document = ToastXmlWriter.Build(payload, new ResolvedImages());
        var attribution = document.Descendants("text")
            .Single(e => e.Attribute("placement")?.Value == "attribution");

        Assert.Equal("Jenkins", attribution.Value);
    }

    // ---------------------------------------------------------------- progress

    [Fact]
    public void Progress_writes_value_and_status()
    {
        var payload = TextPayload("t");
        payload.Visual.Progress = new ToastProgress
        {
            Title = "Deploying",
            Value = 0.42,
            Status = "Uploading",
            ValueOverride = "4/10",
        };

        var progress = ToastXmlWriter.Build(payload, new ResolvedImages()).Descendants("progress").Single();

        Assert.Equal("0.42", progress.Attribute("value")!.Value);
        Assert.Equal("Uploading", progress.Attribute("status")!.Value);
        Assert.Equal("Deploying", progress.Attribute("title")!.Value);
        Assert.Equal("4/10", progress.Attribute("valueStringOverride")!.Value);
    }

    [Fact]
    public void A_null_progress_value_means_indeterminate()
    {
        var payload = TextPayload("t");
        payload.Visual.Progress = new ToastProgress { Value = null, Status = "Working" };

        var progress = ToastXmlWriter.Build(payload, new ResolvedImages()).Descendants("progress").Single();

        Assert.Equal("indeterminate", progress.Attribute("value")!.Value);
    }

    [Theory]
    [InlineData(-5.0, "0")]
    [InlineData(1.7, "1")]
    public void Out_of_range_progress_values_are_clamped(double value, string expected)
    {
        // Windows renders a nonsense bar for values outside 0..1 rather than rejecting them.
        var payload = TextPayload("t");
        payload.Visual.Progress = new ToastProgress { Value = value, Status = "s" };

        var progress = ToastXmlWriter.Build(payload, new ResolvedImages()).Descendants("progress").Single();

        Assert.Equal(expected, progress.Attribute("value")!.Value);
    }

    [Fact]
    public void Progress_value_uses_invariant_formatting()
    {
        // A comma decimal separator under a European locale would produce invalid XML.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var payload = TextPayload("t");
            payload.Visual.Progress = new ToastProgress { Value = 0.5, Status = "s" };

            var progress = ToastXmlWriter.Build(payload, new ResolvedImages()).Descendants("progress").Single();

            Assert.Equal("0.5", progress.Attribute("value")!.Value);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ---------------------------------------------------------------- groups

    [Fact]
    public void Groups_become_group_and_subgroup_elements()
    {
        var payload = TextPayload("t");
        payload.Visual.Groups =
        [
            new ToastGroup
            {
                Columns =
                [
                    new ToastColumn
                    {
                        Weight = 2,
                        TextStacking = "center",
                        Children =
                        [
                            new ToastColumnChild { Text = "CPU", Style = "captionSubtle" },
                            new ToastColumnChild { Text = "94%", Style = "titleNumeral", Align = "center" },
                        ],
                    },
                    new ToastColumn { Children = [new ToastColumnChild { Text = "RAM" }] },
                ],
            },
        ];

        var document = ToastXmlWriter.Build(payload, new ResolvedImages());
        var group = document.Descendants("group").Single();
        var subgroups = group.Elements("subgroup").ToList();

        Assert.Equal(2, subgroups.Count);
        Assert.Equal("2", subgroups[0].Attribute("hint-weight")!.Value);
        Assert.Equal("center", subgroups[0].Attribute("hint-textStacking")!.Value);

        var first = subgroups[0].Elements("text").ToList();
        Assert.Equal("captionSubtle", first[0].Attribute("hint-style")!.Value);
        Assert.Equal("titleNumeral", first[1].Attribute("hint-style")!.Value);
        Assert.Equal("center", first[1].Attribute("hint-align")!.Value);
    }

    [Fact]
    public void Empty_subgroups_and_groups_are_dropped()
    {
        // An empty subgroup renders as dead space in the toast.
        var payload = TextPayload("t");
        payload.Visual.Groups =
        [
            new ToastGroup { Columns = [new ToastColumn { Children = [] }] },
            new ToastGroup { Columns = [] },
        ];

        var document = ToastXmlWriter.Build(payload, new ResolvedImages());

        Assert.Empty(document.Descendants("group"));
        Assert.Empty(document.Descendants("subgroup"));
    }

    [Fact]
    public void A_group_image_without_a_resolved_path_is_skipped_not_emitted_broken()
    {
        // Policy may have rejected it, or the fetch may have failed. Emitting a src that points
        // nowhere gives a silently broken toast.
        var payload = TextPayload("t");
        payload.Visual.Groups =
        [
            new ToastGroup
            {
                Columns =
                [
                    new ToastColumn
                    {
                        Children =
                        [
                            new ToastColumnChild { Src = "https://blocked.example/x.png" },
                            new ToastColumnChild { Text = "still here" },
                        ],
                    },
                ],
            },
        ];

        var document = ToastXmlWriter.Build(payload, new ResolvedImages());

        Assert.Empty(document.Descendants("image"));
        Assert.Equal("still here", document.Descendants("subgroup").Single().Element("text")!.Value);
    }

    [Fact]
    public void Resolved_images_are_emitted_with_their_local_path()
    {
        var logo = new ToastImage { Src = "https://example.com/a.png", Crop = "circle", Alt = "avatar" };
        var payload = TextPayload("t");
        payload.Visual.AppLogo = logo;

        var images = new ResolvedImages();
        images.Add(logo, @"C:\cache\a.png");

        var image = ToastXmlWriter.Build(payload, images).Descendants("image").Single();

        Assert.Equal("appLogoOverride", image.Attribute("placement")!.Value);
        Assert.Equal(@"C:\cache\a.png", image.Attribute("src")!.Value);
        Assert.Equal("circle", image.Attribute("hint-crop")!.Value);
        Assert.Equal("avatar", image.Attribute("alt")!.Value);
    }

    // ---------------------------------------------------------------- normalisation

    [Theory]
    [InlineData("reminder", "reminder")]
    [InlineData("ALARM", "alarm")]
    [InlineData("incomingcall", "incomingCall")]
    [InlineData("urgent", "urgent")]
    public void Scenario_is_normalised_to_the_spelling_windows_expects(string input, string expected)
    {
        Assert.Equal(expected, ToastXmlWriter.NormaliseScenario(input));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("nonsense")]
    [InlineData(null)]
    public void Unrecognised_scenarios_emit_no_attribute(string? input)
    {
        Assert.Null(ToastXmlWriter.NormaliseScenario(input));
    }

    [Theory]
    [InlineData("captionsubtle", "captionSubtle")]
    [InlineData("TITLENUMERAL", "titleNumeral")]
    [InlineData("subheaderSubtle", "subheaderSubtle")]
    [InlineData("body", "body")]
    public void Text_styles_are_normalised_case_correctly(string input, string expected)
    {
        // Windows matches these case-sensitively and ignores anything it does not recognise.
        Assert.Equal(expected, ToastXmlWriter.NormaliseTextStyle(input));
    }

    [Fact]
    public void Unknown_text_styles_are_dropped_rather_than_passed_through()
    {
        Assert.Null(ToastXmlWriter.NormaliseTextStyle("enormous"));
    }

    [Fact]
    public void Centre_is_accepted_as_well_as_center()
    {
        Assert.Equal("center", ToastXmlWriter.NormaliseAlign("centre"));
        Assert.Equal("center", ToastXmlWriter.NormaliseTextStacking("centre"));
    }

    // ---------------------------------------------------------------- audio

    [Fact]
    public void Silent_audio_suppresses_the_source()
    {
        var payload = TextPayload("t");
        payload.Audio = new ToastAudio { Silent = true, Src = "ms-winsoundevent:Notification.Default" };

        var audio = ToastXmlWriter.Build(payload, new ResolvedImages()).Descendants("audio").Single();

        Assert.Equal("true", audio.Attribute("silent")!.Value);
        Assert.Null(audio.Attribute("src"));
    }

    [Fact]
    public void Looping_audio_sets_both_attributes()
    {
        var payload = TextPayload("t");
        payload.Audio = new ToastAudio { Src = "ms-winsoundevent:Notification.Looping.Alarm", Loop = true };

        var audio = ToastXmlWriter.Build(payload, new ResolvedImages()).Descendants("audio").Single();

        Assert.Equal("ms-winsoundevent:Notification.Looping.Alarm", audio.Attribute("src")!.Value);
        Assert.Equal("true", audio.Attribute("loop")!.Value);
    }
}
