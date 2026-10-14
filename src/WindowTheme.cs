using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ApexPing;

internal static class WindowTheme
{
    internal static void Apply(Window window, bool? darkOverride = null)
    {
        bool dark = darkOverride ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        var palette = new Dictionary<string, string>
        {
            ["AppBackgroundBrush"] = dark ? "#202020" : "#F3F3F3",
            ["NavigationBrush"] = dark ? "#70202020" : "#70F3F3F3",
            ["ContentBrush"] = dark ? "#272727" : "#F9F9F9",
            ["SurfaceBrush"] = dark ? "#2D2D2D" : "#FFFFFF",
            ["SubtleBrush"] = dark ? "#262626" : "#F3F3F3",
            ["StrokeBrush"] = dark ? "#404040" : "#E5E5E5",
            ["TextBrush"] = dark ? "#F5F5F5" : "#1B1B1B",
            ["MutedBrush"] = dark ? "#B8B8B8" : "#606060",
            ["AccentBrush"] = dark ? "#FFADA3" : "#C53D32",
            ["AccentHoverBrush"] = dark ? "#FFC1B9" : "#B6332A",
            ["AccentTextBrush"] = dark ? "#39120E" : "#FFFFFF",
            ["HoverBrush"] = dark ? "#383838" : "#EBEBEB",
            ["SelectedBrush"] = dark ? "#3B3B3B" : "#E9E9E9",
            ["SuccessBrush"] = dark ? "#6CCB81" : "#147D3C"
        };
        foreach (var (key, value) in palette)
        {
            Color color = (Color)ColorConverter.ConvertFromString(value);
            if (SystemParameters.HighContrast)
                color = key is "TextBrush" or "MutedBrush" or "SuccessBrush" ? SystemColors.WindowTextColor
                    : key is "AccentBrush" or "AccentHoverBrush" or "SelectedBrush" ? SystemColors.HighlightColor
                    : key == "AccentTextBrush" ? SystemColors.HighlightTextColor
                    : key == "StrokeBrush" ? SystemColors.WindowTextColor : SystemColors.WindowColor;
            var brush = new SolidColorBrush(color); brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero) return;
        int useDark = dark ? 1 : 0, rounded = 2;
        DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int));
        DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        bool mica = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemParameters.HighContrast;
        int backdrop = mica ? 2 : 1;
        int result = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        if (mica && result == 0 && HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
        {
            window.Background = Brushes.Transparent;
            target.BackgroundColor = Colors.Transparent;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
        }
        else
        {
            window.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
            var margins = new Margins();
            DwmExtendFrameIntoClientArea(handle, ref margins);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint handle, ref Margins margins);
}
