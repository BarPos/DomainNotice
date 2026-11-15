using MudBlazor;

namespace DomainNotice.Console.Layout;

/// <summary>Own palette (blue accent, dark navigation in both themes). No third-party branding.</summary>
public static class ConsoleTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#1e6fd9",
            Secondary = "#5c6b7a",
            AppbarBackground = "#16202b",
            AppbarText = "#e8edf2",
            DrawerBackground = "#1f2a37",
            DrawerText = "#c9d3df",
            DrawerIcon = "#8fa3b8",
            Background = "#f3f5f8",
            Surface = "#ffffff",
            Success = "#2e9e44",
            Error = "#d23b3b",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#4d94ff",
            Secondary = "#8fa3b8",
            AppbarBackground = "#0f161e",
            AppbarText = "#e8edf2",
            DrawerBackground = "#161f29",
            DrawerText = "#c9d3df",
            DrawerIcon = "#8fa3b8",
            Background = "#111820",
            Surface = "#1a232d",
            Success = "#3fbf5a",
            Error = "#ef5a5a",
        },
        LayoutProperties = new LayoutProperties { DrawerWidthLeft = "232px", DrawerMiniWidthLeft = "56px" },
    };
}
