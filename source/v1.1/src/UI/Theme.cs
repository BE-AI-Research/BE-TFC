using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BETFC.UI;

/// <summary>Colours for one appearance mode.</summary>
public sealed record Palette(
    bool IsDark,
    Color Window,
    Color Surface,
    Color Text,
    Color SubtleText,
    Color Danger,
    Color Warning,
    Color Accent,
    Color LogBack,
    Color LogText);

/// <summary>
/// Follows the machine's app appearance setting. Registry access is READ-ONLY —
/// BE-TFC still writes nothing but PendingFileRenameOperations (doctrine #6).
///
/// Note on elevation: HKCU here is the hive of the account BE-TFC is running
/// under, which is the *operator's* elevated account, not necessarily the
/// client's logged-in user. That is the right answer — this is the tech's
/// window, so it should match the tech's session.
/// </summary>
public static class Theme
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static Palette Light { get; } = new(
        IsDark:     false,
        Window:     SystemColors.Control,
        Surface:    Color.White,
        Text:       Color.FromArgb(20, 20, 20),
        SubtleText: Color.FromArgb(112, 112, 112),
        Danger:     Color.Firebrick,
        Warning:    Color.FromArgb(158, 106, 0),
        Accent:     Color.FromArgb(0, 90, 158),
        LogBack:    Color.FromArgb(18, 18, 18),
        LogText:    Color.Gainsboro);

    public static Palette Dark { get; } = new(
        IsDark:     true,
        Window:     Color.FromArgb(32, 32, 32),
        Surface:    Color.FromArgb(43, 43, 43),
        Text:       Color.FromArgb(240, 240, 240),
        SubtleText: Color.FromArgb(150, 150, 150),
        // Firebrick is unreadable on a dark surface — same semantics, lifted.
        Danger:     Color.FromArgb(255, 106, 106),
        Warning:    Color.FromArgb(240, 190, 90),
        Accent:     Color.FromArgb(96, 175, 255),
        LogBack:    Color.FromArgb(18, 18, 18),
        LogText:    Color.Gainsboro);

    // Declared AFTER Light/Dark deliberately: static initialisers run in textual
    // order, so hoisting this above them would capture nulls.
    public static Palette Current { get; } = SystemPrefersDark() ? Dark : Light;

    /// <summary>AppsUseLightTheme = 0 means the user wants dark app chrome.
    /// Missing value (older/managed images) means light.</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch { return false; }
    }

    // ─────────────────────────── window chrome ───────────────────────────

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode          = 20;   // Win10 2004+ / Win11
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;   // older Win10 builds

    /// <summary>Paint the title bar to match. Best-effort; a failure here is
    /// cosmetic and must never surface to the user.</summary>
    public static void ApplyTitleBar(Form form)
    {
        if (!form.IsHandleCreated) return;
        int dark = Current.IsDark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode,
                                      ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModeBefore20H1,
                                      ref dark, sizeof(int));
            }
        }
        catch { /* dwmapi unavailable — light title bar, no harm */ }
    }

    /// <summary>Recursively apply the palette to a control tree. Controls that
    /// manage their own colours (the log pane) opt out via <see cref="SkipTag"/>.</summary>
    public const string SkipTag = "theme:skip";

    public static void Apply(Control root)
    {
        if (root is Form f)
        {
            f.BackColor = Current.Window;
            f.ForeColor = Current.Text;
        }

        foreach (Control c in root.Controls)
        {
            if (c.Tag as string == SkipTag) continue;

            switch (c)
            {
                case TreeView tree:
                    tree.BackColor = Current.Surface;
                    tree.ForeColor = Current.Text;
                    tree.LineColor = Current.SubtleText;
                    break;

                case Button b:
                    b.BackColor = Current.IsDark ? Color.FromArgb(58, 58, 58) : SystemColors.Control;
                    b.ForeColor = Current.Text;
                    b.FlatStyle = Current.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                    if (Current.IsDark) b.FlatAppearance.BorderColor = Color.FromArgb(82, 82, 82);
                    break;

                case ListView lv:
                    lv.BackColor = Current.Surface;
                    lv.ForeColor = Current.Text;
                    break;

                default:
                    c.BackColor = Current.Window;
                    c.ForeColor = Current.Text;
                    break;
            }

            if (c.HasChildren) Apply(c);
        }
    }
}
