using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace ClipGlue.Controls;

/// <summary>
/// A thin, rounded, chrome-less scrollbar for the file list and the console.
///
/// This is the one place in the app that builds a visual from a XAML string
/// rather than from imperative C#. A ScrollBar template has to hand a
/// <see cref="Track"/> its Thumb and its two page-scroll RepeatButtons
/// through Track's own dependency properties, and FrameworkElementFactory
/// (the imperative template builder used everywhere else here, e.g.
/// UiHelpers.BuildFlatTemplate) can only append CONTENT children - it has
/// no way to assign a nested element to a property like Track.Thumb. Parsing
/// the template once at startup is the smallest correct option; the palette
/// still comes from <see cref="Theme"/> rather than being duplicated as
/// literal hex, so there is a single source of truth for the colors.
///
/// Only ever attached to ScrollViewers whose horizontal bar is Disabled
/// (the file list and the console), so the template only has to be correct
/// for the vertical orientation.
/// </summary>
public static class ScrollBarStyles
{
    private static Style? _thin;

    /// <summary>Thin (10px gutter / 6px thumb) rounded scrollbar, with no
    /// arrow buttons and a transparent trough.</summary>
    public static Style Thin => _thin ??= Build();

    private static Style Build()
    {
        string xaml = $$"""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type ScrollBar}">
          <Setter Property="OverridesDefaultStyle" Value="True"/>
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Width" Value="10"/>
          <Setter Property="MinWidth" Value="10"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="{x:Type ScrollBar}">
                <Border Background="Transparent" SnapsToDevicePixels="True">
                  <Track x:Name="PART_Track" IsDirectionReversed="True" Focusable="False">
                    <Track.DecreaseRepeatButton>
                      <RepeatButton Command="ScrollBar.PageUpCommand" Focusable="False" IsTabStop="False">
                        <RepeatButton.Template>
                          <ControlTemplate TargetType="{x:Type RepeatButton}">
                            <Border Background="Transparent"/>
                          </ControlTemplate>
                        </RepeatButton.Template>
                      </RepeatButton>
                    </Track.DecreaseRepeatButton>
                    <Track.IncreaseRepeatButton>
                      <RepeatButton Command="ScrollBar.PageDownCommand" Focusable="False" IsTabStop="False">
                        <RepeatButton.Template>
                          <ControlTemplate TargetType="{x:Type RepeatButton}">
                            <Border Background="Transparent"/>
                          </ControlTemplate>
                        </RepeatButton.Template>
                      </RepeatButton>
                    </Track.IncreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb Focusable="False" IsTabStop="False" MinHeight="28">
                        <Thumb.Template>
                          <ControlTemplate TargetType="{x:Type Thumb}">
                            <Border x:Name="Shape" Margin="2,0,2,0" CornerRadius="3"
                                    Background="{{Hex(Theme.ScrollThumb)}}" SnapsToDevicePixels="True"/>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Shape" Property="Background" Value="{{Hex(Theme.ScrollThumbHover)}}"/>
                              </Trigger>
                              <Trigger Property="IsDragging" Value="True">
                                <Setter TargetName="Shape" Property="Background" Value="{{Hex(Theme.ScrollThumbHover)}}"/>
                              </Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </Border>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xaml));
        var style = (Style)XamlReader.Load(stream);
        style.Seal();
        return style;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
