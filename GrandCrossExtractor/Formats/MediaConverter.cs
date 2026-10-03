// Converts decrypted ShiinaRio media into standard files on extraction,
// the way GARbro does by default: S25/MI4 → PNG, OGV → OGG, PAD → WAV.

using System.IO;
using GrandCrossExtractor.UI;

namespace GrandCrossExtractor.Formats;

public static class MediaConverter
{
    /// <summary>
    /// Returns the files to write for an entry. Formats that are not recognized, or fail to
    /// decode, are returned unchanged under their original name.
    /// With <paramref name="scripts"/>, a layered S25 (event CG, standing sprite) that the
    /// scenario scripts show is saved as the complete pictures the game displays instead of its
    /// separate frames; see <see cref="S25Composer"/>. Files are produced lazily: write each one
    /// before asking for the next.
    /// </summary>
    public static IEnumerable<(string FileName, byte[] Data)> Convert(string entryName, byte[] data, S25ScriptIndex? scripts = null)
    {
        string baseName = Path.GetFileNameWithoutExtension(entryName);

        if (S25Decoder.IsS25(data))
        {
            var frames = S25Decoder.DecodeAllFrames(data);
            var layout = scripts != null ? S25Layout.Analyze(data) : null;
            var combinations = layout != null ? scripts!.GetCombinations(entryName, layout) : null;
            if (combinations is { Count: > 0 })
                return S25Composer.Compose(baseName, frames, layout!, combinations).Select(c => (c.Name, EncodePng(c.Image)));
            if (frames.Count == 1)
                return new[] { (baseName + ".png", EncodePng(frames[0].Image)) };
            if (frames.Count > 1)
                return frames.Select(f => ($"{baseName}@{f.Index:D4}.png", EncodePng(f.Image)));
        }
        else if (Mi4Decoder.IsMi4(data))
        {
            var bmp = Mi4Decoder.Decode(data);
            if (bmp != null)
                return new[] { (baseName + ".png", EncodePng(bmp)) };
        }
        else if (AudioDecoder.IsOgv(data))
        {
            var ogg = AudioDecoder.DecodeOgv(data);
            if (ogg != null)
                return new[] { (baseName + ".ogg", ogg) };
        }
        else if (AudioDecoder.IsPad(data))
        {
            var wav = AudioDecoder.DecodePad(data);
            if (wav != null)
                return new[] { (baseName + ".wav", wav) };
        }
        return new[] { (entryName, data) };
    }

    private static byte[] EncodePng(PixelImage image) => Bitmaps.EncodePng(image.ToBitmapSource());
}
