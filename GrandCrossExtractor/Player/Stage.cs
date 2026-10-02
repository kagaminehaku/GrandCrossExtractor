// The engine's 800x600 screen: numbered picture planes (0 = background), a scrolling
// background, a movie, transitions, plane animations and screen effects.
//
// Drawing commands change the planes at once, but the screen keeps showing the previous
// picture (a snapshot laid over it) until $DRAW / $DRAW_EX shows the change. Plane animations
// ($A_CHR, and the cross-fade of $L_CHR) are queued and start with that $DRAW, as the engine's
// action queue does. The timings and formulas follow START.SCN and EFCLIB.SCN; see
// docs/engine-notes.md, section 8.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GrandCrossExtractor.Player;

/// <summary>How $DRAW_EX replaces the screen.</summary>
public enum Transition
{
    Cut,
    CrossFade,
    /// <summary>Rule wipe, bright parts of the rule mask first ($DRAW_EX kind 2).</summary>
    RuleBrightFirst,
    /// <summary>Rule wipe, dark parts first ($DRAW_EX kind 47).</summary>
    RuleDarkFirst,
}

public sealed class Stage
{
    public const int Width = 800, Height = 600;

    private readonly Canvas m_effect = new() { Width = Width, Height = Height };
    private readonly Canvas m_content = new() { Width = Width, Height = Height, Background = Brushes.Black };
    private readonly Image m_overlay = new() { Width = Width, Height = Height, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly Rectangle m_flash = new() { Width = Width, Height = Height, Fill = Brushes.White, Opacity = 0, IsHitTestVisible = false };

    private readonly SortedDictionary<int, Plane> m_planes = new();
    private readonly List<Anim> m_running = new();
    private readonly List<Anim> m_queued = new();
    private readonly Stopwatch m_clock = Stopwatch.StartNew();
    private bool m_renderHooked;

    private bool m_frozen;
    private Anim? m_transition;

    // Background scroll ($EX,9,...)
    private Canvas? m_scroll;
    private int m_scrollWidth;
    private Anim? m_scrollAnim;

    // Movie ($L_MOVIE)
    private MediaElement? m_movie;
    private bool m_movieLoop;
    private TaskCompletionSource? m_movieEnd;

    public Stage(Canvas root)
    {
        root.Width = Width;
        root.Height = Height;
        root.ClipToBounds = true;
        root.Children.Add(m_effect);
        m_effect.Children.Add(m_content);
        m_effect.Children.Add(m_overlay);
        m_effect.Children.Add(m_flash);
        RenderOptions.SetBitmapScalingMode(m_content, BitmapScalingMode.HighQuality);
    }

    /// <summary>The engine's three easing curves: 1 linear, 2 slow start (1-cos), 3 slow end (sin).</summary>
    private static double Ease(int type, double t) => type switch
    {
        2 => 1 - Math.Cos(Math.PI * t / 2),
        3 => Math.Sin(Math.PI * t / 2),
        _ => t,
    };

    #region Planes

    private sealed class Plane
    {
        public readonly Image Element = new() { Stretch = Stretch.Fill };
        public StageImage? Picture;
        public double X, Y;
        public double LoopX, LoopY;
        public Rect Source = new(0, 0, Width, Height);
        public Rect Target = new(0, 0, Width, Height);

        // What the plane showed at the last $DRAW, for its cross-fade
        public bool Changed;
        public ImageSource? OldSource;
        public Transform? OldTransform;
        public double OldOpacity;
        public Image? Ghost;

        // Looping animation (A_CHR 1-6) and its options
        public Anim? Loop;
        public bool StopLoopAtCycleEnd;
        public Action? OnCycle;

        public void Update()
        {
            if (Picture == null)
                return;
            double sx = Target.Width / Math.Max(1, Source.Width), sy = Target.Height / Math.Max(1, Source.Height);
            double bx = Picture.Left + X + LoopX, by = Picture.Top + Y + LoopY;
            Element.RenderTransform = new MatrixTransform(sx, 0, 0, sy, (bx - Source.X) * sx + Target.X, (by - Source.Y) * sy + Target.Y);
        }
    }

    private Plane GetPlane(int number)
    {
        if (!m_planes.TryGetValue(number, out var plane))
        {
            plane = new Plane();
            m_planes[number] = plane;
            Panel.SetZIndex(plane.Element, number * 10 + 5);
            m_content.Children.Add(plane.Element);
        }
        return plane;
    }

    /// <summary>Remembers what the plane shows now, before the first change since the last $DRAW.</summary>
    private static void NoteChange(Plane plane)
    {
        if (plane.Changed)
            return;
        plane.Changed = true;
        plane.OldSource = plane.Picture != null ? plane.Element.Source : null;
        plane.OldTransform = plane.Element.RenderTransform;
        plane.OldOpacity = plane.Element.Opacity;
    }

    /// <summary>
    /// Shows <paramref name="picture"/> on a plane at (x, y). A plane that keeps showing a
    /// picture keeps its looping animation. Null clears the plane.
    /// </summary>
    public void SetPlane(int number, StageImage? picture, double x = 0, double y = 0)
    {
        Freeze();
        if (picture == null)
        {
            RemovePlane(number);
            return;
        }
        // Ending a fade-out removes the plane, so end the animations first
        foreach (var anim in m_running.Where(a => a.Plane == number && !a.Loop).ToList())
            Complete(anim);
        m_queued.RemoveAll(a => a.Plane == number);
        var plane = GetPlane(number);
        NoteChange(plane);
        plane.Picture = picture;
        plane.X = x;
        plane.Y = y;
        plane.Source = plane.Target = new Rect(0, 0, Width, Height);
        plane.Element.Source = picture.Bitmap;
        plane.Element.Width = picture.Bitmap.PixelWidth;
        plane.Element.Height = picture.Bitmap.PixelHeight;
        plane.Element.Opacity = 1;
        plane.Element.OpacityMask = null;
        plane.Update();
    }

    /// <summary>Replaces a plane's picture but keeps its position and animations (expression change).</summary>
    public void ChangePicture(int number, StageImage picture, double x, double y)
    {
        if (!m_planes.TryGetValue(number, out var plane) || plane.Picture == null)
        {
            SetPlane(number, picture, x, y);
            return;
        }
        Freeze();
        NoteChange(plane);
        plane.Picture = picture;
        plane.X = x;
        plane.Y = y;
        plane.Element.Source = picture.Bitmap;
        plane.Element.Width = picture.Bitmap.PixelWidth;
        plane.Element.Height = picture.Bitmap.PixelHeight;
        plane.Update();
    }

    private void RemovePlane(int number)
    {
        if (!m_planes.Remove(number, out var plane))
            return;
        foreach (var anim in m_running.Where(a => a.Plane == number).ToList())
            Complete(anim);
        m_queued.RemoveAll(a => a.Plane == number);
        m_content.Children.Remove(plane.Element);
        if (plane.Ghost != null)
            m_content.Children.Remove(plane.Ghost);
    }

    /// <summary>Clears planes 1-9 ($L_BG with reset 0 starts a new scene).</summary>
    public void ClearCharacters()
    {
        Freeze();
        foreach (int number in m_planes.Keys.Where(n => n > 0).ToList())
            RemovePlane(number);
    }

    public bool TryGetPosition(int number, out double x, out double y)
    {
        x = y = 0;
        if (!m_planes.TryGetValue(number, out var plane))
            return false;
        (x, y) = (plane.X, plane.Y);
        return true;
    }

    #endregion

    #region Animation engine

    private sealed class Anim
    {
        public int Plane = -1;
        public string Kind = "";
        public double Duration;          // ms; loops run until Step says they are done
        public bool Loop;
        public bool Finite;              // a loop with a number of cycles: $WAITA waits for it
        /// <summary>wf = 1: $WAITA does not wait for it and a click does not end it.</summary>
        public bool Background;
        public Action<double> Apply = _ => { };   // progress 0..1
        public Func<double, bool>? Step;          // loops: elapsed ms -> finished?
        public Action? Started;
        public Action? Finished;
        public double Start;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private double Now => m_clock.Elapsed.TotalMilliseconds;

    private void Run(Anim anim)
    {
        // A new animation of the same kind on the same plane replaces the old one
        if (anim.Plane >= 0)
            foreach (var old in m_running.Where(a => a.Plane == anim.Plane && a.Kind == anim.Kind).ToList())
                Complete(old);
        anim.Start = Now;
        m_running.Add(anim);
        anim.Started?.Invoke();
        if (!anim.Loop && anim.Duration <= 0)
        {
            Complete(anim);
            return;
        }
        HookRendering();
    }

    private void Complete(Anim anim)
    {
        if (!m_running.Remove(anim) && anim.Done.Task.IsCompleted)
            return;
        if (!anim.Loop)
            anim.Apply(1);
        anim.Finished?.Invoke();
        anim.Done.TrySetResult();
    }

    private void HookRendering()
    {
        if (m_renderHooked)
            return;
        m_renderHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = Now;
        foreach (var anim in m_running.ToList())
        {
            double elapsed = now - anim.Start;
            if (anim.Loop)
            {
                if (anim.Step?.Invoke(elapsed) == true)
                    Complete(anim);
            }
            else if (elapsed >= anim.Duration)
                Complete(anim);
            else
                anim.Apply(elapsed / anim.Duration);
        }
        if (m_running.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            m_renderHooked = false;
        }
    }

    /// <summary>Animations a click ends and $WAITA waits for.</summary>
    private static bool IsWaitable(Anim a) => !a.Background && (!a.Loop || a.Finite);

    /// <summary>
    /// A click: ends the transition and every animation that is not a background one (wf = 1),
    /// including loops with a number of cycles. Endless loops keep running.
    /// </summary>
    public void FinishAll()
    {
        foreach (var anim in m_running.Where(IsWaitable).ToList())
            Complete(anim);
    }

    /// <summary>Completes when the animations $WAITA waits for, and a non-looping movie, have ended.</summary>
    public Task WhenIdle()
    {
        var tasks = m_running.Where(IsWaitable).Select(a => a.Done.Task).ToList();
        if (m_movie != null && !m_movieLoop && m_movieEnd != null)
            tasks.Add(m_movieEnd.Task);
        return Task.WhenAll(tasks);
    }

    private Anim Queue(int plane, string kind, double duration, bool background, Action<double> apply)
    {
        var anim = new Anim { Plane = plane, Kind = kind, Duration = duration, Background = background, Apply = apply };
        m_queued.Add(anim);
        return anim;
    }

    /// <summary>Runs <paramref name="action"/> when the next $DRAW runs (queue order is kept).</summary>
    private void QueueAction(int plane, Action action) =>
        m_queued.Add(new Anim { Plane = plane, Kind = "action", Started = action });

    #endregion

    #region Plane animations (A_CHR)

    /// <summary>
    /// Cross-fades a plane from what it showed at the last $DRAW to its new picture (function
    /// 306 type 0: $L_CHR, A_CHR 152). A plane that was empty fades in.
    /// </summary>
    public void QueueCrossFade(int number, double ms, bool background)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        m_queued.RemoveAll(a => a.Plane == number && a.Kind == "xfade");
        var anim = Queue(number, "xfade", ms, background, _ => { });
        anim.Started = () =>
        {
            if (plane.OldSource == null)
            {
                plane.Element.Opacity = 0;
                anim.Apply = t => plane.Element.Opacity = t;
                return;
            }
            plane.Ghost ??= new Image { Stretch = Stretch.Fill };
            if (!m_content.Children.Contains(plane.Ghost))
                m_content.Children.Add(plane.Ghost);
            Panel.SetZIndex(plane.Ghost, number * 10 + 6);
            var ghost = plane.Ghost;
            ghost.Source = plane.OldSource;
            ghost.Width = (plane.OldSource as BitmapSource)?.PixelWidth ?? plane.Element.Width;
            ghost.Height = (plane.OldSource as BitmapSource)?.PixelHeight ?? plane.Element.Height;
            ghost.RenderTransform = plane.OldTransform;
            ghost.Visibility = Visibility.Visible;
            double from = plane.OldOpacity;
            anim.Apply = t => ghost.Opacity = from * (1 - t);
        };
        anim.Finished = () =>
        {
            if (plane.Ghost != null)
                plane.Ghost.Visibility = Visibility.Collapsed;
        };
    }

    /// <summary>Fades a plane in from transparent (function 306 type 19, A_CHR 151).</summary>
    public void QueueFadeIn(int number, double ms, bool background)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        Freeze();
        m_queued.RemoveAll(a => a.Plane == number && a.Kind == "xfade");
        plane.Element.Opacity = 0;
        Queue(number, "fade", ms, background, t => plane.Element.Opacity = t);
    }

    /// <summary>Fades a plane out, then removes it (function 306 type 13, A_CHR 150).</summary>
    public void QueueFadeOut(int number, double ms, bool background)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        double from = 1;
        var anim = Queue(number, "fade", ms, background, t => plane.Element.Opacity = from * (1 - t));
        anim.Started = () => from = plane.Element.Opacity;
        anim.Finished = () => RemovePlane(number);
    }

    /// <summary>
    /// Moves a plane from (fromX, fromY) - its current position when null - to (x, y) with one
    /// of the engine's easings, optionally removing it at the end (slide out).
    /// </summary>
    public void QueueMove(int number, double? fromX, double? fromY, double x, double y, int easing, double ms, bool background, bool removeAtEnd)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        double x0 = 0, y0 = 0;
        var anim = Queue(number, "move", ms, background, t =>
        {
            double s = Ease(easing, t);
            plane.X = x0 + (x - x0) * s;
            plane.Y = y0 + (y - y0) * s;
            plane.Update();
        });
        anim.Started = () =>
        {
            x0 = fromX ?? plane.X;
            y0 = fromY ?? plane.Y;
            plane.X = x0;
            plane.Y = y0;
            plane.Update();
        };
        if (removeAtEnd)
            anim.Finished = () => RemovePlane(number);
    }

    /// <summary>Sets the part of the screen a plane is drawn into (A_CHR 40).</summary>
    public void SetViewTarget(int number, Rect target)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        Freeze();
        plane.Target = target;
        plane.Update();
    }

    /// <summary>Sets the part of the plane that is shown, zoomed to the target (A_CHR 41).</summary>
    public void SetViewSource(int number, Rect source)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        Freeze();
        plane.Source = source;
        plane.Update();
    }

    /// <summary>Pans / zooms the shown part to <paramref name="source"/> (A_CHR 42-44: easing 1-3).</summary>
    public void QueueView(int number, Rect source, int easing, double ms, bool background)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        Rect from = default;
        var anim = Queue(number, "view", ms, background, t =>
        {
            double s = Ease(easing, t);
            plane.Source = new Rect(from.X + (source.X - from.X) * s, from.Y + (source.Y - from.Y) * s,
                                    from.Width + (source.Width - from.Width) * s, from.Height + (source.Height - from.Height) * s);
            plane.Update();
        });
        anim.Started = () => from = plane.Source;
    }

    /// <summary>
    /// A_CHR 1-6: the plane moves in a loop. <paramref name="cycles"/> = 0 loops until stopped,
    /// <paramref name="amplitude"/> in pixels, <paramref name="period"/> in ms (at least 30).
    /// 1 hop up, 2 dip down, 3 / 4 triangle wave on y / x, 5 / 6 sine wave on y / x.
    /// </summary>
    public void QueueLoop(int number, int mode, int cycles, double amplitude, double period)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        period = Math.Max(30, period);
        int stopAt = -1, lastCycle = 0;
        var anim = new Anim { Plane = number, Kind = "loop", Loop = true, Finite = cycles > 0 };
        anim.Started = () =>
        {
            plane.Loop = anim;
            plane.StopLoopAtCycleEnd = false;
        };
        anim.Step = ms =>
        {
            int n = (int)(ms / period);
            double ph = ms % period, a = amplitude;
            if (plane.StopLoopAtCycleEnd && stopAt < 0)
                stopAt = n;
            if ((cycles > 0 && n >= cycles) || (stopAt >= 0 && n != stopAt))
                return true;
            if (n != lastCycle)
            {
                lastCycle = n;
                plane.OnCycle?.Invoke();
            }
            double dx = 0, dy = 0, k = a * ph * 2 / period;
            double tri = ph < period / 4 ? -k : ph < period * 3 / 4 ? k - a : -(k - a * 2);
            switch (mode)
            {
                case 1: dy = -Math.Sin(Math.PI * ph / period) * a; break;
                case 2: dy = Math.Sin(Math.PI * ph / period) * a; break;
                case 3: dy = tri; break;
                case 4: dx = tri; break;
                case 5: dy = -Math.Sin(2 * Math.PI * ph / period) * a / 2; break;
                case 6: dx = Math.Sin(2 * Math.PI * ph / period) * a / 2; break;
            }
            plane.LoopX = dx;
            plane.LoopY = dy;
            plane.Update();
            return false;
        };
        anim.Finished = () =>
        {
            plane.LoopX = plane.LoopY = 0;
            plane.Update();
            if (plane.Loop == anim)
                plane.Loop = null;
        };
        m_queued.Add(anim);
    }

    /// <summary>A_CHR 00 stops the loop at the end of its current cycle, A_CHR 09 at once.</summary>
    public void QueueStopLoop(int number, bool now)
    {
        QueueAction(number, () =>
        {
            if (!m_planes.TryGetValue(number, out var plane) || plane.Loop == null)
                return;
            if (now)
                Complete(plane.Loop);
            else
                plane.StopLoopAtCycleEnd = true;
        });
    }

    /// <summary>A_CHR 90 / 91: run <paramref name="onCycle"/> at every cycle of the plane's loop (footsteps).</summary>
    public void QueueCycleAction(int number, Action? onCycle) =>
        QueueAction(number, () =>
        {
            if (m_planes.TryGetValue(number, out var plane))
                plane.OnCycle = onCycle;
        });

    /// <summary>
    /// A_CHR 60-63: the plane appears (or disappears, then is removed) through a rule mask;
    /// <paramref name="reversed"/> swaps the mask's bright and dark parts.
    /// </summary>
    public void QueueRuleFade(int number, BitmapSource rule, bool appear, bool reversed, double ms, bool background)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        Freeze();
        var levels = RuleLevels(rule);
        var mask = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
        var pixels = new byte[Width * Height * 4];
        void Paint(double t)
        {
            // Weight of the plane: like the screen wipe, a 256-level soft edge sweeping across
            double level = 511 * (appear ? t : 1 - t);
            for (int i = 0, p = 0; i < levels.Length; i++, p += 4)
            {
                int r = reversed ? 255 - levels[i] : levels[i];
                double w = Math.Clamp(level + r - 256, 0, 256) * 255 / 256;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = pixels[p + 3] = (byte)w;
            }
            mask.WritePixels(new Int32Rect(0, 0, Width, Height), pixels, Width * 4, 0);
        }
        // The mask is in screen coordinates; the plane's own transform is undone for the brush
        plane.Element.OpacityMask = new ImageBrush(mask)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = ScreenRectInPlane(plane),
            Stretch = Stretch.Fill,
        };
        Paint(0);
        var anim = Queue(number, "fade", ms, background, Paint);
        anim.Finished = () =>
        {
            plane.Element.OpacityMask = null;
            if (!appear)
                RemovePlane(number);
        };
    }

    /// <summary>Where the screen rectangle lies in the plane element's own coordinates.</summary>
    private static Rect ScreenRectInPlane(Plane plane)
    {
        var m = plane.Element.RenderTransform.Value;
        if (!m.HasInverse)
            return new Rect(0, 0, Width, Height);
        m.Invert();
        return Rect.Transform(new Rect(0, 0, Width, Height), m);
    }

    #endregion

    #region Commit and transitions

    /// <summary>Keeps the current picture on screen while the next one is prepared.</summary>
    private void Freeze()
    {
        if (m_frozen)
            return;
        // A running transition would be captured half-way: end it first
        if (m_transition != null)
            Complete(m_transition);
        // Pictures set since the last frame have not been laid out yet
        m_content.UpdateLayout();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(m_content)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, Width, Height),
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, Width, Height));
        }
        var snapshot = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(visual);
        snapshot.Freeze();

        m_overlay.Source = snapshot;
        m_overlay.OpacityMask = null;
        m_overlay.Opacity = 1;
        m_overlay.Visibility = Visibility.Visible;
        m_frozen = true;
    }

    /// <summary>
    /// $DRAW: shows the prepared picture at once and starts the queued animations. Planes
    /// changed by $L_CHR cross-fade on their own.
    /// </summary>
    public void Draw() => Commit(Transition.Cut, 0, null, keepPlaneFades: true);

    /// <summary>
    /// $DRAW_EX: replaces the screen with the prepared picture (cut, cross-fade or rule wipe
    /// over <paramref name="ms"/>) and starts the queued animations. The task completes when
    /// the transition has ended; the engine waits for it.
    /// </summary>
    public Task DrawEx(Transition kind, double ms, BitmapSource? rule) => Commit(kind, ms, rule, keepPlaneFades: false);

    private Task Commit(Transition kind, double ms, BitmapSource? rule, bool keepPlaneFades)
    {
        // $DRAW_EX shows changed planes directly: their own cross-fades are dropped, as the
        // engine clears that flag before a screen transition
        if (!keepPlaneFades)
            m_queued.RemoveAll(a => a.Kind == "xfade" && !a.Background);
        foreach (var anim in m_queued.ToList())
        {
            m_queued.Remove(anim);
            Run(anim);
        }
        foreach (var plane in m_planes.Values)
            plane.Changed = false;

        if (!m_frozen)
            return Task.CompletedTask;
        m_frozen = false;
        if (ms <= 0 || kind == Transition.Cut)
        {
            HideOverlay();
            return Task.CompletedTask;
        }
        var transition = kind == Transition.CrossFade || rule == null
            ? new Anim { Duration = ms, Kind = "transition", Apply = t => m_overlay.Opacity = 1 - t }
            : RuleTransition(ms, rule, kind == Transition.RuleBrightFirst);
        transition.Finished = () =>
        {
            HideOverlay();
            if (m_transition == transition)
                m_transition = null;
        };
        m_transition = transition;
        Run(transition);
        return transition.Done.Task;
    }

    private void HideOverlay()
    {
        if (m_frozen)
            return;
        m_overlay.Visibility = Visibility.Collapsed;
        m_overlay.Source = null;
        m_overlay.OpacityMask = null;
    }

    /// <summary>
    /// Rule wipe as the engine's blend (exe 0x437B40): with t going 0..511, the new picture's
    /// weight at a pixel is clamp(t + rule - 256, 0, 256) / 256, so bright parts of the rule
    /// change first. Dark-first ($DRAW_EX 47) runs the same blend with the pictures swapped
    /// and t going back.
    /// </summary>
    private Anim RuleTransition(double ms, BitmapSource rule, bool brightFirst)
    {
        var levels = RuleLevels(rule);
        var mask = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
        var pixels = new byte[Width * Height * 4];
        m_overlay.OpacityMask = new ImageBrush(mask);
        return new Anim
        {
            Duration = ms, Kind = "transition",
            Apply = p =>
            {
                double t = 511 * (brightFirst ? p : 1 - p);
                for (int i = 0, k = 0; i < levels.Length; i++, k += 4)
                {
                    double w = Math.Clamp(t + levels[i] - 256, 0, 256) / 256;
                    // The overlay holds the old picture
                    double old = brightFirst ? 1 - w : w;
                    pixels[k] = pixels[k + 1] = pixels[k + 2] = pixels[k + 3] = (byte)(old * 255);
                }
                mask.WritePixels(new Int32Rect(0, 0, Width, Height), pixels, Width * 4, 0);
            },
        };
    }

    /// <summary>Brightness of each screen pixel of a rule mask, stretched to 800x600.</summary>
    private static byte[] RuleLevels(BitmapSource rule)
    {
        BitmapSource source = rule;
        if (rule.PixelWidth != Width || rule.PixelHeight != Height)
            source = new TransformedBitmap(rule, new ScaleTransform((double)Width / rule.PixelWidth, (double)Height / rule.PixelHeight));
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Width * Height * 4];
        bgra.CopyPixels(pixels, Width * 4, 0);
        var levels = new byte[Width * Height];
        for (int i = 0; i < levels.Length; i++)
            levels[i] = pixels[i * 4 + 2];
        return levels;
    }

    #endregion

    #region Screen effects ($EFECT, EFCLIB.SCN)

    private const double EffectFrameMs = 1000.0 / 15;   // EFCLIB runs these effects at 15 fps

    /// <summary>
    /// Shows a sequence of frames (one screen transform each) at 15 fps, then puts the screen
    /// back. The task completes at the end; a click ends it early.
    /// </summary>
    private Task EffectFrames(IReadOnlyList<Matrix> frames)
    {
        var transform = new MatrixTransform();
        var anim = new Anim
        {
            Kind = "effect",
            Duration = frames.Count * EffectFrameMs,
            Apply = t => transform.Matrix = frames[Math.Min(frames.Count - 1, (int)(t * frames.Count))],
        };
        anim.Started = () => m_effect.RenderTransform = transform;
        anim.Finished = () => m_effect.RenderTransform = Transform.Identity;
        Run(anim);
        return anim.Done.Task;
    }

    /// <summary>
    /// EFCLIB 34 ($EFECT 0 / 1 / 2): the screen, enlarged by <paramref name="size"/> pixels,
    /// jumps between four offsets, twice.
    /// </summary>
    public Task Shake(int size)
    {
        double sx = (Width + size) / (double)Width, sy = (Height + size) / (double)Height;
        var offsets = new (double X, double Y)[] { (0, -size), (-size / 2.0, -size / 2.0), (-size, -size), (-size / 2.0, -size / 2.0) };
        var frames = new List<Matrix>();
        for (int round = 0; round < 2; round++)
            foreach (var (x, y) in offsets)
                frames.Add(new Matrix(sx, 0, 0, sy, x, y));
        return EffectFrames(frames);
    }

    /// <summary>
    /// EFCLIB 35 ($EFECT 8-15): zooms in by (w, h) pixels a side per step, steps 1, 2, 3, 2, 1,
    /// <paramref name="rounds"/> times.
    /// </summary>
    public Task ZoomPulse(int w, int h, int rounds)
    {
        var frames = new List<Matrix>();
        for (int round = 0; round < rounds; round++)
        {
            foreach (int z in new[] { 1, 2, 3, 2, 1 })
            {
                double tx = w * z, ty = h * z;
                double sx = Width / (Width - 2 * tx), sy = Height / (Height - 2 * ty);
                frames.Add(new Matrix(sx, 0, 0, sy, -tx * sx, -ty * sy));
            }
        }
        return EffectFrames(frames);
    }

    /// <summary>A flash of <paramref name="color"/> held for <paramref name="ms"/> ($EFECT 3-6).</summary>
    public Task Flash(Color color, double ms, bool fade)
    {
        var anim = new Anim
        {
            Kind = "effect",
            Duration = ms,
            Apply = t => m_flash.Opacity = fade ? 1 - t : 1,
        };
        anim.Started = () =>
        {
            m_flash.Fill = new SolidColorBrush(color);
            m_flash.Opacity = 1;
        };
        anim.Finished = () => m_flash.Opacity = 0;
        Run(anim);
        return anim.Done.Task;
    }

    #endregion

    #region Background scroll

    /// <summary>Prepares a horizontally scrolling background above plane 0 ($EX,9,0,count,width).</summary>
    public void ScrollInit(int width)
    {
        Freeze();
        ScrollStop();
        m_scrollWidth = width;
        m_scroll = new Canvas { Width = Width, Height = Height };
        Panel.SetZIndex(m_scroll, 7);
        m_content.Children.Add(m_scroll);
    }

    /// <summary>Sets the scrolling picture; it is drawn twice side by side so it wraps ($EX,9,1,slot,file).</summary>
    public void ScrollImage(StageImage picture)
    {
        if (m_scroll == null)
            ScrollInit(picture.Bitmap.PixelWidth);
        Freeze();
        m_scroll!.Children.Clear();
        if (m_scrollWidth <= 0)
            m_scrollWidth = picture.Bitmap.PixelWidth;
        for (int i = 0; i < 2; i++)
            m_scroll.Children.Add(new Image { Source = picture.Bitmap, Width = picture.Bitmap.PixelWidth, Height = picture.Bitmap.PixelHeight, Stretch = Stretch.Fill });
        ScrollPosition(0);
    }

    /// <summary>
    /// Starts scrolling ($EX,9,2,speed): <paramref name="speed"/> pixels per second; a positive
    /// speed moves the picture to the right.
    /// </summary>
    public void ScrollStart(double speed)
    {
        if (m_scroll == null)
            return;
        if (m_scrollAnim != null)
            Complete(m_scrollAnim);
        m_scrollAnim = new Anim
        {
            Kind = "scroll", Loop = true, Background = true,
            Step = ms =>
            {
                ScrollPosition(ms * speed / 1000);
                return false;
            },
        };
        Run(m_scrollAnim);
    }

    private void ScrollPosition(double travelled)
    {
        if (m_scroll == null || m_scrollWidth <= 0)
            return;
        double offset = ((travelled % m_scrollWidth) + m_scrollWidth) % m_scrollWidth;
        double x = offset - m_scrollWidth;
        for (int i = 0; i < m_scroll.Children.Count; i++)
            Canvas.SetLeft(m_scroll.Children[i], x + i * m_scrollWidth);
    }

    /// <summary>Removes the scrolling background ($EX,9,4).</summary>
    public void ScrollStop()
    {
        if (m_scrollAnim != null)
        {
            Complete(m_scrollAnim);
            m_scrollAnim = null;
        }
        if (m_scroll != null)
        {
            Freeze();
            m_content.Children.Remove(m_scroll);
            m_scroll = null;
        }
    }

    #endregion

    #region Movie

    /// <summary>Plays an MPEG movie on a plane ($L_MOVIE,plane,file,loop,...). Null stops it.</summary>
    public void PlayMovie(int plane, string? path, bool loop, double volume)
    {
        StopMovie();
        if (path == null)
            return;
        m_movieLoop = loop;
        m_movieEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var movie = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Fill,
            Width = Width,
            Height = Height,
            Volume = volume,
            Source = new Uri(path),
        };
        Panel.SetZIndex(movie, plane * 10 + 6);
        movie.MediaEnded += (_, _) =>
        {
            if (movie != m_movie)
                return;
            // Whoever waits for this round ($WAIT_L_MOVIE) may now replace the movie
            var ended = m_movieEnd;
            m_movieEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (m_movieLoop)
            {
                movie.Position = TimeSpan.Zero;
                movie.Play();
            }
            ended?.TrySetResult();
        };
        movie.MediaFailed += (_, _) => m_movieEnd?.TrySetResult();
        m_movie = movie;
        m_content.Children.Add(movie);
        movie.Play();
    }

    public void StopMovie()
    {
        if (m_movie == null)
            return;
        m_movie.Stop();
        m_movie.Close();
        m_content.Children.Remove(m_movie);
        m_movie = null;
        m_movieEnd?.TrySetResult();
        m_movieEnd = null;
    }

    /// <summary>Completes when the movie reaches its end (the end of the current round for a loop).</summary>
    public Task WhenMovieEnds() => m_movie == null || m_movieEnd == null ? Task.CompletedTask : m_movieEnd.Task;

    public void PauseMovie(bool pause)
    {
        if (m_movie == null)
            return;
        if (pause)
            m_movie.Pause();
        else
            m_movie.Play();
    }

    #endregion

    /// <summary>Clears everything (a new chapter starts).</summary>
    public void Reset()
    {
        foreach (var anim in m_running.ToList())
            Complete(anim);
        m_queued.Clear();
        StopMovie();
        ScrollStop();
        foreach (int number in m_planes.Keys.ToList())
            RemovePlane(number);
        m_frozen = false;
        HideOverlay();
        m_flash.Opacity = 0;
        m_effect.RenderTransform = Transform.Identity;
    }
}
