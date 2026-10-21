using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TaskbarSholawat;

/// <param name="Line">Indeks baris di <paramref name="Sholawat"/>.</param>
/// <param name="Confidence">Rata-rata kecocokan (0–1) event takarir informatif di baris ini; 0 = tidak ada
/// event informatif (baris diisi dari urutan saja).</param>
/// <param name="Caption">Teks takarir yang menjadi dasar (untuk diagnosis).</param>
public sealed record CaptionTiming(
    TimeSpan LineStart, TimeSpan LineEnd, Sholawat Sholawat, int Line, bool Refrain, double Confidence, string Caption);

public sealed record CaptionAlignerOptions
{
    /// <summary>Kecocokan (0–100) di atas nilai ini mendukung baris; di bawahnya menolak.</summary>
    public double Baseline { get; init; } = 62;

    /// <summary>Kerangka event sepanjang ini (huruf) dianggap informatif penuh; lebih pendek ditimbang turun —
    /// "حق" / "k" cocok di mana saja.</summary>
    public int FullWeightChars { get; init; } = 3;

    /// <summary>Kerangka lebih pendek dari ini diabaikan (bobot 0).</summary>
    public int MinChars { get; init; } = 2;

    /// <summary>Pindah ke baris berikut / masuk-keluar refrain.</summary>
    public double AdvancePenalty { get; init; } = 1.5;

    public double JumpPenalty { get; init; } = 4;

    public double JumpPerLine { get; init; } = 0.5;

    public double MaxJumpPenalty { get; init; } = 12;

    /// <summary>Bait yang sama dinyanyikan lagi setelah refrain (umum pada Burdah).</summary>
    public double RepeatAfterRefrainPenalty { get; init; } = 3;

    /// <summary>Pergantian bait yang melewati refrain pada lintasan pertama setinggi ini → pola refrain-tiap-bait.</summary>
    public double RefrainPatternShare { get; init; } = 0.5;

    /// <summary>Biaya bait → bait langsung bila pola refrain-tiap-bait terdeteksi.</summary>
    public double SkipRefrainPenalty { get; init; } = 4;

    /// <summary>Baris dengan kecocokan rata-rata (0–1) di bawah ini tidak ditampilkan (jadi sela).</summary>
    public double MinConfidence { get; init; } = 0.73;

    /// <summary>Segmen bait/refrain lebih panjang dari kelipatan durasi khas ini dikenai biaya.</summary>
    public double LongSegment { get; init; } = 1.3;

    /// <summary>Biaya per kelipatan durasi khas di atas <see cref="LongSegment"/>.</summary>
    public double LongSegmentPenalty { get; init; } = 6;

    /// <summary>Batas panjang satu segmen bait/refrain (detik).</summary>
    public double MaxSegmentSeconds { get; init; } = 120;

    /// <summary>Masuk bagian di luar teks diketahui (sela musik, doa, bait yang tidak ada di teks).</summary>
    public double GapPenalty { get; init; } = 3;

    /// <summary>Video bisa mulai di bait mana saja (potongan / medley); mulai jauh dikenai biaya kecil.</summary>
    public double StartPerLine { get; init; } = 0.3;

    public double MaxStartPenalty { get; init; } = 8;
}

/// <summary>
/// Takarir YouTube (teks kasar + waktu) + lirik yang diketahui (1..n sholawat, urut medley) → timing
/// tiap baris lirik untuk seluruh video, setara LRC, sehingga sinkron cukup dari posisi GSMTC tanpa Whisper.
/// <para>
/// Takarir otomatis untuk nyanyian Arab pendek-pendek (1–3 kata per event), salah eja, dan sering berupa
/// transliterasi Latin ("muhammad", "zinat dunya"). Karena itu tiap event dan tiap baris dipetakan ke
/// kerangka konsonan bersama (<see cref="Skeleton"/>) lalu dicocokkan dengan PartialRatio; event pendek
/// ditimbang turun. Viterbi per event: urutan maju (boleh melompat), refrain = baris pertama tiap sholawat
/// yang boleh disisipkan setelah bait mana pun lalu lanjut ke bait berikutnya, dan state "sela" untuk bagian
/// di luar teks diketahui.
/// </para>
/// </summary>
public sealed class CaptionAligner(CaptionAlignerOptions? options = null)
{
    private const double Forbidden = -1e9;

    private static readonly System.Text.RegularExpressions.Regex SunLam =
        new(@"(?<=(?:^|\s|[وفبك])[اأإآ])ل(?=[\u064B-\u0652]*[تثدذرزسشصضطظلن])", System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly CaptionAlignerOptions _o = options ?? new CaptionAlignerOptions();

    private enum Mode
    {
        Sing,
        Refrain,
        Gap,
    }

    public IReadOnlyList<CaptionTiming> Align(Transcript transcript, IReadOnlyList<Sholawat> known)
    {
        var events = transcript.Lines;
        if (known.Count == 0 || events.Count == 0)
            return [];

        // Indeks global baris lintas sholawat (urut medley); refrain = baris pertama tiap sholawat
        var lines = known.SelectMany(s => s.Lines.Select((_, i) => (Sholawat: s, Line: i))).ToList();
        var n = lines.Count;
        var refrainOf = new int[n];
        for (int g = 0, first = 0; g < n; g++)
        {
            if (lines[g].Line == 0)
                first = g;
            refrainOf[g] = first;
        }
        var lineSkeletons = lines.Select(l => Skeleton(l.Sholawat.Lines[l.Line].Ar)).ToArray();

        // Emisi per event: (kecocokan − acuan) × bobot; sela = 0 (netral). Acuan = rata-rata kecocokan event
        // itu ke semua baris (minimal Baseline): "محمد" cocok dengan semua bait → hanya membedakan bait dari
        // refrain, bukan bait satu dari lainnya
        var T = events.Count;
        var match = new double[T, n];
        var weight = new double[T];
        var reference = new double[T];
        for (var t = 0; t < T; t++)
        {
            var sk = Skeleton(events[t].Text);
            weight[t] = sk.Length < _o.MinChars ? 0 : Math.Min(1, sk.Length / (double)_o.FullWeightChars);
            for (var g = 0; g < n; g++)
                match[t, g] = weight[t] == 0 ? 0 : Similarity(sk, lineSkeletons[g]);
            var mean = 0.0;
            for (var g = 0; g < n; g++)
                mean += match[t, g] / n;
            reference[t] = Math.Max(_o.Baseline, mean);
        }

        double Emit(int t, int s)
        {
            var (mode, g) = Decode(s, n);
            if (mode == Mode.Gap || weight[t] == 0)
                return 0;
            var line = mode == Mode.Refrain ? refrainOf[g] : g;
            return weight[t] * (match[t, line] - reference[t]) / 10;
        }

        // Lintasan 1 (Markov per event) → pola refrain & durasi khas. Takarir refrain sering hanya sepotong
        // ("mai", "و ab"), sehingga refrain terlewat dan bait "memanjang". Lintasan 2 (semi-Markov per segmen):
        // bila sebagian besar pergantian bait melewati refrain (Burdah, Qasidah Muhammadiyah), bait → bait
        // langsung dikenai biaya; segmen jauh lebih panjang dari durasi khas mode-nya dikenai biaya
        var path = Viterbi(T, n, refrainOf, Emit, skipRefrain: 0);
        var skip = RefrainShare(path, n, refrainOf) >= _o.RefrainPatternShare ? _o.SkipRefrainPenalty : 0;
        var typical = TypicalDurations(events, path, n);
        path = SegmentViterbi(events, n, refrainOf, Emit, skip, typical);

        return ToTimings(events, path, lines, refrainOf, n, match, weight, _o.MinConfidence);
    }

    // State: [0,n) menyanyikan bait g; [n,2n) refrain setelah bait g; [2n,3n) sela setelah bait g
    private int[] Viterbi(int T, int n, int[] refrainOf, Func<int, int, double> Emit, double skipRefrain)
    {
            var states = 3 * n;
        var score = new double[states];
        var next = new double[states];
        var back = new int[T, states];
        for (var s = 0; s < states; s++)
        {
            var (mode, g) = Decode(s, n);
            var start = -Math.Min(_o.MaxStartPenalty, _o.StartPerLine * (g - refrainOf[g]));
            score[s] = start + Emit(0, s) - (mode == Mode.Sing ? 0 : _o.AdvancePenalty);
        }

        // Transisi hanya ke beberapa state per asal → daftar sebelumnya per tujuan dihitung sekali
        var preds = new List<(int From, double Cost)>[states];
        for (var s = 0; s < states; s++)
            preds[s] = [];
        for (var from = 0; from < states; from++)
        {
            for (var to = 0; to < states; to++)
            {
                var c = Transition(from, to, n, refrainOf, skipRefrain);
                if (c > Forbidden / 2)
                    preds[to].Add((from, c));
            }
        }

        for (var t = 1; t < T; t++)
        {
            for (var to = 0; to < states; to++)
            {
                var best = double.NegativeInfinity;
                var arg = 0;
                foreach (var (from, cost) in preds[to])
                {
                    var v = score[from] + cost;
                    if (v > best)
                        (best, arg) = (v, from);
                }
                next[to] = best + Emit(t, to);
                back[t, to] = arg;
            }
            (score, next) = (next, score);
        }

        var path = new int[T];
        for (var s = 1; s < states; s++)
        {
            if (score[s] > score[path[T - 1]])
                path[T - 1] = s;
        }
        for (var t = T - 1; t > 0; t--)
            path[t - 1] = back[t, path[t]];

        return path;
    }

    // Median durasi segmen Sing / Refrain (≥ 2 event) pada lintasan 1; null bila kurang dari 3 segmen
    private static double?[] TypicalDurations(IReadOnlyList<TranscriptLine> events, int[] path, int n)
    {
        var lists = new[] { new List<double>(), new List<double>() };
        for (int t = 0, first = 0; t < path.Length; t++)
        {
            if (t + 1 < path.Length && path[t + 1] == path[t])
                continue;
            var mode = Decode(path[t], n).Mode;
            if (mode != Mode.Gap && t > first)
                lists[(int)mode].Add(SegmentSeconds(events, first, t));
            first = t + 1;
        }
        return lists.Select(l => l.Count < 3 ? (double?)null : l.Order().ElementAt(l.Count / 2)).ToArray();
    }

    // Segmen event i..j berlangsung sampai event berikutnya mulai (atau event terakhirnya selesai)
    private static double SegmentSeconds(IReadOnlyList<TranscriptLine> events, int i, int j) =>
        ((j + 1 < events.Count ? events[j + 1].Start : events[j].End) - events[i].Start).TotalSeconds;

    /// <summary>
    /// Viterbi semi-Markov: segmen event berturutan berstate sama, tanpa transisi ke state yang sama kecuali
    /// sela. Skor segmen = jumlah emisi + biaya durasi bila lebih dari <see cref="CaptionAlignerOptions.LongSegment"/>
    /// × durasi khas mode-nya. Segmen Sing/Refrain dibatasi <see cref="CaptionAlignerOptions.MaxSegmentSeconds"/>.
    /// </summary>
    private int[] SegmentViterbi(IReadOnlyList<TranscriptLine> events, int n, int[] refrainOf,
        Func<int, int, double> Emit, double skipRefrain, double?[] typical)
    {
        var T = events.Count;
        var states = 3 * n;
        var cum = new double[T + 1, states];
        for (var t = 0; t < T; t++)
        {
            for (var s = 0; s < states; s++)
                cum[t + 1, s] = cum[t, s] + Emit(t, s);
        }

        var preds = new List<(int From, double Cost)>[states];
        for (var s = 0; s < states; s++)
            preds[s] = [];
        for (var from = 0; from < states; from++)
        {
            for (var to = 0; to < states; to++)
            {
                var c = from == to ? (Decode(to, n).Mode == Mode.Gap ? 0 : Forbidden) : Transition(from, to, n, refrainOf, skipRefrain);
                if (c > Forbidden / 2)
                    preds[to].Add((from, c));
            }
        }

        // enterAt[i, s]: skor terbaik memulai segmen state s di event i (transisi dari segmen yang berakhir di i − 1)
        var best = new double[T, states];
        var backStart = new int[T, states];
        var backState = new int[T, states];
        var enterAt = new double[T, states];
        var enterArg = new int[T, states];
        for (var i = 0; i < T; i++)
        {
            for (var s = 0; s < states; s++)
            {
                if (i == 0)
                {
                    var (mode, g) = Decode(s, n);
                    enterAt[0, s] = -Math.Min(_o.MaxStartPenalty, _o.StartPerLine * (g - refrainOf[g]))
                                    - (mode == Mode.Sing ? 0 : _o.AdvancePenalty);
                    enterArg[0, s] = -1;
                    continue;
                }
                var v = double.NegativeInfinity;
                var arg = -1;
                foreach (var (from, cost) in preds[s])
                {
                    var c = best[i - 1, from] + cost;
                    if (c > v)
                        (v, arg) = (c, from);
                }
                enterAt[i, s] = v;
                enterArg[i, s] = arg;
            }

            // Segmen yang berakhir di event i
            for (var s = 0; s < states; s++)
            {
                var mode = Decode(s, n).Mode;
                var v = double.NegativeInfinity;
                var argStart = 0;
                for (var start = i; start >= 0; start--)
                {
                    var seconds = SegmentSeconds(events, start, i);
                    if (mode != Mode.Gap && seconds > _o.MaxSegmentSeconds && start < i)
                        break;
                    if (double.IsNegativeInfinity(enterAt[start, s]))
                        continue;
                    var c = enterAt[start, s] + cum[i + 1, s] - cum[start, s] + DurationCost(mode, seconds, typical);
                    if (c > v)
                        (v, argStart) = (c, start);
                }
                best[i, s] = v;
                backStart[i, s] = argStart;
                backState[i, s] = enterArg[argStart, s];
            }
        }

        var path = new int[T];
        var state = 0;
        for (var s = 1; s < states; s++)
        {
            if (best[T - 1, s] > best[T - 1, state])
                state = s;
        }
        for (var end = T - 1; end >= 0;)
        {
            var start = backStart[end, state];
            for (var t = start; t <= end; t++)
                path[t] = state;
            var prev = backState[end, state];
            end = start - 1;
            if (prev < 0)
                break;
            state = prev;
        }
        return path;
    }

    private double DurationCost(Mode mode, double seconds, double?[] typical)
    {
        if (mode == Mode.Gap || typical[(int)mode] is not { } d || d <= 0)
            return 0;
        var excess = seconds / d - _o.LongSegment;
        return excess > 0 ? -_o.LongSegmentPenalty * excess : 0;
    }

    /// <summary>Bagian pergantian bait (dalam satu sholawat) yang melewati refrain; 0 bila kurang dari 3 pergantian.</summary>
    private static double RefrainShare(int[] path, int n, int[] refrainOf)
    {
        int via = 0, direct = 0;
        for (var t = 1; t < path.Length; t++)
        {
            if (path[t] == path[t - 1])
                continue;
            var (fm, f) = Decode(path[t - 1], n);
            var (tm, g) = Decode(path[t], n);
            if (tm != Mode.Sing || refrainOf[g] == g || refrainOf[f] != refrainOf[g])
                continue;
            if (fm == Mode.Refrain)
                via++;
            else if (fm == Mode.Sing && f != refrainOf[f])
                direct++;
        }
        return via + direct < 3 ? 0 : via / (double)(via + direct);
    }

    private static (Mode Mode, int Line) Decode(int s, int n) => ((Mode)(s / n), s % n);

    private double Transition(int from, int to, int n, int[] refrainOf, double skipRefrain)
    {
        var (fm, f) = Decode(from, n);
        var (tm, g) = Decode(to, n);
        if (from == to)
            return 0;

        return (fm, tm) switch
        {
            // Masuk refrain / sela: bait terakhir yang diingat harus bait yang baru dinyanyikan
            (Mode.Sing, Mode.Refrain) => g == f && refrainOf[f] != f ? -_o.AdvancePenalty : Forbidden,
            (Mode.Sing or Mode.Refrain, Mode.Gap) => g == f ? -_o.GapPenalty : Forbidden,
            // Bait → bait tanpa refrain di antaranya (bukan dari baris refrain yang dinyanyikan di awal)
            (Mode.Sing, Mode.Sing) => Move(f, g) - (refrainOf[f] != f && refrainOf[f] == refrainOf[g] ? skipRefrain : 0),
            // Keluar refrain / sela: lanjut setelah bait terakhir, atau bait itu diulang
            (Mode.Refrain or Mode.Gap, Mode.Sing) => g == f ? -_o.RepeatAfterRefrainPenalty : Move(f, g),
            (Mode.Gap, Mode.Refrain) => g == f && refrainOf[f] != f ? -_o.AdvancePenalty : Forbidden,
            _ => Forbidden,
        };
    }

    // Maju 1 baris murah; lebih jauh bertambah mahal; mundur dilarang
    private double Move(int from, int to)
    {
        var d = to - from;
        return d switch
        {
            1 => -_o.AdvancePenalty,
            > 1 => -Math.Min(_o.MaxJumpPenalty, _o.JumpPenalty + _o.JumpPerLine * (d - 2)),
            _ => Forbidden,
        };
    }

    private static List<CaptionTiming> ToTimings(IReadOnlyList<TranscriptLine> events, int[] path,
        List<(Sholawat Sholawat, int Line)> lines, int[] refrainOf, int n, double[,] match, double[] weight,
        double minConfidence)
    {
        // Event berurutan dengan state sama = satu baris yang sedang dinyanyikan
        var runs = new List<(int State, int First, int Last)>();
        for (var t = 0; t < path.Length; t++)
        {
            if (runs.Count > 0 && runs[^1].State == path[t])
                runs[^1] = runs[^1] with { Last = t };
            else
                runs.Add((path[t], t, t));
        }

        // Kecocokan rata-rata (berbobot) tiap run; run ragu dijadikan sela — lebih baik kosong daripada salah
        var gapState = 2 * n;
        var confidence = new double[runs.Count];
        for (var r = 0; r < runs.Count; r++)
        {
            var (mode, g) = Decode(runs[r].State, n);
            if (mode == Mode.Gap)
                continue;
            var line = mode == Mode.Refrain ? refrainOf[g] : g;
            double sum = 0, w = 0;
            for (var t = runs[r].First; t <= runs[r].Last; t++)
            {
                sum += weight[t] * match[t, line];
                w += weight[t];
            }
            confidence[r] = w == 0 ? 0 : sum / w / 100;
            if (confidence[r] < minConfidence)
                runs[r] = runs[r] with { State = gapState };
        }

        var result = new List<CaptionTiming>(runs.Count);
        for (var r = 0; r < runs.Count; r++)
        {
            var (mode, g) = Decode(runs[r].State, n);
            if (mode == Mode.Gap)
                continue;
            var line = mode == Mode.Refrain ? refrainOf[g] : g;
            var start = events[runs[r].First].Start;
            // Sela diisi: baris berlangsung sampai baris berikutnya mulai; sebelum sela / di akhir video
            // sampai event terakhirnya selesai
            var end = r + 1 < runs.Count && Decode(runs[r + 1].State, n).Mode != Mode.Gap
                ? events[runs[r + 1].First].Start
                : events[runs[r].Last].End;
            var captions = new List<string>();
            for (var t = runs[r].First; t <= runs[r].Last; t++)
                captions.Add(events[t].Text);
            result.Add(new CaptionTiming(start, end, lines[line].Sholawat, lines[line].Line, mode == Mode.Refrain,
                confidence[r], string.Join(" / ", captions)));
        }
        return result;
    }

    /// <summary>
    /// 0–100: bagian huruf <paramref name="query"/> yang muncul berurutan (LCS) di satu potongan
    /// <paramref name="line"/> sepanjang ±1,5× query. Takarir otomatis sering kehilangan konsonan ("الخق" untuk
    /// "الخلق") — PartialRatio menghukum itu terlalu berat untuk kata 2–4 huruf.
    /// </summary>
    internal static double Similarity(string query, string line)
    {
        if (query.Length == 0 || line.Length == 0)
            return 0;
        var span = Math.Min(line.Length, query.Length + (query.Length + 1) / 2);
        var best = 0;
        var row = new int[span + 1];
        var prev = new int[span + 1];
        for (var start = 0; start + span <= line.Length && best < query.Length; start++)
        {
            Array.Clear(prev);
            for (var i = 1; i <= query.Length; i++)
            {
                row[0] = 0;
                for (var j = 1; j <= span; j++)
                    row[j] = query[i - 1] == line[start + j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], row[j - 1]);
                (row, prev) = (prev, row);
            }
            best = Math.Max(best, prev[span]);
        }
        return 100.0 * best / query.Length;
    }

    /// <summary>
    /// Kerangka konsonan bersama untuk teks Arab dan transliterasi Latin: vokal (termasuk ا و ي, hamzah, ع)
    /// dibuang, huruf yang sering tertukar disamakan (ت ط ة → t, ث س ص → s, ح ه → h, د ض → d, ذ ز ظ → z,
    /// ق ك → k), huruf kembar dipadatkan (syaddah / "mm"), spasi dibuang. Hanya untuk mencocokkan.
    /// </summary>
    public static string Skeleton(string text)
    {
        // Lam syamsiyah: "الرحمن" dibaca "ar-rahman" — lam dibuang agar cocok dengan transliterasi
        // NFKC: bentuk presentasi Arab (ﺫَﺍﻙَ, umum di description YouTube) → huruf biasa
        var lower = SunLam.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), "");
        var sb = new StringBuilder(lower.Length);
        void Add(char c)
        {
            if (sb.Length == 0 || sb[^1] != c)
                sb.Append(c);
        }

        for (var i = 0; i < lower.Length; i++)
        {
            var c = lower[i];
            var nextChar = i + 1 < lower.Length ? lower[i + 1] : '\0';
            // Digraf Latin (ejaan Indonesia): sh/sy → c, kh → x, ts → s, th → t (ط), dh → d (ض), dz → z, gh → g
            if (c is 's' && nextChar is 'h' or 'y' || c is 'c' && nextChar is 'h')
            {
                Add('c');
                i++;
                continue;
            }
            if ((c, nextChar) is ('k', 'h') or ('t', 'h') or ('t', 's') or ('d', 'h') or ('d', 'z') or ('g', 'h'))
            {
                Add((c, nextChar) switch { ('k', _) => 'x', ('t', 's') => 's', ('d', 'z') => 'z', ('g', _) => 'g', _ => c });
                i++;
                continue;
            }

            var mapped = c switch
            {
                'ب' or 'b' => 'b',
                'ت' or 'ط' or 'ة' or 't' => 't',
                'ث' or 'س' or 'ص' or 's' => 's',
                'ج' or 'j' => 'j',
                'ح' or 'ه' or 'h' => 'h',
                'خ' or 'x' => 'x',
                'د' or 'ض' or 'd' => 'd',
                'ذ' or 'ز' or 'ظ' or 'z' => 'z',
                'ر' or 'r' => 'r',
                'ش' or 'c' => 'c',
                'غ' or 'g' => 'g',
                'ف' or 'f' or 'v' or 'p' => 'f',
                'ق' or 'ك' or 'q' or 'k' => 'k',
                'ل' or 'l' => 'l',
                'م' or 'm' => 'm',
                'ن' or 'n' => 'n',
                _ => '\0',
            };
            if (mapped != '\0')
                Add(mapped);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Simpan sebagai rekaman pustaka (<c>recordings/&lt;id&gt;.json</c> + satu LRC per bagian sholawat) —
    /// dibaca <see cref="LyricLibrary"/> seperti rekaman buatan tangan. Waktu LRC relatif terhadap awal bagian.
    /// </summary>
    /// <returns>Jalur berkas JSON rekaman.</returns>
    public static string SaveRecording(string libraryDir, string id, string title, double? durationSec,
        IReadOnlyList<CaptionTiming> timings)
    {
        var dir = Path.Combine(libraryDir, "recordings");
        Directory.CreateDirectory(dir);

        // Bagian = deret baris berurutan dari sholawat yang sama
        var segments = new List<object>();
        var i = 0;
        var part = 0;
        while (i < timings.Count)
        {
            var j = i;
            while (j + 1 < timings.Count && timings[j + 1].Sholawat.Id == timings[i].Sholawat.Id)
                j++;
            var segStart = part == 0 ? TimeSpan.Zero : timings[i].LineStart;
            var segEnd = j + 1 < timings.Count ? timings[j + 1].LineStart
                : durationSec is { } d ? TimeSpan.FromSeconds(Math.Max(d, timings[j].LineEnd.TotalSeconds))
                : timings[j].LineEnd;
            var lrcName = $"{id}-{++part}.lrc";
            var lrc = new StringBuilder();
            lrc.AppendLine($"[ti:{title}]");
            lrc.AppendLine("[by:Taskbar Sholawat — dari takarir YouTube + teks diketahui (CaptionAligner)]");
            for (var k = i; k <= j; k++)
            {
                lrc.AppendLine($"[{Stamp(timings[k].LineStart - segStart)}]{timings[k].Sholawat.Lines[timings[k].Line].Ar}");
                // Sela setelah baris (state sela / akhir bagian): baris kosong agar teks tidak menggantung
                var nextStart = k + 1 <= j ? timings[k + 1].LineStart : segEnd;
                if (nextStart - timings[k].LineEnd > TimeSpan.FromSeconds(1))
                    lrc.AppendLine($"[{Stamp(timings[k].LineEnd - segStart)}]");
            }
            File.WriteAllText(Path.Combine(dir, lrcName), lrc.ToString(), new UTF8Encoding(false));
            segments.Add(new { start = Math.Round(segStart.TotalSeconds, 2), end = Math.Round(segEnd.TotalSeconds, 2),
                sholawat = timings[i].Sholawat.Id, timing = lrcName });
            i = j + 1;
        }

        var json = Path.Combine(dir, $"{id}.json");
        var recording = new
        {
            id,
            note = "Dibuat otomatis dari takarir YouTube (CaptionAligner); hapus untuk membuat ulang",
            match = new { titleContains = new[] { title }, durationSec },
            segments,
        };
        File.WriteAllText(json, JsonSerializer.Serialize(recording, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
        return json;
    }

    private static string Stamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }
}
