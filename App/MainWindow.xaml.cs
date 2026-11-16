using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThumbnailExplorer;

public partial class MainWindow : Window
{
    private const int PageSize = 96;
    private readonly ThumbnailStore _store = new(ThumbnailStore.DefaultRoot);
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private readonly List<StrokeCollection> _undo = new();
    private FileEntry[] _entries = [];
    private FileEntry? _selectedFile;
    private string? _folder;
    private CancellationTokenSource? _previewCancellation;
    private int _page;
    private bool _selecting;
    private bool _loading = true;
    private bool _dirty;
    private bool _hasSavedThumbnail;
    private bool _showingFolder;


    public MainWindow()
    {
        InitializeComponent();
        FontPicker.ItemsSource = Fonts.SystemFontFamilies.Select(font => font.Source)
            .Append(ThumbnailStore.DefaultText.Font).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCulture).ToArray();
        FontSizePicker.ItemsSource = new[] { 12, 16, 22, 28, 36, 44, 54, 64, 76, 92, 112, 136, 160 };
        BrushSizePicker.ItemsSource = new[] { 3, 6, 10, 16, 24, 36, 48 };
        BrushSizePicker.SelectedItem = 10;
        SetBrushWidth(10);
        SetColorSwatch(BrushColorButton, Color.FromRgb(103, 83, 220));
        Ink.DefaultDrawingAttributes.Color = Color.FromRgb(103, 83, 220);
        InitializeLayers();
        InitializeExplorerSettings();
        Ink.StrokeCollected += (_, e) =>
        {
            var before = new StrokeCollection(Ink.Strokes.Where(stroke => stroke != e.Stroke));
            RecordUndo(before);
            MarkDirty();
        };

        LoadEditor(null);
        _loading = false;
        UpdateNavigation();
        Loaded += (_, _) => { RefreshNavigationTree(); OpenInitialFolder(); };
    }

    private void OpenInitialFolder()
    {
        string? argument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (argument is not null && VisitPath(argument))
            return;

        if (_settings.Folder is { } lastFolder && Directory.Exists(lastFolder) && Navigate(lastFolder))
            return;

        foreach (string home in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.CurrentDirectory
        })
        {
            if (Directory.Exists(home) && Navigate(home))
                return;
        }
    }

    private bool VisitPath(string input)
    {
        try
        {
            string path = Path.GetFullPath(input.Trim().Trim('"'), _folder ?? Environment.CurrentDirectory);
            if (!File.Exists(path))
                return Navigate(path);

            string? parent = Path.GetDirectoryName(path);
            if (parent is null || !Navigate(parent))
                return false;

            return SelectPath(path);
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("OpenPathError"), error);
            return false;
        }
    }

    private bool SelectPath(string path)
    {
        var file = _entries.FirstOrDefault(entry => entry.FullPath.Equals(path, StringComparison.Ordinal))
            ?? _entries.FirstOrDefault(entry => entry.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (file is null)
            return false;
        if (file != _selectedFile && !ConfirmDiscard())
            return false;

        _showingFolder = true;
        SearchBox.Clear();
        _showingFolder = false;
        _page = Array.IndexOf(_entries, file) / PageSize;
        LoadEditor(file);
        ShowPage();
        Files.ScrollIntoView(file);
        return true;
    }

    private bool Navigate(string path, bool addHistory = true)
    {
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (string.Equals(path, _folder, StringComparison.Ordinal))
            {
                PathBox.Text = path;
                return true;
            }

            if (!ConfirmDiscard())
            {
                PathBox.Text = _folder ?? "";
                return false;
            }

            var items = ReadFolder(path);
            if (addHistory && _folder is not null)
            {
                _back.Push(_folder);
                _forward.Clear();
            }
            _folder = path;
            _entries = items;
            _page = 0;
            _showingFolder = true;
            PathBox.Text = path;
            SearchBox.Clear();
            _showingFolder = false;
            FolderTitle.Text = new DirectoryInfo(path).Name;
            LoadEditor(null);
            ShowPage();
            UpdateNavigation();
            Status.Text = path;
            _settings.Folder = path;
            SelectNavigationFolder();
            return true;
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            PathBox.Text = _folder ?? "";
            ShowError(L.Get("OpenFolderError"), error);
            return false;
        }
    }

    private FileEntry[] ReadFolder(string path)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = _settings.ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = false
        };
        var entries = new List<FileEntry>();
        foreach (FileSystemInfo item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            try
            {
                entries.Add(new FileEntry(item.FullName, item is DirectoryInfo, item.LastWriteTimeUtc,
                    item is FileInfo file ? file.Length : 0));
            }
            catch (FileNotFoundException)
            {
                // The file was removed between enumeration and reading its attributes.
            }
        }
        return SortEntries(entries);
    }

    private FileEntry[] SortEntries(IEnumerable<FileEntry> entries)
    {
        var grouped = entries.OrderByDescending(item => item.IsDirectory);
        var ordered = (_settings.Sort, _settings.SortDescending) switch
        {
            (1, false) => grouped.ThenBy(item => item.Modified),
            (1, true) => grouped.ThenByDescending(item => item.Modified),
            (2, false) => grouped.ThenBy(item => item.Kind, StringComparer.CurrentCultureIgnoreCase),
            (2, true) => grouped.ThenByDescending(item => item.Kind, StringComparer.CurrentCultureIgnoreCase),
            (3, false) => grouped.ThenBy(item => item.Size),
            (3, true) => grouped.ThenByDescending(item => item.Size),
            (_, true) => grouped.ThenByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => grouped.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        var result = ordered.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        foreach (FileEntry item in result)
            item.SetExtensionVisibility(_settings.ShowExtensions);
        return result;
    }

    private bool Refresh()
    {
        if (_folder is null)
            return false;
        try
        {
            FileEntry[] items = ReadFolder(_folder);
            FileEntry? current = _selectedFile;
            FileEntry? replacement = current is null ? null
                : items.FirstOrDefault(item => item.FullPath == current.FullPath && item.IsDirectory == current.IsDirectory);
            if (current is not null && replacement is null && !ConfirmDiscard())
                return false;
            _entries = items;
            if (replacement is null)
                LoadEditor(null);
            else
                _selectedFile = replacement;
            ShowPage();
            RefreshNavigationTree();
            Status.Text = L.Get("FolderRefreshed");
            return true;
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("RefreshError"), error);
            return false;
        }
    }

    private void ShowPage()
    {
        string filter = SearchBox.Text.Trim();
        FileEntry[] matches = _entries.Where(entry =>
            entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        int pageCount = Math.Max(1, (matches.Length + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pageCount - 1);
        var page = matches.Skip(_page * PageSize).Take(PageSize).ToArray();
        _selecting = true;
        Files.ItemsSource = page;
        Files.SelectedItem = page.Contains(_selectedFile) ? _selectedFile : null;
        _selecting = false;
        UpdateFileActions();
        if (VisualTreeHelper.GetChildrenCount(Files) > 0 &&
            VisualTreeHelper.GetChild(Files, 0) is Border { Child: ScrollViewer scroll })
            scroll.ScrollToTop();

        CountLabel.Text = pageCount == 1 ? L.Format("ItemCount", matches.Length) : L.Format("PagedItemCount", matches.Length, _page + 1, pageCount);
        EmptyFolder.Visibility = page.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviousPage.Visibility = NextPage.Visibility = pageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousPage.IsEnabled = _page > 0;
        NextPage.IsEnabled = _page + 1 < pageCount;
        StartPreviews(page);
    }

    private async void StartPreviews(FileEntry[] page)
    {
        _previewCancellation?.Cancel();
        var source = new CancellationTokenSource();
        _previewCancellation = source;
        CancellationToken token = source.Token;
        try
        {
            foreach (FileEntry file in page)
            {
                (BitmapSource? preview, bool custom) = await Task.Run(() => _store.Preview(file), token);
                if (token.IsCancellationRequested)
                    return;
                file.SetThumbnail(preview, custom);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (_previewCancellation == source)
                _previewCancellation = null;
            source.Dispose();
        }
    }

    private void UpdateNavigation()
    {
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
        UpButton.IsEnabled = _folder is not null && Directory.GetParent(_folder) is not null;
    }

    private void NavigateHistory(Stack<string> from, Stack<string> to)
    {
        if (from.TryPeek(out string? next) && _folder is { } current && Navigate(next, addHistory: false))
        {
            from.Pop();
            to.Push(current);
            UpdateNavigation();
        }
    }

    private void BackClick(object sender, RoutedEventArgs e) => NavigateHistory(_back, _forward);
    private void ForwardClick(object sender, RoutedEventArgs e) => NavigateHistory(_forward, _back);

    private void UpClick(object sender, RoutedEventArgs e)
    {
        if (_folder is not null && Directory.GetParent(_folder) is { } parent)
            Navigate(parent.FullName);
    }

    private void ChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.Get("ChooseDisplayFolder"), InitialDirectory = _folder ?? "" };
        if (dialog.ShowDialog(this) == true)
            Navigate(dialog.FolderName);
    }

    private void PathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            VisitPath(PathBox.Text);
            e.Handled = true;
        }
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _showingFolder)
            return;
        _page = 0;
        ShowPage();
    }

    private void PreviousPageClick(object sender, RoutedEventArgs e) { _page--; ShowPage(); }
    private void NextPageClick(object sender, RoutedEventArgs e) { _page++; ShowPage(); }

    private void FileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selecting)
            return;
        UpdateFileActions();
        var next = Files.SelectedItem as FileEntry;
        if (next == _selectedFile)
            return;
        if (ConfirmDiscard())
        {
            LoadEditor(next);
            return;
        }
        _selecting = true;
        Files.SelectedItem = _selectedFile is not null && Files.Items.Contains(_selectedFile) ? _selectedFile : null;
        _selecting = false;
        UpdateFileActions();
    }

    private void FileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(Files, source) is ListBoxItem { DataContext: FileEntry file } &&
            file == _selectedFile)
            Open(file);
    }

    private void FilesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Files.SelectedItem is FileEntry file)
        {
            Open(file);
            e.Handled = true;
        }
    }

    private void FilesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            VisitPath(paths[0]);
            e.Handled = true;
        }
    }

    private void OpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender is MenuItem ? Files.SelectedItem as FileEntry : _selectedFile) is { } file)
            Open(file);
    }

    private void Open(FileEntry file)
    {
        if (file.IsDirectory)
        {
            Navigate(file.FullPath);
            return;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file.FullPath) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception || ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("OpenFileError"), error);
        }
    }

    private void LoadEditor(FileEntry? file)
    {
        _selectedFile = file;
        ThumbnailDraft? draft = null;
        bool hasSelection = file is not null;
        Editor.IsEnabled = hasSelection;
        OpenButton.Content = file is { IsDirectory: true } ? L.Get("OpenFolder") : L.Get("OpenFile");
        SelectedName.Text = hasSelection ? file!.DisplayName : L.Get("SelectItemToEdit");
        SelectedName.ToolTip = hasSelection ? file!.FullPath : null;
        _hasSavedThumbnail = false;
        if (file is not null)
        {
            try
            {
                _hasSavedThumbnail = _store.Exists(file!.FullPath);
                draft = _store.Read(file.FullPath);
                if (draft is null && ThumbnailStore.LoadDefaultBackground(file) is { } image)
                    draft = new ThumbnailDraft([], image, new StrokeCollection());
            }
            catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
            {
                Status.Text = L.Format("LoadThumbnailError", error.Message);
            }
        }
        ApplyDraft(draft);
    }

    private void ApplyDraft(ThumbnailDraft? draft)
    {
        bool wasLoading = _loading;
        _loading = true;
        LoadLayers(draft?.Layers);
        BackgroundImage.Source = draft?.Background;
        Ink.Strokes = draft?.Strokes ?? new StrokeCollection();
        PenButton.IsChecked = false;
        EraserButton.IsChecked = false;
        _undo.Clear();
        _dirty = false;
        _loading = wasLoading;
        UpdateEditorButtons();
    }

    private void UpdateEditorButtons()
    {
        RemoveImageButton.IsEnabled = BackgroundImage.Source is not null;
        RemoveButton.IsEnabled = _hasSavedThumbnail;
        ClearInkButton.IsEnabled = Ink.Strokes.Count > 0;
        UndoButton.IsEnabled = _undo.Count > 0;
    }

    private void MarkDirty()
    {
        if (_loading)
            return;
        _dirty = true;
        Status.Text = L.Get("UnsavedChanges");
        UpdateEditorButtons();
    }

    private static void SetColorSwatch(Button button, Color color)
    {
        button.BorderBrush = new SolidColorBrush(color);
        button.BorderThickness = new Thickness(1, 1, 1, 3);
    }

    private static Color? ChooseColor(Color current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return null;
        return Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
    }

    private void ChooseImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.Get("ChooseImage"),
            Filter = $"{L.Get("ImageFiles")}|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.wdp;*.jxr|{L.Get("AllFiles")}|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            BackgroundImage.Source = ThumbnailStore.LoadImage(dialog.FileName, 1024);
            MarkDirty();
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("LoadImageError"), error);
        }
    }

    private void RemoveImageClick(object sender, RoutedEventArgs e)
    {
        BackgroundImage.Source = null;
        MarkDirty();
    }

    private void BrushSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BrushSizePicker.SelectedItem is int size)
            SetBrushWidth(size);
    }

    private void SetBrushWidth(int size)
    {
        Ink.DefaultDrawingAttributes.Width = size;
        Ink.DefaultDrawingAttributes.Height = size;
    }

    private void BrushColorClick(object sender, RoutedEventArgs e)
    {
        if (ChooseColor(Ink.DefaultDrawingAttributes.Color) is not { } color)
            return;
        Ink.DefaultDrawingAttributes.Color = color;
        SetColorSwatch(BrushColorButton, color);
    }

    private void RecordUndo(StrokeCollection before)
    {
        _undo.Add(before);
        if (_undo.Count > 40)
            _undo.RemoveAt(0);
    }

    private void UndoClick(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0)
            return;
        Ink.Strokes = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        MarkDirty();
    }

    private void ClearInkClick(object sender, RoutedEventArgs e)
    {
        RecordUndo(Ink.Strokes);
        Ink.Strokes = new StrokeCollection();
        MarkDirty();
    }

    private bool Save()
    {
        if (_selectedFile is not { } file)
            return false;
        try
        {
            if (!(file.IsDirectory ? Directory.Exists(file.FullPath) : File.Exists(file.FullPath)))
                throw new FileNotFoundException(L.Get("MissingItem"), file.FullPath);

            var draft = new ThumbnailDraft(_layers, (BitmapSource?)BackgroundImage.Source, Ink.Strokes);
            Surface.UpdateLayout();
            int size = ThumbnailStore.CanvasSize;
            var preview = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            preview.Render(Surface);
            preview.Freeze();
            _store.Save(file.FullPath, draft, preview);
            _hasSavedThumbnail = true;
            _dirty = false;
            UpdateEditorButtons();
            file.SetThumbnail(preview, true);
            StartPreviews(Files.Items.Cast<FileEntry>().ToArray());
            Status.Text = L.Format("ThumbnailSaved", file.Name);
            return true;
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("SaveThumbnailError"), error);
            return false;
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e) => Save();

    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (_selectedFile is not { } file)
            return;
        if (MessageBox.Show(this, L.Get("ConfirmRemoveThumbnail"),
                L.Get("RemoveThumbnailTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        try
        {
            _store.Remove(file.FullPath);
            LoadEditor(file);
            file.SetThumbnail(null, false);
            StartPreviews(Files.Items.Cast<FileEntry>().ToArray());
            Status.Text = L.Get("ThumbnailRemoved");
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            ShowError(L.Get("RemoveThumbnailError"), error);
        }
    }

    private bool ConfirmDiscard() => !_dirty ||
        MessageBox.Show(this, L.Get("ConfirmDiscard"), L.Get("ThumbnailEditor"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = _fileOperationRunning || !ConfirmDiscard();
        if (!e.Cancel)
        {
            _previewCancellation?.Cancel();
            try { _settings.Save(); }
            catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
            {
                ShowError(L.Get("SaveSettingsError"), error);
            }
        }
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleFileShortcut(e))
            return;
        if (e.Key == Key.F5)
        {
            Refresh();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            PathBox.Focus();
            PathBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S && Editor.IsEnabled)
        {
            Save();
            e.Handled = true;
        }
    }

    private void ShowError(string reason, Exception error)
    {
        Status.Text = reason;
        MessageBox.Show(this, $"{reason}\n\n{error.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
