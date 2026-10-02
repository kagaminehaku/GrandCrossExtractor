// The engine's 800x600 screen: numbered picture planes (0 = background), a scrolling
// background, a movie, transitions and plane animations.
//
// Drawing commands change the planes at once, but the screen keeps showing the previous
// picture (a snapshot laid over it) until $DRAW / $DRAW_EX commits the change, with a
// cross-fade or a rule-mask wipe. This gives the engine's "prepare, then show" behaviour.

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GrandCrossExtractor.Player;

public sealed class Stage
{
    public const int Width = 800, Height = 600;

    private readonly Canvas m_shake = new() { Width = Width, Height = Height };
    private readonly Canvas m_content = new() { Width = Width, Height = Height, Background = Brushes.Black };
    private readonly Image m_overlay = new() { Width = Width, Height = Height, Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
    private readonly Rectangle m_flash = new() { Width = Width, Height = Height, Fill = Brushes.White, Opacity = 0, IsHitTestVisible = false };
    private readonly TranslateTransform m_shakeOffset = new();

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
    private int m_scrollDirection = 1;
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
        root.Children.Add(m_shake);
        m_shake.RenderTransform = m_shakeOffset;
        m_shake.Children.Add(m_content);
        m_shake.Children.Add(m_overlay);
        m_shake.Children.Add(m_flash);
        RenderOptions.SetBitmapScalingMode(m_content, BitmapScalingMode.HighQuality);
    }

    #region Planes

    private sealed class Plane
    {
        public readonly Image Element = new() { Stretch = Stretch.Fill };
        public StageImage? Picture;
        public double X, Y;
        public double LoopX, LoopY;
        public Rect Source = new(0, 0, Width, Height);
        public Rect Target = new(0, 0, Width, Height);

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

    /// <summary>Shows <paramref name="picture"/> on a plane at (x, y), resetting its animations. Null clears the plane.</summary>
    public void SetPlane(int number, StageImage? picture, double x = 0, double y = 0)
    {
        Freeze();
        StopAnimations(number);
        if (picture == null)
        {
            if (m_planes.Remove(number, out var old))
                m_content.Children.Remove(old.Element);
            return;
        }
        var plane = GetPlane(number);
        plane.Picture = picture;
        plane.X = x;
        plane.Y = y;
        plane.LoopX = plane.LoopY = 0;
        plane.Source = plane.Target = new Rect(0, 0, Width, Height);
        plane.Element.Source = picture.Bitmap;
        plane.Element.Width = picture.Bitmap.PixelWidth;
        plane.Element.Height = picture.Bitmap.PixelHeight;
        plane.Element.Opacity = 1;
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
        plane.Picture = picture;
        plane.Element.Source = picture.Bitmap;
        plane.Element.Width = picture.Bitmap.PixelWidth;
        plane.Element.Height = picture.Bitmap.PixelHeight;
        plane.Update();
    }

    /// <summary>Clears every plane above the background ($L_BG starts a new scene).</summary>
    public void ClearCharacters()
    {
        foreach (int number in m_planes.Keys.Where(n => n > 0).ToList())
            SetPlane(number, null);
    }

    #endregion

    #region Animations

    private sealed class Anim
    {
        public int Plane = -1;
        public string Kind = "";
        public double Duration;          // ms; loops run until stopped
        public bool Loop;
        public bool Blocking;
        public Action<double> Apply = _ => { };   // progress 0..1, or elapsed ms for loops
        public Action? Finished;
        public double Start;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private double Now => m_clock.Elapsed.TotalMilliseconds;

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private void Run(Anim anim)
    {
        // A new animation of the same kind on the same plane replaces the old one
        foreach (var old in m_running.Where(a => a.Plane == anim.Plane && a.Kind == anim.Kind && a.Plane >= 0).ToList())
            Complete(old);
        anim.Start = Now;
        m_running.Add(anim);
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
                anim.Apply(elapsed);
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

    private void StopAnimations(int plane)
    {
        foreach (var anim in m_running.Where(a => a.Plane == plane).ToList())
            Complete(anim);
        m_queued.RemoveAll(a => a.Plane == plane);
    }

    /// <summary>Ends every running transition and animation at once (click, skip). Loops keep running.</summary>
    public void FinishAll()
    {
        foreach (var anim in m_running.Where(a => !a.Loop).ToList())
            Complete(anim);
    }

    /// <summary>Completes when every transition, timed animation and non-looping movie has ended ($WAITA).</summary>
    public Task WhenIdle()
    {
        var tasks = m_running.Where(a => !a.Loop).Select(a => a.Done.Task).ToList();
        if (m_movie != null && !m_movieLoop && m_movieEnd != null)
            tasks.Add(m_movieEnd.Task);
        return Task.WhenAll(tasks);
    }

    private void Queue(int plane, string kind, double duration, bool blocking, Action<double> apply, Action? finished = null) =>
        m_queued.Add(new Anim { Plane = plane, Kind = kind, Duration = duration, Blocking = blocking, Apply = apply, Finished = finished });

    /// <summary>Fades a plane in (from transparent) or out when the next $DRAW runs.</summary>
    public void QueueFade(int number, bool fadeIn, double ms, bool wait)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        if (fadeIn)
        {
            Freeze();
            plane.Element.Opacity = 0;
        }
        double from = fadeIn ? 0 : plane.Element.Opacity, to = fadeIn ? 1 : 0;
        Queue(number, "fade", ms, wait, t => plane.Element.Opacity = from + (to - from) * t);
    }

    /// <summary>Moves a plane to (x, y) when the next $DRAW runs.</summary>
    public void QueueMove(int number, double x, double y, double ms, bool wait)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        // The move starts from wherever the plane is when $DRAW runs
        double x0 = 0, y0 = 0;
        bool started = false;
        Queue(number, "move", ms, wait, t =>
        {
            if (!started)
            {
                started = true;
                x0 = plane.X;
                y0 = plane.Y;
            }
            plane.X = x0 + (x - x0) * Smooth(t);
            plane.Y = y0 + (y - y0) * Smooth(t);
            plane.Update();
        });
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

    /// <summary>Pans / zooms the shown part to <paramref name="source"/> when the next $DRAW runs (A_CHR 42).</summary>
    public void QueueView(int number, Rect source, double ms, bool wait)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        Rect from = default;
        bool started = false;
        Queue(number, "view", ms, wait, t =>
        {
            if (!started)
            {
                started = true;
                from = plane.Source;
            }
            double s = Smooth(t);
            plane.Source = new Rect(from.X + (source.X - from.X) * s, from.Y + (source.Y - from.Y) * s,
                                    from.Width + (source.Width - from.Width) * s, from.Height + (source.Height - from.Height) * s);
            plane.Update();
        });
    }

    /// <summary>Keeps a plane swaying until stopped: x amplitude, y bounce height, period in ms (A_CHR 01 / 06).</summary>
    public void QueueSway(int number, double ampX, double ampY, double period)
    {
        if (!m_planes.TryGetValue(number, out var plane) || period <= 0)
            return;
        m_queued.Add(new Anim
        {
            Plane = number, Kind = "loop", Loop = true,
            Apply = ms =>
            {
                double phase = ms / period;
                plane.LoopX = ampX * Math.Sin(phase * 2 * Math.PI);
                plane.LoopY = -ampY * Math.Abs(Math.Sin(phase * Math.PI));
                plane.Update();
            },
        });
    }

    /// <summary>Stops a plane's animations and puts it back in place (A_CHR 00).</summary>
    public void ResetPlane(int number)
    {
        if (!m_planes.TryGetValue(number, out var plane))
            return;
        StopAnimations(number);
        plane.LoopX = plane.LoopY = 0;
        plane.Update();
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
    /// Shows the prepared picture ($DRAW / $DRAW_EX): a cross-fade over <paramref name="ms"/>,
    /// or a wipe along <paramref name="rule"/> (a grey-scale mask: dark areas change first),
    /// and starts the queued animations. The task completes when the transition and the
    /// animations marked "wait" have ended.
    /// </summary>
    public Task Commit(double ms, BitmapSource? rule = null)
    {
        var waits = new List<Task>();
        foreach (var anim in m_queued.ToList())
        {
            m_queued.Remove(anim);
            Run(anim);
            if (anim.Blocking)
                waits.Add(anim.Done.Task);
        }

        if (m_frozen)
        {
            m_frozen = false;
            if (ms <= 0)
            {
                HideOverlay();
            }
            else
            {
                var anim = rule != null ? RuleTransition(ms, rule) : new Anim
                {
                    Duration = ms, Kind = "transition",
                    Apply = t => m_overlay.Opacity = 1 - t,
                };
                anim.Finished = () =>
                {
                    HideOverlay();
                    if (m_transition == anim)
                        m_transition = null;
                };
                m_transition = anim;
                Run(anim);
                waits.Add(anim.Done.Task);
            }
        }
        return Task.WhenAll(waits);
    }

    private void HideOverlay()
    {
        if (m_frozen)
            return;
        m_overlay.Visibility = Visibility.Collapsed;
        m_overlay.Source = null;
        m_overlay.OpacityMask = null;
    }

    private Anim RuleTransition(double ms, BitmapSource rule)
    {
        const int Vague = 64;
        var levels = RuleLevels(rule);
        var mask = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
        var pixels = new byte[Width * Height * 4];
        m_overlay.OpacityMask = new ImageBrush(mask);
        return new Anim
        {
            Duration = ms, Kind = "transition",
            Apply = t =>
            {
                double threshold = -Vague + (255 + Vague) * t;
                for (int i = 0, p = 0; i < levels.Length; i++, p += 4)
                {
                    double a = (levels[i] - threshold) * 255 / Vague;
                    byte v = a <= 0 ? (byte)0 : a >= 255 ? (byte)255 : (byte)a;
                    pixels[p] = pixels[p + 1] = pixels[p + 2] = pixels[p + 3] = v;
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

    #region Effects

    /// <summary>Shakes the screen for <paramref name="ms"/>: horizontal and vertical amplitude in pixels.</summary>
    public void Shake(double ampX, double ampY, double ms)
    {
        Run(new Anim
        {
            Kind = "shake", Duration = ms,
            Apply = t =>
            {
                double fade = 1 - t;
                m_shakeOffset.X = ampX * fade * Math.Sin(t * ms / 25.0);
                m_shakeOffset.Y = ampY * fade * Math.Cos(t * ms / 31.0);
            },
            Finished = () => m_shakeOffset.X = m_shakeOffset.Y = 0,
        });
    }

    /// <summary>A white flash fading out over <paramref name="ms"/>.</summary>
    public void Flash(double ms)
    {
        Run(new Anim
        {
            Kind = "flash", Duration = ms,
            Apply = t => m_flash.Opacity = 0.9 * (1 - t),
            Finished = () => m_flash.Opacity = 0,
        });
    }

    #endregion

    #region Background scroll

    /// <summary>Prepares a horizontally scrolling background above plane 0 ($EX,9,0,direction,width).</summary>
    public void ScrollInit(int direction, int width)
    {
        Freeze();
        ScrollStop();
        m_scrollDirection = direction == 0 ? -1 : 1;
        m_scrollWidth = width;
        m_scroll = new Canvas { Width = Width, Height = Height };
        Panel.SetZIndex(m_scroll, 7);
        m_content.Children.Add(m_scroll);
    }

    /// <summary>Sets the scrolling picture; it is drawn twice side by side so it wraps ($EX,9,1,layer,file).</summary>
    public void ScrollImage(StageImage picture)
    {
        if (m_scroll == null)
            ScrollInit(1, picture.Bitmap.PixelWidth);
        Freeze();
        m_scroll!.Children.Clear();
        if (m_scrollWidth <= 0)
            m_scrollWidth = picture.Bitmap.PixelWidth;
        for (int i = 0; i < 2; i++)
            m_scroll.Children.Add(new Image { Source = picture.Bitmap, Width = picture.Bitmap.PixelWidth, Height = picture.Bitmap.PixelHeight, Stretch = Stretch.Fill });
        ScrollPosition(0);
    }

    /// <summary>Starts scrolling ($EX,9,2,speed). The speed unit is not known; 3 px/s per unit looks right.</summary>
    public void ScrollStart(double speed)
    {
        if (m_scroll == null)
            return;
        if (m_scrollAnim != null)
            Complete(m_scrollAnim);
        double pixelsPerMs = speed * 3 / 1000.0;
        m_scrollAnim = new Anim { Kind = "scroll", Loop = true, Apply = ms => ScrollPosition(ms * pixelsPerMs) };
        Run(m_scrollAnim);
    }

    private void ScrollPosition(double travelled)
    {
        if (m_scroll == null || m_scrollWidth <= 0)
            return;
        // Direction 1 moves the picture to the right: the walking character in front of it faces
        // left (the opposite looks like walking backwards)
        double offset = travelled % m_scrollWidth;
        double x = m_scrollDirection > 0 ? offset - m_scrollWidth : -offset;
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

    /// <summary>Clears everything (a new scenario file or chapter starts).</summary>
    public void Reset()
    {
        foreach (var anim in m_running.ToList())
            Complete(anim);
        m_queued.Clear();
        StopMovie();
        ScrollStop();
        foreach (var plane in m_planes.Values)
            m_content.Children.Remove(plane.Element);
        m_planes.Clear();
        m_frozen = false;
        HideOverlay();
        m_flash.Opacity = 0;
        m_shakeOffset.X = m_shakeOffset.Y = 0;
    }
}
