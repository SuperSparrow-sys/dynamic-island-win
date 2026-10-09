using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicBay.Settings;

/// <summary>Light/dark palette for the settings window, following the Windows app theme.</summary>
public static class SettingsTheme
{
    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static ResourceDictionary Create(bool light)
    {
        var d = new ResourceDictionary();
        void B(string key, uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            d[key] = b;
        }
        if (light)
        {
            B("S.Window", 0xFFF3F3F3);
            B("S.Text", 0xFF1B1B1F);
            B("S.Text2", 0xFF5E5E66);
            B("S.Text3", 0xFF8E8E96);
            B("S.Card", 0xB3FFFFFF);
            B("S.CardBorder", 0x14000000);
            B("S.Hover", 0x0A000000);
            B("S.Pressed", 0x14000000);
            B("S.Divider", 0x12000000);
            B("S.Field", 0xFFFFFFFF);
            B("S.FieldBorder", 0x24000000);
            B("S.SwitchOff", 0x29787880);
            B("S.NavSelected", 0x0F000000);
            B("S.Preview", 0xFFDDE3EE);
        }
        else
        {
            B("S.Window", 0xFF1F1F22);
            B("S.Text", 0xFFFFFFFF);
            B("S.Text2", 0xFFB4B4BC);
            B("S.Text3", 0xFF7C7C86);
            B("S.Card", 0x0DFFFFFF);
            B("S.CardBorder", 0x12FFFFFF);
            B("S.Hover", 0x0FFFFFFF);
            B("S.Pressed", 0x17FFFFFF);
            B("S.Divider", 0x14FFFFFF);
            B("S.Field", 0x0FFFFFFF);
            B("S.FieldBorder", 0x1FFFFFFF);
            B("S.SwitchOff", 0x52787880);
            B("S.NavSelected", 0x12FFFFFF);
            B("S.Preview", 0xFF2C2F3A);
        }
        B("S.Accent", 0xFF0A84FF);
        B("S.AccentText", 0xFFFFFFFF);
        B("S.Green", 0xFF30D158);
        B("S.Red", 0xFFFF453A);
        B("S.Orange", 0xFFFF9F0A);
        B("S.Spotify", 0xFF1ED760);
        return d;
    }
}
