// Which layer combinations of each S25 the game actually shows, read from the scenario scripts
// (the plain Shift-JIS .TXT files, normally in the game's *_T.WAR).
//
// Pictures are set with
//   $L_MONT,<plane>,<dir>\<file>.s25,<x>,<y>,<?>,m,<base>,<layer 1>,<layer 2>,...
// ($L_CHR takes the same "m,..." tail for standing sprites). Value v at position k selects
// slot k*100+v (position 0 is the base); -1, or a value left out, switches the layer off.
// Zoomed versions of a CG ("EV02_02L", "EV02_02M") are swapped in by the engine and never
// named in the script: they use the combinations of the unzoomed file.

using System.IO;
using GrandCrossExtractor.Core;

namespace GrandCrossExtractor.Formats;

public sealed class S25ScriptIndex
{
    private const int SlotsPerLayer = 100;

    // Upper-case S25 file name -> combinations (slot lists, base first) in order of first use
    private readonly Dictionary<string, List<int[]>> m_combinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> m_seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of script files that were read.</summary>
    public int ScriptCount { get; private set; }

    public bool IsEmpty => m_combinations.Count == 0;

    /// <summary>Reads every .TXT entry of the archives next to <paramref name="archivePath"/>.</summary>
    public static S25ScriptIndex FromGameFolder(string archivePath, EncryptionScheme? scheme)
    {
        var index = new S25ScriptIndex();
        string? dir = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        if (dir == null)
            return index;

        foreach (var path in Directory.GetFiles(dir, "*.war").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ArcView? view = null;
            WarcArchive? warc = null;
            try
            {
                view = new ArcView(path);
                warc = WarcOpener.TryOpen(view, scheme);
                if (warc == null)
                    continue;
                foreach (var entry in warc.Entries.Where(e => e.Name.EndsWith(".TXT", StringComparison.OrdinalIgnoreCase)))
                    index.AddScript(Encodings.cp932.GetString(WarcOpener.OpenEntry(warc, entry)));
            }
            catch
            {
                // An archive that cannot be read just contributes no scripts
            }
            finally
            {
                // The archive owns the view once it is open
                if (warc != null)
                    warc.Dispose();
                else
                    view?.Dispose();
            }
        }
        return index;
    }

    public void AddScript(string text)
    {
        bool any = false;
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Split(';')[0].Split('\t')[0].Trim();
            if (!line.StartsWith("$L_MONT,", StringComparison.Ordinal) && !line.StartsWith("$L_CHR,", StringComparison.Ordinal))
                continue;

            var fields = line.Split(',');
            // Lower-case "m" only: upper-case "M" changes the picture already on screen
            // without naming the file, which the script alone cannot resolve
            int marker = Array.IndexOf(fields, "m");
            if (marker < 3)
                continue;
            string file = Path.GetFileName(fields[2].Replace('\\', '/'));
            if (file.Length == 0)
                continue;

            var slots = new List<int>();
            bool valid = true;
            for (int k = 0; marker + 1 + k < fields.Length; k++)
            {
                string value = fields[marker + 1 + k].Trim();
                if (value.Length == 0)
                    continue;
                if (!int.TryParse(value, out int v) || v >= SlotsPerLayer) { valid = false; break; }
                if (v >= 0)
                    slots.Add(k * SlotsPerLayer + v);
            }
            if (!valid || slots.Count == 0 || slots[0] >= SlotsPerLayer)
                continue;

            Add(file, slots.ToArray());
            any = true;
        }
        if (any)
            ScriptCount++;
    }

    private void Add(string file, int[] slots)
    {
        if (!m_seen.TryGetValue(file, out var seen))
        {
            m_seen[file] = seen = new HashSet<string>();
            m_combinations[file] = new List<int[]>();
        }
        if (seen.Add(string.Join(",", slots)))
            m_combinations[file].Add(slots);
    }

    /// <summary>
    /// Combinations the scripts show for an S25 entry, limited to ones its slots can form.
    /// Empty when the scripts never show the file.
    /// </summary>
    public List<int[]> GetCombinations(string entryName, S25Layout layout)
    {
        string file = Path.GetFileName(entryName);
        if (!m_combinations.TryGetValue(file, out var list))
        {
            // Zoomed version: EV02_02L.S25 / EV02_02M.S25 -> EV02_02.S25
            string stem = Path.GetFileNameWithoutExtension(file);
            if (stem.Length > 1 && (stem.EndsWith('L') || stem.EndsWith('M')))
                m_combinations.TryGetValue(stem[..^1] + Path.GetExtension(file), out list);
        }
        return list?.Where(c => layout.IsValidCombination(c)).ToList() ?? new List<int[]>();
    }
}
