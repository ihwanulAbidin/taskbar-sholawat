using System.Text;

namespace TaskbarSholawat;

/// <summary>
/// Bentuk teks Arab untuk pencarian (PLAN 3.2): harakat & tatweel dibuang, varian alif/ya/ta marbuthah
/// disamakan, spasi dirapikan. Hanya untuk mencocokkan — tampilan tetap memakai teks asli berharakat.
/// </summary>
public static class ArabicNormalizer
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            // U+064B–U+0652 harakat (fathatan … sukun), U+0670 alif khanjariyah, U+0640 tatweel
            if (c is >= 'ً' and <= 'ْ' or 'ٰ' or 'ـ')
                continue;

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c switch
            {
                'أ' or 'إ' or 'آ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => c,
            });
        }
        return sb.ToString();
    }
}
