// Message text laid out the way the engine's text system does (START.SCN, style bank 0):
// ＭＳ ゴシック 24 px, fixed advances of 23 px (full width) and 11 px (half width), 29 px
// lines, white, no edge by default, line breaks with the engine's kinsoku table, and gaiji
// characters drawn from GAIJI.S25.

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using GrandCrossExtractor.Formats;

namespace GrandCrossExtractor.Player;

public sealed class MessageText : FrameworkElement
{
    public const int FullAdvance = 23, HalfAdvance = 11, LineHeight = 29, FontHeight = 24;

    // Kinsoku table of START.SCN ("_P..."): characters that may not start a line, and that may not end one
    private const string NoLineStart = "。，、．：；゛゜ヽヾゝゞ々）〕］｝〉》」』】°′″℃￠％‰”―　・ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ";
    private const string NoLineEnd = "（〔［｛〈《「『【￥＄￡";

    private static readonly Typeface s_face = new(new FontFamily("MS Gothic, ＭＳ ゴシック, Yu Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // Glyphs are the same for every message: lay each character out once
    private readonly Dictionary<char, FormattedText> m_glyphs = new();

    private string m_text = "";
    private int m_shown;

    /// <summary>Gaiji picture for a character (① = GAIJI.S25 frame 0, ...), or null.</summary>
    public Func<char, S25Frame?>? Gaiji { get; set; }

    public Brush Foreground { get; set; } = Brushes.White;

    /// <summary>Sets the message and how many of its characters are visible.</summary>
    public void SetText(string text, int shown)
    {
        if (text == m_text && shown == m_shown)
            return;
        m_text = text;
        m_shown = Math.Clamp(shown, 0, text.Length);
        InvalidateVisual();
    }

    private static bool IsHalfWidth(char c) => c < 0x80 || (c >= '｡' && c <= 'ﾟ');

    private static int GaijiIndex(char c) => c >= '①' && c <= '⑳' ? c - '①' : -1;

    /// <summary>Position of every character, wrapping at the element's width.</summary>
    private List<Point> Layout()
    {
        var positions = new List<Point>(m_text.Length);
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double x = 0, y = 0;
        for (int i = 0; i < m_text.Length; i++)
        {
            char c = m_text[i];
            if (c == '\n')
            {
                positions.Add(new Point(x, y));
                x = 0;
                y += LineHeight;
                continue;
            }
            int advance = IsHalfWidth(c) ? HalfAdvance : FullAdvance;
            bool overflows = x > 0 && x + advance > width;
            // An opening bracket that would be the last character of the line moves to the next one
            bool lonelyOpener = x > 0 && NoLineEnd.Contains(c) && i + 1 < m_text.Length &&
                                x + advance + (IsHalfWidth(m_text[i + 1]) ? HalfAdvance : FullAdvance) > width;
            if ((overflows && !NoLineStart.Contains(c)) || lonelyOpener)
            {
                x = 0;
                y += LineHeight;
            }
            positions.Add(new Point(x, y));
            x += advance;
        }
        return positions;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (m_text.Length == 0)
            return;
        var positions = Layout();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < m_shown; i++)
        {
            char c = m_text[i];
            if (c == '\n')
                continue;
            var p = positions[i];
            if (GaijiIndex(c) is int g and >= 0 && Gaiji?.Invoke(c) is { } frame)
            {
                dc.DrawImage(frame.Image, new Rect(p.X + frame.OffsetX, p.Y + frame.OffsetY, frame.Width, frame.Height));
                continue;
            }
            if (!m_glyphs.TryGetValue(c, out var glyph))
                m_glyphs[c] = glyph = new FormattedText(c.ToString(), CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                                                        s_face, FontHeight, Foreground, dpi);
            dc.DrawText(glyph, p);
        }
    }
}
