using System;
using System.Windows;
using System.Windows.Markup;

namespace TransferFTP
{
    /// <summary>App-wide control styles so every window gets the dark, rounded look without per-control code.</summary>
    public static class Styles
    {
        /// <summary>Swaps colour tokens like {TEXT} in a XAML string for the current theme's hex values.</summary>
        public static string Fill(string xaml)
        {
            return xaml
                .Replace("{BG}", Theme.Hex(Theme.BgColor))
                .Replace("{PANEL}", Theme.Hex(Theme.PanelColor))
                .Replace("{TEXT}", Theme.Hex(Theme.TextColor))
                .Replace("{MUTED}", Theme.Hex(Theme.MutedColor))
                .Replace("{ACCENT}", Theme.Hex(Theme.AccentColor))
                .Replace("{ACCENTTINT}", Theme.Hex(Theme.AccentTintColor))
                .Replace("{TEAL}", Theme.Hex(Theme.TealColor))
                .Replace("{GREEN}", Theme.Hex(Theme.GreenColor))
                .Replace("{RED}", Theme.Hex(Theme.RedColor))
                .Replace("{LINE}", Theme.Hex(Theme.LineColor))
                .Replace("{SUBTLE}", Theme.Hex(Theme.SubtleColor))
                .Replace("{OFF}", Theme.Hex(Theme.OffColor))
                .Replace("{POPUP}", Theme.Hex(Theme.PopupColor))
                .Replace("{DARK}", Theme.Hex(Theme.DarkColor))
                .Replace("{THUMB}", Theme.IsLight ? "#38000000" : "#33FFFFFF")
                .Replace("{THUMBHOVER}", Theme.IsLight ? "#66000000" : "#66FFFFFF");
        }

        public static void Install()
        {
            try
            {
                var dict = (ResourceDictionary)XamlReader.Parse(Fill(Xaml));
                Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch (Exception ex) { Log.Write("Styles: " + ex.Message); }
        }

        private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <!-- Scroll bars: thin, rounded, see-through -->
  <Style x:Key='TfThumb' TargetType='Thumb'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'><Border CornerRadius='4' Background='{TemplateBinding Background}'/></ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='TfPage' TargetType='RepeatButton'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <ControlTemplate x:Key='TfVertical' TargetType='ScrollBar'>
    <Grid Background='Transparent'>
      <Track x:Name='PART_Track' IsDirectionReversed='True'>
        <Track.DecreaseRepeatButton><RepeatButton Style='{StaticResource TfPage}' Command='ScrollBar.PageUpCommand'/></Track.DecreaseRepeatButton>
        <Track.IncreaseRepeatButton><RepeatButton Style='{StaticResource TfPage}' Command='ScrollBar.PageDownCommand'/></Track.IncreaseRepeatButton>
        <Track.Thumb><Thumb x:Name='thumb' Style='{StaticResource TfThumb}' Background='{THUMB}' Width='5' Margin='0,6' HorizontalAlignment='Center'/></Track.Thumb>
      </Track>
    </Grid>
    <ControlTemplate.Triggers>
      <Trigger Property='IsMouseOver' Value='True'>
        <Setter TargetName='thumb' Property='Width' Value='8'/>
        <Setter TargetName='thumb' Property='Background' Value='{THUMBHOVER}'/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <ControlTemplate x:Key='TfHorizontal' TargetType='ScrollBar'>
    <Grid Background='Transparent'>
      <Track x:Name='PART_Track' IsDirectionReversed='False'>
        <Track.DecreaseRepeatButton><RepeatButton Style='{StaticResource TfPage}' Command='ScrollBar.PageLeftCommand'/></Track.DecreaseRepeatButton>
        <Track.IncreaseRepeatButton><RepeatButton Style='{StaticResource TfPage}' Command='ScrollBar.PageRightCommand'/></Track.IncreaseRepeatButton>
        <Track.Thumb><Thumb x:Name='thumb' Style='{StaticResource TfThumb}' Background='{THUMB}' Height='5' Margin='6,0' VerticalAlignment='Center'/></Track.Thumb>
      </Track>
    </Grid>
    <ControlTemplate.Triggers>
      <Trigger Property='IsMouseOver' Value='True'>
        <Setter TargetName='thumb' Property='Height' Value='8'/>
        <Setter TargetName='thumb' Property='Background' Value='{THUMBHOVER}'/>
      </Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <Style TargetType='ScrollBar'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Width' Value='14'/>
    <Setter Property='MinWidth' Value='14'/>
    <Setter Property='Template' Value='{StaticResource TfVertical}'/>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='MinWidth' Value='0'/>
        <Setter Property='Height' Value='14'/>
        <Setter Property='MinHeight' Value='14'/>
        <Setter Property='Template' Value='{StaticResource TfHorizontal}'/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- Text inputs -->
  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='Background' Value='{SUBTLE}'/>
    <Setter Property='BorderBrush' Value='{LINE}'/>
    <Setter Property='CaretBrush' Value='{TEXT}'/>
    <Setter Property='SelectionBrush' Value='{ACCENT}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='10,0'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8' SnapsToDevicePixels='True'>
            <Grid>
              <TextBlock x:Name='hint' Text='{TemplateBinding Tag}' Foreground='{MUTED}' Margin='{TemplateBinding Padding}' Padding='2,0,0,0'
                         VerticalAlignment='{TemplateBinding VerticalContentAlignment}' IsHitTestVisible='False' Visibility='Collapsed' TextTrimming='CharacterEllipsis'/>
              <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' Focusable='False'/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='Text' Value=''><Setter TargetName='hint' Property='Visibility' Value='Visible'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='{ACCENT}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='bd' Property='Opacity' Value='0.5'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='PasswordBox'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='Background' Value='{SUBTLE}'/>
    <Setter Property='BorderBrush' Value='{LINE}'/>
    <Setter Property='CaretBrush' Value='{TEXT}'/>
    <Setter Property='SelectionBrush' Value='{ACCENT}'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='10,0'/>
    <Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='PasswordBox'>
          <Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8' SnapsToDevicePixels='True'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' Focusable='False'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='{ACCENT}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Combo boxes -->
  <ControlTemplate x:Key='TfComboToggle' TargetType='ToggleButton'>
    <Border x:Name='bd' Background='{SUBTLE}' BorderBrush='{LINE}' BorderThickness='1' CornerRadius='8' SnapsToDevicePixels='True'>
      <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0' Data='M0,0 L4,4 8,0' Stroke='{MUTED}' StrokeThickness='1.6'/>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='{ACCENT}'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='Height' Value='34'/>
    <Setter Property='MaxDropDownHeight' Value='360'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton Template='{StaticResource TfComboToggle}' Focusable='False' ClickMode='Press'
                          IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'/>
            <ContentPresenter IsHitTestVisible='False' Margin='12,0,30,0' VerticalAlignment='Center' HorizontalAlignment='Left'
                              Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'/>
            <Popup Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
              <Border Background='{POPUP}' BorderBrush='{LINE}' BorderThickness='1' CornerRadius='10' Margin='0,4,0,0' Padding='4'
                      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='{TemplateBinding MaxDropDownHeight}'>
                <ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='bd' Padding='10,7' CornerRadius='6' Background='Transparent'><ContentPresenter/></Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='{SUBTLE}'/></Trigger>
            <Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='{ACCENTTINT}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Check boxes -->
  <Style TargetType='CheckBox'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <StackPanel Orientation='Horizontal' Background='Transparent'>
            <Border x:Name='box' Width='18' Height='18' CornerRadius='5' BorderThickness='1.5' BorderBrush='{OFF}' Background='Transparent' VerticalAlignment='Center'>
              <Path x:Name='tick' Data='M3,9 L7,13 15,4' Stroke='{DARK}' StrokeThickness='2.2' Visibility='Collapsed' Stretch='Uniform' Margin='3'/>
            </Border>
            <ContentPresenter x:Name='cp' Margin='8,0,0,0' VerticalAlignment='Center'/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='box' Property='Background' Value='{ACCENT}'/>
              <Setter TargetName='box' Property='BorderBrush' Value='{ACCENT}'/>
              <Setter TargetName='tick' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='Content' Value='{x:Null}'><Setter TargetName='cp' Property='Margin' Value='0'/></Trigger>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='box' Property='BorderBrush' Value='{ACCENT}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Progress bars -->
  <Style TargetType='ProgressBar'>
    <Setter Property='Height' Value='6'/>
    <Setter Property='Foreground' Value='{ACCENT}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ProgressBar'>
          <Grid>
            <Border x:Name='PART_Track' CornerRadius='3' Background='{SUBTLE}'/>
            <Border x:Name='PART_Indicator' CornerRadius='3' Background='{TemplateBinding Foreground}' HorizontalAlignment='Left'/>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Tooltips -->
  <Style TargetType='ToolTip'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='{POPUP}' BorderBrush='{LINE}' BorderThickness='1' CornerRadius='10' Padding='12,8' MaxWidth='340'>
            <ContentPresenter TextElement.FontSize='12.5'>
              <ContentPresenter.Resources>
                <Style TargetType='TextBlock'><Setter Property='TextWrapping' Value='Wrap'/></Style>
              </ContentPresenter.Resources>
            </ContentPresenter>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Context menus -->
  <Style TargetType='ContextMenu'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Background='{POPUP}' BorderBrush='{LINE}' BorderThickness='1' CornerRadius='10' Padding='4'>
            <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='bd' Padding='12,7' CornerRadius='6' Background='Transparent' MinWidth='220'>
            <Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
              <ContentPresenter ContentSource='Header' RecognizesAccessKey='True'/>
              <TextBlock Grid.Column='1' Text='{TemplateBinding InputGestureText}' Foreground='{MUTED}' Margin='24,0,0,0'/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='{SUBTLE}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='bd' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Separator'><Border Height='1' Margin='8,4' Background='{LINE}'/></ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- File lists -->
  <Style TargetType='ListView'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='VirtualizingPanel.IsVirtualizing' Value='True'/>
    <Setter Property='VirtualizingPanel.VirtualizationMode' Value='Recycling'/>
  </Style>
  <Style TargetType='ListViewItem'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ListViewItem'>
          <Border x:Name='bd' Background='Transparent' CornerRadius='6' Padding='0,4' SnapsToDevicePixels='True'>
            <GridViewRowPresenter Columns='{TemplateBinding GridView.ColumnCollection}' Content='{TemplateBinding Content}' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{SUBTLE}'/></Trigger>
            <Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='{ACCENTTINT}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='GridViewColumnHeader'>
    <Setter Property='Foreground' Value='{MUTED}'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='HorizontalContentAlignment' Value='Left'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='GridViewColumnHeader'>
          <Grid Background='Transparent'>
            <Border Padding='10,8,10,8' BorderThickness='0,0,0,1' BorderBrush='{LINE}'>
              <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'/>
            </Border>
            <Thumb x:Name='PART_HeaderGripper' HorizontalAlignment='Right' Width='8' Margin='0,0,-4,0' Cursor='SizeWE'>
              <Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='Transparent'/></ControlTemplate></Thumb.Template>
            </Thumb>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='{TEXT}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ListBox'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='BorderThickness' Value='0'/>
  </Style>
  <Style TargetType='ListBoxItem'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ListBoxItem'>
          <Border x:Name='bd' Background='Transparent' CornerRadius='8' Padding='10,8' Margin='0,1'><ContentPresenter/></Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{SUBTLE}'/></Trigger>
            <Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='{ACCENTTINT}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Plain buttons used inside list rows -->
  <Style TargetType='Button'>
    <Setter Property='Foreground' Value='{TEXT}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='bd' Background='{SUBTLE}' CornerRadius='8' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{ACCENTTINT}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Splitters -->
  <Style TargetType='GridSplitter'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Focusable' Value='False'/>
  </Style>
</ResourceDictionary>";
    }
}
