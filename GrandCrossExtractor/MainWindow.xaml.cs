// Interaction logic for MainWindow.xaml
// Supports browsing, extracting, and previewing resources from GRAND†CROSS games

using System.ComponentModel;
using System.IO;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GrandCrossExtractor.Core;
using GrandCrossExtractor.Formats;
using GrandCrossExtractor.Player;
using GrandCrossExtractor.UI;

namespace GrandCrossExtractor;

public partial class MainWindow : Window
{
    private WarcArchive? m_currentArchive;
    private List<Entry> m_allEntries = new();
    private List<Entry> m_filteredEntries = new();
    private byte[]? m_currentRawData;
    private byte[]? m_currentAudioWav;
    private SoundPlayer? m_soundPlayer;
    private List<S25Frame>? m_currentS25Frames;
    private int m_currentFrameIndex = 0;
    private string? m_currentArchivePath;

    // Frames of a standalone .S25 file opened directly (no WAR archive involved)
    private List<S25Frame>? m_standaloneFrames;

    public MainWindow()
    {
        InitializeComponent();
        InitializeSchemes();
        Loaded += (_, _) => ReportSchemeLoadErrors();
    }

    private void ReportSchemeLoadErrors()
    {
        var fm = FormatManager.Instance;
        if (fm.LoadErrors.Count == 0)
            return;
        MessageBox.Show(this,
            "Some encryption schemes could not be loaded:\n\n• " + string.Join("\n• ", fm.LoadErrors) +
            $"\n\nThe application needs {FormatManager.SchemeFileName} and the ShiinaImage folder " +
            "(Common.bin and the per-game .tail files) next to its executable. " +
            "Restore them from the release package or rebuild the project.",
            "Scheme Data Problem", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void InitializeSchemes()
    {
        CmbScheme.Items.Clear();
        CmbScheme.Items.Add("Auto Detect");

        // Target Grand†CROSS Games (only those whose scheme data actually loaded)
        foreach (var game in FormatManager.GrandCrossGames)
        {
            if (FormatManager.Instance.GetScheme(game) != null)
                CmbScheme.Items.Add($"GRAND†CROSS: {game}");
        }

        // Other ShiinaRio schemes
        foreach (var s in FormatManager.Instance.KnownSchemes)
        {
            if (!FormatManager.GrandCrossGames.Contains(s.Name))
            {
                CmbScheme.Items.Add(s.Name);
            }
        }

        CmbScheme.SelectedIndex = 0;
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open GRAND†CROSS / ShiinaRio Archive",
            Filter = "ShiinaRio Archives (*.war;*.warc;*.s25)|*.war;*.warc;*.s25|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenArchive(dialog.FileName);
        }
    }

    private void OpenArchive(string path)
    {
        try
        {
            StopAudio();
            m_currentArchive?.Dispose();
            m_currentArchive = null;
            m_currentArchivePath = null;
            m_standaloneFrames = null;

            m_lastAttemptedPath = path;
            var arcView = new ArcView(path);

            EncryptionScheme? selectedScheme = GetSelectedScheme();

            // Try open as WAR / WARC
            var warc = WarcOpener.TryOpen(arcView, selectedScheme);
            if (warc != null)
            {
                m_currentArchive = warc;
                m_allEntries = warc.Entries;
                m_currentArchivePath = path;

                TxtStatusArchive.Text = $"Archive: {Path.GetFileName(path)}";
                TxtStatusScheme.Text = $"Scheme: {warc.SchemeName}";

                long totalUnpacked = m_allEntries.Sum(x => (long)x.UnpackedSize);
                TxtStatusTotalSize.Text = $"Total: {FileSizeConverter.FormatBytes(totalUnpacked)}";

                BtnExtractSelected.IsEnabled = true;
                BtnExtractAll.IsEnabled = true;
                BtnPlayStory.IsEnabled = StoryFlow.For(warc.SchemeName) != null;

                ApplyFilter();
                return;
            }
            BtnPlayStory.IsEnabled = false;

            // Check if it's a standalone S25 file
            byte[] header = arcView.View.ReadBytes(0, 8);
            if (S25Decoder.IsS25(header))
            {
                byte[] s25Bytes = arcView.View.ReadBytes(0, (uint)arcView.MaxOffset);
                arcView.Dispose();
                var frames = S25Decoder.DecodeAllFrames(s25Bytes);
                if (frames.Count > 0)
                {
                    m_currentArchivePath = path;
                    m_standaloneFrames = frames;
                    m_allEntries = new List<Entry>();
                    for (int i = 0; i < frames.Count; i++)
                    {
                        m_allEntries.Add(new Entry
                        {
                            Name = $"{Path.GetFileNameWithoutExtension(path)}@{frames[i].Index:D4}.png",
                            Type = "image",
                            Offset = i, // index into m_standaloneFrames
                            Size = (uint)s25Bytes.Length,
                            UnpackedSize = frames[i].Width * frames[i].Height * 4
                        });
                    }

                    TxtStatusArchive.Text = $"S25 Image: {Path.GetFileName(path)}";
                    TxtStatusScheme.Text = "S25 Multi-Frame Image";
                    BtnExtractSelected.IsEnabled = true;
                    BtnExtractAll.IsEnabled = true;
                    ApplyFilter();
                    return;
                }
            }

            bool isWarc = WarcOpener.IsWarc(arcView);
            arcView.Dispose();
            if (!isWarc)
            {
                MessageBox.Show(this, "This file is not a GRAND†CROSS .WAR archive or a valid .S25 image.",
                    "Unsupported File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string hint = selectedScheme == null
                ? "The game could not be detected automatically (no known game .exe next to the archive and no matching file name).\n" +
                  "Select the game in the Scheme dropdown and the archive will be reopened."
                : $"It could not be decrypted with the '{selectedScheme.Name}' scheme. Try another game in the Scheme dropdown.";
            MessageBox.Show(this, "Could not open this archive.\n\n" + hint,
                "Unsupported or Encrypted Archive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error opening archive:\n{ex.Message}", "Open Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private EncryptionScheme? GetSelectedScheme()
    {
        if (CmbScheme.SelectedIndex <= 0) return null; // Auto Detect
        string? text = CmbScheme.SelectedItem as string;
        if (string.IsNullOrEmpty(text)) return null;

        if (text.StartsWith("GRAND†CROSS: "))
            text = text.Substring("GRAND†CROSS: ".Length);

        return FormatManager.Instance.GetScheme(text);
    }

    // Last archive the user tried to open, even if opening failed, so picking a scheme retries it
    private string? m_lastAttemptedPath;

    private void CmbScheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Reopen the current (or last failed) archive with the newly selected scheme
        string? path = m_currentArchivePath ?? m_lastAttemptedPath;
        if (path != null && IsLoaded && m_standaloneFrames == null)
        {
            OpenArchive(path);
        }
    }

    private void FilterType_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || FileListView == null || TxtSearch == null) return;
        ApplyFilter();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || FileListView == null || TxtSearch == null) return;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (m_allEntries == null || FileListView == null || TxtSearch == null) return;

        string search = TxtSearch.Text.Trim();
        bool filterImages = FilterImages?.IsChecked == true;
        bool filterAudio = FilterAudio?.IsChecked == true;
        bool filterScripts = FilterScripts?.IsChecked == true;

        m_filteredEntries = m_allEntries.Where(entry =>
        {
            if (!string.IsNullOrEmpty(search) && !entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                return false;

            if (filterImages && entry.Type != "image") return false;
            if (filterAudio && entry.Type != "audio") return false;
            if (filterScripts && entry.Type != "text") return false;

            return true;
        }).ToList();

        FileListView.ItemsSource = m_filteredEntries;
        if (TxtStatusCount != null)
            TxtStatusCount.Text = $"{m_filteredEntries.Count} of {m_allEntries.Count} items";
    }

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileListView.SelectedItem is Entry entry)
        {
            LoadPreview(entry);
            BtnExtractCurrent.IsEnabled = true;
        }
        else
        {
            ClearPreview();
            BtnExtractCurrent.IsEnabled = false;
        }
    }

    private void FileListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is Entry entry)
        {
            ExtractSingleEntry(entry);
        }
    }

    private void ClearPreview()
    {
        StopAudio();
        PanelWelcome.Visibility = Visibility.Visible;
        PanelImagePreview.Visibility = Visibility.Collapsed;
        PanelAudioPreview.Visibility = Visibility.Collapsed;
        PanelTextPreview.Visibility = Visibility.Collapsed;
        PanelHexPreview.Visibility = Visibility.Collapsed;
        PanelFrameSelector.Visibility = Visibility.Collapsed;
        TxtPreviewName.Text = "No file selected";
        TxtPreviewDetails.Text = "Select an entry from the list to preview";
        m_currentRawData = null;
        m_currentAudioWav = null;
        m_currentS25Frames = null;
    }

    private void LoadPreview(Entry entry)
    {
        try
        {
            ClearPreview();
            PanelWelcome.Visibility = Visibility.Collapsed;

            TxtPreviewName.Text = entry.Name;
            TxtPreviewDetails.Text = $"Size: {FileSizeConverter.FormatBytes(entry.UnpackedSize)} | Type: {entry.Type}";

            if (m_standaloneFrames != null)
            {
                m_currentS25Frames = m_standaloneFrames;
                m_currentFrameIndex = (int)entry.Offset;
                ShowS25Frame(m_currentFrameIndex);
                PanelImagePreview.Visibility = Visibility.Visible;
                return;
            }

            byte[] data;
            if (m_currentArchive != null)
            {
                data = WarcOpener.OpenEntry(m_currentArchive, entry);
            }
            else
            {
                return;
            }

            m_currentRawData = data;

            // 1. Image Preview
            if (entry.Type == "image" || S25Decoder.IsS25(data) || Mi4Decoder.IsMi4(data))
            {
                if (S25Decoder.IsS25(data))
                {
                    var frames = S25Decoder.DecodeAllFrames(data);
                    if (frames.Count > 0)
                    {
                        m_currentS25Frames = frames;
                        m_currentFrameIndex = 0;
                        ShowS25Frame(0);
                        if (frames.Count > 1)
                        {
                            PanelFrameSelector.Visibility = Visibility.Visible;
                            TxtFrameIndicator.Text = $"1 / {frames.Count}";
                        }
                        PanelImagePreview.Visibility = Visibility.Visible;
                        return;
                    }
                }
                else if (Mi4Decoder.IsMi4(data))
                {
                    var bmp = Mi4Decoder.Decode(data);
                    if (bmp != null)
                    {
                        ImgPreview.Source = bmp.ToBitmapSource();
                        TxtPreviewDetails.Text += $" | {bmp.Width}x{bmp.Height} px (MI4)";
                        PanelImagePreview.Visibility = Visibility.Visible;
                        return;
                    }
                }
                else
                {
                    // Standard BMP/PNG/JPG
                    try
                    {
                        using var ms = new MemoryStream(data);
                        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        if (decoder.Frames.Count > 0)
                        {
                            var frame = decoder.Frames[0];
                            ImgPreview.Source = frame;
                            TxtPreviewDetails.Text += $" | {frame.PixelWidth}x{frame.PixelHeight} px";
                            PanelImagePreview.Visibility = Visibility.Visible;
                            return;
                        }
                    }
                    catch { }
                }
            }

            // 2. Audio Preview
            if (entry.Type == "audio" || AudioDecoder.IsOgv(data) || AudioDecoder.IsPad(data))
            {
                TxtAudioTrackName.Text = entry.Name;
                if (AudioDecoder.IsPad(data))
                {
                    m_currentAudioWav = AudioDecoder.DecodePad(data);
                    TxtAudioFormat.Text = "ShiinaRio PAD Audio (Decoded to 16-bit PCM WAV)";
                }
                else if (AudioDecoder.IsOgv(data))
                {
                    byte[]? ogg = AudioDecoder.DecodeOgv(data);
                    if (ogg != null)
                    {
                        m_currentAudioWav = AudioDecoder.OggToWav(ogg);
                        TxtAudioFormat.Text = "ShiinaRio OGV Audio (Decoded to 16-bit PCM WAV)";
                    }
                }
                else if (data.Length >= 4 && data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
                {
                    m_currentAudioWav = AudioDecoder.OggToWav(data);
                    TxtAudioFormat.Text = "Ogg Vorbis Audio";
                }
                else
                {
                    m_currentAudioWav = data;
                    TxtAudioFormat.Text = "Standard Audio (WAV)";
                }

                PanelAudioPreview.Visibility = Visibility.Visible;
                return;
            }

            // 3. Text / Script Preview
            if (entry.Type == "text" || IsLikelyText(data))
            {
                string text = DecodeText(data);
                TxtPreviewContent.Text = text;
                PanelTextPreview.Visibility = Visibility.Visible;
                return;
            }

            // 4. Fallback to Hex View
            TxtHexContent.Text = GenerateHexDump(data, 1024);
            PanelHexPreview.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            TxtPreviewDetails.Text = $"Preview error: {ex.Message}";
        }
    }

    private void ShowS25Frame(int index)
    {
        if (m_currentS25Frames == null || index < 0 || index >= m_currentS25Frames.Count) return;
        var frame = m_currentS25Frames[index];
        ImgPreview.Source = frame.Image.ToBitmapSource();
        TxtPreviewDetails.Text = $"Frame {index + 1}/{m_currentS25Frames.Count} | {frame.Width}x{frame.Height} px | Offset: ({frame.OffsetX}, {frame.OffsetY})";
        TxtFrameIndicator.Text = $"{index + 1} / {m_currentS25Frames.Count}";
    }

    private void BtnPrevFrame_Click(object sender, RoutedEventArgs e)
    {
        if (m_currentS25Frames != null && m_currentFrameIndex > 0)
        {
            m_currentFrameIndex--;
            ShowS25Frame(m_currentFrameIndex);
        }
    }

    private void BtnNextFrame_Click(object sender, RoutedEventArgs e)
    {
        if (m_currentS25Frames != null && m_currentFrameIndex < m_currentS25Frames.Count - 1)
        {
            m_currentFrameIndex++;
            ShowS25Frame(m_currentFrameIndex);
        }
    }

    private void BtnAudioPlay_Click(object sender, RoutedEventArgs e)
    {
        if (m_currentAudioWav != null)
        {
            try
            {
                StopAudio();
                var ms = new MemoryStream(m_currentAudioWav);
                m_soundPlayer = new SoundPlayer(ms);
                m_soundPlayer.Play();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to play audio: {ex.Message}", "Playback Error");
            }
        }
    }

    private void BtnAudioStop_Click(object sender, RoutedEventArgs e)
    {
        StopAudio();
    }

    private void StopAudio()
    {
        if (m_soundPlayer != null)
        {
            m_soundPlayer.Stop();
            m_soundPlayer.Dispose();
            m_soundPlayer = null;
        }
    }

    private static bool IsLikelyText(byte[] data)
    {
        if (data.Length == 0) return true;
        int checkLen = Math.Min(data.Length, 512);
        int printable = 0;
        for (int i = 0; i < checkLen; i++)
        {
            byte b = data[i];
            if (b == 9 || b == 10 || b == 13 || (b >= 32 && b <= 126) || b >= 128)
                printable++;
        }
        return (double)printable / checkLen > 0.85;
    }

    private static string DecodeText(byte[] data)
    {
        // Try Shift-JIS first for Japanese visual novels
        try
        {
            return Encodings.cp932.GetString(data);
        }
        catch
        {
            return Encoding.UTF8.GetString(data);
        }
    }

    private static string GenerateHexDump(byte[] data, int maxBytes)
    {
        int length = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder();
        sb.AppendLine($"--- Displaying first {length} of {data.Length} bytes ---");
        sb.AppendLine("Offset(h)  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  Decoded Text");
        sb.AppendLine("-------------------------------------------------------------------------");

        for (int i = 0; i < length; i += 16)
        {
            sb.AppendFormat("{0:X8}   ", i);
            int rowCount = Math.Min(16, length - i);

            for (int j = 0; j < 16; j++)
            {
                if (j == 8) sb.Append(" ");
                if (j < rowCount)
                    sb.AppendFormat("{0:X2} ", data[i + j]);
                else
                    sb.Append("   ");
            }

            sb.Append(" ");
            for (int j = 0; j < rowCount; j++)
            {
                byte b = data[i + j];
                char c = (b >= 32 && b <= 126) ? (char)b : '.';
                sb.Append(c);
            }
            sb.AppendLine();
        }

        if (data.Length > length)
        {
            sb.AppendLine($"... and {data.Length - length} more bytes.");
        }

        return sb.ToString();
    }

    #region Play Story

    private async void BtnPlayStory_Click(object sender, RoutedEventArgs e)
    {
        if (m_currentArchive == null || m_currentArchivePath == null)
            return;
        var scheme = GetSelectedScheme() ?? FormatManager.Instance.GetScheme(m_currentArchive.SchemeName);
        var flow = scheme != null ? StoryFlow.For(scheme.Name) : null;
        if (scheme == null || flow == null)
        {
            MessageBox.Show(this, "Play Story does not know this game's story yet. It is available for: Oreimo Plus.",
                "Play Story", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(m_currentArchivePath))!;
        BtnPlayStory.IsEnabled = false;
        string previous = TxtStatusArchive.Text;
        TxtStatusArchive.Text = "Opening the game's archives...";
        try
        {
            var data = await Task.Run(() => GameData.Open(folder, scheme));
            new PlayerWindow(data, flow) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the game for Play Story:\n{ex.Message}", "Play Story",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TxtStatusArchive.Text = previous;
            BtnPlayStory.IsEnabled = true;
        }
    }

    #endregion

    #region Extraction

    private void BtnExtractCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is Entry entry)
        {
            ExtractSingleEntry(entry);
        }
    }

    /// <summary>
    /// Produces the file(s) to write for an entry: decrypted data, optionally converted
    /// (S25 → PNG per frame, OGV → OGG, ...), with layered S25 images composed as the
    /// <paramref name="scripts"/> show them. Files are produced lazily: write each one before
    /// taking the next. Safe to call from a worker thread.
    /// </summary>
    private static IEnumerable<(string FileName, byte[] Data)> GetOutputFiles(
        WarcArchive? archive, List<S25Frame>? standaloneFrames, Entry entry, bool convert, S25ScriptIndex? scripts)
    {
        if (standaloneFrames != null)
        {
            var frame = standaloneFrames[(int)entry.Offset];
            return new[] { (entry.Name, Bitmaps.EncodePng(frame.Image.ToBitmapSource())) };
        }
        if (archive == null)
            throw new InvalidOperationException("No archive is open.");

        byte[] data = WarcOpener.OpenEntry(archive, entry);
        return convert ? MediaConverter.Convert(entry.Name, data, scripts) : new[] { (entry.Name, data) };
    }

    // Scenario scripts of the open archive's game folder, read once per folder and scheme
    private S25ScriptIndex? m_scriptIndex;
    private string? m_scriptIndexKey;

    /// <summary>
    /// The layer combinations the game's scenario scripts show, read from the archives next to
    /// the open one (normally *_T.WAR). Null when no scripts are found there.
    /// </summary>
    private async Task<S25ScriptIndex?> GetScriptIndexAsync()
    {
        if (m_currentArchive == null || m_currentArchivePath == null)
            return null;
        var scheme = GetSelectedScheme() ?? FormatManager.Instance.GetScheme(m_currentArchive.SchemeName);
        string path = m_currentArchivePath;
        string key = $"{Path.GetDirectoryName(Path.GetFullPath(path))}|{scheme?.Name}";
        if (m_scriptIndexKey != key)
        {
            string previous = TxtStatusArchive.Text;
            TxtStatusArchive.Text = "Reading the scenario scripts...";
            m_scriptIndex = await Task.Run(() => S25ScriptIndex.FromGameFolder(path, scheme));
            m_scriptIndexKey = key;
            TxtStatusArchive.Text = previous;
        }
        return m_scriptIndex is { IsEmpty: false } ? m_scriptIndex : null;
    }

    private static readonly HashSet<string> s_reservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Reduces a name taken from an archive to a plain file name. Entry names come from the
    /// archive itself, so a crafted one such as "..\..\x.exe" or "C:\x.bat" must not choose
    /// where the file is written.
    /// </summary>
    private static string SafeFileName(string name)
    {
        name = Path.GetFileName(name.Replace('/', '\\'));
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
            name = "unnamed";
        if (s_reservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
            name = "_" + name;
        return name;
    }

    /// <summary>Full path for an extracted file, guaranteed to lie inside <paramref name="directory"/>.</summary>
    private static string SafeOutputPath(string directory, string name)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, SafeFileName(name)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Refusing to write '{name}' outside the destination folder.");
        return path;
    }

    private async void ExtractSingleEntry(Entry entry)
    {
        try
        {
            bool convert = ChkConvert.IsChecked == true;
            S25ScriptIndex? scripts = null;

            if (convert && ChkCompose.IsChecked == true && m_currentArchive != null && m_standaloneFrames == null &&
                m_currentRawData != null && S25Layout.Analyze(m_currentRawData) is { } layout)
            {
                scripts = await GetScriptIndexAsync();
                // Several composed pictures: extract them like a selection, with progress
                if (scripts != null && scripts.GetCombinations(entry.Name, layout).Count > 1)
                {
                    var ofd = new OpenFolderDialog { Title = $"Select Destination Folder for the Composed Pictures of {entry.Name}" };
                    if (ofd.ShowDialog(this) == true)
                        await ExtractEntriesAsync(new List<Entry> { entry }, ofd.FolderName);
                    return;
                }
            }

            using var files = GetOutputFiles(m_currentArchive, m_standaloneFrames, entry, convert, scripts).GetEnumerator();
            if (!files.MoveNext())
                return;
            var first = files.Current;
            bool more = files.MoveNext();

            var sfd = new SaveFileDialog
            {
                Title = more ? "Export File (all frames are saved in the chosen folder)" : "Export File",
                FileName = SafeFileName(first.FileName),
                Filter = "All Files (*.*)|*.*"
            };
            if (sfd.ShowDialog(this) != true)
                return;

            string dir = Path.GetDirectoryName(sfd.FileName)!;
            File.WriteAllBytes(sfd.FileName, first.Data);
            int count = 1;
            for (; more; more = files.MoveNext(), count++)
                File.WriteAllBytes(SafeOutputPath(dir, files.Current.FileName), files.Current.Data);

            MessageBox.Show(this, count > 1
                    ? $"Saved {count} files to:\n{dir}"
                    : $"Saved successfully to:\n{sfd.FileName}",
                "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export of '{entry.Name}' failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnExtractSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = FileListView.SelectedItems.Cast<Entry>().ToList();
        if (selected.Count == 0) return;

        var ofd = new OpenFolderDialog
        {
            Title = "Select Destination Folder for Selected Files"
        };

        if (ofd.ShowDialog(this) == true)
        {
            await ExtractEntriesAsync(selected, ofd.FolderName);
        }
    }

    private async void BtnExtractAll_Click(object sender, RoutedEventArgs e)
    {
        if (m_allEntries.Count == 0) return;

        var ofd = new OpenFolderDialog
        {
            Title = "Select Destination Folder for All Files"
        };

        if (ofd.ShowDialog(this) == true)
        {
            await ExtractEntriesAsync(m_allEntries, ofd.FolderName);
        }
    }

    private bool m_extracting;

    private async Task ExtractEntriesAsync(List<Entry> entries, string destinationDir)
    {
        if (m_currentArchive == null && m_standaloneFrames == null) return;

        // Changing the scheme or opening another file would dispose the archive mid-extraction
        m_extracting = true;
        BtnOpen.IsEnabled = false;
        BtnExtractSelected.IsEnabled = false;
        BtnExtractAll.IsEnabled = false;
        CmbScheme.IsEnabled = false;

        var archive = m_currentArchive;
        var standaloneFrames = m_standaloneFrames;
        bool convert = ChkConvert.IsChecked == true;
        int written = 0;
        var failures = new List<string>();

        S25ScriptIndex? scripts = null;
        if (convert && ChkCompose.IsChecked == true && archive != null &&
            entries.Any(e => e.Name.EndsWith(".S25", StringComparison.OrdinalIgnoreCase)))
        {
            scripts = await GetScriptIndexAsync();
            if (scripts == null)
                MessageBox.Show(this,
                    "No scenario scripts were found next to this archive (normally the game's *_T.WAR), " +
                    "so it is not known which layer combinations the game shows.\n\n" +
                    "Layered images will be saved as separate frames.",
                    "Compose Layers", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        ProgExtraction.Visibility = Visibility.Visible;
        ProgExtraction.Value = 0;
        ProgExtraction.Maximum = entries.Count;

        await Task.Run(() =>
        {
            Directory.CreateDirectory(destinationDir);

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                try
                {
                    int perEntry = 0;
                    foreach (var (fileName, data) in GetOutputFiles(archive, standaloneFrames, entry, convert, scripts))
                    {
                        File.WriteAllBytes(SafeOutputPath(destinationDir, fileName), data);
                        written++;
                        // Composed sets can take minutes on their own: show that work is going on
                        if (++perEntry % 25 == 0)
                        {
                            string text = $"Extracting: {entry.Name} ({i + 1}/{entries.Count}), {perEntry:N0} pictures...";
                            Dispatcher.BeginInvoke(() => TxtStatusArchive.Text = text);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{entry.Name}: {ex.Message}");
                }

                int current = i + 1;
                Dispatcher.Invoke(() =>
                {
                    ProgExtraction.Value = current;
                    TxtStatusArchive.Text = $"Extracting: {entry.Name} ({current}/{entries.Count})...";
                });
            }
        });

        EndExtraction();

        int ok = entries.Count - failures.Count;
        if (failures.Count == 0)
        {
            MessageBox.Show(this, $"Extracted {ok} entries ({written} files) to:\n{destinationDir}",
                "Extraction Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            const int shown = 15;
            string list = string.Join("\n", failures.Take(shown));
            if (failures.Count > shown)
                list += $"\n... and {failures.Count - shown} more";
            MessageBox.Show(this,
                $"Extracted {ok} of {entries.Count} entries ({written} files) to:\n{destinationDir}\n\n" +
                $"{failures.Count} entries failed. If most entries failed, the wrong game scheme is probably selected.\n\n{list}",
                "Extraction Finished With Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EndExtraction()
    {
        m_extracting = false;
        ProgExtraction.Visibility = Visibility.Collapsed;
        BtnOpen.IsEnabled = true;
        BtnExtractSelected.IsEnabled = true;
        BtnExtractAll.IsEnabled = true;
        CmbScheme.IsEnabled = true;
        TxtStatusArchive.Text = $"Archive: {Path.GetFileName(m_currentArchivePath ?? "")}";
    }

    #endregion

    protected override void OnClosing(CancelEventArgs e)
    {
        if (m_extracting)
        {
            // The worker thread is still reading the archive; disposing it now would crash
            MessageBox.Show(this, "Please wait for the extraction to finish.", "Extraction In Progress",
                MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }
        StopAudio();
        m_currentArchive?.Dispose();
        base.OnClosing(e);
    }
}