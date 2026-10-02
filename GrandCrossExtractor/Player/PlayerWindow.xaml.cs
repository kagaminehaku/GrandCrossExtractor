// Story player window: shows a game's scenario the way the engine does (pictures, text,
// voice, music, choices), reading everything from the game's own archives.
// The script interpreter is in PlayerWindow.Script.cs.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GrandCrossExtractor.Formats;

namespace GrandCrossExtractor.Player;

public partial class PlayerWindow : Window
{
    private readonly GameData m_data;
    private readonly StoryFlow m_flow;
    private readonly Stage m_stage;
    private readonly AudioEngine m_audio;

    private CancellationTokenSource? m_run;
    private CancellationToken m_token;

    // Modes
    private bool m_auto;
    private bool m_skip;
    private bool m_ctrlHeld;
    private bool Skipping => m_skip || m_ctrlHeld;

    // A click (or Enter, Space, ...) the script is waiting for
    private TaskCompletionSource? m_click;

    // The message window was hidden by the user (right click); the next click shows it again
    private bool m_userHidWindow;

    public sealed record LogEntry(string Speaker, string Text, string? Voice)
    {
        public Visibility SpeakerVisibility => Speaker.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility VoiceVisibility => Voice != null ? Visibility.Visible : Visibility.Hidden;
    }

    private readonly List<LogEntry> m_log = new();
    private const int LogLimit = 2000;

    public PlayerWindow(GameData data, StoryFlow flow)
    {
        InitializeComponent();
        m_data = data;
        m_flow = flow;
        m_stage = new Stage(StageCanvas);
        m_audio = new AudioEngine();

        Title = $"Play Story - {flow.Title}";
        TxtTitle.Text = flow.Title;
        TxtSubtitle.Text = "Reads the game's own scenario files. Character animations, screen effects and some transitions " +
                           "are approximations of the engine's. Saving is not supported: use the chapter list to come back.";

        var chapters = new ListCollectionView(flow.GetChapters(data.ScriptTitle).ToList());
        chapters.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StoryFlow.Chapter.Group)));
        ChapterList.ItemsSource = chapters;

        var window = data.GetSystemFrame(0);
        if (window != null)
        {
            ImgWindow.Source = window.Image;
            Canvas.SetLeft(ImgWindow, window.OffsetX);
            Canvas.SetTop(ImgWindow, window.OffsetY);
            ImgWindow.Width = window.Width;
            ImgWindow.Height = window.Height;
        }
        else
        {
            WindowFallback.Visibility = Visibility.Visible;
        }
        UpdateModeButtons();
    }

    #region Menu

    private bool IsRunning => m_run != null;

    private void ShowMenu()
    {
        BtnResume.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        BtnStart.Content = IsRunning ? "↺ Start from the beginning" : "▶ Start from the beginning";
        MenuLayer.Visibility = Visibility.Visible;
        m_stage.PauseMovie(true);
    }

    private void HideMenu()
    {
        MenuLayer.Visibility = Visibility.Collapsed;
        m_stage.PauseMovie(false);
        Focus();
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e) => Start(null);

    private void BtnResume_Click(object sender, RoutedEventArgs e) => HideMenu();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuLayer.Visibility == Visibility.Visible && IsRunning)
            HideMenu();
        else
            ShowMenu();
    }

    private void ChapterList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChapterList.SelectedItem is StoryFlow.Chapter chapter)
            Start(chapter.File);
    }

    /// <summary>Plays the story from a scenario file (null = from the beginning), ending any run in progress.</summary>
    private async void Start(string? file)
    {
        m_run?.Cancel();
        var run = new CancellationTokenSource();
        m_run = run;
        m_token = run.Token;
        m_skip = m_auto = false;
        UpdateModeButtons();
        HideMenu();
        ResetScreen();

        try
        {
            await RunFlowAsync(file);
            if (m_run == run)
            {
                m_run = null;
                ResetScreen();
                ShowMenu();
            }
        }
        catch (OperationCanceledException)
        {
            // Another chapter was started, or the window closed
        }
        catch (Exception ex)
        {
            if (m_run == run)
            {
                m_run = null;
                ResetScreen();
                MessageBox.Show(this, $"The story stopped because of an error:\n{ex.Message}", "Play Story",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowMenu();
            }
        }
    }

    private void ResetScreen()
    {
        m_stage.Reset();
        m_audio.StopAll();
        HideMessageWindow();
        ChoiceLayer.Visibility = Visibility.Collapsed;
        ChoiceLayer.Children.Clear();
        LogLayer.Visibility = Visibility.Collapsed;
        m_userHidWindow = false;
    }

    #endregion

    #region Input

    /// <summary>Click, Enter, Space: ends animations, shows the whole message, or goes on.</summary>
    private void Advance()
    {
        if (MenuLayer.Visibility == Visibility.Visible || ChoiceLayer.Visibility == Visibility.Visible)
            return;
        if (LogLayer.Visibility == Visibility.Visible)
        {
            LogLayer.Visibility = Visibility.Collapsed;
            return;
        }
        if (m_userHidWindow)
        {
            m_userHidWindow = false;
            if (m_messageShown)
                MessageLayer.Visibility = Visibility.Visible;
            return;
        }
        m_click?.TrySetResult();
    }

    private Task NextClick()
    {
        if (m_click == null || m_click.Task.IsCompleted)
            m_click = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return m_click.Task;
    }

    private void Screen_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Advance();

    private void Screen_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (LogLayer.Visibility == Visibility.Visible)
            LogLayer.Visibility = Visibility.Collapsed;
        else
            ToggleHideWindow();
    }

    private void ToggleHideWindow()
    {
        if (MenuLayer.Visibility == Visibility.Visible || ChoiceLayer.Visibility == Visibility.Visible)
            return;
        if (m_userHidWindow)
        {
            Advance();
            return;
        }
        if (!m_messageShown)
            return;
        m_userHidWindow = true;
        MessageLayer.Visibility = Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Alt) != 0:
            case Key.F11:
                ToggleFullScreen();
                break;
            case Key.Enter:
            case Key.Space:
            case Key.PageDown:
                if (MenuLayer.Visibility == Visibility.Visible && key == Key.Enter && ChapterList.SelectedItem is StoryFlow.Chapter chapter)
                    Start(chapter.File);
                else
                    Advance();
                break;
            case Key.LeftCtrl:
            case Key.RightCtrl:
                if (!m_ctrlHeld)
                {
                    m_ctrlHeld = true;
                    UpdateModeButtons();
                    m_stage.FinishAll();
                    m_click?.TrySetResult();
                }
                break;
            case Key.A:
                ToggleAuto();
                break;
            case Key.S:
                ToggleSkip();
                break;
            case Key.H:
            case Key.Delete:
                ToggleHideWindow();
                break;
            case Key.L:
            case Key.PageUp:
                ShowLog();
                break;
            case Key.Escape:
                if (LogLayer.Visibility == Visibility.Visible)
                    LogLayer.Visibility = Visibility.Collapsed;
                else if (WindowStyle == WindowStyle.None)
                    ToggleFullScreen();
                else if (MenuLayer.Visibility == Visibility.Visible)
                {
                    if (IsRunning)
                        HideMenu();
                }
                else
                    ShowMenu();
                break;
            default:
                return;
        }
        // Keep the chapter list's own keyboard navigation
        if (MenuLayer.Visibility != Visibility.Visible || key is Key.Escape or Key.F11 or Key.Enter)
            e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            m_ctrlHeld = false;
            UpdateModeButtons();
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MenuLayer.Visibility == Visibility.Visible)
            return;
        if (LogLayer.Visibility == Visibility.Visible)
        {
            // Scrolling down past the newest line closes the log
            if (e.Delta < 0 && LogScroll.VerticalOffset >= LogScroll.ScrollableHeight)
            {
                LogLayer.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }
        if (e.Delta > 0)
            ShowLog();
        else
            Advance();
        e.Handled = true;
    }

    private void ToolBar_MouseEnter(object sender, MouseEventArgs e) => ToolBar.Opacity = 1;

    private void ToolBar_MouseLeave(object sender, MouseEventArgs e) => ToolBar.Opacity = 0;

    // A click on the tool bar is not a click on the story
    private void ToolBar_MouseButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void BtnAuto_Click(object sender, RoutedEventArgs e) => ToggleAuto();

    private void BtnSkip_Click(object sender, RoutedEventArgs e) => ToggleSkip();

    private void BtnLog_Click(object sender, RoutedEventArgs e) => ShowLog();

    private void BtnHide_Click(object sender, RoutedEventArgs e) => ToggleHideWindow();

    private void BtnFullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleAuto()
    {
        m_auto = !m_auto;
        if (m_auto)
            m_skip = false;
        UpdateModeButtons();
        m_modeChanged?.TrySetResult();
    }

    private void ToggleSkip()
    {
        m_skip = !m_skip;
        if (m_skip)
        {
            m_auto = false;
            m_stage.FinishAll();
            m_click?.TrySetResult();
        }
        UpdateModeButtons();
    }

    // Wakes a message that waits in auto mode when the mode changes
    private TaskCompletionSource? m_modeChanged;

    private void UpdateModeButtons()
    {
        BtnAuto.Tag = m_auto ? "On" : null;
        BtnSkip.Tag = Skipping ? "On" : null;
        TxtMode.Text = Skipping ? "SKIP ▶▶" : m_auto ? "AUTO ▶" : "";
    }

    private WindowState m_restoreState;

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = m_restoreState;
        }
        else
        {
            m_restoreState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Re-enter maximized so the window covers the task bar
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
    }

    #endregion

    #region Message window

    private bool m_messageShown;

    private void ShowMessageWindow()
    {
        m_messageShown = true;
        if (!m_userHidWindow)
            MessageLayer.Visibility = Visibility.Visible;
    }

    private void HideMessageWindow()
    {
        m_messageShown = false;
        MessageLayer.Visibility = Visibility.Collapsed;
        TxtNext.Visibility = Visibility.Collapsed;
    }

    private void SetSpeaker(string? speaker)
    {
        var plate = speaker != null ? m_data.GetNamePlate(speaker) : null;
        if (plate != null)
        {
            ImgNamePlate.Source = plate.Image;
            ImgNamePlate.Width = plate.Width;
            ImgNamePlate.Height = plate.Height;
            Canvas.SetLeft(ImgNamePlate, plate.OffsetX);
            Canvas.SetTop(ImgNamePlate, plate.OffsetY);
            ImgNamePlate.Visibility = Visibility.Visible;
            TxtName.Text = "";
        }
        else
        {
            ImgNamePlate.Visibility = Visibility.Collapsed;
            TxtName.Text = speaker ?? "";
        }
    }

    /// <summary>Shows the first <paramref name="shown"/> characters; the rest keeps its place, invisible.</summary>
    private void SetMessageText(string text, int shown)
    {
        TxtMessage.Inlines.Clear();
        shown = Math.Clamp(shown, 0, text.Length);
        TxtMessage.Inlines.Add(new Run(text[..shown]));
        if (shown < text.Length)
            TxtMessage.Inlines.Add(new Run(text[shown..]) { Foreground = Brushes.Transparent });
    }

    #endregion

    #region Choices

    /// <summary>
    /// Shows a choice and waits for the answer. With <paramref name="imageSlot"/> the options are
    /// the game's own buttons (SYSTEM.S25 slot + 10 per option, +1 when highlighted); options
    /// whose bit is set in <paramref name="hidden"/> are left out. Returns the option's index.
    /// </summary>
    private async Task<int> ChooseAsync(IReadOnlyList<string> options, int imageSlot = -1, int hidden = 0)
    {
        m_skip = false;
        UpdateModeButtons();
        HideMessageWindow();
        m_stage.FinishAll();

        var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChoiceLayer.Children.Clear();
        var shown = Enumerable.Range(0, options.Count).Where(i => (hidden & (1 << i)) == 0).ToList();

        bool images = imageSlot >= 0 && shown.All(i => m_data.GetSystemFrame(imageSlot + i * 10) != null);
        var plain = m_data.GetSystemFrame(311);
        var plainHot = m_data.GetSystemFrame(312) ?? plain;

        for (int n = 0; n < shown.Count; n++)
        {
            int index = shown[n];
            FrameworkElement element;
            double width, height;
            if (images)
            {
                var normal = m_data.GetSystemFrame(imageSlot + index * 10)!;
                var hot = m_data.GetSystemFrame(imageSlot + index * 10 + 1) ?? normal;
                element = HoverImage(normal.Image, hot.Image);
                width = normal.Width;
                height = normal.Height;
            }
            else if (plain != null)
            {
                var grid = new Grid { Width = plain.Width, Height = plain.Height };
                grid.Children.Add(HoverImage(plain.Image, plainHot!.Image));
                grid.Children.Add(new TextBlock
                {
                    Text = options[index], FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    FontFamily = new FontFamily("Meiryo, Yu Gothic UI, MS Gothic"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x20, 0x30, 0x50), BlurRadius = 4, ShadowDepth = 1.5 },
                });
                element = grid;
                width = plain.Width;
                height = plain.Height;
            }
            else
            {
                element = new Button { Content = options[index], FontSize = 22, Width = 520, Height = 64 };
                width = 520;
                height = 64;
            }

            // Two columns of picture buttons, one column of text buttons
            int columns = images && shown.Count > 4 ? 2 : 1;
            int rows = (shown.Count + columns - 1) / columns;
            double gap = 10;
            double top = Math.Max(10, (Stage.Height - 20 - rows * (height + gap)) / 2);
            double left = (Stage.Width - columns * width - (columns - 1) * gap) / 2;
            Canvas.SetLeft(element, left + (n % columns) * (width + gap));
            Canvas.SetTop(element, top + (n / columns) * (height + gap));
            element.Cursor = Cursors.Hand;
            element.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                answer.TrySetResult(index);
            };
            if (element is Button button)
                button.Click += (_, _) => answer.TrySetResult(index);
            ChoiceLayer.Children.Add(element);
        }

        ChoiceLayer.Visibility = Visibility.Visible;
        try
        {
            int result = await answer.Task.WaitAsync(m_token);
            AddLog("", $"⇒ {options[result]}", null);
            return result;
        }
        finally
        {
            ChoiceLayer.Visibility = Visibility.Collapsed;
            ChoiceLayer.Children.Clear();
        }
    }

    private static Image HoverImage(ImageSource normal, ImageSource hot)
    {
        var image = new Image { Source = normal, Stretch = Stretch.Fill };
        image.MouseEnter += (_, _) => image.Source = hot;
        image.MouseLeave += (_, _) => image.Source = normal;
        return image;
    }

    #endregion

    #region Backlog

    private void AddLog(string speaker, string text, string? voice)
    {
        m_log.Add(new LogEntry(speaker, text, voice));
        if (m_log.Count > LogLimit)
            m_log.RemoveRange(0, m_log.Count - LogLimit);
    }

    private void ShowLog()
    {
        if (MenuLayer.Visibility == Visibility.Visible || ChoiceLayer.Visibility == Visibility.Visible || m_log.Count == 0)
            return;
        LogList.ItemsSource = null;
        LogList.ItemsSource = m_log.ToList();
        LogLayer.Visibility = Visibility.Visible;
        LogScroll.UpdateLayout();
        LogScroll.ScrollToEnd();
    }

    private void BtnCloseLog_Click(object sender, RoutedEventArgs e) => LogLayer.Visibility = Visibility.Collapsed;

    private void BtnLogVoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string voice } && m_data.ReadAudio(voice) is { } audio)
            m_audio.Play(AudioEngine.Voice, audio, 1);
    }

    #endregion

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        m_run?.Cancel();
        m_run = null;
        m_stage.Reset();
        m_audio.Dispose();
        m_data.Dispose();
        base.OnClosing(e);
    }
}
