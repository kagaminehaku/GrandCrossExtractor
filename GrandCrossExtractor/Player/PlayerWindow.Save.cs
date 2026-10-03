// Save and load pages, drawn with the game's own SAVE / LOAD graphics (SYSTEM.S25 slots 2010 /
// 2011, page tabs 2030 + 10 per page, back button 2170): ten slots a page in two columns of five,
// numbered by 2300 + page (the AUTO page: AUTO1-9 and QUICK), the gold frame 2230 on the slot
// under the mouse and NEW (2220) on the slot saved last. Thumbnails follow START.SCN 2CBF4.

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
            m_pendingThumbnail = SaveThumbnail();
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

    /// <summary>
    /// The thumbnail the game stores with a save (START.SCN 2CBF4): the fixed THSAVE.S25 picture
    /// of the topmost plane that shows an event CG of its list (on plane 0 a playing movie counts
    /// instead), with the overlay pictures of that plane and the planes above drawn at their
    /// positions; a scaled screenshot when no plane has one.
    /// </summary>
    private BitmapSource SaveThumbnail()
    {
        var tables = m_data.GetThumbnailTables();
        var frames = tables == null ? null : m_data.LoadFrames("d\\thsave.s25")?.ToDictionary(f => f.Slot);
        if (tables == null || frames == null)
            return StageThumbnail();

        var planes = m_stage.PictureNames().Where(p => p.Number is >= 0 and <= 9).OrderByDescending(p => p.Number).ToList();
        S25Frame? picture = null;
        int found = -1;
        foreach (var plane in planes)
        {
            int index;
            if (plane.Number == 0 && m_stage.MovieName is { } movie)
                index = Array.IndexOf(tables.Movies, movie) is var m and >= 0 ? 3000 + m : -1;
            else
                index = Array.IndexOf(tables.Pictures, plane.Name);
            if (index >= 0 && frames.TryGetValue(index, out picture))
            {
                found = plane.Number;
                break;
            }
        }
        if (picture == null)
            return StageThumbnail();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(picture.Image, new Rect(picture.OffsetX, picture.OffsetY, picture.Width, picture.Height));
            foreach (var plane in planes.Where(p => p.Number >= found).OrderBy(p => p.Number))
            {
                int index = Array.IndexOf(tables.Overlays, plane.Name);
                if (index >= 0 && frames.TryGetValue(2000 + index, out var overlay))
                    dc.DrawImage(overlay.Image, new Rect((int)plane.X * 100 / Stage.Width + overlay.OffsetX,
                        (int)plane.Y * 75 / Stage.Height + overlay.OffsetY, overlay.Width, overlay.Height));
            }
        }
        var bitmap = new RenderTargetBitmap(100, 75, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
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
        if (m_data.GetSystemFrame(2300 + m_savePage) is { } numbers)
            AddFrame(numbers);

        // NEW marks the slot saved last from this page (auto and quick saves do not count)
        var saves = Enumerable.Range(0, SaveStore.Pages * SaveStore.SlotsPerPage).Select(Saves.Read).ToList();
        int newest = -1;
        for (int slot = 0; slot < SaveStore.FirstAuto; slot++)
            if (saves[slot] is { } save && (newest < 0 || save.Time > saves[newest]!.Time))
                newest = slot;

        var gold = m_data.GetSystemFrame(2230);
        var marker = m_data.GetSystemFrame(2220);
        for (int k = 0; k < SaveStore.SlotsPerPage; k++)
        {
            int slot = m_savePage * SaveStore.SlotsPerPage + k;
            // Slot k: the hit box 2200 (300 x 88 at 57,57) moved 352 px per column, 101 px per row
            var data = saves[slot];
            var cell = new Canvas { Width = 300, Height = 88, Background = Brushes.Transparent };
            Canvas.SetLeft(cell, 57 + 352 * (k / 5));
            Canvas.SetTop(cell, 57 + 101 * (k % 5));
            if (data != null && Saves.Thumbnail(slot) is { } thumbnail)
            {
                var image = new Image { Source = thumbnail, Width = 100, Height = 75, Stretch = Stretch.Fill };
                Canvas.SetLeft(image, 8);
                Canvas.SetTop(image, 7);
                cell.Children.Add(image);
            }
            if (data != null)
            {
                cell.Children.Add(SlotText(data.Time.ToString("yyyy/MM/dd/HH:mm"), 138, 13, 15));
                cell.Children.Add(SlotText(SlotCaption(data.Text), 138, 43, 14));
            }
            if (slot == newest && marker != null)
                cell.Children.Add(SlotPicture(marker));
            if (m_saving || data != null)
            {
                cell.Cursor = Cursors.Hand;
                var frame = gold == null ? null : SlotPicture(gold);
                cell.MouseEnter += (_, _) =>
                {
                    if (frame != null && !cell.Children.Contains(frame))
                        cell.Children.Add(frame);
                };
                cell.MouseLeave += (_, _) => cell.Children.Remove(frame);
                cell.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SlotClicked(slot, data);
                };
            }
            SaveLayer.Children.Add(cell);
        }
    }

    /// <summary>A SYSTEM.S25 picture placed for slot 0 (hit box at 57,57), put in a slot cell.</summary>
    private static Image SlotPicture(S25Frame frame)
    {
        var image = new Image { Source = frame.Image, Width = frame.Width, Height = frame.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        Canvas.SetLeft(image, frame.OffsetX - 57);
        Canvas.SetTop(image, frame.OffsetY - 57);
        return image;
    }

    /// <summary>The message stored with a save: its first 22 bytes (11 full-width characters), then "..." (START.SCN 2C7EB).</summary>
    private static string SlotCaption(string text)
    {
        string shown = ScenarioScript.DisplayText(text);
        int bytes = 0;
        for (int i = 0; i < shown.Length; i++)
        {
            bytes += shown[i] < 0x80 || shown[i] is >= '｡' and <= 'ﾟ' ? 1 : 2;
            if (bytes > 22)
                return shown[..i] + "...";
        }
        return shown;
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
            var save = NewSave(Math.Max(0, m_messageIndex - 1), m_lastText);
            try
            {
                Saves.Write(slot, save, m_pendingThumbnail ?? SaveThumbnail());
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
        Load(data);
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

    /// <summary>Where the story is now, with the flow script's variables.</summary>
    private SaveData NewSave(int message, string text) =>
        new(m_file, message, m_machine?.GetA(PlayedVar) ?? m_played, text, DateTime.Now) { Vars = m_machine?.Snapshot(), Line = m_messageLine };

    private void Load(SaveData data) => Start(data.File, data.Played, data.Line >= 0 ? data.Line : data.Message, data.Vars, data.Line >= 0);

    /// <summary>Auto save at the first message of a scenario file (START.SCN function 197).</summary>
    private void AutoSave(int message, string text)
    {
        try
        {
            Saves.WriteAuto(NewSave(message, text), SaveThumbnail());
        }
        catch
        {
            // The story goes on without the auto save
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => ShowSavePage(saving: true);

    private void BtnLoad_Click(object sender, RoutedEventArgs e) => ShowSavePage(saving: false);
}
