using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace ThumbnailExplorer;

public partial class MainWindow
{
    private bool _selectingNavigation;

    private void RefreshNavigationTree()
    {
        var expanded = NavigationItems(FolderTree.Items).Where(item => item.IsExpanded && item.Tag is string)
            .Select(item => (string)item.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectingNavigation = true;
        try
        {
            FolderTree.Items.Clear();
            var locations = new TreeViewItem { Header = L.Get("QuickFolders"), IsExpanded = true };
            AddLocation(locations, L.Get("Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            AddLocation(locations, L.Get("Documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddLocation(locations, L.Get("Downloads"), DownloadsFolder());
            AddLocation(locations, L.Get("Pictures"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            AddLocation(locations, L.Get("Home"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            FolderTree.Items.Add(locations);

            var computer = new TreeViewItem { Header = "PC", IsExpanded = true };
            try
            {
                foreach (string drive in Directory.GetLogicalDrives())
                    computer.Items.Add(CreateFolderNode(L.Format("Drive", drive[..2]), drive));
            }
            catch (IOException)
            {
                // Known folders remain available if the drive list cannot be read.
            }
            FolderTree.Items.Add(computer);

            RestoreNavigationExpansion(FolderTree.Items, expanded);
        }
        finally { _selectingNavigation = false; }
        SelectNavigationFolder();
    }

    private static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        int result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr path);
        try
        {
            return result == 0 ? Marshal.PtrToStringUni(path) ?? "" : "";
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);

    private void AddLocation(TreeViewItem group, string label, string path)
    {
        if (Directory.Exists(path))
            group.Items.Add(CreateFolderNode(label, path));
    }

    private TreeViewItem CreateFolderNode(string label, string path)
    {
        var item = new TreeViewItem { Header = label, Tag = path, ToolTip = path };
        item.Items.Add(""); // Reserve the expander until the directory is first opened.
        item.Expanded += NavigationNodeExpanded;
        return item;
    }

    private void NavigationNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is TreeViewItem { Tag: string path } item)
            LoadNavigationChildren(item, path);
    }

    private void LoadNavigationChildren(TreeViewItem node, string path)
    {
        if (node.Items.Count != 1 || node.Items[0] is not string)
            return;

        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = _settings.ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
                IgnoreInaccessible = false
            };
            var children = new DirectoryInfo(path).EnumerateDirectories("*", options)
                .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            node.Items.Clear();
            foreach (var child in children)
                node.Items.Add(CreateFolderNode(child.Name, child.FullName));
        }
        catch (Exception error) when (ThumbnailStore.IsReadFailure(error))
        {
            node.Items.Clear();
            node.Items.Add(new TreeViewItem { Header = L.Get("LoadFailed"), IsEnabled = false });
        }
    }

    private static IEnumerable<TreeViewItem> NavigationItems(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            yield return item;
            foreach (var child in NavigationItems(item.Items))
                yield return child;
        }
    }

    private void RestoreNavigationExpansion(ItemCollection items, HashSet<string> expanded)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is string path && expanded.Contains(path))
            {
                item.IsExpanded = true;
                LoadNavigationChildren(item, path);
            }
            if (item.IsExpanded)
                RestoreNavigationExpansion(item.Items, expanded);
        }
    }

    private void SelectNavigationFolder()
    {
        if (_selectingNavigation || _folder is not { } path)
            return;

        _selectingNavigation = true;
        try
        {
            if (FolderTree.SelectedItem is TreeViewItem { Tag: string selected } &&
                selected.Equals(path, StringComparison.OrdinalIgnoreCase))
                return;

            var nodes = NavigationItems(FolderTree.Items).ToArray();
            var match = nodes.FirstOrDefault(item => item.Tag is string folder &&
                folder.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                // Start at the deepest already known ancestor and discover the remaining path.
                var node = nodes.Where(item => item.Tag is string folder && IsNavigationAncestor(folder, path))
                    .OrderByDescending(item => ((string)item.Tag).Length).FirstOrDefault();
                while (node?.Tag is string parent && IsNavigationAncestor(parent, path))
                {
                    node.IsExpanded = true;
                    LoadNavigationChildren(node, parent);
                    node = node.Items.OfType<TreeViewItem>().FirstOrDefault(child => child.Tag is string folder &&
                        (folder.Equals(path, StringComparison.OrdinalIgnoreCase) || IsNavigationAncestor(folder, path)));
                }
                match = node;
            }

            if (match is not null)
            {
                for (var parent = ItemsControl.ItemsControlFromItemContainer(match) as TreeViewItem;
                    parent is not null; parent = ItemsControl.ItemsControlFromItemContainer(parent) as TreeViewItem)
                    parent.IsExpanded = true;
                match.IsSelected = true;
                match.BringIntoView();
            }
            else if (FolderTree.SelectedItem is TreeViewItem previous)
                previous.IsSelected = false;
        }
        finally { _selectingNavigation = false; }
    }

    private static bool IsNavigationAncestor(string ancestor, string path) =>
        path.Length > ancestor.Length &&
        path.StartsWith(Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private void FolderTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_selectingNavigation)
            return;
        if (e.NewValue is TreeViewItem item && item.Tag is string path)
        {
            if (!Navigate(path))
                SelectNavigationFolder();
        }
        else if (e.NewValue is TreeViewItem group)
            group.IsExpanded = true;
    }
}
