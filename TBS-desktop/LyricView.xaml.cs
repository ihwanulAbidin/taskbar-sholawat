using System.Windows;
using System.Windows.Controls;

namespace TaskbarSholawat;

public partial class LyricView : UserControl
{
    // Ukuran dasar pada skala 1 (diuji M1 di taskbar 48 px)
    private const double ArabicFontSize = 20;
    private const double TranslationFontSize = 11;
    private const double TranslationLineHeight = 13;

    private string? _translation;
    private bool _translationInTooltip;

    public LyricView() => InitializeComponent();

    public void ShowLine(string arabic, string? translation)
    {
        Arabic.Text = arabic;
        _translation = translation;
        UpdateTranslation();
    }

    /// <summary>Skala ukuran huruf kedua baris (1 = normal).</summary>
    public void SetTextScale(double scale)
    {
        Arabic.FontSize = ArabicFontSize * scale;
        Translation.FontSize = TranslationFontSize * scale;
        Translation.LineHeight = TranslationLineHeight * scale;
    }

    /// <summary>Terjemah dipindah ke tooltip supaya baris Arab memakai seluruh tinggi (Mode A berukuran besar).</summary>
    public bool TranslationInTooltip
    {
        get => _translationInTooltip;
        set
        {
            _translationInTooltip = value;
            UpdateTranslation();
        }
    }

    private void UpdateTranslation()
    {
        Translation.Text = _translation ?? "";
        var hasTranslation = !string.IsNullOrEmpty(_translation);
        Translation.Visibility = hasTranslation && !_translationInTooltip ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = hasTranslation && _translationInTooltip ? _translation : null;
    }
}
