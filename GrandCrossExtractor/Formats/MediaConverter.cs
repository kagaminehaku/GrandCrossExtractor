// Converts decrypted ShiinaRio media into standard files on extraction,
// the way GARbro does by default: S25/MI4 → PNG, OGV → OGG, PAD → WAV.

using System.IO;
using System.Windows.Media.Imaging;

namespace GrandCrossExtractor.Formats;

public static class MediaConverter
{
    /// <summary>
    /// Returns the files to write for an entry. Formats that are not recognized, or fail to
    /// decode, are returned unchanged under their original name.
    /// </summary>
    public static List<(string FileName, byte[] Data)> Convert(string entryName, byte[] data)
    {
        string baseName = Path.GetFileNameWithoutExtension(entryName);

        if (S25Decoder.IsS25(data))
        {
            var frames = S25Decoder.DecodeAllFrames(data);
            if (frames.Count == 1)
                return new() { (baseName + ".png", EncodePng(frames[0].Image)) };
            if (frames.Count > 1)
                return frames.Select(f => ($"{baseName}@{f.Index:D4}.png", EncodePng(f.Image))).ToList();
        }
        else if (Mi4Decoder.IsMi4(data))
        {
            var bmp = Mi4Decoder.Decode(data);
            if (bmp != null)
                return new() { (baseName + ".png", EncodePng(bmp)) };
        }
        else if (AudioDecoder.IsOgv(data))
        {
            var ogg = AudioDecoder.DecodeOgv(data);
            if (ogg != null)
                return new() { (baseName + ".ogg", ogg) };
        }
        else if (AudioDecoder.IsPad(data))
        {
            var wav = AudioDecoder.DecodePad(data);
            if (wav != null)
                return new() { (baseName + ".wav", wav) };
        }
        return new() { (entryName, data) };
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
