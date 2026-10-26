using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.UiLogic.Export;
using SeConv.Core;
using SkiaSharp;
using Xunit;

namespace SeConvTests.Core;

public class LetterSpacingTest
{
    [Theory]
    [InlineData(ExportBoxType.OneBox)]
    [InlineData(ExportBoxType.BoxPerLine)]
    public void SupSpacingChangesDecodedWidthAndKeepsCenterAndTiming(ExportBoxType boxType)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LetterSpacing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var subtitle = new Subtitle();
            subtitle.Paragraphs.Add(new Paragraph("Ordinary Latin subtitles" + Environment.NewLine + "A shorter line", 1000, 4000));
            var widths = new List<int>();
            foreach (var spacing in new[] { -1.5f, 0f, 2f })
            {
                var path = Path.Combine(directory, spacing + ".sup");
                ImageOutputWriter.Write(subtitle, path, new ExportHandlerBluRaySup(), new ConversionOptions
                {
                    Patterns = [],
                    Format = "BluRaySup",
                    Resolution = (3840, 2160),
                    ImageStyle = new ImageExportStyle
                    {
                        FontSize = 80,
                        LetterSpacing = spacing,
                        BackgroundColor = new SKColor(0, 0, 0, 160),
                        BackgroundCornerRadius = 12,
                        BoxType = boxType,
                    },
                });

                var item = Assert.Single(BitmapSubtitleLoader.LoadBluRaySup(path));
                using (item)
                {
                    widths.Add(item.Bitmap.Width);
                    Assert.InRange(Math.Abs(item.Position!.Value.X * 2 + item.Bitmap.Width - 3840), 0, 1);
                    Assert.InRange(item.StartTime.TotalMilliseconds, 999, 1001);
                    Assert.InRange(item.EndTime.TotalMilliseconds, 3999, 4001);
                }
            }

            Assert.True(widths[0] < widths[1], string.Join(", ", widths));
            Assert.True(widths[1] < widths[2], string.Join(", ", widths));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
