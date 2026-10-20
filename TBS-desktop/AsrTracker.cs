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
/// potongan berturut-turut tidak yakin.
/// </summary>
public sealed class AsrTracker(AsrTrackerOptions? options = null)
{
    private readonly AsrTrackerOptions _options = options ?? new AsrTrackerOptions();
    private Sholawat? _current;
    private int _line = -1;
    private Sholawat? _pending;
    private int _pendingCount;
    private int _uncertainCount;

    /// <summary>Diteruskan ke <see cref="AsrMatcher.Match"/> potongan berikutnya.</summary>
    public AsrHint? Hint => _current is null ? null : new AsrHint(_current, _line);

    public AsrTrackerState State =>
        new(_current is null ? AsrTrackerStatus.Hidden : AsrTrackerStatus.Tracking, _current, _line);

    public AsrTrackerState Push(AsrMatch match)
    {
        var best = match.Best;
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

        if (!confident)
        {
            _pending = null;
            _pendingCount = 0;
            return Uncertain();
        }

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

    public void Reset()
    {
        _current = null;
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
