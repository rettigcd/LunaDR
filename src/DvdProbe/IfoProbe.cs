using RipDvd.Core.Ifo;

namespace DvdProbe;

/// <summary>Probe code for reading IFO files and printing what they say.</summary>
internal static class IfoProbe
{
    public static string ResolveVideoTs(string arg)
    {
        if (arg.Length <= 3 && arg.TrimEnd(':', '\\').Length == 1)
            return arg.TrimEnd(':', '\\') + @":\VIDEO_TS";
        return arg;
    }

    public static void PrintTitles(string videoTs, bool verbose)
    {
        var vmg = VmgIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, "VIDEO_TS.IFO")));
        Console.WriteLine($"VMG: version {vmg.Version:X}, {vmg.NumberOfTitleSets} title sets, " +
                          $"{vmg.Titles.Count} titles, provider '{vmg.ProviderId}'");
        Console.WriteLine();

        var vtsCache = new Dictionary<int, VtsIfo>();
        VtsIfo GetVts(int n) => vtsCache.TryGetValue(n, out var v) ? v
            : vtsCache[n] = VtsIfo.Parse(File.ReadAllBytes(Path.Combine(videoTs, $"VTS_{n:00}_0.IFO")), n);

        Console.WriteLine("Title VTS TTN Ang Chap  PGCs  Cells  PGC time   Cell sum   Nominal      Sectors      Bytes  Audio Subp");
        foreach (var t in vmg.Titles)
        {
            var vts = GetVts(t.TitleSetNumber);
            var chapters = vts.Chapters[t.TitleSetTitleNumber - 1];
            var pgcs = chapters.Select(c => c.PgcNumber).Distinct().Select(n => vts.Pgcs[n - 1]).ToList();
            var cells = pgcs.SelectMany(p => p.Cells).ToList();
            var pgcTime = pgcs.Select(p => p.PlaybackTime).Aggregate((a, b) => a + b);
            var cellTime = cells.Select(c => c.PlaybackTime).Aggregate((a, b) => a + b);
            long sectors = cells.Sum(c => c.SectorCount);

            Console.WriteLine($"{t.TitleNumber,5} {t.TitleSetNumber,3} {t.TitleSetTitleNumber,3} {t.NumberOfAngles,3} " +
                              $"{t.NumberOfChapters,4} {string.Join(",", pgcs.Select(p => p.Number)),5} {cells.Count,6}  " +
                              $"{Fmt(pgcTime)} {Fmt(cellTime)} {pgcTime.Nominal.ToString(@"h\:mm\:ss\.ff")} {sectors,9} {sectors * 2048,12}" +
                              $"  {pgcs[0].AudioControl.Take(vts.Audio.Count).Count(a => a.Available),5}" +
                              $" {pgcs[0].SubpictureControl.Take(vts.Subpictures.Count).Count(s => s.Available),4}");

            if (verbose) PrintDetails(vts, pgcs, chapters);
        }
    }

    private static string Fmt(DvdTime t)
    {
        var real = t.ToTimeSpan();
        return $"{(int)real.TotalHours}:{real.Minutes:00}:{real.Seconds:00}".PadLeft(9) + " ";
    }

    private static void PrintDetails(VtsIfo vts, List<Pgc> pgcs, IReadOnlyList<PartOfTitle> chapters)
    {
        var v = vts.Video;
        Console.WriteLine($"      video: MPEG{v.MpegVersion} {v.TvSystem} {v.Width}x{v.Height} {v.AspectRatio}" +
                          $" CC1={v.Line21Field1} CC2={v.Line21Field2} letterboxed={v.Letterboxed}");
        // Only streams marked "available" in the PGC are real; discs often declare unused ones.
        for (int i = 0; i < vts.Audio.Count; i++)
        {
            var a = vts.Audio[i];
            var ctl = pgcs[0].AudioControl[i];
            if (!ctl.Available) continue;
            Console.WriteLine($"      audio {i}: {a.Coding} {a.Channels}ch {a.SampleRate}Hz lang={a.Language} ext={a.CodeExtension}" +
                              $" available={ctl.Available} substream={ctl.StreamNumber}");
        }
        for (int i = 0; i < vts.Subpictures.Count; i++)
        {
            var s = vts.Subpictures[i];
            var ctl = pgcs[0].SubpictureControl[i];
            if (!ctl.Available) continue;
            Console.WriteLine($"      subp {i}: lang={s.Language} ext={s.CodeExtension} available={ctl.Available}" +
                              $" 4:3={ctl.Stream4x3} wide={ctl.StreamWide} lb={ctl.StreamLetterbox} ps={ctl.StreamPanScan}");
        }
        Console.WriteLine($"      chapters: {string.Join(" ", chapters.Select(c => $"{c.PgcNumber}/{c.ProgramNumber}"))}");
        foreach (var pgc in pgcs)
        {
            Console.WriteLine($"      PGC {pgc.Number}: entry={pgc.IsEntryPgc} ttn={pgc.TitleNumber} time={pgc.PlaybackTime}" +
                              $" programs(entry cells)={string.Join(",", pgc.ProgramEntryCells)} next={pgc.NextPgc} prev={pgc.PreviousPgc}");
            foreach (var c in pgc.Cells)
                Console.WriteLine($"        cell {c.Number,2}: vob {c.VobId,2} cell {c.CellId,2}  sectors {c.FirstSector,7}-{c.LastSector,7}" +
                                  $" ({c.SectorCount,6})  time {c.PlaybackTime}  block {c.BlockMode}/{c.BlockType}" +
                                  $" seamless={c.SeamlessPlay} stc={c.StcDiscontinuity}");
        }
        Console.WriteLine();
    }
}
