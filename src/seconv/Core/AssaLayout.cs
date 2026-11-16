using System.Globalization;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.BluRaySup;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.UiLogic.Export;
using SkiaSharp;

namespace SeConv.Core;

internal sealed class AssaLayout
{
    private static readonly Regex Blocks = new(@"\{[^}]*\}");
    private static readonly Regex SafeTag = new(@"^(?:an[1-9]|pos\([^)]*\)|[ibu][01]?|fn[^\\]*|fs\d+(?:\.\d+)?|fsc[xy]\d+(?:\.\d+)?|fsp-?\d+(?:\.\d+)?|[1-4]?c&H[\da-fA-F]+&?|(?:alpha|[1-4]a)&H[\da-fA-F]+&?|(?:[xy]?(?:bord|shad)|blur|be)\d+(?:\.\d+)?|fad\([^)]*\))$");
    private static readonly Regex Fade = new(@"\\fad\([^)]*\)");
    private static readonly Regex FontSize = new(@"\\fs(\d+(?:\.\d+)?)(?=\\|\})");
    private readonly ExportAssaStyles _styles;
    private readonly AssaPositioning _positioning;
    private readonly ImageExportStyle _options;

    internal sealed class Cue
    {
        public required Paragraph Paragraph { get; init; }
        public List<Paragraph> Sources { get; } = [];
        public required int SourceIndex { get; init; }
        public bool Positioned { get; init; }
        public float FontRatio { get; set; } = 1;
        public SKRectI Bounds { get; set; }
        public SKPointI? Position { get; set; }
        public int CollisionGroup { get; set; }
    }

    public List<Cue> Cues { get; } = [];

    public AssaLayout(Subtitle subtitle, ExportAssaStyles styles, ImageExportStyle options)
    {
        _styles = styles;
        _positioning = new AssaPositioning(subtitle, styles);
        _options = options;
        var groups = new List<Cue>();
        var groupsByText = new Dictionary<string, List<Cue>>(StringComparer.Ordinal);
        for (var i = 0; i < subtitle.Paragraphs.Count; i++)
        {
            var paragraph = subtitle.Paragraphs[i];
            var text = Visible(paragraph);
            if (!groupsByText.TryGetValue(text, out var candidates))
            {
                candidates = [];
                groupsByText.Add(text, candidates);
            }
            var group = options.CollapseAssaLayers || options.AvoidSubtitleOverlap
                ? candidates.LastOrDefault(g => g.Sources.All(p => DecorativeCopies(p, paragraph))) : null;
            if (group == null)
            {
                group = new Cue
                {
                    Paragraph = paragraph, SourceIndex = i + 1,
                    Positioned = _positioning.IsPositioned(paragraph), CollisionGroup = groups.Count,
                };
                groups.Add(group);
                candidates.Add(group);
            }
            group.Sources.Add(paragraph);
        }

        foreach (var group in groups)
        {
            if (group.Sources.Count > 1)
            {
                Log($"events {string.Join(",", group.Sources.Select(p => subtitle.Paragraphs.IndexOf(p) + 1))}: decorative group{(options.CollapseAssaLayers ? ", collapsed" : ", move together")}");
            }
            IEnumerable<Paragraph> sources = options.CollapseAssaLayers
                ? new[] { group.Sources.OrderByDescending(PrimaryOpacity).ThenByDescending(p => p.Layer).First() }
                : group.Sources;
            foreach (var source in sources)
            {
                // A clone keeps the parsed source and the input file untouched.
                var paragraph = new Paragraph(source);
                if (options.CollapseAssaLayers && group.Sources.Count > 1)
                {
                    paragraph.StartTime.TotalMilliseconds = group.Sources.Min(p => p.StartTime.TotalMilliseconds);
                    paragraph.EndTime.TotalMilliseconds = group.Sources.Max(p => p.EndTime.TotalMilliseconds);
                }
                var cue = new Cue
                {
                    Paragraph = paragraph, SourceIndex = subtitle.Paragraphs.IndexOf(source) + 1,
                    Positioned = _positioning.IsPositioned(source), CollisionGroup = group.CollisionGroup,
                };
                cue.Sources.AddRange(group.Sources);
                Cues.Add(cue);
            }
        }
        var ordered = Cues.OrderBy(c => c.Paragraph.StartTime.TotalMilliseconds).ThenBy(c => c.SourceIndex).ToList();
        Cues.Clear();
        Cues.AddRange(ordered);
        if (options.RelativePositionedFontSize) SetRelativeSizes();
    }

    private bool Safe(Paragraph p)
    {
        return string.IsNullOrWhiteSpace(p.Effect) && Blocks.Matches(p.Text).All(block =>
            block.Value[1..^1].Split('\\', StringSplitOptions.RemoveEmptyEntries).All(tag => SafeTag.IsMatch(tag.Trim())));
    }

    private static string Visible(Paragraph p) => HtmlUtil.RemoveHtmlTags(ExportTextTags.ToRenderableText(p.Text), true);

    private bool DecorativeCopies(Paragraph a, Paragraph b)
    {
        var duration = Math.Min(a.DurationTotalMilliseconds, b.DurationTotalMilliseconds);
        var tolerance = Math.Min(20, duration * 0.02);
        if (duration <= 0 || Math.Abs(a.StartTime.TotalMilliseconds - b.StartTime.TotalMilliseconds) > tolerance ||
            Math.Abs(a.EndTime.TotalMilliseconds - b.EndTime.TotalMilliseconds) > tolerance) return false;

        if (!Safe(a) || !Safe(b) || a.Actor != b.Actor || string.IsNullOrWhiteSpace(Visible(a)) ||
            _positioning.Alignment(a) != _positioning.Alignment(b) || Fade.Match(a.Text).Value != Fade.Match(b.Text).Value ||
            (a.Layer == b.Layer && a.Extra == b.Extra && a.Text == b.Text)) return false;

        var pa = _positioning.SourceAnchor(a);
        var pb = _positioning.SourceAnchor(b);
        var (width, height) = _styles.ScriptResolution;
        return Math.Abs(pa.X - pb.X) <= width * 0.005 && Math.Abs(pa.Y - pb.Y) <= height * 0.005 &&
               Math.Abs(SourceSize(a) - SourceSize(b)) <= Math.Max(SourceSize(a), SourceSize(b)) * 0.05;
    }

    private int PrimaryOpacity(Paragraph paragraph)
    {
        var parameter = new ImageParameter { FontColor = _styles.GetStyle(paragraph.Extra).Primary };
        using var bitmap = parameter.Bitmap;
        ExportTextTags.ApplyTransparencyTags(parameter, paragraph.Text);
        return parameter.FontColor.Alpha * parameter.AlphaPercent;
    }

    private float SourceSize(Paragraph p)
    {
        var size = (float)_styles.GetStyle(p.Extra).FontSize;
        var matches = FontSize.Matches(p.Text);
        if (matches.Count == 1 && HasLeadingSize(p.Text, matches[0]) &&
            float.TryParse(matches[0].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var inline)) size = inline;
        return size;
    }

    private static bool HasLeadingSize(string text, Match match)
    {
        var blockStart = text.LastIndexOf('{', match.Index);
        return blockStart >= 0 && string.IsNullOrWhiteSpace(Blocks.Replace(text[..blockStart], ""));
    }

    private static bool HasUniformSize(Paragraph paragraph)
    {
        var sizes = FontSize.Matches(paragraph.Text);
        return sizes.Count == 0 || (sizes.Count == 1 && HasLeadingSize(paragraph.Text, sizes[0]));
    }

    private void SetRelativeSizes()
    {
        var ordinary = Cues.Where(c => !c.Positioned && Safe(c.Paragraph) && HasUniformSize(c.Paragraph)).DistinctBy(c => c.CollisionGroup).ToList();
        var sizes = ordinary.GroupBy(c => SourceSize(c.Paragraph)).OrderByDescending(g => g.Count()).ToList();
        if (ordinary.Count < 2 || sizes.Count == 0 || sizes[0].Key <= 0 || sizes[0].Count() < ordinary.Count * 0.6)
        {
            Log("relative sizing skipped: no dominant ordinary-dialogue size");
            return;
        }
        var reference = sizes[0].Key;
        foreach (var cue in Cues.Where(c => c.Positioned && Safe(c.Paragraph)))
        {
            var size = SourceSize(cue.Paragraph);
            // Mixed inline sizes have no single reliable relationship to dialogue.
            if (size <= 0 || !HasUniformSize(cue.Paragraph) || size >= reference * 0.9) continue;
            cue.FontRatio = Math.Clamp(size / reference, 0.5f, 1f);
            Log($"event {cue.SourceIndex}: font {_options.FontSize} -> {_options.FontSize * cue.FontRatio:0.##} (reference {reference})");
        }
    }

    public void ApplyTypography(Cue cue, ImageParameter parameter)
    {
        if (!_options.RelativePositionedFontSize) return;
        parameter.FontSize = _options.FontSize * cue.FontRatio;
    }

    public void ReportBounds(Cue cue, ImageParameter parameter)
    {
        if (!_options.DebugAssaLayout) return;
        var anchor = _positioning.SourceAnchor(cue.Paragraph);
        var bounds = RenderedBounds(parameter);
        Log($"event {cue.SourceIndex}: source anchor ({anchor.X},{anchor.Y}), output ({bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height})");
    }

    public void Solve(int width, int height, double fps)
    {
        // shortcut: greedy placement can leave avoidable overlap, add backtracking if real crowded scenes require it.
        var placed = new List<Cue>();
        foreach (var group in Cues.GroupBy(c => c.CollisionGroup)
                     .OrderByDescending(g => g.Any(c => c.Positioned)).ThenBy(g => g.Min(c => c.Paragraph.StartTime.TotalMilliseconds)))
        {
            var members = group.ToList();
            var bounds = members.Select(c => c.Bounds).Aggregate(Union);
            var neighbors = placed.Where(p => members.Any(c => OverlapsInTime(c, p, fps))).ToList();
            var x = Math.Clamp(bounds.Left, 0, Math.Max(0, width - bounds.Width));
            var y = Math.Clamp(bounds.Top, 0, Math.Max(0, height - bounds.Height));
            var xs = neighbors.SelectMany(c => new[] { c.Bounds.Left - bounds.Width, c.Bounds.Right }).Append(x)
                .Append(0).Append(Math.Max(0, width - bounds.Width)).Where(v => v >= 0 && v + bounds.Width <= width).Distinct().ToList();
            var ys = neighbors.SelectMany(c => new[] { c.Bounds.Top - bounds.Height, c.Bounds.Bottom }).Append(y)
                .Append(0).Append(Math.Max(0, height - bounds.Height)).Where(v => v >= 0 && v + bounds.Height <= height).Distinct().ToList();
            var candidates = from cx in xs from cy in ys
                             let rectangle = new SKRectI(cx, cy, cx + bounds.Width, cy + bounds.Height)
                             let area = neighbors.Sum(c => IntersectionArea(rectangle, c.Bounds))
                             let distance = Math.Pow(cx - bounds.Left, 2) + Math.Pow(cy - bounds.Top, 2)
                             orderby area, distance, Math.Abs(cx - bounds.Left), cy, cx
                             select (rectangle, area);
            var best = candidates.FirstOrDefault((rectangle: bounds, area: long.MaxValue));
            var dx = best.rectangle.Left - bounds.Left;
            var dy = best.rectangle.Top - bounds.Top;
            if (neighbors.Any(c => IntersectionArea(bounds, c.Bounds) > 0)) Log($"event {members[0].SourceIndex}: collision detected");
            foreach (var cue in members)
            {
                var old = cue.Bounds;
                var left = Math.Clamp(old.Left + dx, 0, Math.Max(0, width - old.Width));
                var top = Math.Clamp(old.Top + dy, 0, Math.Max(0, height - old.Height));
                cue.Position = new SKPointI(left, top);
                cue.Bounds = new SKRectI(left, top, left + old.Width, top + old.Height);
                if (dx != 0 || dy != 0) Log($"event {cue.SourceIndex}: ({old.Left},{old.Top}) -> ({cue.Bounds.Left},{cue.Bounds.Top})");
                placed.Add(cue);
            }
            if (best.area > 0) Log($"event {members[0].SourceIndex}: unresolved collision. Kept on screen with minimum candidate overlap");
        }
    }

    private static bool OverlapsInTime(Cue a, Cue b, double fps)
    {
        var startA = BluRaySupPicture.MillisecondsToPts((long)Math.Round(a.Paragraph.StartTime.TotalMilliseconds, MidpointRounding.AwayFromZero), fps);
        var startB = BluRaySupPicture.MillisecondsToPts((long)Math.Round(b.Paragraph.StartTime.TotalMilliseconds, MidpointRounding.AwayFromZero), fps);
        var endA = Math.Max(startA + 1, BluRaySupPicture.MillisecondsToPts((long)Math.Round(a.Paragraph.EndTime.TotalMilliseconds, MidpointRounding.AwayFromZero), fps));
        var endB = Math.Max(startB + 1, BluRaySupPicture.MillisecondsToPts((long)Math.Round(b.Paragraph.EndTime.TotalMilliseconds, MidpointRounding.AwayFromZero), fps));
        return startA < endB && startB < endA;
    }

    private static SKRectI Union(SKRectI a, SKRectI b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    private static long IntersectionArea(SKRectI a, SKRectI b) => (long)Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private void Log(string message)
    {
        if (_options.DebugAssaLayout) Console.Error.WriteLine("ASS layout: " + message);
    }

    internal static SKRectI RenderedBounds(ImageParameter p)
    {
        var position = FullFrameImage.GetPosition(p, p.Bitmap.Width, p.Bitmap.Height);
        return new SKRectI(position.X, position.Y, position.X + p.Bitmap.Width, position.Y + p.Bitmap.Height);
    }
}
