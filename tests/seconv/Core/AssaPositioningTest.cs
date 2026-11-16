using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.UiLogic.Export;
using SeConv.Core;
using SkiaSharp;
using Xunit;

namespace SeConvTests.Core;

public class AssaPositioningTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AssaPositioning_" + Guid.NewGuid().ToString("N"));

    public AssaPositioningTest() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private static Subtitle Load()
    {
        var subtitle = new Subtitle();
        new AdvancedSubStationAlpha().LoadSubtitle(subtitle, File.ReadAllLines(Fixtures.Path("positioning.ass")).ToList(), null);
        return subtitle;
    }

    private static ImageExportStyle Style(bool preserve = true) => new()
    {
        PreserveAssaPositioning = preserve,
        UseAssaStyles = false,
        FontName = "SF Pro Display",
        FontSize = 96,
        LetterSpacing = 1,
        OutlineWidth = 0,
        ShadowWidth = 0,
        BackgroundColor = new SKColor(0, 0, 0, 128),
        BackgroundCornerRadius = 16,
        BoxType = ExportBoxType.OneBox,
        BoxPaddingLeft = 36,
        BoxPaddingRight = 36,
        BoxPaddingTop = 24,
        BoxPaddingBottom = 24,
        BottomTopMargin = 620,
        LeftRightMargin = 240,
    };

    private string Write(Subtitle subtitle, ImageExportStyle? style = null)
    {
        var path = Path.Combine(_directory, Guid.NewGuid() + ".sup");
        ImageOutputWriter.Write(subtitle, path, new ExportHandlerBluRaySup(), new ConversionOptions
        {
            Patterns = [], Format = "BluRaySup", Resolution = (3840, 2160), ImageStyle = style ?? Style(),
        });
        return path;
    }

    [Fact]
    public void OrdinaryDialogueKeepsGlobalAppearanceAndPosition()
    {
        var subtitle = Load();
        subtitle.Paragraphs.RemoveRange(3, subtitle.Paragraphs.Count - 3);
        var expected = File.ReadAllBytes(Write(subtitle, Style(false)));
        Assert.Equal(expected, File.ReadAllBytes(Write(subtitle)));
        // The new flag alone replaces full ASS styling, without requiring --no-assa-styles.
        var style = Style();
        style.UseAssaStyles = true;
        Assert.Equal(expected, File.ReadAllBytes(Write(subtitle, style)));
        var items = BitmapSubtitleLoader.LoadBluRaySup(Write(subtitle));
        foreach (var item in items)
        {
            using (item)
            {
                Assert.InRange(Math.Abs(2 * item.Position!.Value.X + item.Bitmap.Width - 3840), 0, 1);
                Assert.Equal(2160 - 620, item.Position.Value.Y + item.Bitmap.Height);
            }
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(384, 288)]
    public void AnchorsScaleEachAxisAndHonorEventOverrides(int width, int height)
    {
        var subtitle = Load();
        subtitle.Header = subtitle.Header.Replace("PlayResX: 1920", $"PlayResX: {width}")
            .Replace("PlayResY: 1080", $"PlayResY: {height}");
        var positioning = new AssaPositioning(subtitle, ExportAssaStyles.FromHeader(subtitle.Header)!);
        // A fixed bitmap isolates anchor arithmetic from font metrics and on-screen clamping.
        using var bitmap = new SKBitmap(100, 100);
        foreach (var (index, x, y, anchorX, anchorY) in new[]
        {
            (3, width / 2.0, height - 240.0, 50, 100),
            (4, (width + 1200 - 60) / 2.0, height - 150.0, 50, 100),
            (5, 1500.0, 200.0, 100, 0),
            (6, width / 2.0, 60.0, 50, 0),
            (7, width / 2.0, height - 180.0, 50, 100),
        })
        {
            var parameter = new ImageParameter { Bitmap = bitmap, ScreenWidth = 3840, ScreenHeight = 2160 };
            positioning.ApplyAlignment(parameter, subtitle.Paragraphs[index]);
            positioning.ApplyPosition(parameter, subtitle.Paragraphs[index]);
            Assert.Equal((int)Math.Round(Math.Clamp(x * 3840 / width - anchorX, 0, 3740), MidpointRounding.AwayFromZero), parameter.OverridePosition!.Value.X);
            Assert.Equal((int)Math.Round(Math.Clamp(y * 2160 / height - anchorY, 0, 2060), MidpointRounding.AwayFromZero), parameter.OverridePosition.Value.Y);
        }
    }

    [Fact]
    public void AmbiguousBottomCenterLayoutUsesGlobalPositionUnlessAn2ForcesSourcePlacement()
    {
        var subtitle = Load();
        subtitle.Paragraphs.RemoveRange(1, subtitle.Paragraphs.Count - 1);
        var positioning = new AssaPositioning(subtitle, ExportAssaStyles.FromHeader(subtitle.Header)!);
        using var bitmap = new SKBitmap(100, 100);
        var parameter = new ImageParameter { Bitmap = bitmap, ScreenWidth = 3840, ScreenHeight = 2160 };
        var paragraph = subtitle.Paragraphs[0];
        positioning.ApplyPosition(parameter, paragraph);
        Assert.Null(parameter.OverridePosition);
        paragraph.Text = "{\\an2}" + paragraph.Text;
        positioning.ApplyAlignment(parameter, paragraph);
        positioning.ApplyPosition(parameter, paragraph);
        Assert.Equal(new SKPointI(1870, 1940), parameter.OverridePosition);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(384, 288)]
    public void DecodedPgsScalesSourceCoordinatesTo4K(int width, int height)
    {
        var subtitle = Load();
        subtitle.Header = subtitle.Header.Replace("PlayResX: 1920", $"PlayResX: {width}")
            .Replace("PlayResY: 1080", $"PlayResY: {height}");
        subtitle.Paragraphs.Clear();
        subtitle.Paragraphs.Add(new Paragraph($"{{\\an9\\pos({width * 3 / 4},{height / 4})}}Scaled position", 1000, 2000) { Extra = "Main" });
        subtitle.Paragraphs.Add(new Paragraph("Scaled margin", 3000, 4000)
        {
            Extra = "Main", MarginL = (width / 2).ToString(), MarginR = "0", MarginV = (height / 4).ToString(),
        });
        var items = BitmapSubtitleLoader.LoadBluRaySup(Write(subtitle));
        try
        {
            Assert.Equal(2, items.Count);
            Assert.Equal(2880, items[0].Position!.Value.X + items[0].Bitmap.Width);
            Assert.Equal(540, items[0].Position!.Value.Y);
            var center = (width + width / 2.0 - 60) / 2 * 3840 / width;
            Assert.InRange(Math.Abs(items[1].Position!.Value.X + items[1].Bitmap.Width / 2.0 - center), 0, 1);
            Assert.Equal(1620, items[1].Position!.Value.Y + items[1].Bitmap.Height);
        }
        finally
        {
            foreach (var item in items) item.Dispose();
        }
    }

    [Theory]
    [InlineData(9, 3540, 750, false)]
    [InlineData(10, 360, 2040, true)]
    [InlineData(11, 3615, 1800, true)]
    [InlineData(12, 180, 2100, true)]
    public void OptextRegressionUsesGlobalBitmapAtAssAnchor(int index, int centerX, int y, bool bottom)
    {
        var subtitle = Load();
        var paragraph = new Paragraph(subtitle.Paragraphs[index]);
        subtitle.Paragraphs.Clear();
        subtitle.Paragraphs.Add(paragraph);
        using var actual = Assert.Single(BitmapSubtitleLoader.LoadBluRaySup(Write(subtitle)));
        using var expected = Assert.Single(BitmapSubtitleLoader.LoadBluRaySup(Write(subtitle, Style(false))));
        Assert.Equal(expected.Bitmap.Width, actual.Bitmap.Width);
        Assert.Equal(expected.Bitmap.Height, actual.Bitmap.Height);
        Assert.Equal(expected.Bitmap.Bytes, actual.Bitmap.Bytes);
        var expectedX = Math.Clamp(centerX - actual.Bitmap.Width / 2.0, 0, 3840 - actual.Bitmap.Width);
        Assert.InRange(Math.Abs(actual.Position!.Value.X - expectedX), 0, 1);
        Assert.Equal(bottom ? y - actual.Bitmap.Height : y, actual.Position.Value.Y);
        Assert.True(actual.Bitmap.Height > 4 * 24); // Manual line breaks survive parsing and rendering.
    }

    [Fact]
    public void InlineVisualOverridesAreDiscardedButPositionAndFadeSurvive()
    {
        var subtitle = Load();
        subtitle.Paragraphs.RemoveRange(9, subtitle.Paragraphs.Count - 9);
        var items = BitmapSubtitleLoader.LoadBluRaySup(Write(subtitle));
        try
        {
            var positioned = items.Single(i => Math.Abs(i.StartTime.TotalMilliseconds - 11000) < 2);
            Assert.Equal(3000, positioned.Position!.Value.X + positioned.Bitmap.Width);
            Assert.Equal(400, positioned.Position.Value.Y);
            var plain = new Subtitle();
            plain.Paragraphs.Add(new Paragraph("Explicit position", 11000, 12000));
            using var expected = Assert.Single(BitmapSubtitleLoader.LoadBluRaySup(Write(plain, Style(false))));
            Assert.Equal(expected.Bitmap.Bytes, positioned.Bitmap.Bytes);
            Assert.Contains(items, i => i.StartTime.TotalMilliseconds > 17000 && i.StartTime.TotalMilliseconds < 17200);
            Assert.Equal(120, items.Single(i => Math.Abs(i.StartTime.TotalMilliseconds - 13000) < 2).Position!.Value.Y);
        }
        finally
        {
            foreach (var item in items) item.Dispose();
        }
    }

    [Fact]
    public void OverlappingOptextCuesKeepSeparateScreenRegions()
    {
        var bytes = File.ReadAllBytes(Write(Load()));
        var pairs = new List<(int X1, int X2)>();
        for (var offset = 0; offset < bytes.Length; offset += 13 + (bytes[offset + 11] << 8 | bytes[offset + 12]))
        {
            var payload = offset + 13;
            if (bytes[offset + 10] == 0x16 && bytes[payload + 10] == 2)
            {
                pairs.Add((bytes[payload + 15] << 8 | bytes[payload + 16], bytes[payload + 23] << 8 | bytes[payload + 24]));
            }
        }
        Assert.NotEmpty(pairs);
        Assert.All(pairs, pair => Assert.True(Math.Abs(pair.X1 - pair.X2) > 2000, pair.ToString()));
    }
}
