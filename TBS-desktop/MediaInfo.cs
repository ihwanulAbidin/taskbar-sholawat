using Windows.Media.Control;

namespace TaskbarSholawat;

public enum MediaChangeKind { Sessions, CurrentSession, MediaProperties, Playback, Timeline }

/// <summary>Satu pembacaan sesi GSMTC. <see cref="RawPosition"/> berlaku pada <see cref="LastUpdated"/>.</summary>
public sealed record MediaSnapshot(
    string AppId,
    string Title,
    string Artist,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus Status,
    double PlaybackRate,
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan RawPosition,
    DateTimeOffset LastUpdated)
{
    public bool IsPlaying => Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    public TimeSpan PositionAt(DateTimeOffset now) =>
        MediaInfo.Extrapolate(RawPosition, LastUpdated, now, PlaybackRate, IsPlaying, EndTime);
}

/// <summary>
/// Info media Windows (GSMTC) — PLAN bagian 3.2. Event WinRT datang dari thread pool,
/// jadi <see cref="Changed"/> juga dipicu di sana; marshal sendiri bila dipakai UI.
/// </summary>
public sealed class MediaInfo : IDisposable
{
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly object _gate = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _subscribed = [];
    private bool _disposed;

    /// <summary>Jenis perubahan + AppUserModelId sesi (kosong untuk perubahan daftar sesi).</summary>
    public event Action<MediaChangeKind, string>? Changed;

    private MediaInfo(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        _manager = manager;
        _manager.SessionsChanged += OnSessionsChanged;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        Resubscribe();
    }

    public static async Task<MediaInfo> CreateAsync() =>
        new(await GlobalSystemMediaTransportControlsSessionManager.RequestAsync());

    /// <summary>Sesi yang dipilih Windows sebagai "sedang aktif" (yang dikendalikan tombol media).</summary>
    public Task<MediaSnapshot?> ReadCurrentAsync() => ReadAsync(_manager.GetCurrentSession());

    public string? CurrentAppId => _manager.GetCurrentSession()?.SourceAppUserModelId;

    public async Task<IReadOnlyList<MediaSnapshot>> ReadAllAsync()
    {
        var result = new List<MediaSnapshot>();
        foreach (var session in _manager.GetSessions())
        {
            if (await ReadAsync(session) is { } snap)
                result.Add(snap);
        }
        return result;
    }

    private static async Task<MediaSnapshot?> ReadAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
            return null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            return new MediaSnapshot(
                session.SourceAppUserModelId ?? "",
                props?.Title ?? "",
                props?.Artist ?? "",
                playback.PlaybackStatus,
                playback.PlaybackRate ?? 1.0,
                timeline.StartTime,
                timeline.EndTime,
                timeline.Position,
                timeline.LastUpdatedTime);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Sesi bisa hilang di antara GetSessions dan pembacaan (tab ditutup) — anggap tidak ada
            return null;
        }
    }

    /// <summary>
    /// GSMTC hanya memberi snapshot posisi; browser memperbaruinya saat situs memanggil
    /// setPositionState (play/pause/seek), bukan tiap detik — PLAN T4.
    /// </summary>
    public static TimeSpan Extrapolate(TimeSpan position, DateTimeOffset lastUpdated, DateTimeOffset now,
        double rate, bool playing, TimeSpan end)
    {
        var result = position;
        if (playing)
        {
            // Jam sumber bisa sedikit di depan jam kita; jangan mundurkan posisi
            var elapsed = now - lastUpdated;
            if (elapsed > TimeSpan.Zero)
                result += elapsed * rate;
        }
        if (result < TimeSpan.Zero)
            result = TimeSpan.Zero;
        // EndTime 0 = durasi tidak diketahui (siaran langsung / situs tidak melapor)
        if (end > TimeSpan.Zero && result > end)
            result = end;
        return result;
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        Resubscribe();
        Changed?.Invoke(MediaChangeKind.Sessions, "");
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Changed?.Invoke(MediaChangeKind.CurrentSession, sender.GetCurrentSession()?.SourceAppUserModelId ?? "");

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) =>
        Changed?.Invoke(MediaChangeKind.MediaProperties, s.SourceAppUserModelId ?? "");

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) =>
        Changed?.Invoke(MediaChangeKind.Playback, s.SourceAppUserModelId ?? "");

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) =>
        Changed?.Invoke(MediaChangeKind.Timeline, s.SourceAppUserModelId ?? "");

    // Daftar sesi diganti utuh tiap SessionsChanged; lepas handler sesi lama agar objeknya tidak tertahan
    private void Resubscribe()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            Unsubscribe();
            foreach (var session in _manager.GetSessions())
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                _subscribed.Add(session);
            }
        }
    }

    private void Unsubscribe()
    {
        foreach (var session in _subscribed)
        {
            try
            {
                session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Sumber sesi sudah keluar; tidak ada handler yang perlu dilepas lagi
            }
        }
        _subscribed.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            Unsubscribe();
        }
    }
}
