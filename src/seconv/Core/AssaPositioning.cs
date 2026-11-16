using System.Globalization;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.UiLogic.Export;

namespace SeConv.Core;

internal sealed class AssaPositioning
{
    private readonly ExportAssaStyles _styles;
    private readonly int? _dialogueMarginV;

    public AssaPositioning(Subtitle subtitle, ExportAssaStyles styles)
    {
        _styles = styles;
        // Count ordinary-looking events, not style names or unused style definitions.
        _dialogueMarginV = subtitle.Paragraphs
            .Where(p => Alignment(p) == ExportAlignment.BottomCenter && !HasExplicitPosition(p) && !IsAsymmetric(p))
            .GroupBy(p => Margin(p.MarginV, _styles.GetStyle(p.Extra).MarginVertical))
            .OrderByDescending(g => g.Count())
            .Select(g => (int?)g.Key)
            .FirstOrDefault();
    }

    public void ApplyAlignment(ImageParameter parameter, Paragraph paragraph)
    {
        if (IsPositioned(paragraph))
        {
            parameter.Alignment = Alignment(paragraph);
        }
    }

    public void ApplyPosition(ImageParameter parameter, Paragraph paragraph)
    {
        if (!IsPositioned(paragraph))
        {
            return;
        }

        var (width, height) = _styles.ScriptResolution;
        if (ExportTextTags.TryGetPosition(paragraph.Text, out _, out _))
        {
            ExportTextTags.ApplyPositionTag(parameter, paragraph.Text, width, height);
            return;
        }

        var (x, y) = SourceAnchor(paragraph);

        // Reuse the existing anchor, independent-axis scaling and on-screen clamping.
        ExportTextTags.ApplyPositionTag(parameter,
            string.Create(CultureInfo.InvariantCulture, $"{{\\pos({x},{y})}}"), width, height);
    }

    internal (double X, double Y) SourceAnchor(Paragraph paragraph)
    {
        if (ExportTextTags.TryGetPosition(paragraph.Text, out var positionX, out var positionY))
        {
            return (positionX, positionY);
        }
        var (width, height) = _styles.ScriptResolution;
        var style = _styles.GetStyle(paragraph.Extra);
        var alignment = Alignment(paragraph);
        var left = Margin(paragraph.MarginL, style.MarginLeft);
        var right = Margin(paragraph.MarginR, style.MarginRight);
        var vertical = Margin(paragraph.MarginV, style.MarginVertical);
        var x = alignment switch
        {
            ExportAlignment.TopLeft or ExportAlignment.MiddleLeft or ExportAlignment.BottomLeft => left,
            ExportAlignment.TopRight or ExportAlignment.MiddleRight or ExportAlignment.BottomRight => width - right,
            _ => (width + (double)left - right) / 2,
        };
        var y = alignment switch
        {
            ExportAlignment.TopLeft or ExportAlignment.TopCenter or ExportAlignment.TopRight => vertical,
            ExportAlignment.MiddleLeft or ExportAlignment.MiddleCenter or ExportAlignment.MiddleRight => height / 2.0,
            _ => height - vertical,
        };

        return (x, y);
    }

    internal bool IsPositioned(Paragraph paragraph)
    {
        return HasExplicitPosition(paragraph) || Alignment(paragraph) != ExportAlignment.BottomCenter ||
               IsAsymmetric(paragraph) ||
               (_dialogueMarginV.HasValue &&
                Margin(paragraph.MarginV, _styles.GetStyle(paragraph.Extra).MarginVertical) != _dialogueMarginV.Value);
    }

    private static bool HasExplicitPosition(Paragraph paragraph)
    {
        return ExportTextTags.TryGetPosition(paragraph.Text, out _, out _) ||
               ExportTextTags.GetAlignment(paragraph.Text, (ExportAlignment)(-1)) != (ExportAlignment)(-1);
    }

    internal ExportAlignment Alignment(Paragraph paragraph)
    {
        var style = _styles.GetStyle(paragraph.Extra);
        var alignment = ExportTextTags.GetAlignment($"{{\\an{style.Alignment}}}", ExportAlignment.BottomCenter);
        return ExportTextTags.GetAlignment(paragraph.Text, alignment);
    }

    private bool IsAsymmetric(Paragraph paragraph)
    {
        var style = _styles.GetStyle(paragraph.Extra);
        var left = Margin(paragraph.MarginL, style.MarginLeft);
        var right = Margin(paragraph.MarginR, style.MarginRight);
        // shortcut: small asymmetries remain dialogue, use explicit \an or \pos for subtle offsets.
        return Math.Abs((double)left - right) >= _styles.ScriptResolution.Width * 0.1 &&
               Math.Max(left, right) >= 2.0 * Math.Min(left, right);
    }

    private static int Margin(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var margin) && margin > 0
            ? margin
            : fallback;
    }
}
