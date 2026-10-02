// Save slots of the story player. A save records where the story is (routes played, scenario
// file, message number); loading replays that file silently up to the message, which rebuilds
// the pictures, music and loops on screen at that point.

using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace GrandCrossExtractor.Player;

public sealed record SaveData(string File, int Message, int Played, string Text, DateTime Time);

public sealed class SaveStore
{
    public const int SlotsPerPage = 10, Pages = 10;

    private readonly string m_folder;

    public SaveStore(string gameName)
    {
        string safe = string.Concat(gameName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        m_folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrandCrossExtractor", "saves", safe);
    }

    private string DataPath(int slot) => Path.Combine(m_folder, $"save{slot:D3}.json");
    private string ThumbnailPath(int slot) => Path.Combine(m_folder, $"save{slot:D3}.png");

    public SaveData? Read(int slot)
    {
        try
        {
            return File.Exists(DataPath(slot)) ? JsonSerializer.Deserialize<SaveData>(File.ReadAllText(DataPath(slot))) : null;
        }
        catch
        {
            return null;    // A damaged save shows as empty
        }
    }

    public BitmapSource? Thumbnail(int slot)
    {
        try
        {
            if (!File.Exists(ThumbnailPath(slot)))
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(ThumbnailPath(slot));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public void Write(int slot, SaveData data, BitmapSource thumbnail)
    {
        Directory.CreateDirectory(m_folder);
        File.WriteAllText(DataPath(slot), JsonSerializer.Serialize(data));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(thumbnail));
        using var stream = File.Create(ThumbnailPath(slot));
        encoder.Save(stream);
    }
}
