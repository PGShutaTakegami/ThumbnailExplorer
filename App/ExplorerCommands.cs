using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThumbnailExplorer;

public partial class MainWindow
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private readonly ExplorerSettings _settings = ExplorerSettings.Load();
    private bool _fileOperationRunning;

    public static readonly DependencyProperty TileScaleProperty = DependencyProperty.Register(
        nameof(TileScale), typeof(double), typeof(MainWindow), new PropertyMetadata(1.0));
    public double TileScale { get => (double)GetValue(TileScaleProperty); private set => SetValue(TileScaleProperty, value); }

    private void InitializeExplorerSettings()
    {
        SortPicker.SelectedIndex = Math.Clamp(_settings.Sort, 0, 3);
        DescendingToggle.IsChecked = _settings.SortDescending;
        HiddenToggle.IsChecked = _settings.ShowHidden;
        ExtensionsToggle.IsChecked = _settings.ShowExtensions;
        ThemePicker.SelectedIndex = _settings.Theme switch { "Dark" => 1, "Beige" => 2, _ => 0 };
        ZoomSlider.Value = double.IsFinite(_settings.Zoom) ? Math.Clamp(_settings.Zoom, .65, 1.65) : 1;
        UpdateZoom();
        ApplyTheme();
        Files.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(Files, source) is ListBoxItem { IsSelected: false } item)
            {
                Files.SelectedItems.Clear();
                item.IsSelected = true;
            }
        };
        UpdateFileActions();
    }

    private void ApplyTheme()
    {
        string[] colors = _settings.Theme switch
        {
            "Dark" => ["#191D27", "#222837", "#2C3446", "#FFFFFF", "#FFFFFF", "#404B62", "#2B3449", "#414C69"],
            "Beige" => ["#F0E7D7", "#FFF8EB", "#FFFCF4", "#392C21", "#685848", "#DBCCB4", "#EFE2CD", "#E9DCC5"],
            _ => ["#F1F4F9", "#FFFFFF", "#FFFFFF", "#17243A", "#52627C", "#D7DEEA", "#EEF1FA", "#E5E2FC"]
        };
        string[] keys = ["Workspace", "Panel", "Field", "InkText", "Muted", "Line", "Well", "Hover"];
        for (int i = 0; i < keys.Length; i++)
            Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        ApplyTitleBarTheme(this);
    }

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        _settings.Theme = ThemePicker.SelectedIndex switch { 1 => "Dark", 2 => "Beige", _ => "Light" };
        ApplyTheme();
    }

    private void UpdateZoom()
    {
        TileScale = ZoomSlider.Value;
        ZoomLabel.Text = TileScale.ToString("P0");
    }

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        _settings.Zoom = ZoomSlider.Value;
        UpdateZoom();
    }

    private void FilesMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + Math.Sign(e.Delta) * .1, ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        _settings.Sort = SortPicker.SelectedIndex;
        ApplySort();
    }

    private void SortOrderChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _settings.SortDescending = DescendingToggle.IsChecked == true;
        ApplySort();
    }

    private void ApplySort()
    {
        _entries = SortEntries(_entries);
        _page = 0;
        ShowPage();
    }

    private void HiddenChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _settings.ShowHidden = HiddenToggle.IsChecked == true;
        if (_folder is not null && !Refresh())
        {
            _settings.ShowHidden = !_settings.ShowHidden;
            _loading = true;
            HiddenToggle.IsChecked = _settings.ShowHidden;
            _loading = false;
        }
    }

    private void ExtensionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _settings.ShowExtensions = ExtensionsToggle.IsChecked == true;
        foreach (FileEntry item in _entries)
            item.SetExtensionVisibility(_settings.ShowExtensions);
        if (_selectedFile is { IsDirectory: false } file)
            SelectedName.Text = file.DisplayName;
    }

    private FileEntry[] SelectedFiles() => Files.SelectedItems.Cast<FileEntry>().ToArray();

    private void UpdateFileActions()
    {
        int count = Files.SelectedItems.Count;
        RenameButton.IsEnabled = count == 1;
        CopyButton.IsEnabled = CutButton.IsEnabled = ZipButton.IsEnabled = count > 0;
    }

    private void FileMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = item.Tag switch
            {
                "single" => Files.SelectedItems.Count == 1,
                "multiple" => Files.SelectedItems.Count > 0,
                _ => _folder is not null
            };
        }
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void NewFolderClick(object sender, RoutedEventArgs e)
    {
        if (_folder is not { } folder || AskForName(L.Get("NewFolder"), L.Get("DefaultFolder")) is not { } name)
            return;
        try
        {
            string path = FileOperations.ValidateName(name, folder);
            if (Path.Exists(path))
                throw new IOException(L.Get("AlreadyExists"));
            Directory.CreateDirectory(path);
            Refresh();
            Status.Text = L.Format("Created", Path.GetFileName(path));
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("CreateFolderError"), error);
        }
    }

    private async void RenameClick(object sender, RoutedEventArgs e)
    {
        if (_folder is not { } folder || SelectedFiles() is not [var file] || !ConfirmDiscard())
            return;
        if (AskForName(L.Get("Rename"), file.Name, selectStem: !file.IsDirectory) is not { } name)
            return;
        try
        {
            string destination = FileOperations.ValidateName(name, folder);
            if (destination == file.FullPath)
                return;
            await RunFileOperation(() => FileOperations.Transfer(file.FullPath, destination, move: true, _store));
            LoadEditor(null);
            Refresh();
            SelectPath(destination);
            Status.Text = L.Format("Renamed", file.Name);
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            if (!Path.Exists(file.FullPath))
                LoadEditor(null);
            Refresh();
            ShowError(L.Get("RenameError"), error);
        }
    }

    private string? AskForName(string title, string initial, bool selectStem = false)
    {
        var input = new TextBox
        {
            Text = initial, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(7),
            MinWidth = 310
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = L.Get("Cancel"), IsCancel = true, MinWidth = 85 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(input);
        content.Children.Add(buttons);
        var dialog = new Window
        {
            Owner = this, Title = title, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Content = content
        };
        dialog.Background = (Brush)Resources["Panel"];
        dialog.Foreground = (Brush)Resources["InkText"];
        input.Background = (Brush)Resources["Field"];
        input.Foreground = dialog.Foreground;
        ok.Background = cancel.Background = input.Background;
        ok.Foreground = cancel.Foreground = dialog.Foreground;
        dialog.SourceInitialized += (_, _) => ApplyTitleBarTheme(dialog);
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            int stemLength = selectStem ? Path.GetFileNameWithoutExtension(initial).Length : initial.Length;
            input.Select(0, stemLength == 0 ? initial.Length : stemLength);
        };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void CopyClick(object sender, RoutedEventArgs e) => PutFilesOnClipboard(cut: false);
    private void CutClick(object sender, RoutedEventArgs e) => PutFilesOnClipboard(cut: true);

    private void PutFilesOnClipboard(bool cut)
    {
        string[] paths = SelectedFiles().Select(file => file.FullPath).ToArray();
        if (paths.Length == 0)
            return;
        try
        {
            SetClipboardFiles(paths, cut);
            Status.Text = cut ? L.Format("CutCount", paths.Length) : L.Format("CopiedCount", paths.Length);
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("WriteClipboardError"), error);
        }
    }

    private static void SetClipboardFiles(string[] paths, bool cut)
    {
        var data = new DataObject(DataFormats.FileDrop, paths);
        using var effect = new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1));
        data.SetData(DropEffectFormat, effect);
        Clipboard.SetDataObject(data, copy: true);
    }

    private async void PasteClick(object sender, RoutedEventArgs e)
    {
        if (_folder is not { } folder)
            return;
        try
        {
            IDataObject? data = Clipboard.GetDataObject();
            if (data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            {
                Status.Text = L.Get("ClipboardEmpty");
                return;
            }

            bool move = data.GetData(DropEffectFormat, autoConvert: false) is MemoryStream effect &&
                effect.ToArray() is { Length: >= 4 } bytes && (BitConverter.ToInt32(bytes) & 2) != 0;
            var failed = new List<string>();
            var errors = new List<string>();
            int completed = 0;
            await RunFileOperation(() =>
            {
                foreach (string input in paths.Distinct(StringComparer.Ordinal))
                {
                    try
                    {
                        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
                        string destination = FileOperations.ValidateName(Path.GetFileName(source), folder);
                        if (!move || !source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                            destination = FileOperations.AvailableName(destination, Directory.Exists(source));
                        FileOperations.Transfer(source, destination, move, _store);
                        completed++;
                    }
                    catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
                    {
                        if (Path.Exists(input))
                            failed.Add(input);
                        errors.Add($"{Path.GetFileName(input)}: {error.Message}");
                    }
                }
            });
            Refresh();
            Status.Text = L.Format(move ? "MovedCount" : "CopiedCount", completed);
            if (move && Clipboard.GetData(DataFormats.FileDrop) is string[] current && current.SequenceEqual(paths))
            {
                if (failed.Count > 0)
                    SetClipboardFiles(failed.ToArray(), cut: true);
                else
                    Clipboard.Clear();
            }
            if (errors.Count > 0)
                ShowError(L.Format("FailedCount", errors.Count), new IOException(string.Join("\n", errors.Take(8))));
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("PasteError"), error);
        }
    }

    private async void ZipClick(object sender, RoutedEventArgs e)
    {
        if (_folder is not { } folder || SelectedFiles() is not { Length: > 0 } files)
            return;
        string defaultName = files.Length == 1
            ? (files[0].IsDirectory ? files[0].Name : Path.GetFileNameWithoutExtension(files[0].Name)) + ".zip"
            : L.Get("DefaultArchive");
        if (AskForName(L.Get("CompressZip"), defaultName, selectStem: true) is not { } name)
            return;
        try
        {
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                name += ".zip";
            string destination = FileOperations.AvailableName(FileOperations.ValidateName(name, folder), false);
            await RunFileOperation(() => FileOperations.Compress(files.Select(file => file.FullPath), destination));
            Refresh();
            Status.Text = L.Format("Created", Path.GetFileName(destination));
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("ZipError"), error);
        }
    }

    private async Task RunFileOperation(Action operation)
    {
        _fileOperationRunning = true;
        IsEnabled = false;
        Status.Text = L.Get("Working");
        try { await Task.Run(operation); }
        finally
        {
            IsEnabled = true;
            _fileOperationRunning = false;
        }
    }

    private bool HandleFileShortcut(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (Files.IsKeyboardFocusWithin && e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
            RenameClick(Files, e);
        else if (Files.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C: CopyClick(Files, e); break;
                case Key.X: CutClick(Files, e); break;
                case Key.V: PasteClick(Files, e); break;
                default: return false;
            }
        }
        else
            return false;

        e.Handled = true;
        return true;
    }
}
