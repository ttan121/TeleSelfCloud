using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeleSelfCloud.Infrastructure.Transfers;

namespace TeleSelfCloud.Desktop;

internal sealed class FilePreviewWindow : Window
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".jpg", ".jpeg", ".png", ".tif", ".tiff"
    };

    private static readonly HashSet<string> ExternalPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".cs", ".css", ".html", ".ini", ".js", ".json", ".log", ".md", ".txt", ".xml", ".yaml", ".yml"
    };

    private static readonly HashSet<string> StreamingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".m4v", ".mkv", ".mov", ".mp4", ".webm"
    };

    public const long MaximumPreviewBytes = 100L * 1024 * 1024;
    private const int MaximumTextBytes = 2 * 1024 * 1024;

    public FilePreviewWindow(string fileName, string? path, string message, bool darkMode)
        : this(fileName, BuildMessage(message, darkMode), darkMode)
    {
        if (path != null) Closed += (_, _) => DeleteTemporaryFiles(path);
    }

    private FilePreviewWindow(string fileName, UIElement content, bool darkMode)
    {
        Title = $"{UiText.Instance.Get("preview.title")} — {fileName}";
        Width = 920;
        Height = 680;
        MinWidth = 560;
        MinHeight = 400;
        Background = darkMode ? new SolidColorBrush(Color.FromRgb(11, 18, 32)) : new SolidColorBrush(Color.FromRgb(246, 247, 251));
        Opacity = 0;
        ContentRendered += (_, _) => Opacity = 1;
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var header = new Border
        {
            Background = darkMode ? new SolidColorBrush(Color.FromRgb(18, 28, 43)) : Brushes.White,
            BorderBrush = darkMode ? new SolidColorBrush(Color.FromRgb(41, 53, 72)) : new SolidColorBrush(Color.FromRgb(225, 228, 236)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 18, 24, 18),
            Child = new TextBlock
            {
                Text = fileName,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = darkMode ? new SolidColorBrush(Color.FromRgb(243, 244, 246)) : new SolidColorBrush(Color.FromRgb(26, 34, 52)),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(content, 1);
        layout.Children.Add(header);
        layout.Children.Add(content);
        Content = layout;
    }

    public static bool SupportsPreview(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return SupportsStreamingPreview(fileName) || ImageExtensions.Contains(extension) || TextExtensions.Contains(extension) ||
               ExternalPreviewExtensions.Contains(extension) || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SupportsStreamingPreview(string fileName) => StreamingExtensions.Contains(Path.GetExtension(fileName));

    public static FilePreviewWindow CreateStreaming(string fileName, string url, MediaStreamServer server, bool darkMode)
    {
        ArgumentNullException.ThrowIfNull(server);
        var foreground = darkMode ? new SolidColorBrush(Color.FromRgb(193, 203, 216)) : new SolidColorBrush(Color.FromRgb(68, 74, 88));
        var launch = new Button
        {
            Content = UiText.Instance.Get("preview.media.open"),
            Padding = new Thickness(18, 11, 18, 11),
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        AutomationProperties.SetName(launch, UiText.Instance.Get("preview.media.open"));
        launch.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(Window.GetWindow(launch), UiText.Instance.LocalizeMessage(ex.Message), UiText.Instance.Get("preview.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(28),
            Children =
            {
                new TextBlock
                {
                    Text = UiText.Instance.Get("preview.media.help"),
                    Foreground = foreground,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 540,
                    Margin = new Thickness(0, 0, 0, 20)
                },
                launch
            }
        };
        var window = new FilePreviewWindow(fileName, content, darkMode);
        window.Closed += (_, _) => server.Dispose();
        return window;
    }

    public static async Task<FilePreviewWindow> CreateAsync(string fileName, string path, bool darkMode)
    {
        var extension = Path.GetExtension(fileName);
        var surface = darkMode ? new SolidColorBrush(Color.FromRgb(18, 28, 43)) : Brushes.White;
        var border = darkMode ? new SolidColorBrush(Color.FromRgb(41, 53, 72)) : new SolidColorBrush(Color.FromRgb(225, 228, 236));
        UIElement content;
        if (ExternalPreviewExtensions.Contains(extension))
        {
            var open = new Button
            {
                Content = UiText.Instance.Get("preview.pdf.open"),
                Padding = new Thickness(16, 9, 16, 9),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    MessageBox.Show(Window.GetWindow(open), UiText.Instance.LocalizeMessage(ex.Message), UiText.Instance.Get("preview.title"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
                Children =
                {
                    new TextBlock
                    {
                        Text = UiText.Instance.Get("preview.pdf.external"),
                        Foreground = darkMode ? new SolidColorBrush(Color.FromRgb(193, 203, 216)) : new SolidColorBrush(Color.FromRgb(68, 74, 88)),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        MaxWidth = 480,
                        Margin = new Thickness(0, 0, 0, 16)
                    },
                    open
                }
            };
        }
        else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var entries = await Task.Run(() => ZipPreviewReader.Read(path));
            var list = new ListBox
            {
                ItemsSource = entries.Select(entry => entry.IsDirectory
                    ? $"{UiText.Instance.Get("preview.zip.folder")}  {entry.Name}"
                    : $"{entry.Name}  •  {entry.UncompressedSize:N0} {UiText.Instance.Get("preview.zip.bytes")}"),
                Background = surface,
                Foreground = darkMode ? new SolidColorBrush(Color.FromRgb(224, 231, 240)) : new SolidColorBrush(Color.FromRgb(38, 45, 60)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12)
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            AutomationProperties.SetName(list, UiText.Instance.Get("preview.zip.contents"));
            content = entries.Count == 0
                ? BuildMessage(UiText.Instance.Get("preview.zip.empty"), darkMode)
                : list;
        }
        else if (ImageExtensions.Contains(extension))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1600;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            content = new Border
            {
                Margin = new Thickness(20),
                Background = surface,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Child = new Image { Source = image, Stretch = Stretch.Uniform, MaxWidth = 1600 }
            };
        }
        else if (TextExtensions.Contains(extension))
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumTextBytes)
                content = BuildMessage(UiText.Instance.Get("preview.textTooLarge"), darkMode);
            else
            {
                var text = await File.ReadAllTextAsync(path);
                content = new Border
                {
                    Margin = new Thickness(20),
                    Background = surface,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    Child = new TextBox
                    {
                        Text = text,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.NoWrap,
                        AcceptsReturn = true,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13,
                        Padding = new Thickness(18),
                        BorderThickness = new Thickness(0),
                        Background = surface,
                        Foreground = darkMode ? new SolidColorBrush(Color.FromRgb(224, 231, 240)) : new SolidColorBrush(Color.FromRgb(38, 45, 60))
                    }
                };
            }
        }
        else
        {
            content = BuildMessage(UiText.Instance.Get("preview.unavailable"), darkMode);
        }

        var window = new FilePreviewWindow(fileName, content, darkMode);
        window.Closed += (_, _) => DeleteTemporaryFiles(path);
        return window;
    }

    private static UIElement BuildMessage(string message, bool darkMode) => new Border
    {
        Margin = new Thickness(20),
        Padding = new Thickness(28),
        Background = darkMode ? new SolidColorBrush(Color.FromRgb(18, 28, 43)) : Brushes.White,
        BorderBrush = darkMode ? new SolidColorBrush(Color.FromRgb(41, 53, 72)) : new SolidColorBrush(Color.FromRgb(225, 228, 236)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = message,
            FontSize = 15,
            Foreground = darkMode ? new SolidColorBrush(Color.FromRgb(193, 203, 216)) : new SolidColorBrush(Color.FromRgb(68, 74, 88)),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 560
        }
    };

    private static void DeleteTemporaryFiles(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
