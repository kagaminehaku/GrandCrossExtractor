// Save and load pages, drawn with the game's own SAVE / LOAD graphics (SYSTEM.S25 slots 2010 /
// 2011, page tabs 2030 + 10 per page, back button 2170): ten slots a page in two columns of five.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GrandCrossExtractor.Formats;

namespace GrandCrossExtractor.Player;

public partial class PlayerWindow
{
    private SaveStore? m_saves;
    private SaveStore Saves => m_saves ??= new SaveStore(m_data.SchemeName);

    private int m_savePage;
    private bool m_saving;
    private BitmapSource? m_pendingThumbnail;

    private bool CanSave => IsRunning && ChoiceLayer.Visibility != Visibility.Visible && m_file.Length > 0;

    /// <summary>Opens the SAVE page (<paramref name="saving"/>) or the LOAD page.</summary>
    private void ShowSavePage(bool saving)
    {
        if (saving && !CanSave)
            return;
        m_saving = saving;
        // The thumbnail shows the screen as it is now, before the page covers it
        if (saving)
            m_pendingThumbnail = StageThumbnail();
        m_stage.PauseMovie(true);
        BuildSavePage();
        SaveLayer.Visibility = Visibility.Visible;
    }

    private void HideSavePage()
    {
        SaveLayer.Visibility = Visibility.Collapsed;
        SaveLayer.Children.Clear();
        m_stage.PauseMovie(false);
        Focus();
    }

    private BitmapSource StageThumbnail()
    {
        var full = new RenderTargetBitmap(Stage.Width, Stage.Height, 96, 96, PixelFormats.Pbgra32);
        full.Render(StageCanvas);
        var small = new TransformedBitmap(full, new ScaleTransform(100.0 / Stage.Width, 75.0 / Stage.Height));
        var copy = new WriteableBitmap(small);
        copy.Freeze();
        return copy;
    }

    private void BuildSavePage()
    {
        SaveLayer.Children.Clear();
        if (m_data.GetSystemFrame(m_saving ? 2010 : 2011) is { } page)
            AddFrame(page);

        // Page tabs: slot 2030 + 10 per page; +2 = the current page, +1 = highlighted
        for (int p = 0; p < SaveStore.Pages; p++)
        {
            int number = p;
            var normal = m_data.GetSystemFrame(2030 + p * 10);
            if (normal == null)
                continue;
            var current = m_data.GetSystemFrame(2032 + p * 10) ?? normal;
            var hot = m_data.GetSystemFrame(2031 + p * 10) ?? normal;
            AddButton(p == m_savePage ? current : normal, p == m_savePage ? current : hot, () =>
            {
                m_savePage = number;
                BuildSavePage();
            });
        }
        if (m_data.GetSystemFrame(2170) is { } back)
            AddButton(back, m_data.GetSystemFrame(2171) ?? back, HideSavePage);

        for (int k = 0; k < SaveStore.SlotsPerPage; k++)
        {
            int slot = m_savePage * SaveStore.SlotsPerPage + k;
            double x = 63 + 352 * (k / 5), y = 62 + 101 * (k % 5);
            var data = Saves.Read(slot);
            var cell = new Canvas { Width = 300, Height = 82, Background = Brushes.Transparent };
            Canvas.SetLeft(cell, x);
            Canvas.SetTop(cell, y);
            if (data != null && Saves.Thumbnail(slot) is { } thumbnail)
            {
                var image = new Image { Source = thumbnail, Width = 100, Height = 75, Stretch = Stretch.Fill };
                Canvas.SetLeft(image, 2);
                Canvas.SetTop(image, 2);
                cell.Children.Add(image);
            }
            if (data != null)
            {
                cell.Children.Add(SlotText(data.Time.ToString("yyyy/MM/dd/HH:mm"), 132, 8, 15));
                string scene = ScenarioScript.DisplayText(data.Text);
                if (scene.Length > 15)
                    scene = scene[..15] + "…";
                cell.Children.Add(SlotText(scene, 132, 38, 14));
            }
            bool usable = m_saving || data != null;
            if (usable)
            {
                cell.Cursor = Cursors.Hand;
                cell.MouseEnter += (_, _) => cell.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
                cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
                cell.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SlotClicked(slot, data);
                };
            }
            SaveLayer.Children.Add(cell);
        }
    }

    private static TextBlock SlotText(string text, double x, double y, double size)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, Foreground = Brushes.White, IsHitTestVisible = false,
            FontFamily = new FontFamily("MS Gothic, ＭＳ ゴシック, Yu Gothic"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x80, 0x20, 0x40), BlurRadius = 0, ShadowDepth = 1 },
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        return block;
    }

    private void SlotClicked(int slot, SaveData? data)
    {
        if (m_saving)
        {
            if (data != null && MessageBox.Show(this, "Overwrite this save?", "Save", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            var save = new SaveData(m_file, Math.Max(0, m_messageIndex - 1), m_played, m_lastText, DateTime.Now);
            try
            {
                Saves.Write(slot, save, m_pendingThumbnail ?? StageThumbnail());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save:\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            BuildSavePage();
            return;
        }
        if (data == null)
            return;
        HideSavePage();
        Start(data.File, data.Played, data.Message);
    }

    private void AddFrame(S25Frame frame)
    {
        var image = new Image { Source = frame.Image, Width = frame.Width, Height = frame.Height, Stretch = Stretch.Fill };
        Canvas.SetLeft(image, frame.OffsetX);
        Canvas.SetTop(image, frame.OffsetY);
        SaveLayer.Children.Add(image);
    }

    private void AddButton(S25Frame normal, S25Frame hot, Action action)
    {
        var image = new Image { Stretch = Stretch.Fill, Cursor = Cursors.Hand };
        void Show(S25Frame f)
        {
            image.Source = f.Image;
            image.Width = f.Width;
            image.Height = f.Height;
            Canvas.SetLeft(image, f.OffsetX);
            Canvas.SetTop(image, f.OffsetY);
        }
        Show(normal);
        image.MouseEnter += (_, _) => Show(hot);
        image.MouseLeave += (_, _) => Show(normal);
        image.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        SaveLayer.Children.Add(image);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => ShowSavePage(saving: true);

    private void BtnLoad_Click(object sender, RoutedEventArgs e) => ShowSavePage(saving: false);
}
