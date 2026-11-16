using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.UiLogic.Export;
using SeConv.Core;
using SkiaSharp;
using Xunit;

namespace SeConvTests.Core;

public sealed class AssaLayoutTest
{
    private static Subtitle Load()
    {
        var subtitle = new Subtitle();
        new AdvancedSubStationAlpha().LoadSubtitle(subtitle, File.ReadAllLines(Fixtures.Path("layout.ass")).ToList(), null);
        return subtitle;
    }

    private static ImageExportStyle Style() => new()
    {
        PreserveAssaPositioning = true, FontName = "SF Pro Display", FontSize = 96,
        LetterSpacing = 1, OutlineWidth = 0, ShadowWidth = 0,
        BackgroundColor = new SKColor(0, 0, 0, 128), BackgroundCornerRadius = 16,
        BoxType = ExportBoxType.OneBox, BoxPaddingLeft = 36, BoxPaddingRight = 36,
        BoxPaddingTop = 24, BoxPaddingBottom = 24, BottomTopMargin = 620, LeftRightMargin = 240,
    };

    private static AssaLayout Layout(Subtitle subtitle, ImageExportStyle style) =>
        new(subtitle, ExportAssaStyles.FromHeader(subtitle.Header)!, style);

    [Fact]
    public void CollapseKeepsSourcesBreaksPositionAndInputUnchanged()
    {
        var subtitle = Load();
        var before = subtitle.Paragraphs.Select(p => (p.Text, p.StartTime.TotalMilliseconds, p.EndTime.TotalMilliseconds)).ToArray();
        var style = Style();
        style.CollapseAssaLayers = true;
        var layout = Layout(subtitle, style);
        Assert.Equal(7, layout.Cues.Count);
        var collapsed = Assert.Single(layout.Cues, c => c.Sources.Count == 2);
        Assert.Contains(Environment.NewLine, collapsed.Paragraph.Text);
        Assert.Equal(subtitle.Paragraphs[4].Text, collapsed.Paragraph.Text);
        Assert.Same(subtitle.Paragraphs[3], collapsed.Sources[0]);
        Assert.Equal(before, subtitle.Paragraphs.Select(p => (p.Text, p.StartTime.TotalMilliseconds, p.EndTime.TotalMilliseconds)));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(384, 288)]
    public void DuplicateToleranceScalesWithScriptResolution(int width, int height)
    {
        var subtitle = Load();
        subtitle.Header = subtitle.Header.Replace("1920", width.ToString()).Replace("1080", height.ToString());
        subtitle.Paragraphs.Clear();
        subtitle.Paragraphs.Add(new Paragraph($"{{\\pos({width / 2},{height / 2})}}Same", 1000, 3000) { Extra = "Translation", Layer = 0 });
        subtitle.Paragraphs.Add(new Paragraph($"{{\\pos({width / 2 + 1},{height / 2 + 1})}}Same", 1010, 3010) { Extra = "Translation", Layer = 1 });
        var style = Style();
        style.CollapseAssaLayers = true;
        var cue = Assert.Single(Layout(subtitle, style).Cues);
        Assert.Equal(1000, cue.Paragraph.StartTime.TotalMilliseconds);
        Assert.Equal(3010, cue.Paragraph.EndTime.TotalMilliseconds);
    }

    [Fact]
    public void TransparentDecorationCannotReplaceVisibleText()
    {
        var subtitle = Load();
        subtitle.Paragraphs.Clear();
        subtitle.Paragraphs.Add(new Paragraph(@"{\pos(960,640)}Visible", 1000, 3000) { Extra = "Translation" });
        subtitle.Paragraphs.Add(new Paragraph(@"{\pos(960,640)\1a&HFF&}Visible", 1000, 3000) { Extra = "Translation", Layer = 1 });
        var style = Style();
        style.CollapseAssaLayers = true;
        Assert.Equal(subtitle.Paragraphs[0].Text, Assert.Single(Layout(subtitle, style).Cues).Paragraph.Text);
    }

    [Theory]
    [InlineData(@"{\pos(1300,640)}Same", 1000, 3000, "", "")]
    [InlineData(@"{\pos(960,640)}Same", 1200, 3200, "", "")]
    [InlineData(@"{\pos(960,640)\k20}Same", 1000, 3000, "", "")]
    [InlineData(@"{\move(960,640,970,640)}Same", 1000, 3000, "", "")]
    [InlineData(@"{\pos(960,640)\t(\fs20)}Same", 1000, 3000, "", "")]
    [InlineData(@"{\pos(960,640)\p1}Same", 1000, 3000, "", "")]
    [InlineData(@"{\pos(960,640)\clip(1,2,3,4)}Same", 1000, 3000, "", "")]
    [InlineData(@"{\pos(960,640)\fad(100,100)}Same", 1000, 3000, "", "")]
    [InlineData(@"{\pos(960,640)}Same", 1000, 3000, "other", "")]
    [InlineData(@"{\pos(960,640)}Same", 1000, 3000, "", "Banner")]
    [InlineData(@"{\pos(960,640)}Same\N", 1000, 3000, "", "")]
    [InlineData(@"{\pos(5000,640)}Same", 1000, 3000, "", "")]
    public void AmbiguousEventsRemainSeparate(string text, int start, int end, string actor, string effect)
    {
        var subtitle = Load();
        subtitle.Paragraphs.Clear();
        subtitle.Paragraphs.Add(new Paragraph(@"{\pos(960,640)}Same", 1000, 3000) { Extra = "Translation" });
        subtitle.Paragraphs.Add(new Paragraph(text, start, end) { Extra = "Translation", Layer = 1, Actor = actor, Effect = effect });
        var style = Style();
        style.CollapseAssaLayers = true;
        Assert.Equal(2, Layout(subtitle, style).Cues.Count);
    }

    [Fact]
    public void RelativeSizingUsesDialogueAndKeepsOrdinaryAndGlobalTypography()
    {
        var style = Style();
        style.RelativePositionedFontSize = true;
        var handler = Render(Load(), style);
        Assert.All(handler.Cues.Take(3), c => Assert.Equal(96, c.Size));
        Assert.All(handler.Cues.Skip(3), c => Assert.Equal(64, c.Size));
        Assert.All(handler.Cues, c => Assert.Equal("SF Pro Display", c.Font));
        var ordinary = Render(Load(), Style());
        Assert.Equal(ordinary.Cues[0], handler.Cues[0]);
    }

    [Theory]
    [InlineData(10, 48)]
    [InlineData(40, 64)]
    [InlineData(58, 96)]
    [InlineData(120, 96)]
    public void LeadingInlineSizeIsBoundedAndNeverEnlarges(int original, int expected)
    {
        var subtitle = Load();
        subtitle.Paragraphs[4].Text = $"{{\\pos(960,640)\\fs{original}}}Translation";
        var style = Style();
        style.RelativePositionedFontSize = true;
        Assert.Equal(expected, Render(subtitle, style).Cues[4].Size);
    }

    [Fact]
    public void AmbiguousHierarchyAndMixedInlineSizesKeepGlobalSize()
    {
        var subtitle = Load();
        subtitle.Paragraphs[1].Text = @"{\fs40}Other ordinary size";
        subtitle.Paragraphs[2].Text = @"{\fs20}Another ordinary size";
        var style = Style();
        style.RelativePositionedFontSize = true;
        Assert.All(Layout(subtitle, style).Cues, c => Assert.Equal(1, c.FontRatio));
        subtitle = Load();
        subtitle.Paragraphs[4].Text = @"{\pos(960,640)}First{\fs20}small";
        Assert.Equal(1, Layout(subtitle, style).Cues[4].FontRatio);
    }

    [Fact]
    public void CollisionSolverPrioritizesSignsAndKeepsOnePositionAcrossLifetimes()
    {
        var subtitle = Load();
        subtitle.Paragraphs.Clear();
        subtitle.Paragraphs.Add(new Paragraph("Dialogue", 10000, 14000) { Extra = "Speech" });
        subtitle.Paragraphs.Add(new Paragraph(@"{\an2\pos(960,770)}Sign", 10500, 12000) { Extra = "Speech" });
        subtitle.Paragraphs.Add(new Paragraph("Unrelated", 15000, 16000) { Extra = "Speech" });
        var baseline = Render(subtitle, Style());
        var style = Style();
        style.AvoidSubtitleOverlap = true;
        var actual = Render(subtitle, style);
        Assert.True(baseline.Cues[0].Bounds.IntersectsWith(baseline.Cues[1].Bounds));
        Assert.False(actual.Cues[0].Bounds.IntersectsWith(actual.Cues[1].Bounds));
        Assert.Equal(baseline.Cues[1], actual.Cues[1]);
        Assert.Equal(baseline.Cues[2], actual.Cues[2]);
        Assert.Equal(TimeSpan.FromSeconds(10), actual.Cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(14), actual.Cues[0].End);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollisionDetectionKeepsDecorativeLayersTogether(bool collapse)
    {
        var style = Style();
        style.CollapseAssaLayers = collapse;
        style.AvoidSubtitleOverlap = true;
        style.RelativePositionedFontSize = true;
        var handler = Render(Load(), style);
        Assert.Equal(collapse ? 7 : 8, handler.Cues.Count);
        if (!collapse)
        {
            Assert.InRange(Math.Abs(handler.Cues[3].Bounds.Left - handler.Cues[4].Bounds.Left), 0, 4);
            Assert.InRange(Math.Abs(handler.Cues[3].Bounds.Top - handler.Cues[4].Bounds.Top), 0, 2);
        }
        for (var i = 0; i < handler.Cues.Count; i++)
        {
            var a = handler.Cues[i];
            Assert.InRange(a.Bounds.Left, 0, 3840 - a.Bounds.Width);
            Assert.InRange(a.Bounds.Top, 0, 2160 - a.Bounds.Height);
            for (var j = i + 1; j < handler.Cues.Count; j++)
            {
                var b = handler.Cues[j];
                if (!collapse && i == 3 && j == 4) continue;
                if (a.Start < b.End && b.Start < a.End) Assert.False(a.Bounds.IntersectsWith(b.Bounds), $"{i}, {j}");
            }
        }
        Assert.Equal(handler.Cues, Render(Load(), style).Cues);
    }

    [Fact]
    public void CrowdedFallbackKeepsAllEventsInsideFrame()
    {
        var subtitle = Load();
        subtitle.Paragraphs.Clear();
        for (var i = 0; i < 4; i++) subtitle.Paragraphs.Add(new Paragraph(@"{\pos(960,540)}" + i, 1000, 3000) { Extra = "Translation" });
        var style = Style();
        style.AvoidSubtitleOverlap = true;
        var layout = Layout(subtitle, style);
        foreach (var cue in layout.Cues) cue.Bounds = new SKRectI(0, 0, 80, 80);
        layout.Solve(100, 100, 25);
        Assert.Equal(4, layout.Cues.Count);
        Assert.All(layout.Cues, c =>
        {
            Assert.InRange(c.Bounds.Left, 0, 20);
            Assert.InRange(c.Bounds.Top, 0, 20);
        });
    }

    [Fact]
    public void OversizedMultilineImageFitsWithoutDroppingText()
    {
        var subtitle = Load();
        subtitle.Paragraphs.Clear();
        subtitle.Paragraphs.Add(new Paragraph(string.Join(Environment.NewLine, Enumerable.Repeat("Tall subtitle", 30)), 1000, 3000) { Extra = "Speech" });
        var style = Style();
        style.AvoidSubtitleOverlap = true;
        var cue = Assert.Single(Render(subtitle, style).Cues);
        Assert.InRange(cue.Bounds.Width, 1, 3840);
        Assert.InRange(cue.Bounds.Height, 1, 2160);
        Assert.Equal(30, cue.Text.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void AllFeaturesProduceDecodable4KPgsWithFadeSteps()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sup");
        try
        {
            var style = Style();
            style.CollapseAssaLayers = true;
            style.RelativePositionedFontSize = true;
            style.AvoidSubtitleOverlap = true;
            ImageOutputWriter.Write(Load(), path, new ExportHandlerBluRaySup(), Options(style));
            var items = BitmapSubtitleLoader.LoadBluRaySup(path);
            try
            {
                Assert.NotEmpty(items);
                Assert.All(items, item =>
                {
                    Assert.Equal(3840, item.ScreenWidth);
                    Assert.Equal(2160, item.ScreenHeight);
                    Assert.InRange(item.Position!.Value.X, 0, 3840 - item.Bitmap.Width);
                    Assert.InRange(item.Position.Value.Y, 0, 2160 - item.Bitmap.Height);
                });
                Assert.Contains(items, item => item.StartTime.TotalMilliseconds > 12000 && item.StartTime.TotalMilliseconds < 12200);
                Assert.True(File.ReadAllBytes(path).Length > 1000);
            }
            finally { foreach (var item in items) item.Dispose(); }
        }
        finally { File.Delete(path); }
    }

    private static ConversionOptions Options(ImageExportStyle style) => new()
    {
        Patterns = [], Format = "BluRaySup", Resolution = (3840, 2160), ImageStyle = style,
    };

    private static RecordingHandler Render(Subtitle subtitle, ImageExportStyle style)
    {
        var handler = new RecordingHandler();
        ImageOutputWriter.Write(subtitle, "unused", handler, Options(style));
        return handler;
    }

    private sealed record RenderedCue(SKRectI Bounds, float Size, string Font, string Text, TimeSpan Start, TimeSpan End);

    private sealed class RecordingHandler : IExportHandler
    {
        public List<RenderedCue> Cues { get; } = [];
        public ExportImageType ExportImageType => ExportImageType.BluRaySup;
        public string Extension => ".sup";
        public string Title => "Layout test";
        public bool UseFileName => true;
        public void WriteHeader(string path, ImageParameter p) { }
        public void CreateParagraph(ImageParameter p) => Cues.Add(new RenderedCue(AssaLayout.RenderedBounds(p), p.FontSize, p.FontName, p.Text, p.StartTime, p.EndTime));
        public void WriteParagraph(ImageParameter p) { }
        public void WriteFooter() { }
    }
}
