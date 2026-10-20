namespace TaskbarSholawat;

public sealed record AsrTrackerOptions
{
    /// <summary>Skor kandidat teratas minimal agar potongan dianggap yakin.</summary>
    public double MinScore { get; init; } = 70;

    /// <summary>Selisih minimal ke sholawat lain agar potongan dianggap yakin.</summary>
    public double MinMargin { get; init; } = 5;

    /// <summary>Dari keadaan kosong, potongan dengan skor setinggi ini langsung ditampilkan tanpa menunggu
    /// <see cref="SwitchAfter"/>.</summary>
    public double AcquireScore { get; init; } = 80;

    /// <summary>Ganti sholawat hanya setelah sekian potongan yakin berturut-turut menunjuk sholawat baru.</summary>
    public int SwitchAfter { get; init; } = 2;

    /// <summary>Sembunyikan setelah sekian potongan tidak yakin berturut-turut.</summary>
    public int HideAfter { get; init; } = 3;

    /// <summary>Ambang untuk mode teks-diketahui — kandidat hanya baris satu teks, jadi boleh lebih rendah.
    /// <see cref="MinMargin"/> dipakai sebagai selisih ke baris yang tidak bersebelahan.</summary>
    public static AsrTrackerOptions KnownText { get; } = new() { MinScore = 55, MinMargin = 3 };
}

public enum AsrTrackerStatus
{
    /// <summary>Belum/tidak lagi yakin — tampilkan judul saja atau sembunyikan (PLAN bagian 5).</summary>
    Hidden,

    /// <summary>Potongan terakhir mendukung sholawat saat ini.</summary>
    Tracking,

    /// <summary>Potongan terakhir tidak yakin atau menunjuk sholawat lain; tampilan lama dipertahankan.</summary>
    Holding,
}

public readonly record struct AsrTrackerState(AsrTrackerStatus Status, Sholawat? Sholawat, int Line);

/// <summary>
/// Histeresis hasil <see cref="AsrMatcher"/> per potongan (disiapkan untuk SyncEngine M8): tampilan tidak
/// berpindah sholawat karena satu potongan yang mirip sholawat lain, dan disembunyikan saat beberapa
/// potongan berturut-turut tidak yakin. Dengan <paramref name="knownTexts"/> (hasil
/// <see cref="AsrMatcher.Align"/>; satu teks, atau 2–3 untuk medley) yang dilacak hanya teks + baris di dalam
/// himpunan itu; Hidden = baris tidak diketahui (tampilkan judul saja).
/// </summary>
public sealed class AsrTracker(AsrTrackerOptions? options = null, IReadOnlyList<Sholawat>? knownTexts = null)
{
    private readonly AsrTrackerOptions _options =
        options ?? (knownTexts is { Count: > 0 } ? AsrTrackerOptions.KnownText : new AsrTrackerOptions());
    private readonly IReadOnlyList<Sholawat>? _known = knownTexts is { Count: > 0 } ? knownTexts : null;
    private Sholawat? _current = knownTexts is { Count: 1 } ? knownTexts[0] : null;
    private int _line = -1;
    private Sholawat? _pending;
    private int _pendingCount;
    private int _uncertainCount;

    /// <summary>Diteruskan ke <see cref="AsrMatcher.Match"/> / <see cref="AsrMatcher.Align"/> potongan berikutnya.</summary>
    public AsrHint? Hint => _current is null ? null : new AsrHint(_current, _line);

    public AsrTrackerState State =>
        new(_current is null || _line < 0 ? AsrTrackerStatus.Hidden : AsrTrackerStatus.Tracking, _current, _line);

    public AsrTrackerState Push(AsrMatch match)
    {
        var best = match.Best;
        if (_known is not null)
            return PushKnown(match);

        var supportsCurrent = best is not null && _current is not null
            && ReferenceEquals(best.Sholawat, _current) && best.Score >= _options.MinScore;
        var confident = best is not null && best.Score >= _options.MinScore && match.Margin >= _options.MinMargin;

        // Bait yang sama persis di dua sholawat memberi selisih ±0; bila salah satunya sholawat saat ini,
        // anggap mendukung sholawat ini
        if (supportsCurrent)
        {
            _line = best!.CurrentLine;
            _pending = null;
            _pendingCount = 0;
            _uncertainCount = 0;
            return new AsrTrackerState(AsrTrackerStatus.Tracking, _current, _line);
        }

        // Potongan tidak yakin tidak mereset kandidat yang ditunggu: transkripsi nyanyian sering selang-seling
        // yakin/tidak yakin (uji nyata Burdah 2026-10-21), yang menentukan adalah potongan yakin yang konsisten
        if (!confident)
            return Uncertain();

        _uncertainCount = 0;
        if (ReferenceEquals(_pending, best!.Sholawat))
        {
            _pendingCount++;
        }
        else
        {
            _pending = best.Sholawat;
            _pendingCount = 1;
        }

        var acquire = _current is null && best.Score >= _options.AcquireScore;
        if (acquire || _pendingCount >= _options.SwitchAfter)
        {
            _current = best.Sholawat;
            _line = best.CurrentLine;
            _pending = null;
            _pendingCount = 0;
            return new AsrTrackerState(AsrTrackerStatus.Tracking, _current, _line);
        }
        return new AsrTrackerState(_current is null ? AsrTrackerStatus.Hidden : AsrTrackerStatus.Holding, _current, _line);
    }

    private AsrTrackerState PushKnown(AsrMatch match)
    {
        var best = match.Best;
        if (best is null || !_known!.Contains(best.Sholawat)
            || best.Score < _options.MinScore || match.Margin < _options.MinMargin)
        {
            if (++_uncertainCount >= _options.HideAfter)
                _line = -1;
            return KnownState(_line < 0 ? AsrTrackerStatus.Hidden : AsrTrackerStatus.Holding);
        }

        _uncertainCount = 0;
        // Medley: pindah teks butuh potongan yakin berturut-turut seperti di korpus terbuka; posisi awal
        // (belum ada teks/baris) langsung diterima karena kandidat sudah terbatas pada teks yang diketahui
        if (ReferenceEquals(best.Sholawat, _current) || _current is null || _line < 0)
        {
            _current = best.Sholawat;
            _line = best.CurrentLine;
            _pending = null;
            _pendingCount = 0;
            return KnownState(AsrTrackerStatus.Tracking);
        }

        _pendingCount = ReferenceEquals(_pending, best.Sholawat) ? _pendingCount + 1 : 1;
        _pending = best.Sholawat;
        if (_pendingCount >= _options.SwitchAfter)
        {
            _current = best.Sholawat;
            _line = best.CurrentLine;
            _pending = null;
            _pendingCount = 0;
            return KnownState(AsrTrackerStatus.Tracking);
        }
        return KnownState(AsrTrackerStatus.Holding);
    }

    private AsrTrackerState KnownState(AsrTrackerStatus status) => new(status, _current, _line);

    public void Reset()
    {
        _current = _known is { Count: 1 } ? _known[0] : null;
        _line = -1;
        _pending = null;
        _pendingCount = 0;
        _uncertainCount = 0;
    }

    private AsrTrackerState Uncertain()
    {
        if (++_uncertainCount >= _options.HideAfter)
        {
            _current = null;
            _line = -1;
        }
        return new AsrTrackerState(_current is null ? AsrTrackerStatus.Hidden : AsrTrackerStatus.Holding, _current, _line);
    }
}
