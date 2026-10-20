using System.Globalization;
using System.IO;

namespace TaskbarSholawat;

/// <summary>
/// Uji M3: catat isi GSMTC tiap detik untuk menilai judul dan posisi terekstrapolasi.
/// Pemakaian: TaskbarSholawat.exe --selftest --media [--seconds 60] → media-probe.log di samping exe.
/// Baris "UPDATE" muncul saat sumber mengirim snapshot posisi baru: drift = tebakan ekstrapolasi
/// snapshot lama pada waktu snapshot baru − posisi baru (nilai besar = seek, bukan galat ekstrapolasi).
/// </summary>
public static class MediaProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var seconds = int.TryParse(SelfTest.ArgValue(args, "--seconds"), out var s) && s > 0 ? s : 60;
        var path = Path.Combine(PortablePaths.AppDir, "media-probe.log");
        await using var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        var gate = new object();
        // FormattableString: angka diformat invarian (locale Indonesia memakai koma desimal)
        void Log(FormattableString msg)
        {
            lock (gate)
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            using var media = await MediaInfo.CreateAsync();
            media.Changed += (kind, app) => Log($"EVENT {kind} {app}");
            Log($"Probe {seconds} s; kolom: [*=sesi aktif] app | status x rate | judul — artis | raw @umur | ekstrapolasi / akhir");

            var previous = new Dictionary<string, MediaSnapshot>();
            for (var i = 0; i < seconds; i++)
            {
                var now = DateTimeOffset.Now;
                var current = media.CurrentAppId;
                var sessions = await media.ReadAllAsync();
                if (sessions.Count == 0)
                    Log($"(tidak ada sesi media)");

                foreach (var snap in sessions)
                {
                    if (previous.TryGetValue(snap.AppId, out var old) && old.LastUpdated != snap.LastUpdated && old.IsPlaying)
                    {
                        var drift = old.PositionAt(snap.LastUpdated) - snap.RawPosition;
                        Log($"UPDATE {snap.AppId} drift {drift.TotalSeconds:+0.000;-0.000} s (tebakan {Fmt(old.PositionAt(snap.LastUpdated))}, baru {Fmt(snap.RawPosition)})");
                    }
                    previous[snap.AppId] = snap;

                    var mark = snap.AppId == current ? "*" : " ";
                    Log($"{mark} {snap.AppId} | {snap.Status} x{snap.PlaybackRate:0.##} | {snap.Title} — {snap.Artist} | raw {Fmt(snap.RawPosition)} @{(now - snap.LastUpdated).TotalSeconds:0.0} s | {Fmt(snap.PositionAt(now))} / {Fmt(snap.EndTime)}");
                }
                await Task.Delay(1000);
            }
            Log($"HASIL: OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"HASIL: GAGAL {ex}");
            return 1;
        }
    }

    private static string Fmt(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(t < TimeSpan.Zero ? "-" : "")}{(int)t.Duration().TotalMinutes}:{t.Duration():ss\\.fff}");
}
