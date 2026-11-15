using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace DomainNotice.Agent.Session;

/// <summary>
/// Colours and control styles of the announcement window. They match the preview in the Console
/// (console.css: .dn-agent-*), so what an admin sees while writing is what users get.
/// </summary>
internal static class NoticeTheme
{
    public static readonly SolidColorBrush TitleBarBackground = Brush(0x2B, 0x3A, 0x4A);
    public static readonly SolidColorBrush TitleBarText = Brush(0xFF, 0xFF, 0xFF);
    public static readonly SolidColorBrush Text = Brush(0x1B, 0x1F, 0x23);
    public static readonly SolidColorBrush MutedText = Brush(0x5B, 0x66, 0x70);
    public static readonly SolidColorBrush ErrorText = Brush(0xB0, 0x1E, 0x1E);
    public static readonly SolidColorBrush FooterBackground = Brush(0xF3, 0xF5, 0xF7);
    public static readonly SolidColorBrush FooterBorder = Brush(0xDD, 0xE3, 0xE9);
    public static readonly SolidColorBrush WindowBorder = Brush(0xB8, 0xC2, 0xCC);

    public const double BodyFontSize = 15;
    public static readonly System.Windows.Media.FontFamily Font = new("Segoe UI");

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(WpfColor.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Flat button like the preview: white with a grey border, or blue for the first (default) button. Null if the
    /// style cannot be built (then the standard button look is used; the window must never fail to show).
    /// </summary>
    public static Style? ButtonStyle(bool primary)
    {
        try
        {
            return primary ? Primary.Value : Secondary.Value;
        }
        catch (Exception ex)
        {
            SessionLog.Write($"Button style unavailable, using the standard look: {ex.Message}");
            return null;
        }
    }

    private static readonly Lazy<Style> Primary = new(() => BuildButtonStyle("#1E6FD9", "#1E6FD9", "#FFFFFF", "#1A5FBA"));
    private static readonly Lazy<Style> Secondary = new(() => BuildButtonStyle("#FFFFFF", "#8A99A8", "#1B1F23", "#EEF2F6"));

    private static Style BuildButtonStyle(string background, string border, string foreground, string hover)
    {
        var xaml = $$"""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Background" Value="{{background}}" />
              <Setter Property="BorderBrush" Value="{{border}}" />
              <Setter Property="Foreground" Value="{{foreground}}" />
              <Setter Property="BorderThickness" Value="1" />
              <Setter Property="Padding" Value="18,6" />
              <Setter Property="MinWidth" Value="80" />
              <Setter Property="FontSize" Value="14" />
              <Setter Property="Cursor" Value="Hand" />
              <Setter Property="FocusVisualStyle" Value="{x:Null}" />
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Button">
                    <Border x:Name="Chrome"
                            Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="3" Padding="{TemplateBinding Padding}">
                      <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Chrome" Property="Background" Value="{{hover}}" />
                      </Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="Chrome" Property="BorderThickness" Value="2" />
                      </Trigger>
                      <Trigger Property="IsEnabled" Value="False">
                        <Setter TargetName="Chrome" Property="Opacity" Value="0.5" />
                        <Setter Property="Cursor" Value="Arrow" />
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        var style = (Style)XamlReader.Parse(xaml);
        style.Seal();
        return style;
    }
}
