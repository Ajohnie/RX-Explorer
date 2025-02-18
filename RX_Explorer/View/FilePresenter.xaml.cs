﻿using ComputerVision;
using Microsoft.Toolkit.Uwp.UI.Controls;
using Microsoft.UI.Xaml.Controls;
using RX_Explorer.Class;
using RX_Explorer.CustomControl;
using RX_Explorer.Dialog;
using RX_Explorer.Interface;
using RX_Explorer.SeparateWindow.PropertyWindow;
using ShareClassLibrary;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Data.Xml.Dom;
using Windows.Devices.Input;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.WindowManagement;
using Windows.UI.WindowManagement.Preview;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using NavigationViewItem = Microsoft.UI.Xaml.Controls.NavigationViewItem;
using TreeViewNode = Microsoft.UI.Xaml.Controls.TreeViewNode;

namespace RX_Explorer
{
    public sealed partial class FilePresenter : Page, IDisposable
    {
        public ObservableCollection<FileSystemStorageItemBase> FileCollection { get; } = new ObservableCollection<FileSystemStorageItemBase>();

        private readonly ListViewHeaderController ListViewDetailHeader = new ListViewHeaderController();

        private FileControl Container
        {
            get
            {
                if (WeakToFileControl.TryGetTarget(out FileControl Instance))
                {
                    return Instance;
                }
                else
                {
                    return null;
                }
            }
        }

        public List<ValueTuple<string, string>> GoAndBackRecord { get; } = new List<ValueTuple<string, string>>();

        public int RecordIndex { get; set; }

        private StorageAreaWatcher AreaWatcher;

        private WeakReference<FileControl> weakToFileControl;
        public WeakReference<FileControl> WeakToFileControl
        {
            get
            {
                return weakToFileControl;
            }
            set
            {
                if (value != null && value.TryGetTarget(out FileControl Control))
                {
                    AreaWatcher.SetTreeView(Control.FolderTree);
                }

                weakToFileControl = value;
            }
        }

        private SemaphoreSlim EnterLock;

        private readonly PointerEventHandler PointerPressedEventHandler;

        private ListViewBase itemPresenter;

        public ListViewBase ItemPresenter
        {
            get => itemPresenter;
            set
            {
                if (value != itemPresenter)
                {
                    itemPresenter?.RemoveHandler(PointerPressedEvent, PointerPressedEventHandler);
                    itemPresenter = value;
                    itemPresenter.AddHandler(PointerPressedEvent, PointerPressedEventHandler, true);

                    SelectionExtention?.Dispose();
                    SelectionExtention = new ListViewBaseSelectionExtention(itemPresenter, DrawRectangle);

                    if (itemPresenter is GridView)
                    {
                        if (ListViewControl != null)
                        {
                            ListViewControl.Visibility = Visibility.Collapsed;
                            ListViewControl.ItemsSource = null;
                        }

                        GridViewControl.ItemsSource = FileCollection;
                        GridViewControl.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        if (GridViewControl != null)
                        {
                            GridViewControl.Visibility = Visibility.Collapsed;
                            GridViewControl.ItemsSource = null;
                        }

                        ListViewControl.ItemsSource = FileCollection;
                        ListViewControl.Visibility = Visibility.Visible;
                    }
                }
            }
        }

        private volatile FileSystemStorageFolder currentFolder;
        public FileSystemStorageFolder CurrentFolder
        {
            get
            {
                return currentFolder;
            }
            set
            {
                if (value != null)
                {
                    Container.UpdateAddressButton(value.Path);

                    Container.GlobeSearch.PlaceholderText = $"{Globalization.GetString("SearchBox_PlaceholderText")} {value.Name}";
                    Container.GoParentFolder.IsEnabled = value.Path != Path.GetPathRoot(value.Path);
                    Container.GoBackRecord.IsEnabled = RecordIndex > 0;
                    Container.GoForwardRecord.IsEnabled = RecordIndex < GoAndBackRecord.Count - 1;

                    if (Container.WeakToTabItem.TryGetTarget(out TabViewItem Item))
                    {
                        Item.Header = string.IsNullOrEmpty(value.DisplayName) ? $"<{Globalization.GetString("UnknownText")}>" : value.DisplayName;
                    }

                    AreaWatcher?.StartWatchDirectory(value.Path, SettingControl.IsDisplayHiddenItem);

                    if (this.FindParentOfType<BladeItem>() is BladeItem Parent)
                    {
                        Parent.Header = value.DisplayName;
                    }
                }

                TaskBarController.SetText(value?.DisplayName);

                currentFolder = value;
            }
        }

        private WiFiShareProvider WiFiProvider;
        private ListViewBaseSelectionExtention SelectionExtention;
        private FileSystemStorageItemBase TabTarget;
        private DateTimeOffset LastPressTime;
        private string LastPressString;
        private CancellationTokenSource DelayRenameCancel;
        private CancellationTokenSource DelayEnterCancel;
        private int CurrentViewModeIndex = -1;

        public FileSystemStorageItemBase SelectedItem
        {
            get => ItemPresenter.SelectedItem as FileSystemStorageItemBase;
            set
            {
                ItemPresenter.SelectedItem = value;

                if (value != null)
                {
                    (ItemPresenter.ContainerFromItem(value) as SelectorItem)?.Focus(FocusState.Programmatic);
                }
            }
        }

        public List<FileSystemStorageItemBase> SelectedItems => ItemPresenter.SelectedItems.Select((Item) => Item as FileSystemStorageItemBase).ToList();

        public FilePresenter()
        {
            InitializeComponent();

            FileCollection.CollectionChanged += FileCollection_CollectionChanged;
            ListViewDetailHeader.Filter.RefreshListRequested += Filter_RefreshListRequested;

            PointerPressedEventHandler = new PointerEventHandler(ViewControl_PointerPressed);

            AreaWatcher = new StorageAreaWatcher(FileCollection);
            EnterLock = new SemaphoreSlim(1, 1);

            CoreWindow Window = CoreWindow.GetForCurrentThread();
            Window.KeyDown += FilePresenter_KeyDown;
            Window.Dispatcher.AcceleratorKeyActivated += Dispatcher_AcceleratorKeyActivated;

            Loaded += FilePresenter_Loaded;

            Application.Current.Suspending += Current_Suspending;
            Application.Current.Resuming += Current_Resuming;
            SortCollectionGenerator.Current.SortWayChanged += Current_SortWayChanged;
            ViewModeController.ViewModeChanged += Current_ViewModeChanged;
        }

        private void Dispatcher_AcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (Container.CurrentPresenter == this
                && args.KeyStatus.IsMenuKeyDown
                && MainPage.ThisPage.NavView.SelectedItem is NavigationViewItem NavItem
                && Convert.ToString(NavItem.Content) == Globalization.GetString("MainPage_PageDictionary_ThisPC_Label"))
            {
                switch (args.VirtualKey)
                {
                    case VirtualKey.Left:
                        {
                            Container.GoBackRecord_Click(null, null);
                            break;
                        }
                    case VirtualKey.Right:
                        {
                            Container.GoForwardRecord_Click(null, null);
                            break;
                        }
                }
            }
        }

        private async void FilePresenter_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (Container.CurrentPresenter == this
                && MainPage.ThisPage.NavView.SelectedItem is NavigationViewItem NavItem
                && Convert.ToString(NavItem.Content) == Globalization.GetString("MainPage_PageDictionary_ThisPC_Label"))
            {
                if (Container.WeakToTabItem.TryGetTarget(out TabViewItem Tab) && (Tab.Content as Frame) == TabViewContainer.CurrentNavigationControl)
                {
                    CoreVirtualKeyStates CtrlState = sender.GetKeyState(VirtualKey.Control);
                    CoreVirtualKeyStates ShiftState = sender.GetKeyState(VirtualKey.Shift);

                    if (!QueueContentDialog.IsRunningOrWaiting && !Container.BlockKeyboardShortCutInput)
                    {
                        args.Handled = true;

                        if (!CtrlState.HasFlag(CoreVirtualKeyStates.Down) && !ShiftState.HasFlag(CoreVirtualKeyStates.Down))
                        {
                            NavigateToStorageItem(args.VirtualKey);
                        }

                        switch (args.VirtualKey)
                        {
                            case VirtualKey.Space when SettingControl.IsQuicklookEnable:
                                {
                                    using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                    {
                                        if (await Exclusive.Controller.CheckIfQuicklookIsAvaliableAsync().ConfigureAwait(true))
                                        {
                                            string ViewPathWithQuicklook;

                                            if (string.IsNullOrEmpty(SelectedItem?.Path))
                                            {
                                                if (!string.IsNullOrEmpty(CurrentFolder?.Path))
                                                {
                                                    ViewPathWithQuicklook = CurrentFolder.Path;
                                                }
                                                else
                                                {
                                                    break;
                                                }
                                            }
                                            else
                                            {
                                                ViewPathWithQuicklook = SelectedItem.Path;
                                            }

                                            await Exclusive.Controller.ViewWithQuicklookAsync(ViewPathWithQuicklook).ConfigureAwait(false);
                                        }
                                    }

                                    break;
                                }
                            case VirtualKey.F2:
                                {
                                    Rename_Click(null, null);
                                    break;
                                }
                            case VirtualKey.F5:
                                {
                                    Refresh_Click(null, null);
                                    break;
                                }
                            case VirtualKey.Enter when SelectedItems.Count == 1 && SelectedItem is FileSystemStorageItemBase Item:
                                {
                                    await EnterSelectedItem(Item).ConfigureAwait(false);
                                    break;
                                }
                            case VirtualKey.Back:
                                {
                                    Container.GoBackRecord_Click(null, null);
                                    break;
                                }
                            case VirtualKey.L when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    Container.AddressBox.Focus(FocusState.Programmatic);
                                    break;
                                }
                            case VirtualKey.V when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    Paste_Click(null, null);
                                    break;
                                }
                            case VirtualKey.A when CtrlState.HasFlag(CoreVirtualKeyStates.Down) && SelectedItem == null:
                                {
                                    ItemPresenter.SelectAll();
                                    break;
                                }
                            case VirtualKey.C when CtrlState.HasFlag(CoreVirtualKeyStates.Down) && ShiftState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    Clipboard.Clear();

                                    DataPackage Package = new DataPackage
                                    {
                                        RequestedOperation = DataPackageOperation.Copy
                                    };

                                    Package.SetText(SelectedItem?.Path ?? CurrentFolder?.Path ?? string.Empty);

                                    Clipboard.SetContent(Package);
                                    break;
                                }
                            case VirtualKey.C when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    Copy_Click(null, null);
                                    break;
                                }
                            case VirtualKey.X when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    Cut_Click(null, null);
                                    break;
                                }
                            case VirtualKey.Delete:
                            case VirtualKey.D when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    Delete_Click(null, null);
                                    break;
                                }
                            case VirtualKey.F when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    Container.GlobeSearch.Focus(FocusState.Programmatic);
                                    break;
                                }
                            case VirtualKey.N when ShiftState.HasFlag(CoreVirtualKeyStates.Down) && CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    CreateFolder_Click(null, null);
                                    break;
                                }
                            case VirtualKey.Z when CtrlState.HasFlag(CoreVirtualKeyStates.Down) && OperationRecorder.Current.Count > 0:
                                {
                                    await Ctrl_Z_Click().ConfigureAwait(false);
                                    break;
                                }
                            case VirtualKey.E when ShiftState.HasFlag(CoreVirtualKeyStates.Down) && CurrentFolder != null:
                                {
                                    _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                                    break;
                                }
                            case VirtualKey.T when ShiftState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    OpenInTerminal_Click(null, null);
                                    break;
                                }
                            case VirtualKey.T when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    CloseAllFlyout();

                                    if (SelectedItem != null)
                                    {
                                        if (SelectedItem is FileSystemStorageFolder)
                                        {
                                            await TabViewContainer.ThisPage.CreateNewTabAsync(SelectedItem.Path).ConfigureAwait(true);
                                        }
                                    }
                                    else
                                    {
                                        await TabViewContainer.ThisPage.CreateNewTabAsync().ConfigureAwait(true);
                                    }

                                    break;
                                }
                            case VirtualKey.Q when CtrlState.HasFlag(CoreVirtualKeyStates.Down):
                                {
                                    OpenFolderInNewWindow_Click(null, null);
                                    break;
                                }
                            case VirtualKey.Up:
                            case VirtualKey.Down:
                                {
                                    if (SelectedItem == null)
                                    {
                                        SelectedItem = FileCollection.FirstOrDefault();
                                    }

                                    break;
                                }
                            case VirtualKey.B when CtrlState.HasFlag(CoreVirtualKeyStates.Down) && SelectedItem != null:
                                {
                                    await Container.CreateNewBlade(SelectedItem.Path).ConfigureAwait(true);
                                    break;
                                }
                            default:
                                {
                                    args.Handled = false;
                                    break;
                                }
                        }
                    }
                }
            }
        }

        private async void Current_ViewModeChanged(object sender, ViewModeController.ViewModeChangedEventArgs e)
        {
            if (e.Path == CurrentFolder?.Path && CurrentViewModeIndex != e.Index)
            {
                switch (e.Index)
                {
                    case 0:
                        {
                            ItemPresenter = FindName("GridViewControl") as ListViewBase;

                            GridViewControl.ItemTemplate = GridViewTileDataTemplate;
                            GridViewControl.ItemsPanel = HorizontalGridViewPanel;

                            while (true)
                            {
                                if (GridViewControl.FindChildOfType<ScrollViewer>() is ScrollViewer Scroll)
                                {
                                    Scroll.HorizontalScrollMode = ScrollMode.Disabled;
                                    Scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                                    Scroll.VerticalScrollMode = ScrollMode.Auto;
                                    Scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                                    break;
                                }
                                else
                                {
                                    await Task.Delay(200).ConfigureAwait(true);
                                }
                            }

                            break;
                        }
                    case 1:
                        {
                            ItemPresenter = FindName("ListViewControl") as ListViewBase;
                            break;
                        }
                    case 2:
                        {
                            ItemPresenter = FindName("GridViewControl") as ListViewBase;

                            GridViewControl.ItemTemplate = GridViewListDataTemplate;
                            GridViewControl.ItemsPanel = VerticalGridViewPanel;

                            while (true)
                            {
                                if (GridViewControl.FindChildOfType<ScrollViewer>() is ScrollViewer Scroll)
                                {
                                    Scroll.HorizontalScrollMode = ScrollMode.Auto;
                                    Scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                                    Scroll.VerticalScrollMode = ScrollMode.Disabled;
                                    Scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                                    break;
                                }
                                else
                                {
                                    await Task.Delay(200).ConfigureAwait(true);
                                }
                            }

                            break;
                        }
                    case 3:
                        {
                            ItemPresenter = FindName("GridViewControl") as ListViewBase;

                            GridViewControl.ItemTemplate = GridViewLargeImageDataTemplate;
                            GridViewControl.ItemsPanel = HorizontalGridViewPanel;

                            while (true)
                            {
                                if (GridViewControl.FindChildOfType<ScrollViewer>() is ScrollViewer Scroll)
                                {
                                    Scroll.HorizontalScrollMode = ScrollMode.Disabled;
                                    Scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                                    Scroll.VerticalScrollMode = ScrollMode.Auto;
                                    Scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                                    break;
                                }
                                else
                                {
                                    await Task.Delay(200).ConfigureAwait(true);
                                }
                            }

                            break;
                        }
                    case 4:
                        {
                            ItemPresenter = FindName("GridViewControl") as ListViewBase;

                            GridViewControl.ItemTemplate = GridViewMediumImageDataTemplate;
                            GridViewControl.ItemsPanel = HorizontalGridViewPanel;

                            while (true)
                            {
                                if (GridViewControl.FindChildOfType<ScrollViewer>() is ScrollViewer Scroll)
                                {
                                    Scroll.HorizontalScrollMode = ScrollMode.Disabled;
                                    Scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                                    Scroll.VerticalScrollMode = ScrollMode.Auto;
                                    Scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                                    break;
                                }
                                else
                                {
                                    await Task.Delay(200).ConfigureAwait(true);
                                }
                            }

                            break;
                        }
                    case 5:
                        {
                            ItemPresenter = FindName("GridViewControl") as ListViewBase;

                            GridViewControl.ItemTemplate = GridViewSmallImageDataTemplate;
                            GridViewControl.ItemsPanel = HorizontalGridViewPanel;

                            while (true)
                            {
                                if (GridViewControl.FindChildOfType<ScrollViewer>() is ScrollViewer Scroll)
                                {
                                    Scroll.HorizontalScrollMode = ScrollMode.Disabled;
                                    Scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                                    Scroll.VerticalScrollMode = ScrollMode.Auto;
                                    Scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                                    break;
                                }
                                else
                                {
                                    await Task.Delay(200).ConfigureAwait(true);
                                }
                            }

                            break;
                        }
                }

                await SQLite.Current.SetPathConfiguration(new PathConfiguration(CurrentFolder.Path, e.Index)).ConfigureAwait(false);

                CurrentViewModeIndex = e.Index;
            }
        }

        private void Current_SortWayChanged(object sender, string fromPath)
        {
            if (fromPath == CurrentFolder.Path)
            {
                ListViewDetailHeader.Indicator.SetIndicatorStatus(SortCollectionGenerator.Current.SortTarget, SortCollectionGenerator.Current.SortDirection);

                FileSystemStorageItemBase[] ItemList = SortCollectionGenerator.Current.GetSortedCollection(FileCollection).ToArray();

                FileCollection.Clear();

                foreach (FileSystemStorageItemBase Item in ItemList)
                {
                    FileCollection.Add(Item);
                }
            }
        }

        private async Task DisplayItemsInFolderCore(string FolderPath, bool ForceRefresh = false, bool SkipNavigationRecord = false)
        {
            await EnterLock.WaitAsync().ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(FolderPath))
            {
                throw new ArgumentNullException(nameof(FolderPath), "Parameter could not be null or empty");
            }

            try
            {
                if (!ForceRefresh)
                {
                    if (FolderPath == CurrentFolder?.Path)
                    {
                        return;
                    }
                }

                if (!SkipNavigationRecord && !ForceRefresh)
                {
                    if (RecordIndex != GoAndBackRecord.Count - 1 && GoAndBackRecord.Count != 0)
                    {
                        GoAndBackRecord.RemoveRange(RecordIndex + 1, GoAndBackRecord.Count - RecordIndex - 1);
                    }

                    if (GoAndBackRecord.Count > 0)
                    {
                        string ParentPath = Path.GetDirectoryName(FolderPath);

                        if (ParentPath == GoAndBackRecord[GoAndBackRecord.Count - 1].Item1)
                        {
                            GoAndBackRecord[GoAndBackRecord.Count - 1] = (ParentPath, FolderPath);
                        }
                        else
                        {
                            GoAndBackRecord[GoAndBackRecord.Count - 1] = (GoAndBackRecord[GoAndBackRecord.Count - 1].Item1, SelectedItems.Count > 1 ? string.Empty : (SelectedItem?.Path ?? string.Empty));
                        }
                    }

                    GoAndBackRecord.Add((FolderPath, string.Empty));

                    RecordIndex = GoAndBackRecord.Count - 1;
                }

                if (await FileSystemStorageItemBase.OpenAsync(FolderPath).ConfigureAwait(true) is FileSystemStorageFolder Folder)
                {
                    CurrentFolder = Folder;
                }
                else
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = $"{Globalization.GetString("QueueDialog_LocatePathFailure_Content")} \r\"{FolderPath}\"",
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                    };

                    _ = await dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }

                if (Container.FolderTree.SelectedNode == null && Container.FolderTree.RootNodes.FirstOrDefault((Node) => (Node.Content as TreeViewNodeContent)?.Path == Path.GetPathRoot(FolderPath)) is TreeViewNode RootNode)
                {
                    Container.FolderTree.SelectNodeAndScrollToVertical(RootNode);
                }

                FileCollection.Clear();

                await Container.ViewModeControl.SetCurrentPathAsync(FolderPath).ConfigureAwait(true);

                PathConfiguration Config = await SQLite.Current.GetPathConfiguration(FolderPath).ConfigureAwait(true);

                List<FileSystemStorageItemBase> ChildItems = await CurrentFolder.GetChildItemsAsync(SettingControl.IsDisplayHiddenItem).ConfigureAwait(true);

                if (ChildItems.Count > 0)
                {
                    HasFile.Visibility = Visibility.Collapsed;

                    foreach (FileSystemStorageItemBase SubItem in SortCollectionGenerator.Current.GetSortedCollection(ChildItems, Config.SortColumn, Config.SortDirection))
                    {
                        FileCollection.Add(SubItem);
                    }
                }
                else
                {
                    HasFile.Visibility = Visibility.Visible;
                }

                StatusTips.Text = Globalization.GetString("FilePresenterBottomStatusTip_TotalItem").Replace("{ItemNum}", FileCollection.Count.ToString());

                ListViewDetailHeader.Filter.SetDataSource(FileCollection);
                ListViewDetailHeader.Indicator.SetIndicatorStatus(Config.SortColumn.GetValueOrDefault(), Config.SortDirection.GetValueOrDefault());
            }
            catch (Exception ex)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = $"{Globalization.GetString("QueueDialog_AccessFolderFailure_Content")} {ex.Message}",
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);
            }
            finally
            {
                EnterLock.Release();
            }
        }

        public Task DisplayItemsInFolder(FileSystemStorageFolder Folder, bool ForceRefresh = false, bool SkipNavigationRecord = false)
        {
            if (Folder == null)
            {
                throw new ArgumentNullException(nameof(Folder), "Parameter could not be null or empty");
            }

            return DisplayItemsInFolderCore(Folder.Path, ForceRefresh, SkipNavigationRecord);
        }

        public Task DisplayItemsInFolder(string FolderPath, bool ForceRefresh = false, bool SkipNavigationRecord = false)
        {
            return DisplayItemsInFolderCore(FolderPath, ForceRefresh, SkipNavigationRecord);
        }

        private void Presenter_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int Delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;

            if (Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
            {
                if (Delta > 0)
                {
                    if (Container.ViewModeControl.ViewModeIndex > 0)
                    {
                        Container.ViewModeControl.ViewModeIndex--;
                    }
                }
                else
                {
                    if (Container.ViewModeControl.ViewModeIndex < ViewModeController.SelectionSource.Length - 1)
                    {
                        Container.ViewModeControl.ViewModeIndex++;
                    }
                }

                e.Handled = true;
            }
        }

        private void Current_Resuming(object sender, object e)
        {
            AreaWatcher.StartWatchDirectory(AreaWatcher.CurrentLocation, SettingControl.IsDisplayHiddenItem);
        }

        private void Current_Suspending(object sender, SuspendingEventArgs e)
        {
            AreaWatcher.StopWatchDirectory();
        }

        private void FilePresenter_Loaded(object sender, RoutedEventArgs e)
        {
            if (this.FindParentOfType<BladeItem>() is BladeItem Parent)
            {
                Parent.Header = CurrentFolder?.DisplayName;
            }
        }

        private void NavigateToStorageItem(VirtualKey Key)
        {
            char Input = Convert.ToChar(Key);

            if (char.IsLetterOrDigit(Input))
            {
                string SearchString = Input.ToString();

                try
                {
                    if (LastPressString != SearchString && (DateTimeOffset.Now - LastPressTime).TotalMilliseconds < 1200)
                    {
                        SearchString = LastPressString + SearchString;

                        IEnumerable<FileSystemStorageItemBase> Group = FileCollection.Where((Item) => Item.Name.StartsWith(SearchString, StringComparison.OrdinalIgnoreCase));

                        if (Group.Any() && (SelectedItem == null || !Group.Contains(SelectedItem)))
                        {
                            SelectedItem = Group.FirstOrDefault();
                            ItemPresenter.ScrollIntoView(SelectedItem);
                        }
                    }
                    else
                    {
                        IEnumerable<FileSystemStorageItemBase> Group = FileCollection.Where((Item) => Item.Name.StartsWith(SearchString, StringComparison.OrdinalIgnoreCase));

                        if (Group.Any())
                        {
                            if (SelectedItem != null)
                            {
                                FileSystemStorageItemBase[] ItemArray = Group.ToArray();

                                int NextIndex = Array.IndexOf(ItemArray, SelectedItem);

                                if (NextIndex != -1)
                                {
                                    if (NextIndex < ItemArray.Length - 1)
                                    {
                                        SelectedItem = ItemArray[NextIndex + 1];
                                    }
                                    else
                                    {
                                        SelectedItem = ItemArray.FirstOrDefault();
                                    }
                                }
                                else
                                {
                                    SelectedItem = ItemArray.FirstOrDefault();
                                }
                            }
                            else
                            {
                                SelectedItem = Group.FirstOrDefault();
                            }

                            ItemPresenter.ScrollIntoView(SelectedItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, $"{nameof(NavigateToStorageItem)} throw an exception");
                }
                finally
                {
                    LastPressString = SearchString;
                    LastPressTime = DateTimeOffset.Now;
                }
            }
        }

        private void FileCollection_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                HasFile.Visibility = FileCollection.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 关闭右键菜单
        /// </summary>
        private void CloseAllFlyout()
        {
            FileFlyout.Hide();
            FolderFlyout.Hide();
            EmptyFlyout.Hide();
            MixedFlyout.Hide();
            LnkItemFlyout.Hide();
        }

        private async Task Ctrl_Z_Click()
        {
            if (OperationRecorder.Current.Count > 0)
            {
                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Undoing")).ConfigureAwait(true);

                try
                {
                    foreach (string Action in OperationRecorder.Current.Pop())
                    {
                        string[] SplitGroup = Action.Split("||", StringSplitOptions.RemoveEmptyEntries);

                        switch (SplitGroup[1])
                        {
                            case "Move":
                                {
                                    if (CurrentFolder.Path.Equals(Path.GetDirectoryName(SplitGroup[3]), StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (await FileSystemStorageItemBase.OpenAsync(Path.GetDirectoryName(SplitGroup[0])).ConfigureAwait(true) is FileSystemStorageFolder OriginFolder)
                                        {
                                            switch (SplitGroup[2])
                                            {
                                                case "File":
                                                    {
                                                        if (Path.GetExtension(SplitGroup[3]) == ".lnk")
                                                        {
                                                            await FileSystemStorageItemBase.MoveAsync(SplitGroup[3], OriginFolder.Path, (s, arg) =>
                                                            {
                                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                                {
                                                                    Container.ProBar.IsIndeterminate = false;
                                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                                }
                                                            }, true).ConfigureAwait(true);
                                                        }
                                                        else if (await FileSystemStorageItemBase.OpenAsync(Path.Combine(CurrentFolder.Path, Path.GetFileName(SplitGroup[3]))).ConfigureAwait(true) is FileSystemStorageFile File)
                                                        {
                                                            await File.MoveAsync(OriginFolder, (s, arg) =>
                                                            {
                                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                                {
                                                                    Container.ProBar.IsIndeterminate = false;
                                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                                }
                                                            }, true).ConfigureAwait(true);
                                                        }
                                                        else
                                                        {
                                                            throw new FileNotFoundException();
                                                        }

                                                        break;
                                                    }
                                                case "Folder":
                                                    {
                                                        if (await FileSystemStorageItemBase.OpenAsync(Path.Combine(CurrentFolder.Path, Path.GetFileName(SplitGroup[3]))).ConfigureAwait(true) is FileSystemStorageFolder Folder)
                                                        {
                                                            await Folder.MoveAsync(OriginFolder, (s, arg) =>
                                                            {
                                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                                {
                                                                    Container.ProBar.IsIndeterminate = false;
                                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                                }
                                                            }, true).ConfigureAwait(true);
                                                        }
                                                        else
                                                        {
                                                            throw new FileNotFoundException();
                                                        }

                                                        break;
                                                    }
                                            }
                                        }
                                        else
                                        {
                                            throw new DirectoryNotFoundException();
                                        }
                                    }
                                    else if (CurrentFolder.Path.Equals(Path.GetDirectoryName(SplitGroup[0]), StringComparison.OrdinalIgnoreCase))
                                    {
                                        switch (SplitGroup[2])
                                        {
                                            case "File":
                                                {
                                                    if (Path.GetExtension(SplitGroup[3]) == ".lnk")
                                                    {
                                                        await FileSystemStorageItemBase.MoveAsync(SplitGroup[3], CurrentFolder.Path, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        string TargetFolderPath = Path.GetDirectoryName(SplitGroup[3]);

                                                        if (await FileSystemStorageItemBase.CheckExistAsync(TargetFolderPath).ConfigureAwait(true))
                                                        {
                                                            if (await FileSystemStorageItemBase.OpenAsync(Path.Combine(TargetFolderPath, Path.GetFileName(SplitGroup[3]))).ConfigureAwait(true) is FileSystemStorageFile File)
                                                            {
                                                                await File.MoveAsync(CurrentFolder, (s, arg) =>
                                                                {
                                                                    if (Container.ProBar.Value < arg.ProgressPercentage)
                                                                    {
                                                                        Container.ProBar.IsIndeterminate = false;
                                                                        Container.ProBar.Value = arg.ProgressPercentage;
                                                                    }
                                                                }, true).ConfigureAwait(true);
                                                            }
                                                            else
                                                            {
                                                                throw new FileNotFoundException();
                                                            }
                                                        }
                                                        else
                                                        {
                                                            throw new DirectoryNotFoundException();
                                                        }
                                                    }

                                                    break;
                                                }
                                            case "Folder":
                                                {
                                                    if (await FileSystemStorageItemBase.OpenAsync(SplitGroup[3]).ConfigureAwait(true) is FileSystemStorageFolder Folder)
                                                    {
                                                        await Folder.MoveAsync(CurrentFolder, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        throw new DirectoryNotFoundException();
                                                    }

                                                    break;
                                                }
                                        }
                                    }
                                    else
                                    {
                                        if (await FileSystemStorageItemBase.OpenAsync(Path.GetDirectoryName(SplitGroup[0])).ConfigureAwait(true) is FileSystemStorageFolder OriginFolder)
                                        {
                                            switch (SplitGroup[2])
                                            {
                                                case "File":
                                                    {
                                                        if (Path.GetExtension(SplitGroup[3]) == ".lnk")
                                                        {
                                                            await FileSystemStorageItemBase.MoveAsync(SplitGroup[3], OriginFolder.Path, (s, arg) =>
                                                            {
                                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                                {
                                                                    Container.ProBar.IsIndeterminate = false;
                                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                                }
                                                            }, true).ConfigureAwait(true);
                                                        }
                                                        else
                                                        {
                                                            if (await FileSystemStorageItemBase.OpenAsync(SplitGroup[3]).ConfigureAwait(true) is FileSystemStorageFile File)
                                                            {
                                                                await File.MoveAsync(OriginFolder, (s, arg) =>
                                                                {
                                                                    if (Container.ProBar.Value < arg.ProgressPercentage)
                                                                    {
                                                                        Container.ProBar.IsIndeterminate = false;
                                                                        Container.ProBar.Value = arg.ProgressPercentage;
                                                                    }
                                                                }, true).ConfigureAwait(true);
                                                            }
                                                            else
                                                            {
                                                                throw new FileNotFoundException();
                                                            }
                                                        }

                                                        break;
                                                    }
                                                case "Folder":
                                                    {
                                                        if (await FileSystemStorageItemBase.OpenAsync(SplitGroup[3]).ConfigureAwait(true) is FileSystemStorageFolder Folder)
                                                        {
                                                            await Folder.MoveAsync(OriginFolder, (s, arg) =>
                                                            {
                                                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                                                {
                                                                    Container.ProBar.IsIndeterminate = false;
                                                                    Container.ProBar.Value = arg.ProgressPercentage;
                                                                }
                                                            }, true).ConfigureAwait(true);
                                                        }
                                                        else
                                                        {
                                                            throw new DirectoryNotFoundException();
                                                        }

                                                        if (!SettingControl.IsDetachTreeViewAndPresenter)
                                                        {
                                                            foreach (TreeViewNode RootNode in Container.FolderTree.RootNodes)
                                                            {
                                                                await RootNode.UpdateAllSubNodeAsync().ConfigureAwait(true);
                                                            }
                                                        }

                                                        break;
                                                    }
                                            }
                                        }
                                        else
                                        {
                                            throw new DirectoryNotFoundException();
                                        }
                                    }

                                    break;
                                }
                            case "Copy":
                                {
                                    if (CurrentFolder.Path.Equals(Path.GetDirectoryName(SplitGroup[3]), StringComparison.OrdinalIgnoreCase))
                                    {
                                        switch (SplitGroup[2])
                                        {
                                            case "File":
                                                {
                                                    if (Path.GetExtension(SplitGroup[3]) == ".lnk")
                                                    {
                                                        await FileSystemStorageItemBase.DeleteAsync(SplitGroup[3], true, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else if (await FileSystemStorageItemBase.OpenAsync(Path.Combine(CurrentFolder.Path, Path.GetFileName(SplitGroup[3]))).ConfigureAwait(true) is FileSystemStorageFile File)
                                                    {
                                                        await File.DeleteAsync(true, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        throw new FileNotFoundException();
                                                    }

                                                    break;
                                                }
                                            case "Folder":
                                                {
                                                    if (await FileSystemStorageItemBase.OpenAsync(Path.Combine(CurrentFolder.Path, Path.GetFileName(SplitGroup[3]))).ConfigureAwait(true) is FileSystemStorageFolder Folder)
                                                    {
                                                        await Folder.DeleteAsync(true, (s, arg) =>
                                                        {
                                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                                            {
                                                                Container.ProBar.IsIndeterminate = false;
                                                                Container.ProBar.Value = arg.ProgressPercentage;
                                                            }
                                                        }, true).ConfigureAwait(true);
                                                    }
                                                    else
                                                    {
                                                        throw new DirectoryNotFoundException();
                                                    }

                                                    break;
                                                }
                                        }
                                    }
                                    else
                                    {
                                        await FileSystemStorageItemBase.DeleteAsync(SplitGroup[3], true, (s, arg) =>
                                        {
                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = arg.ProgressPercentage;
                                            }
                                        }, true).ConfigureAwait(true);

                                        if (!SettingControl.IsDetachTreeViewAndPresenter)
                                        {
                                            foreach (TreeViewNode RootNode in Container.FolderTree.RootNodes)
                                            {
                                                await RootNode.UpdateAllSubNodeAsync().ConfigureAwait(true);
                                            }
                                        }
                                    }
                                    break;
                                }
                            case "Delete":
                                {
                                    using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                    {
                                        if (!await Exclusive.Controller.RestoreItemInRecycleBinAsync(SplitGroup[0]).ConfigureAwait(true))
                                        {
                                            QueueContentDialog Dialog = new QueueContentDialog
                                            {
                                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                Content = $"{Globalization.GetString("QueueDialog_RecycleBinRestoreError_Content")} {Environment.NewLine}{Path.GetFileName(SplitGroup[0])}",
                                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                            };

                                            _ = Dialog.ShowAsync().ConfigureAwait(true);
                                        }
                                    }

                                    break;
                                }
                        }
                    }
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_WarningTitle"),
                        Content = Globalization.GetString("QueueDialog_UndoFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }

                await Container.LoadingActivation(false).ConfigureAwait(false);
            }
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            List<FileSystemStorageItemBase> SelectedItemsCopy = SelectedItems;

            if (SelectedItemsCopy.Count > 0)
            {
                try
                {
                    Clipboard.Clear();

                    DataPackage Package = new DataPackage
                    {
                        RequestedOperation = DataPackageOperation.Copy
                    };

                    Package.Properties.PackageFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;

                    IEnumerable<FileSystemStorageItemBase> StorageItems = SelectedItemsCopy.Where((Item) => Item is not IUnsupportedStorageItem);

                    if (StorageItems.Any())
                    {
                        List<IStorageItem> TempItemList = new List<IStorageItem>();

                        foreach (FileSystemStorageItemBase Item in StorageItems)
                        {
                            if (await Item.GetStorageItemAsync().ConfigureAwait(true) is IStorageItem It)
                            {
                                TempItemList.Add(It);
                            }
                        }

                        if (TempItemList.Count > 0)
                        {
                            Package.SetStorageItems(TempItemList, false);
                        }
                    }

                    IEnumerable<FileSystemStorageItemBase> NotStorageItems = SelectedItemsCopy.Where((Item) => Item is IUnsupportedStorageItem);

                    if (NotStorageItems.Any())
                    {
                        XmlDocument Document = new XmlDocument();

                        XmlElement RootElemnt = Document.CreateElement("RX-Explorer");
                        Document.AppendChild(RootElemnt);

                        XmlElement KindElement = Document.CreateElement("Kind");
                        KindElement.InnerText = "RX-Explorer-TransferNotStorageItem";
                        RootElemnt.AppendChild(KindElement);

                        foreach (FileSystemStorageItemBase Item in NotStorageItems)
                        {
                            XmlElement InnerElement = Document.CreateElement("Item");
                            InnerElement.InnerText = Item.Path;
                            RootElemnt.AppendChild(InnerElement);
                        }

                        Package.SetText(Document.GetXml());
                    }

                    Clipboard.SetContent(Package);

                    FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnableAccessClipboard_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await Dialog.ShowAsync().ConfigureAwait(false);
                }
            }
        }

        private async void Paste_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            try
            {
                DataPackageView Package = Clipboard.GetContent();

                List<string> PathList = new List<string>();

                if (Package.Contains(StandardDataFormats.StorageItems))
                {
                    IReadOnlyList<IStorageItem> ItemList = await Package.GetStorageItemsAsync();
                    PathList.AddRange(ItemList.Select((Item) => Item.Path));
                }

                if (Package.Contains(StandardDataFormats.Text))
                {
                    string XmlText = await Package.GetTextAsync();

                    if (XmlText.Contains("RX-Explorer"))
                    {
                        XmlDocument Document = new XmlDocument();
                        Document.LoadXml(XmlText);

                        IXmlNode KindNode = Document.SelectSingleNode("/RX-Explorer/Kind");

                        if (KindNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                        {
                            PathList.AddRange(Document.SelectNodes("/RX-Explorer/Item").Select((Node) => Node.InnerText));
                        }
                    }
                }

                if (PathList.Count > 0)
                {
                    if (Package.RequestedOperation.HasFlag(DataPackageOperation.Move))
                    {
                        if (PathList.All((Path) => System.IO.Path.GetDirectoryName(Path) != CurrentFolder.Path))
                        {
                            await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                            try
                            {
                                await FileSystemStorageItemBase.MoveAsync(PathList, CurrentFolder.Path, (s, arg) =>
                                {
                                    if (Container.ProBar.Value < arg.ProgressPercentage)
                                    {
                                        Container.ProBar.IsIndeterminate = false;
                                        Container.ProBar.Value = arg.ProgressPercentage;
                                    }
                                }).ConfigureAwait(true);
                            }
                            catch (FileNotFoundException)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                            }
                            catch (FileCaputureException)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                            }
                            catch (InvalidOperationException)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                    PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                };

                                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                {
                                    await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                                }
                            }
                            catch (Exception)
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                            }
                        }
                    }
                    else if (Package.RequestedOperation.HasFlag(DataPackageOperation.Copy))
                    {
                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                        try
                        {
                            await FileSystemStorageItemBase.CopyAsync(PathList, CurrentFolder.Path, (s, arg) =>
                            {
                                if (Container.ProBar.Value < arg.ProgressPercentage)
                                {
                                    Container.ProBar.IsIndeterminate = false;
                                    Container.ProBar.Value = arg.ProgressPercentage;
                                }
                            }).ConfigureAwait(true);
                        }
                        catch (FileNotFoundException)
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await Dialog.ShowAsync().ConfigureAwait(true);
                        }
                        catch (InvalidOperationException)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                            };

                            if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                            }
                        }
                        catch (Exception)
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await dialog.ShowAsync().ConfigureAwait(true);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex.HResult is unchecked((int)0x80040064) or unchecked((int)0x8004006A))
            {
                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                {
                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                    if (await Exclusive.Controller.PasteRemoteFile(CurrentFolder.Path).ConfigureAwait(true))
                    {
                        await Container.LoadingActivation(false).ConfigureAwait(true);
                    }
                    else
                    {
                        await Container.LoadingActivation(false).ConfigureAwait(true);

                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                    }
                }
            }
            catch
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_FailToGetClipboardError_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
            finally
            {
                await Container.LoadingActivation(false).ConfigureAwait(true);
                FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));
            }
        }

        private async void Cut_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            List<FileSystemStorageItemBase> SelectedItemsCopy = SelectedItems;

            if (SelectedItemsCopy.Count > 0)
            {
                try
                {
                    Clipboard.Clear();

                    DataPackage Package = new DataPackage
                    {
                        RequestedOperation = DataPackageOperation.Move
                    };

                    Package.Properties.PackageFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;

                    IEnumerable<FileSystemStorageItemBase> StorageItems = SelectedItemsCopy.Where((Item) => Item is not IUnsupportedStorageItem);

                    if (StorageItems.Any())
                    {
                        List<IStorageItem> TempItemList = new List<IStorageItem>();

                        foreach (FileSystemStorageItemBase Item in StorageItems)
                        {
                            if (await Item.GetStorageItemAsync().ConfigureAwait(true) is IStorageItem It)
                            {
                                TempItemList.Add(It);
                            }
                        }

                        if (TempItemList.Count > 0)
                        {
                            Package.SetStorageItems(TempItemList, false);
                        }
                    }

                    IEnumerable<FileSystemStorageItemBase> NotStorageItems = SelectedItemsCopy.Where((Item) => Item is IUnsupportedStorageItem);

                    if (NotStorageItems.Any())
                    {
                        XmlDocument Document = new XmlDocument();

                        XmlElement RootElemnt = Document.CreateElement("RX-Explorer");
                        Document.AppendChild(RootElemnt);

                        XmlElement KindElement = Document.CreateElement("Kind");
                        KindElement.InnerText = "RX-Explorer-TransferNotStorageItem";
                        RootElemnt.AppendChild(KindElement);

                        foreach (FileSystemStorageItemBase Item in NotStorageItems)
                        {
                            XmlElement InnerElement = Document.CreateElement("Item");
                            InnerElement.InnerText = Item.Path;
                            RootElemnt.AppendChild(InnerElement);
                        }

                        Package.SetText(Document.GetXml());
                    }

                    Clipboard.SetContent(Package);

                    FileCollection.Where((Item) => Item.ThumbnailOpacity != 1d).ToList().ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.Normal));
                    SelectedItemsCopy.ForEach((Item) => Item.SetThumbnailOpacity(ThumbnailStatus.ReduceOpacity));
                }
                catch
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnableAccessClipboard_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(false);
                }
            }
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItems.Count > 0)
            {
                //We should take the path of what we want to delete first. Or we might delete some items incorrectly
                string[] PathList = SelectedItems.Select((Item) => Item.Path).ToArray();

                bool ExecuteDelete = false;

                if (ApplicationData.Current.LocalSettings.Values["DeleteConfirmSwitch"] is bool DeleteConfirm)
                {
                    if (DeleteConfirm)
                    {
                        DeleteDialog QueueContenDialog = new DeleteDialog(Globalization.GetString("QueueDialog_DeleteFiles_Content"));

                        if (await QueueContenDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                        {
                            ExecuteDelete = true;
                        }
                    }
                    else
                    {
                        ExecuteDelete = true;
                    }
                }
                else
                {
                    DeleteDialog QueueContenDialog = new DeleteDialog(Globalization.GetString("QueueDialog_DeleteFiles_Content"));

                    if (await QueueContenDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        ExecuteDelete = true;
                    }
                }

                bool PermanentDelete = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

                if (ApplicationData.Current.LocalSettings.Values["AvoidRecycleBin"] is bool IsAvoidRecycleBin)
                {
                    PermanentDelete |= IsAvoidRecycleBin;
                }

                if (ExecuteDelete)
                {
                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Deleting")).ConfigureAwait(true);

                    try
                    {
                        await FileSystemStorageItemBase.DeleteAsync(PathList, PermanentDelete, (s, arg) =>
                        {
                            if (Container.ProBar.Value < arg.ProgressPercentage)
                            {
                                Container.ProBar.IsIndeterminate = false;
                                Container.ProBar.Value = arg.ProgressPercentage;
                            }
                        }).ConfigureAwait(true);
                    }
                    catch (FileNotFoundException)
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_DeleteItemError_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                    }
                    catch (FileCaputureException)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                    }
                    catch (InvalidOperationException)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedDelete_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                        {
                            await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                        }
                    }
                    catch (Exception)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_DeleteFailUnexpectError_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                    }

                    await Container.LoadingActivation(false).ConfigureAwait(false);
                }
            }
        }

        private async void Rename_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItems.Count > 0)
            {
                if (SelectedItems.Count > 1)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_RenameNumError_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                else
                {
                    FileSystemStorageItemBase RenameItem = SelectedItem;

                    RenameDialog dialog = new RenameDialog(RenameItem);

                    if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                    {
                        if (await FileSystemStorageItemBase.CheckExistAsync(Path.Combine(CurrentFolder.Path, dialog.DesireName)).ConfigureAwait(true))
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_RenameExist_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_ContinueButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                            };

                            if (await Dialog.ShowAsync().ConfigureAwait(true) != ContentDialogResult.Primary)
                            {
                                return;
                            }
                        }

                        try
                        {
                            await RenameItem.RenameAsync(dialog.DesireName).ConfigureAwait(true);
                        }
                        catch (FileLoadException)
                        {
                            QueueContentDialog LoadExceptionDialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_FileOccupied_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                            };

                            _ = await LoadExceptionDialog.ShowAsync().ConfigureAwait(true);
                        }
                        catch (InvalidOperationException)
                        {
                            QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedRenameFile_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                            };

                            if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                            }
                        }
                        catch
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnauthorizedRename_StartExplorer_Content"),
                                PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                            };

                            if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                            }
                        }
                    }
                }
            }
        }

        private async void BluetoothShare_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (await SelectedItem.GetStorageItemAsync().ConfigureAwait(true) is StorageFile ShareFile)
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(ShareFile.Path).ConfigureAwait(true))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }

                IReadOnlyList<Radio> RadioDevice = await Radio.GetRadiosAsync();

                if (RadioDevice.Any((Device) => Device.Kind == RadioKind.Bluetooth && Device.State == RadioState.On))
                {
                    BluetoothUI Bluetooth = new BluetoothUI();
                    if ((await Bluetooth.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                    {
                        BluetoothFileTransfer FileTransfer = new BluetoothFileTransfer(ShareFile);

                        _ = await FileTransfer.ShowAsync().ConfigureAwait(true);
                    }
                }
                else
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                        Content = Globalization.GetString("QueueDialog_OpenBluetooth_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };
                    _ = await dialog.ShowAsync().ConfigureAwait(true);
                }
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);
            }
        }

        private void ViewControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DelayRenameCancel?.Cancel();

            foreach (SelectorItem RemovedItem in e.RemovedItems.Select((Item) => ItemPresenter.ContainerFromItem(Item)).OfType<SelectorItem>())
            {
                RemovedItem.CanDrag = false;
            }

            foreach (SelectorItem SelectedItem in e.AddedItems.Select((Item) => ItemPresenter.ContainerFromItem(Item)).OfType<SelectorItem>())
            {
                SelectedItem.CanDrag = true;
            }

            ItemPresenter.UpdateLayout();

            if (SelectedItem is FileSystemStorageFile File)
            {
                FileEdit.IsEnabled = false;
                FileShare.IsEnabled = true;

                ChooseOtherApp.IsEnabled = true;
                RunWithSystemAuthority.IsEnabled = false;

                switch (File.Type.ToLower())
                {
                    case ".mp4":
                    case ".wmv":
                        {
                            FileEdit.IsEnabled = true;
                            Transcode.IsEnabled = true;
                            VideoEdit.IsEnabled = true;
                            VideoMerge.IsEnabled = true;
                            break;
                        }
                    case ".mkv":
                    case ".m4a":
                    case ".mov":
                    case ".mp3":
                    case ".flac":
                    case ".wma":
                    case ".alac":
                    case ".png":
                    case ".bmp":
                    case ".jpg":
                    case ".heic":
                    case ".gif":
                    case ".tiff":
                        {
                            FileEdit.IsEnabled = true;
                            VideoEdit.IsEnabled = false;
                            VideoMerge.IsEnabled = false;
                            Transcode.IsEnabled = true;
                            break;
                        }
                    case ".exe":
                        {
                            ChooseOtherApp.IsEnabled = false;
                            RunWithSystemAuthority.IsEnabled = true;
                            break;
                        }
                    case ".msi":
                    case ".bat":
                        {
                            RunWithSystemAuthority.IsEnabled = true;
                            break;
                        }
                    case ".msc":
                        {
                            ChooseOtherApp.IsEnabled = false;
                            break;
                        }
                }
            }

            string[] StatusTipsSplit = StatusTips.Text.Split("  |  ", StringSplitOptions.RemoveEmptyEntries);

            if (SelectedItems.Count > 0)
            {
                string SizeInfo = string.Empty;

                if (SelectedItems.All((Item) => Item is FileSystemStorageFile))
                {
                    ulong TotalSize = 0;
                    foreach (ulong Size in SelectedItems.Select((Item) => Item.SizeRaw).ToArray())
                    {
                        TotalSize += Size;
                    }

                    SizeInfo = $"  |  {TotalSize.ToFileSizeDescription()}";
                }

                if (StatusTipsSplit.Length > 0)
                {
                    StatusTips.Text = $"{StatusTipsSplit[0]}  |  {Globalization.GetString("FilePresenterBottomStatusTip_SelectedItem").Replace("{ItemNum}", SelectedItems.Count.ToString())}{SizeInfo}";
                }
                else
                {
                    StatusTips.Text += $"  |  {Globalization.GetString("FilePresenterBottomStatusTip_SelectedItem").Replace("{ItemNum}", SelectedItems.Count.ToString())}{SizeInfo}";
                }
            }
            else
            {
                if (StatusTipsSplit.Length > 0)
                {
                    StatusTips.Text = StatusTipsSplit[0];
                }
            }
        }

        private void ViewControl_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Item)
            {
                PointerPoint PointerInfo = e.GetCurrentPoint(null);

                if (PointerInfo.Properties.IsMiddleButtonPressed && Item is FileSystemStorageFolder)
                {
                    SelectionExtention.Disable();
                    SelectedItem = Item;
                    _ = TabViewContainer.ThisPage.CreateNewTabAsync(null, Item.Path);
                }
                else if ((e.OriginalSource as FrameworkElement).FindParentOfType<SelectorItem>() != null)
                {
                    if (ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple)
                    {
                        if (e.KeyModifiers == VirtualKeyModifiers.None)
                        {
                            if (SelectedItems.Contains(Item))
                            {
                                SelectionExtention.Disable();
                            }
                            else
                            {
                                if (PointerInfo.Properties.IsLeftButtonPressed)
                                {
                                    SelectedItem = Item;
                                }

                                if (e.OriginalSource is ListViewItemPresenter || (e.OriginalSource is TextBlock Block && Block.Name == "EmptyTextblock"))
                                {
                                    SelectionExtention.Enable();
                                }
                                else
                                {
                                    SelectionExtention.Disable();
                                }
                            }
                        }
                        else
                        {
                            SelectionExtention.Disable();
                        }
                    }
                    else
                    {
                        SelectionExtention.Disable();
                    }
                }
            }
            else
            {
                SelectedItem = null;
                SelectionExtention.Enable();
            }
        }

        private async void ViewControl_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.PointerDeviceType == PointerDeviceType.Mouse)
            {
                e.Handled = true;
                Container.BlockKeyboardShortCutInput = true;

                if (ItemPresenter is GridView)
                {
                    if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Context)
                    {
                        if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                        {
                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                        }
                        else
                        {
                            SelectedItem = Context;

                            switch (Context)
                            {
                                case LinkStorageFile:
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        break;
                                    }
                                case FileSystemStorageFolder:
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FolderFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        break;
                                    }
                                case FileSystemStorageFile:
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        break;
                                    }
                            }
                        }
                    }
                    else
                    {
                        SelectedItem = null;
                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                    }
                }
                else
                {
                    if (e.OriginalSource is FrameworkElement Element)
                    {
                        if (Element.Name == "EmptyTextblock")
                        {
                            SelectedItem = null;
                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                        }
                        else
                        {
                            if (Element.DataContext is FileSystemStorageItemBase Context)
                            {
                                if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                                {
                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                }
                                else
                                {
                                    if (SelectedItem == Context && SettingControl.IsDoubleClickEnable)
                                    {
                                        switch (Context)
                                        {
                                            case LinkStorageFile:
                                                {
                                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                    break;
                                                }
                                            case FileSystemStorageFolder:
                                                {
                                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FolderFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                    break;
                                                }
                                            case FileSystemStorageFile:
                                                {
                                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                    break;
                                                }
                                        }
                                    }
                                    else
                                    {
                                        if (e.OriginalSource is TextBlock)
                                        {
                                            SelectedItem = Context;

                                            switch (Context)
                                            {
                                                case LinkStorageFile:
                                                    {
                                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                        break;
                                                    }
                                                case FileSystemStorageFolder:
                                                    {
                                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FolderFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                        break;
                                                    }
                                                case FileSystemStorageFile:
                                                    {
                                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                        break;
                                                    }
                                            }
                                        }
                                        else
                                        {
                                            SelectedItem = null;
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                SelectedItem = null;
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                        }
                    }
                }

                Container.BlockKeyboardShortCutInput = false;
            }
        }

        private async void FileProperty_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            AppWindow NewWindow = await AppWindow.TryCreateAsync();
            NewWindow.RequestSize(new Size(420, 600));
            NewWindow.RequestMoveRelativeToCurrentViewContent(new Point(Window.Current.Bounds.Width / 2 - 200, Window.Current.Bounds.Height / 2 - 300));
            NewWindow.PersistedStateId = "Properties";
            NewWindow.Title = Globalization.GetString("Properties_Window_Title");
            NewWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            NewWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            NewWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            ElementCompositionPreview.SetAppWindowContent(NewWindow, new PropertyBase(NewWindow, SelectedItem));
            WindowManagementPreview.SetPreferredMinSize(NewWindow, new Size(420, 600));

            await NewWindow.TryShowAsync();
        }

        private async void Compression_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFile File)
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(File.Path).ConfigureAwait(true))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }

                CompressDialog dialog = new CompressDialog(true, Path.GetFileName(File.Path));

                if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                {
                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Compressing")).ConfigureAwait(true);

                    try
                    {
                        switch (dialog.Type)
                        {
                            case CompressionType.Zip:
                                {
                                    await CompressionUtil.CreateZipAsync(File, Path.Combine(CurrentFolder.Path, dialog.FileName), (int)dialog.Level, async (s, e) =>
                                    {
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                        {
                                            if (Container.ProBar.Value < e.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = e.ProgressPercentage;
                                            }
                                        });
                                    }).ConfigureAwait(true);

                                    break;
                                }
                            case CompressionType.Tar:
                                {
                                    await CompressionUtil.CreateTarAsync(File, Path.Combine(CurrentFolder.Path, dialog.FileName), async (s, e) =>
                                    {
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                        {
                                            if (Container.ProBar.Value < e.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = e.ProgressPercentage;
                                            }
                                        });
                                    }).ConfigureAwait(true);

                                    break;
                                }
                            case CompressionType.Gzip:
                                {
                                    await CompressionUtil.CreateGzipAsync(File, Path.Combine(CurrentFolder.Path, dialog.FileName), (int)dialog.Level, async (s, e) =>
                                    {
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                        {
                                            if (Container.ProBar.Value < e.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = e.ProgressPercentage;
                                            }
                                        });
                                    }).ConfigureAwait(true);

                                    break;
                                }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        QueueContentDialog dialog1 = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedCompression_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                        };

                        if (await dialog1.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                        {
                            _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        QueueContentDialog dialog2 = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_CompressionError_Content") + ex.Message,
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog2.ShowAsync().ConfigureAwait(true);
                    }

                    await Container.LoadingActivation(false).ConfigureAwait(false);
                }
            }
        }

        private async void Decompression_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFile File)
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(File.Path).ConfigureAwait(true))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }

                switch (File.Type.ToLower())
                {
                    case ".zip":
                    case ".tar":
                    case ".gz":
                        {
                            break;
                        }
                    default:
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_FileTypeIncorrect_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            await dialog.ShowAsync().ConfigureAwait(true);

                            return;
                        }
                }

                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Extracting")).ConfigureAwait(true);

                try
                {
                    FileSystemStorageFolder TargetFolder = ((sender as FrameworkElement)?.Name == "DecompressionOption2")
                                                            ? await FileSystemStorageItemBase.CreateAsync(Path.Combine(Path.GetDirectoryName(File.Path), Path.GetFileNameWithoutExtension(File.Name)), StorageItemTypes.Folder, CreateOption.GenerateUniqueName).ConfigureAwait(true) as FileSystemStorageFolder
                                                            : CurrentFolder;

                    if (TargetFolder == null)
                    {
                        throw new UnauthorizedAccessException();
                    }

                    switch (File.Type.ToLower())
                    {
                        case ".zip":
                            {
                                await CompressionUtil.ExtractZipAsync(TargetFolder, File, async (s, e) =>
                                {
                                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                    {
                                        if (Container.ProBar.Value < e.ProgressPercentage)
                                        {
                                            Container.ProBar.IsIndeterminate = false;
                                            Container.ProBar.Value = e.ProgressPercentage;
                                        }
                                    });
                                }).ConfigureAwait(true);

                                break;
                            }
                        case ".tar":
                            {
                                await CompressionUtil.ExtractTarAsync(TargetFolder, File, async (s, e) =>
                                {
                                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                    {
                                        if (Container.ProBar.Value < e.ProgressPercentage)
                                        {
                                            Container.ProBar.IsIndeterminate = false;
                                            Container.ProBar.Value = e.ProgressPercentage;
                                        }
                                    });
                                }).ConfigureAwait(true);

                                break;
                            }
                        case ".gz":
                            {
                                await CompressionUtil.ExtractGZipAsync(TargetFolder, File, async (s, e) =>
                                {
                                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                    {
                                        if (Container.ProBar.Value < e.ProgressPercentage)
                                        {
                                            Container.ProBar.IsIndeterminate = false;
                                            Container.ProBar.Value = e.ProgressPercentage;
                                        }
                                    });
                                }).ConfigureAwait(true);

                                break;
                            }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedDecompression_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                    }
                }
                catch (NotImplementedException)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_CanNotDecompressEncrypted_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_DecompressionError_Content") + ex.Message,
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await dialog.ShowAsync().ConfigureAwait(true);
                }
                finally
                {
                    await Container.LoadingActivation(false).ConfigureAwait(true);
                }
            }
        }

        private async void ViewControl_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;

            DelayRenameCancel?.Cancel();

            if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase ReFile)
            {
                await EnterSelectedItem(ReFile).ConfigureAwait(false);
            }
            else if (e.OriginalSource is Grid)
            {
                if (Path.GetPathRoot(CurrentFolder?.Path) == CurrentFolder?.Path)
                {
                    MainPage.ThisPage.NavView_BackRequested(null, null);
                }
                else
                {
                    Container.GoParentFolder_Click(null, null);
                }
            }
        }

        private async void Transcode_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (!await FileSystemStorageItemBase.CheckExistAsync(SelectedItem.Path).ConfigureAwait(true))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            if (GeneralTransformer.IsAnyTransformTaskRunning)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            switch (SelectedItem.Type.ToLower())
            {
                case ".mkv":
                case ".mp4":
                case ".mp3":
                case ".flac":
                case ".wma":
                case ".wmv":
                case ".m4a":
                case ".mov":
                case ".alac":
                    {
                        if ((await SelectedItem.GetStorageItemAsync().ConfigureAwait(true)) is StorageFile Source)
                        {
                            TranscodeDialog dialog = new TranscodeDialog(Source);

                            if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                            {
                                try
                                {
                                    string DestFilePath = Path.Combine(CurrentFolder.Path, $"{Source.Path}.{dialog.MediaTranscodeEncodingProfile.ToLower()}");

                                    if (await FileSystemStorageItemBase.CreateAsync(DestFilePath, StorageItemTypes.File, CreateOption.GenerateUniqueName).ConfigureAwait(true) is FileSystemStorageItemBase Item)
                                    {
                                        if (await Item.GetStorageItemAsync().ConfigureAwait(true) is StorageFile DestinationFile)
                                        {
                                            await GeneralTransformer.TranscodeFromAudioOrVideoAsync(Source, DestinationFile, dialog.MediaTranscodeEncodingProfile, dialog.MediaTranscodeQuality, dialog.SpeedUp).ConfigureAwait(true);
                                        }
                                        else
                                        {
                                            throw new FileNotFoundException();
                                        }
                                    }
                                    else
                                    {
                                        throw new FileNotFoundException();
                                    }
                                }
                                catch (Exception)
                                {
                                    QueueContentDialog Dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_UnauthorizedCreateNewFile_Content"),
                                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                                    };

                                    if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                    {
                                        _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                                    }
                                }
                            }
                        }
                        else
                        {
                            QueueContentDialog Dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            _ = await Dialog.ShowAsync().ConfigureAwait(true);
                        }

                        break;
                    }
                case ".png":
                case ".bmp":
                case ".jpg":
                case ".heic":
                case ".tiff":
                    {
                        if (SelectedItem is FileSystemStorageFile File)
                        {
                            TranscodeImageDialog Dialog = null;

                            using (IRandomAccessStream OriginStream = await File.GetRandomAccessStreamFromFileAsync(FileAccessMode.Read).ConfigureAwait(true))
                            {
                                BitmapDecoder Decoder = await BitmapDecoder.CreateAsync(OriginStream);
                                Dialog = new TranscodeImageDialog(Decoder.PixelWidth, Decoder.PixelHeight);
                            }

                            if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                            {
                                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Transcoding")).ConfigureAwait(true);

                                await GeneralTransformer.TranscodeFromImageAsync(File, Dialog.TargetFile, Dialog.IsEnableScale, Dialog.ScaleWidth, Dialog.ScaleHeight, Dialog.InterpolationMode).ConfigureAwait(true);

                                await Container.LoadingActivation(false).ConfigureAwait(true);
                            }
                        }

                        break;
                    }
            }
        }

        private async void FolderProperty_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            AppWindow NewWindow = await AppWindow.TryCreateAsync();
            NewWindow.RequestSize(new Size(420, 600));
            NewWindow.RequestMoveRelativeToCurrentViewContent(new Point(Window.Current.Bounds.Width / 2 - 200, Window.Current.Bounds.Height / 2 - 300));
            NewWindow.PersistedStateId = "Properties";
            NewWindow.Title = Globalization.GetString("Properties_Window_Title");
            NewWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            NewWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            NewWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            ElementCompositionPreview.SetAppWindowContent(NewWindow, new PropertyBase(NewWindow, SelectedItem));
            WindowManagementPreview.SetPreferredMinSize(NewWindow, new Size(420, 600));

            await NewWindow.TryShowAsync();
        }

        private async void WIFIShare_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem != null)
            {
                if (QRTeachTip.IsOpen)
                {
                    QRTeachTip.IsOpen = false;
                }

                await Task.Run(() =>
                {
                    SpinWait.SpinUntil(() => WiFiProvider == null);
                }).ConfigureAwait(true);

                WiFiProvider = new WiFiShareProvider();
                WiFiProvider.ThreadExitedUnexpectly += WiFiProvider_ThreadExitedUnexpectly;

                using (MD5 MD5Alg = MD5.Create())
                {
                    string Hash = MD5Alg.GetHash(SelectedItem.Path);
                    QRText.Text = WiFiProvider.CurrentUri + Hash;
                    WiFiProvider.FilePathMap = new KeyValuePair<string, string>(Hash, SelectedItem.Path);
                }

                QrCodeEncodingOptions options = new QrCodeEncodingOptions()
                {
                    DisableECI = true,
                    CharacterSet = "UTF-8",
                    Width = 250,
                    Height = 250,
                    ErrorCorrection = ErrorCorrectionLevel.Q
                };

                BarcodeWriter Writer = new BarcodeWriter
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = options
                };

                WriteableBitmap Bitmap = Writer.Write(QRText.Text);
                using (SoftwareBitmap PreTransImage = SoftwareBitmap.CreateCopyFromBuffer(Bitmap.PixelBuffer, BitmapPixelFormat.Bgra8, 250, 250))
                using (SoftwareBitmap TransferImage = ComputerVisionProvider.ExtendImageBorder(PreTransImage, Colors.White, 0, 75, 75, 0))
                {
                    SoftwareBitmapSource Source = new SoftwareBitmapSource();
                    QRImage.Source = Source;
                    await Source.SetBitmapAsync(TransferImage);
                }

                await Task.Delay(500).ConfigureAwait(true);

                QRTeachTip.Target = ItemPresenter.ContainerFromItem(SelectedItem) as FrameworkElement;
                QRTeachTip.IsOpen = true;

                await WiFiProvider.StartToListenRequest().ConfigureAwait(false);
            }
        }

        private async void WiFiProvider_ThreadExitedUnexpectly(object sender, Exception e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                QRTeachTip.IsOpen = false;

                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_WiFiError_Content") + e.Message,
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };
                _ = await dialog.ShowAsync().ConfigureAwait(true);
            });
        }

        private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
        {
            DataPackage Package = new DataPackage();
            Package.SetText(QRText.Text);
            Clipboard.SetContent(Package);
        }

        private async void UseSystemFileMananger_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
        }

        private async void ParentProperty_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (await FileSystemStorageItemBase.CheckExistAsync(CurrentFolder.Path).ConfigureAwait(true))
            {
                AppWindow NewWindow = await AppWindow.TryCreateAsync();
                NewWindow.RequestSize(new Size(420, 600));
                NewWindow.RequestMoveRelativeToCurrentViewContent(new Point(Window.Current.Bounds.Width / 2 - 200, Window.Current.Bounds.Height / 2 - 300));
                NewWindow.PersistedStateId = "Properties";
                NewWindow.Title = Globalization.GetString("Properties_Window_Title");
                NewWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                NewWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                NewWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                ElementCompositionPreview.SetAppWindowContent(NewWindow, new PropertyBase(NewWindow, CurrentFolder));
                WindowManagementPreview.SetPreferredMinSize(NewWindow, new Size(420, 600));

                await NewWindow.TryShowAsync();
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);
            }
        }

        private async void ItemOpen_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageItemBase ReFile)
            {
                await EnterSelectedItem(ReFile).ConfigureAwait(false);
            }
        }

        private void QRText_GotFocus(object sender, RoutedEventArgs e)
        {
            ((TextBox)sender).SelectAll();
        }

        private async void CreateFolder_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (await FileSystemStorageItemBase.CheckExistAsync(CurrentFolder.Path).ConfigureAwait(true))
            {
                if (await FileSystemStorageItemBase.CreateAsync(Path.Combine(CurrentFolder.Path, Globalization.GetString("Create_NewFolder_Admin_Name")), StorageItemTypes.Folder, CreateOption.GenerateUniqueName).ConfigureAwait(true) is FileSystemStorageItemBase NewFolder)
                {
                    while (true)
                    {
                        if (FileCollection.FirstOrDefault((Item) => Item == NewFolder) is FileSystemStorageItemBase NewItem)
                        {
                            ItemPresenter.UpdateLayout();

                            ItemPresenter.ScrollIntoView(NewItem);

                            SelectedItem = NewItem;

                            if ((ItemPresenter.ContainerFromItem(NewItem) as SelectorItem)?.ContentTemplateRoot is FrameworkElement Element)
                            {
                                if (Element.FindName("NameLabel") is TextBlock NameLabel)
                                {
                                    NameLabel.Visibility = Visibility.Collapsed;
                                }

                                if (Element.FindName("NameEditBox") is TextBox EditBox)
                                {
                                    EditBox.Tag = NewItem;
                                    EditBox.Text = NewItem.Name;
                                    EditBox.Visibility = Visibility.Visible;
                                    EditBox.Focus(FocusState.Programmatic);
                                    EditBox.SelectAll();
                                }

                                Container.BlockKeyboardShortCutInput = true;
                            }

                            break;
                        }
                        else
                        {
                            await Task.Delay(500).ConfigureAwait(true);
                        }
                    }
                }
                else
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedCreateFolder_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                    }
                }
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);
            }
        }

        private async void EmptyFlyout_Opening(object sender, object e)
        {
            try
            {
                DataPackageView Package = Clipboard.GetContent();

                if (Package.Contains(StandardDataFormats.StorageItems))
                {
                    Paste.IsEnabled = true;
                }
                else if (Package.Contains(StandardDataFormats.Text))
                {
                    string XmlText = await Package.GetTextAsync();

                    if (XmlText.Contains("RX-Explorer"))
                    {
                        XmlDocument Document = new XmlDocument();
                        Document.LoadXml(XmlText);
                        IXmlNode KindNode = Document.SelectSingleNode("/RX-Explorer/Kind");

                        if (KindNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                        {
                            Paste.IsEnabled = true;
                        }
                        else
                        {
                            Paste.IsEnabled = false;
                        }
                    }
                    else
                    {
                        Paste.IsEnabled = false;
                    }
                }
                else
                {
                    Paste.IsEnabled = false;
                }
            }
            catch
            {
                Paste.IsEnabled = false;
            }

            if (OperationRecorder.Current.Count > 0)
            {
                Undo.IsEnabled = true;
            }
            else
            {
                Undo.IsEnabled = false;
            }
        }

        private async void SystemShare_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem != null)
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(SelectedItem.Path).ConfigureAwait(true))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                else
                {
                    if ((await SelectedItem.GetStorageItemAsync().ConfigureAwait(true)) is StorageFile ShareItem)
                    {
                        DataTransferManager.GetForCurrentView().DataRequested += (s, args) =>
                        {
                            DataPackage Package = new DataPackage();
                            Package.Properties.Title = ShareItem.DisplayName;
                            Package.Properties.Description = ShareItem.DisplayType;
                            Package.SetStorageItems(new StorageFile[] { ShareItem });
                            args.Request.Data = Package;
                        };

                        DataTransferManager.ShowShareUI();
                    }
                    else
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                    }
                }
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            try
            {
                if (await FileSystemStorageItemBase.CheckExistAsync(CurrentFolder.Path).ConfigureAwait(true))
                {
                    await DisplayItemsInFolder(CurrentFolder, true).ConfigureAwait(true);
                }
                else
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex, $"{ nameof(Refresh_Click)} throw an exception");
            }
        }

        private async void ViewControl_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!SettingControl.IsDoubleClickEnable && ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple && e.ClickedItem is FileSystemStorageItemBase ReFile)
            {
                CoreVirtualKeyStates CtrlState = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
                CoreVirtualKeyStates ShiftState = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift);

                if (!CtrlState.HasFlag(CoreVirtualKeyStates.Down) && !ShiftState.HasFlag(CoreVirtualKeyStates.Down))
                {
                    await EnterSelectedItem(ReFile).ConfigureAwait(false);
                }
            }
        }

        public async Task EnterSelectedItem(string Path, bool RunAsAdministrator = false)
        {
            FileSystemStorageItemBase Item = await FileSystemStorageItemBase.OpenAsync(Path).ConfigureAwait(true);

            await EnterSelectedItem(Item, RunAsAdministrator).ConfigureAwait(false);
        }

        public async Task EnterSelectedItem(FileSystemStorageItemBase ReFile, bool RunAsAdministrator = false)
        {
            if (Interlocked.Exchange(ref TabTarget, ReFile) == null)
            {
                try
                {
                    switch (TabTarget)
                    {
                        case FileSystemStorageFile File:
                            {
                                if (!await FileSystemStorageItemBase.CheckExistAsync(File.Path).ConfigureAwait(true))
                                {
                                    QueueContentDialog Dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                                    return;
                                }

                                string AdminExecutablePath = await SQLite.Current.GetDefaultProgramPickerRecordAsync(File.Type).ConfigureAwait(true);

                                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                {
                                    if (!string.IsNullOrEmpty(AdminExecutablePath) && AdminExecutablePath != Package.Current.Id.FamilyName)
                                    {
                                        if (Path.IsPathRooted(AdminExecutablePath))
                                        {
                                            await Exclusive.Controller.RunAsync(AdminExecutablePath, Path.GetDirectoryName(AdminExecutablePath), WindowState.Normal, false, false, false, File.Path).ConfigureAwait(true);
                                        }
                                        else
                                        {
                                            if ((await Launcher.FindFileHandlersAsync(File.Type)).FirstOrDefault((Item) => Item.PackageFamilyName == AdminExecutablePath) is AppInfo Info)
                                            {
                                                if (await File.GetStorageItemAsync().ConfigureAwait(true) is StorageFile InnerFile)
                                                {
                                                    if (!await Launcher.LaunchFileAsync(InnerFile, new LauncherOptions { TargetApplicationPackageFamilyName = Info.PackageFamilyName, DisplayApplicationPicker = false }))
                                                    {
                                                        ProgramPickerDialog Dialog = new ProgramPickerDialog(File);

                                                        if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                        {
                                                            if (Dialog.SelectedProgram.Path == Package.Current.Id.FamilyName)
                                                            {
                                                                switch (File.Type.ToLower())
                                                                {
                                                                    case ".jpg":
                                                                    case ".png":
                                                                    case ".bmp":
                                                                        {
                                                                            if (AnimationController.Current.IsEnableAnimation)
                                                                            {
                                                                                Container.Frame.Navigate(typeof(PhotoViewer), File, new DrillInNavigationTransitionInfo());
                                                                            }
                                                                            else
                                                                            {
                                                                                Container.Frame.Navigate(typeof(PhotoViewer), File, new SuppressNavigationTransitionInfo());
                                                                            }
                                                                            break;
                                                                        }
                                                                    case ".mkv":
                                                                    case ".mp4":
                                                                    case ".mp3":
                                                                    case ".flac":
                                                                    case ".wma":
                                                                    case ".wmv":
                                                                    case ".m4a":
                                                                    case ".mov":
                                                                    case ".alac":
                                                                        {
                                                                            if (AnimationController.Current.IsEnableAnimation)
                                                                            {
                                                                                Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                                                            }
                                                                            else
                                                                            {
                                                                                Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                                                            }
                                                                            break;
                                                                        }
                                                                    case ".txt":
                                                                        {
                                                                            if (AnimationController.Current.IsEnableAnimation)
                                                                            {
                                                                                Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                                                            }
                                                                            else
                                                                            {
                                                                                Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                                                            }
                                                                            break;
                                                                        }
                                                                    case ".pdf":
                                                                        {
                                                                            if (AnimationController.Current.IsEnableAnimation)
                                                                            {
                                                                                Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                                                            }
                                                                            else
                                                                            {
                                                                                Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                                                            }
                                                                            break;
                                                                        }
                                                                }
                                                            }
                                                            else
                                                            {
                                                                if (Path.IsPathRooted(Dialog.SelectedProgram.Path))
                                                                {
                                                                    await Exclusive.Controller.RunAsync(Dialog.SelectedProgram.Path, Path.GetDirectoryName(Dialog.SelectedProgram.Path), WindowState.Normal, false, false, false, File.Path).ConfigureAwait(true);
                                                                }
                                                                else
                                                                {
                                                                    if (!await Launcher.LaunchFileAsync(InnerFile, new LauncherOptions { TargetApplicationPackageFamilyName = Dialog.SelectedProgram.Path, DisplayApplicationPicker = false }))
                                                                    {
                                                                        if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute1)
                                                                        {
                                                                            ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] = ProgramExcute1.Replace($"{File.Type}|{File.Name};", string.Empty);
                                                                        }

                                                                        QueueContentDialog dialog = new QueueContentDialog
                                                                        {
                                                                            Title = Globalization.GetString("Common_Dialog_TipTitle"),
                                                                            Content = Globalization.GetString("QueueDialog_OpenFailure_Content"),
                                                                            PrimaryButtonText = Globalization.GetString("QueueDialog_OpenFailure_PrimaryButton"),
                                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                                        };

                                                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                                        {
                                                                            if (!await Launcher.LaunchFileAsync(InnerFile))
                                                                            {
                                                                                LauncherOptions options = new LauncherOptions
                                                                                {
                                                                                    DisplayApplicationPicker = true
                                                                                };
                                                                                _ = await Launcher.LaunchFileAsync(InnerFile, options);
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    QueueContentDialog Dialog = new QueueContentDialog
                                                    {
                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                        Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                    };

                                                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                                }
                                            }
                                            else
                                            {
                                                ProgramPickerDialog Dialog = new ProgramPickerDialog(File);

                                                if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                {
                                                    if (Dialog.SelectedProgram.Path == Package.Current.Id.FamilyName)
                                                    {
                                                        switch (File.Type.ToLower())
                                                        {
                                                            case ".jpg":
                                                            case ".png":
                                                            case ".bmp":
                                                                {
                                                                    if (AnimationController.Current.IsEnableAnimation)
                                                                    {
                                                                        Container.Frame.Navigate(typeof(PhotoViewer), File, new DrillInNavigationTransitionInfo());
                                                                    }
                                                                    else
                                                                    {
                                                                        Container.Frame.Navigate(typeof(PhotoViewer), File, new SuppressNavigationTransitionInfo());
                                                                    }
                                                                    break;
                                                                }
                                                            case ".mkv":
                                                            case ".mp4":
                                                            case ".mp3":
                                                            case ".flac":
                                                            case ".wma":
                                                            case ".wmv":
                                                            case ".m4a":
                                                            case ".mov":
                                                                {
                                                                    if (AnimationController.Current.IsEnableAnimation)
                                                                    {
                                                                        Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                                                    }
                                                                    else
                                                                    {
                                                                        Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                                                    }
                                                                    break;
                                                                }
                                                            case ".txt":
                                                                {
                                                                    if (AnimationController.Current.IsEnableAnimation)
                                                                    {
                                                                        Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                                                    }
                                                                    else
                                                                    {
                                                                        Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                                                    }
                                                                    break;
                                                                }
                                                            case ".pdf":
                                                                {
                                                                    if (AnimationController.Current.IsEnableAnimation)
                                                                    {
                                                                        Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                                                    }
                                                                    else
                                                                    {
                                                                        Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                                                    }
                                                                    break;
                                                                }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        if (Path.IsPathRooted(Dialog.SelectedProgram.Path))
                                                        {
                                                            await Exclusive.Controller.RunAsync(Dialog.SelectedProgram.Path, Path.GetDirectoryName(Dialog.SelectedProgram.Path), WindowState.Normal, false, false, false, File.Path).ConfigureAwait(true);
                                                        }
                                                        else
                                                        {
                                                            if (await File.GetStorageItemAsync().ConfigureAwait(true) is StorageFile InnerFile)
                                                            {
                                                                if (!await Launcher.LaunchFileAsync(InnerFile, new LauncherOptions { TargetApplicationPackageFamilyName = Dialog.SelectedProgram.Path, DisplayApplicationPicker = false }))
                                                                {
                                                                    if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute1)
                                                                    {
                                                                        ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] = ProgramExcute1.Replace($"{File.Type}|{File.Name};", string.Empty);
                                                                    }

                                                                    QueueContentDialog dialog = new QueueContentDialog
                                                                    {
                                                                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                                                                        Content = Globalization.GetString("QueueDialog_OpenFailure_Content"),
                                                                        PrimaryButtonText = Globalization.GetString("QueueDialog_OpenFailure_PrimaryButton"),
                                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                                    };

                                                                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                                    {
                                                                        if (!await Launcher.LaunchFileAsync(InnerFile))
                                                                        {
                                                                            LauncherOptions options = new LauncherOptions
                                                                            {
                                                                                DisplayApplicationPicker = true
                                                                            };
                                                                            _ = await Launcher.LaunchFileAsync(InnerFile, options);
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                            else
                                                            {
                                                                QueueContentDialog dialog = new QueueContentDialog
                                                                {
                                                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                    Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                                                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                                };

                                                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        switch (File.Type.ToLower())
                                        {
                                            case ".jpg":
                                            case ".png":
                                            case ".bmp":
                                                {
                                                    if (AnimationController.Current.IsEnableAnimation)
                                                    {
                                                        Container.Frame.Navigate(typeof(PhotoViewer), File, new DrillInNavigationTransitionInfo());
                                                    }
                                                    else
                                                    {
                                                        Container.Frame.Navigate(typeof(PhotoViewer), File, new SuppressNavigationTransitionInfo());
                                                    }
                                                    break;
                                                }
                                            case ".mkv":
                                            case ".mp4":
                                            case ".mp3":
                                            case ".flac":
                                            case ".wma":
                                            case ".wmv":
                                            case ".m4a":
                                            case ".mov":
                                                {
                                                    if (AnimationController.Current.IsEnableAnimation)
                                                    {
                                                        Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                                    }
                                                    else
                                                    {
                                                        Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                                    }
                                                    break;
                                                }
                                            case ".txt":
                                                {
                                                    if (AnimationController.Current.IsEnableAnimation)
                                                    {
                                                        Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                                    }
                                                    else
                                                    {
                                                        Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                                    }
                                                    break;
                                                }
                                            case ".pdf":
                                                {
                                                    if (AnimationController.Current.IsEnableAnimation)
                                                    {
                                                        Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                                    }
                                                    else
                                                    {
                                                        Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                                    }
                                                    break;
                                                }
                                            case ".lnk":
                                                {
                                                    if (File is LinkStorageFile Item)
                                                    {
                                                        if (Item.LinkType == ShellLinkType.Normal)
                                                        {
                                                            switch (await FileSystemStorageItemBase.OpenAsync(Item.LinkTargetPath).ConfigureAwait(true))
                                                            {
                                                                case FileSystemStorageFolder:
                                                                    {
                                                                        await DisplayItemsInFolder(Item.LinkTargetPath).ConfigureAwait(true);
                                                                        break;
                                                                    }
                                                                case FileSystemStorageFile:
                                                                    {
                                                                        await Item.LaunchAsync().ConfigureAwait(true);
                                                                        break;
                                                                    }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            await Item.LaunchAsync().ConfigureAwait(true);
                                                        }
                                                    }

                                                    break;
                                                }
                                            case ".exe":
                                            case ".bat":
                                            case ".msi":
                                                {
                                                    await Exclusive.Controller.RunAsync(File.Path, Path.GetDirectoryName(File.Path), WindowState.Normal, RunAsAdministrator).ConfigureAwait(true);

                                                    break;
                                                }
                                            case ".msc":
                                                {
                                                    await Exclusive.Controller.RunAsync("powershell.exe", string.Empty, WindowState.Normal, false, true, false, "-Command", File.Path).ConfigureAwait(true);

                                                    break;
                                                }
                                            default:
                                                {
                                                    ProgramPickerDialog Dialog = new ProgramPickerDialog(File);

                                                    if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                    {
                                                        if (Dialog.SelectedProgram.Path == Package.Current.Id.FamilyName)
                                                        {
                                                            switch (File.Type.ToLower())
                                                            {
                                                                case ".jpg":
                                                                case ".png":
                                                                case ".bmp":
                                                                    {
                                                                        if (AnimationController.Current.IsEnableAnimation)
                                                                        {
                                                                            Container.Frame.Navigate(typeof(PhotoViewer), File, new DrillInNavigationTransitionInfo());
                                                                        }
                                                                        else
                                                                        {
                                                                            Container.Frame.Navigate(typeof(PhotoViewer), File, new SuppressNavigationTransitionInfo());
                                                                        }
                                                                        break;
                                                                    }
                                                                case ".mkv":
                                                                case ".mp4":
                                                                case ".mp3":
                                                                case ".flac":
                                                                case ".wma":
                                                                case ".wmv":
                                                                case ".m4a":
                                                                case ".mov":
                                                                    {
                                                                        if (AnimationController.Current.IsEnableAnimation)
                                                                        {
                                                                            Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                                                        }
                                                                        else
                                                                        {
                                                                            Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                                                        }
                                                                        break;
                                                                    }
                                                                case ".txt":
                                                                    {
                                                                        if (AnimationController.Current.IsEnableAnimation)
                                                                        {
                                                                            Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                                                        }
                                                                        else
                                                                        {
                                                                            Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                                                        }
                                                                        break;
                                                                    }
                                                                case ".pdf":
                                                                    {
                                                                        if (AnimationController.Current.IsEnableAnimation)
                                                                        {
                                                                            Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                                                        }
                                                                        else
                                                                        {
                                                                            Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                                                        }
                                                                        break;
                                                                    }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            if (Path.IsPathRooted(Dialog.SelectedProgram.Path))
                                                            {
                                                                await Exclusive.Controller.RunAsync(Dialog.SelectedProgram.Path, Path.GetDirectoryName(Dialog.SelectedProgram.Path), WindowState.Normal, false, false, false, File.Path).ConfigureAwait(true);
                                                            }
                                                            else
                                                            {
                                                                if (await File.GetStorageItemAsync().ConfigureAwait(true) is StorageFile InnerFile)
                                                                {
                                                                    if (!await Launcher.LaunchFileAsync(InnerFile, new LauncherOptions { TargetApplicationPackageFamilyName = Dialog.SelectedProgram.Path, DisplayApplicationPicker = false }))
                                                                    {
                                                                        if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute1)
                                                                        {
                                                                            ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] = ProgramExcute1.Replace($"{File.Type}|{File.Name};", string.Empty);
                                                                        }

                                                                        QueueContentDialog dialog = new QueueContentDialog
                                                                        {
                                                                            Title = Globalization.GetString("Common_Dialog_TipTitle"),
                                                                            Content = Globalization.GetString("QueueDialog_OpenFailure_Content"),
                                                                            PrimaryButtonText = Globalization.GetString("QueueDialog_OpenFailure_PrimaryButton"),
                                                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                                                        };

                                                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                                                        {
                                                                            if (!await Launcher.LaunchFileAsync(InnerFile))
                                                                            {
                                                                                LauncherOptions options = new LauncherOptions
                                                                                {
                                                                                    DisplayApplicationPicker = true
                                                                                };
                                                                                _ = await Launcher.LaunchFileAsync(InnerFile, options);
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    QueueContentDialog dialog = new QueueContentDialog
                                                                    {
                                                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                                                        Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                                                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                                                    };

                                                                    _ = await dialog.ShowAsync().ConfigureAwait(true);
                                                                }
                                                            }
                                                        }
                                                    }
                                                    break;
                                                }
                                        }
                                    }
                                }

                                break;
                            }
                        case FileSystemStorageFolder Folder:
                            {
                                if (await FileSystemStorageItemBase.CheckExistAsync(Folder.Path).ConfigureAwait(true))
                                {
                                    await DisplayItemsInFolder(Folder).ConfigureAwait(true);
                                }
                                else
                                {
                                    QueueContentDialog Dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                }

                                break;
                            }
                    }
                }
                catch (InvalidOperationException)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    await Dialog.ShowAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    LogTracer.Log(ex, $"{nameof(EnterSelectedItem)} throw an exception");
                }
                finally
                {
                    Interlocked.Exchange(ref TabTarget, null);
                }
            }
        }

        private async void VideoEdit_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (GeneralTransformer.IsAnyTransformTaskRunning)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);
            }
            else
            {
                if ((await SelectedItem.GetStorageItemAsync().ConfigureAwait(true)) is StorageFile File)
                {
                    VideoEditDialog Dialog = new VideoEditDialog(File);

                    if ((await Dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                    {
                        if (await CurrentFolder.GetStorageItemAsync().ConfigureAwait(true) is StorageFolder Folder)
                        {
                            StorageFile ExportFile = await Folder.CreateFileAsync($"{File.DisplayName} - {Globalization.GetString("Crop_Image_Name_Tail")}{Dialog.ExportFileType}", CreationCollisionOption.GenerateUniqueName);
                            await GeneralTransformer.GenerateCroppedVideoFromOriginAsync(ExportFile, Dialog.Composition, Dialog.MediaEncoding, Dialog.TrimmingPreference).ConfigureAwait(true);
                        }
                    }
                }
                else
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
            }
        }

        private async void VideoMerge_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (GeneralTransformer.IsAnyTransformTaskRunning)
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_TipTitle"),
                    Content = Globalization.GetString("QueueDialog_TaskWorking_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            if ((await SelectedItem.GetStorageItemAsync().ConfigureAwait(true)) is StorageFile Item)
            {
                VideoMergeDialog Dialog = new VideoMergeDialog(Item);

                if ((await Dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                {
                    if (await CurrentFolder.GetStorageItemAsync().ConfigureAwait(true) is StorageFolder Folder)
                    {
                        StorageFile ExportFile = await Folder.CreateFileAsync($"{Item.DisplayName} - {Globalization.GetString("Merge_Image_Name_Tail")}{Dialog.ExportFileType}", CreationCollisionOption.GenerateUniqueName);

                        await GeneralTransformer.GenerateMergeVideoFromOriginAsync(ExportFile, Dialog.Composition, Dialog.MediaEncoding).ConfigureAwait(true);
                    }
                }
            }
        }

        private async void ChooseOtherApp_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFile File)
            {
                ProgramPickerDialog Dialog = new ProgramPickerDialog(File);

                if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                {
                    if (Dialog.SelectedProgram.Path == Package.Current.Id.FamilyName)
                    {
                        switch (File.Type.ToLower())
                        {
                            case ".jpg":
                            case ".png":
                            case ".bmp":
                                {
                                    if (AnimationController.Current.IsEnableAnimation)
                                    {
                                        Container.Frame.Navigate(typeof(PhotoViewer), File, new DrillInNavigationTransitionInfo());
                                    }
                                    else
                                    {
                                        Container.Frame.Navigate(typeof(PhotoViewer), File, new SuppressNavigationTransitionInfo());
                                    }
                                    break;
                                }
                            case ".mkv":
                            case ".mp4":
                            case ".mp3":
                            case ".flac":
                            case ".wma":
                            case ".wmv":
                            case ".m4a":
                            case ".mov":
                                {
                                    if (AnimationController.Current.IsEnableAnimation)
                                    {
                                        Container.Frame.Navigate(typeof(MediaPlayer), File, new DrillInNavigationTransitionInfo());
                                    }
                                    else
                                    {
                                        Container.Frame.Navigate(typeof(MediaPlayer), File, new SuppressNavigationTransitionInfo());
                                    }
                                    break;
                                }
                            case ".txt":
                                {
                                    if (AnimationController.Current.IsEnableAnimation)
                                    {
                                        Container.Frame.Navigate(typeof(TextViewer), File, new DrillInNavigationTransitionInfo());
                                    }
                                    else
                                    {
                                        Container.Frame.Navigate(typeof(TextViewer), File, new SuppressNavigationTransitionInfo());
                                    }
                                    break;
                                }
                            case ".pdf":
                                {
                                    if (AnimationController.Current.IsEnableAnimation)
                                    {
                                        Container.Frame.Navigate(typeof(PdfReader), File, new DrillInNavigationTransitionInfo());
                                    }
                                    else
                                    {
                                        Container.Frame.Navigate(typeof(PdfReader), File, new SuppressNavigationTransitionInfo());
                                    }
                                    break;
                                }
                        }
                    }
                    else
                    {
                        if (Path.IsPathRooted(Dialog.SelectedProgram.Path))
                        {
                            using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                            {
                                try
                                {
                                    await Exclusive.Controller.RunAsync(Dialog.SelectedProgram.Path, Path.GetDirectoryName(Dialog.SelectedProgram.Path), WindowState.Normal, false, false, false, File.Path).ConfigureAwait(true);
                                }
                                catch (Exception)
                                {
                                    QueueContentDialog dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    await dialog.ShowAsync().ConfigureAwait(true);
                                }
                            }
                        }
                        else
                        {
                            if ((await File.GetStorageItemAsync().ConfigureAwait(true)) is StorageFile InnerFile)
                            {
                                if (!await Launcher.LaunchFileAsync(InnerFile, new LauncherOptions { TargetApplicationPackageFamilyName = Dialog.SelectedProgram.Path, DisplayApplicationPicker = false }))
                                {
                                    if (ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] is string ProgramExcute)
                                    {
                                        ApplicationData.Current.LocalSettings.Values["AdminProgramForExcute"] = ProgramExcute.Replace($"{InnerFile.FileType}|{InnerFile.Name};", string.Empty);
                                    }

                                    QueueContentDialog dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_TipTitle"),
                                        Content = Globalization.GetString("QueueDialog_OpenFailure_Content"),
                                        PrimaryButtonText = Globalization.GetString("QueueDialog_OpenFailure_PrimaryButton"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                    };

                                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                    {
                                        if (!await Launcher.LaunchFileAsync(InnerFile))
                                        {
                                            LauncherOptions Options = new LauncherOptions
                                            {
                                                DisplayApplicationPicker = true
                                            };

                                            _ = await Launcher.LaunchFileAsync(InnerFile, Options);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                QueueContentDialog dialog = new QueueContentDialog
                                {
                                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                    Content = Globalization.GetString("QueueDialog_UnableAccessFile_Content"),
                                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                };

                                _ = await dialog.ShowAsync().ConfigureAwait(true);
                            }
                        }
                    }
                }
            }
        }

        private async void RunWithSystemAuthority_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem != null)
            {
                await EnterSelectedItem(SelectedItem, true).ConfigureAwait(false);
            }
        }

        private async void ListHeaderName_Click(object sender, RoutedEventArgs e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Name, SortDirection.Descending).ConfigureAwait(true);
            }
            else
            {
                await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Name, SortDirection.Ascending).ConfigureAwait(true);
            }
        }

        private async void ListHeaderModifiedTime_Click(object sender, RoutedEventArgs e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.ModifiedTime, SortDirection.Descending).ConfigureAwait(true);
            }
            else
            {
                await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.ModifiedTime, SortDirection.Ascending).ConfigureAwait(true);
            }
        }

        private async void ListHeaderType_Click(object sender, RoutedEventArgs e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Type, SortDirection.Descending).ConfigureAwait(true);
            }
            else
            {
                await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Type, SortDirection.Ascending).ConfigureAwait(true);
            }
        }

        private async void ListHeaderSize_Click(object sender, RoutedEventArgs e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Size, SortDirection.Descending).ConfigureAwait(true);
            }
            else
            {
                await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Size, SortDirection.Ascending).ConfigureAwait(true);
            }
        }

        private void QRTeachTip_Closing(TeachingTip sender, TeachingTipClosingEventArgs args)
        {
            QRImage.Source = null;
            WiFiProvider.Dispose();
            WiFiProvider = null;
        }

        private async void CreateFile_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            NewFileDialog Dialog = new NewFileDialog();

            if ((await Dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
            {
                try
                {
                    switch (Path.GetExtension(Dialog.NewFileName))
                    {
                        case ".zip":
                            {
                                await SpecialTypeGenerator.Current.CreateZipFile(CurrentFolder.Path, Dialog.NewFileName).ConfigureAwait(true);
                                break;
                            }
                        case ".rtf":
                            {
                                await SpecialTypeGenerator.Current.CreateRtfFile(CurrentFolder.Path, Dialog.NewFileName).ConfigureAwait(true);
                                break;
                            }
                        case ".xlsx":
                            {
                                await SpecialTypeGenerator.Current.CreateExcelFile(CurrentFolder.Path, Dialog.NewFileName).ConfigureAwait(true);
                                break;
                            }
                        case ".lnk":
                            {
                                LinkOptionsDialog dialog = new LinkOptionsDialog();

                                if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                {
                                    using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                                    {
                                        if (!await Exclusive.Controller.CreateLinkAsync(Path.Combine(CurrentFolder.Path, Dialog.NewFileName), dialog.Path, dialog.WorkDirectory, dialog.WindowState, dialog.HotKey, dialog.Comment, dialog.Arguments).ConfigureAwait(true))
                                        {
                                            throw new UnauthorizedAccessException();
                                        }
                                    }
                                }

                                break;
                            }
                        default:
                            {
                                if (await FileSystemStorageItemBase.CreateAsync(Path.Combine(CurrentFolder.Path, Dialog.NewFileName), StorageItemTypes.File, CreateOption.GenerateUniqueName).ConfigureAwait(true) == null)
                                {
                                    throw new UnauthorizedAccessException();
                                }

                                break;
                            }
                    }
                }
                catch (Exception)
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedCreateNewFile_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                    }
                }
            }
        }

        private async void CompressFolder_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFolder Folder)
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(Folder.Path).ConfigureAwait(true))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }

                CompressDialog dialog = new CompressDialog(false, Path.GetFileName(Folder.Path));

                if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
                {
                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Compressing")).ConfigureAwait(true);

                    try
                    {
                        switch (dialog.Type)
                        {
                            case CompressionType.Zip:
                                {
                                    await CompressionUtil.CreateZipAsync(Folder, Path.Combine(CurrentFolder.Path, dialog.FileName), (int)dialog.Level, async (s, e) =>
                                    {
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                        {
                                            if (Container.ProBar.Value < e.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = e.ProgressPercentage;
                                            }
                                        });
                                    }).ConfigureAwait(true);

                                    break;
                                }
                            case CompressionType.Tar:
                                {
                                    await CompressionUtil.CreateTarAsync(Folder, Path.Combine(CurrentFolder.Path, dialog.FileName), async (s, e) =>
                                    {
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                        {
                                            if (Container.ProBar.Value < e.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = e.ProgressPercentage;
                                            }
                                        });
                                    }).ConfigureAwait(true);

                                    break;
                                }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        QueueContentDialog dialog1 = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedCompression_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                        };

                        if (await dialog1.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                        {
                            _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        QueueContentDialog dialog2 = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_CompressionError_Content") + ex.Message,
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog2.ShowAsync().ConfigureAwait(true);
                    }

                    await Container.LoadingActivation(false).ConfigureAwait(true);
                }
            }
        }

        private async void ViewControl_DragOver(object sender, DragEventArgs e)
        {
            var Deferral = e.GetDeferral();

            try
            {
                Container.CurrentPresenter = this;

                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                    {
                        e.AcceptedOperation = DataPackageOperation.Move;
                        e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_MoveTo")} {CurrentFolder.Name}";
                    }
                    else
                    {
                        e.AcceptedOperation = DataPackageOperation.Copy;
                        e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_CopyTo")} {CurrentFolder.Name}";
                    }

                    e.DragUIOverride.IsContentVisible = true;
                    e.DragUIOverride.IsCaptionVisible = true;
                }
                else if (e.DataView.Contains(StandardDataFormats.Text))
                {
                    string XmlText = await e.DataView.GetTextAsync();

                    if (XmlText.Contains("RX-Explorer"))
                    {
                        XmlDocument Document = new XmlDocument();
                        Document.LoadXml(XmlText);

                        IXmlNode KindNode = Document.SelectSingleNode("/RX-Explorer/Kind");

                        if (KindNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                        {
                            if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                            {
                                e.AcceptedOperation = DataPackageOperation.Copy;
                                e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_CopyTo")} {CurrentFolder.Name}";
                            }
                            else
                            {
                                e.AcceptedOperation = DataPackageOperation.Move;
                                e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_MoveTo")} {CurrentFolder.Name}";
                            }
                        }
                        else
                        {
                            e.AcceptedOperation = DataPackageOperation.None;
                        }
                    }
                    else
                    {
                        e.AcceptedOperation = DataPackageOperation.None;
                    }
                }
                else
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex);
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private async void ItemContainer_Drop(object sender, DragEventArgs e)
        {
            DragOperationDeferral Deferral = e.GetDeferral();

            try
            {
                List<string> PathList = new List<string>();

                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    IReadOnlyList<IStorageItem> ItemList = await e.DataView.GetStorageItemsAsync();
                    PathList.AddRange(ItemList.Select((Item) => Item.Path));
                }

                if (e.DataView.Contains(StandardDataFormats.Text))
                {
                    string XmlText = await e.DataView.GetTextAsync();

                    if (XmlText.Contains("RX-Explorer"))
                    {
                        XmlDocument Document = new XmlDocument();
                        Document.LoadXml(XmlText);

                        IXmlNode KindNode = Document.SelectSingleNode("/RX-Explorer/Kind");

                        if (KindNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                        {
                            PathList.AddRange(Document.SelectNodes("/RX-Explorer/Item").Select((Node) => Node.InnerText));
                        }
                    }
                }

                if (PathList.Count > 0)
                {
                    if ((sender as SelectorItem).Content is FileSystemStorageItemBase Item)
                    {
                        switch (e.AcceptedOperation)
                        {
                            case DataPackageOperation.Copy:
                                {
                                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                                    try
                                    {
                                        await FileSystemStorageItemBase.CopyAsync(PathList, Item.Path, (s, arg) =>
                                        {
                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = arg.ProgressPercentage;
                                            }
                                        }).ConfigureAwait(true);
                                    }
                                    catch (FileNotFoundException)
                                    {
                                        QueueContentDialog Dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    catch (InvalidOperationException)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                        };

                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                        {
                                            await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                                    }

                                    break;
                                }
                            case DataPackageOperation.Move:
                                {
                                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                                    try
                                    {
                                        await FileSystemStorageItemBase.MoveAsync(PathList, Item.Path, (s, arg) =>
                                        {
                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = arg.ProgressPercentage;
                                            }
                                        }).ConfigureAwait(true);
                                    }
                                    catch (FileNotFoundException)
                                    {
                                        QueueContentDialog Dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    catch (FileCaputureException)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    catch (InvalidOperationException)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                        };

                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                        {
                                            await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                                    }

                                    break;
                                }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80040064))
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_CopyFromUnsupportedArea_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
            catch
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_FailToGetClipboardError_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
            finally
            {
                await Container.LoadingActivation(false).ConfigureAwait(true);
                e.Handled = true;
                Deferral.Complete();
            }
        }

        private void ViewControl_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                args.ItemContainer.DragStarting -= ItemContainer_DragStarting;
                args.ItemContainer.Drop -= ItemContainer_Drop;
                args.ItemContainer.DragOver -= ItemContainer_DragOver;
                args.ItemContainer.PointerEntered -= ItemContainer_PointerEntered;
                args.ItemContainer.DragEnter -= ItemContainer_DragEnter;
            }
            else
            {
                args.ItemContainer.UseSystemFocusVisuals = false;

                if (args.Item is FileSystemStorageFolder)
                {
                    args.ItemContainer.AllowDrop = true;
                    args.ItemContainer.Drop += ItemContainer_Drop;
                    args.ItemContainer.DragOver += ItemContainer_DragOver;
                    args.ItemContainer.DragEnter += ItemContainer_DragEnter;
                    args.ItemContainer.DragLeave += ItemContainer_DragLeave;
                }

                args.ItemContainer.DragStarting += ItemContainer_DragStarting;
                args.ItemContainer.PointerEntered += ItemContainer_PointerEntered;

                args.RegisterUpdateCallback(async (s, e) =>
                {
                    if (e.Item is FileSystemStorageItemBase Item)
                    {
                        await Item.LoadMorePropertyAsync().ConfigureAwait(false);
                    }
                });
            }
        }

        private void ItemContainer_DragLeave(object sender, DragEventArgs e)
        {
            DelayEnterCancel?.Cancel();
        }

        private void ItemContainer_DragEnter(object sender, DragEventArgs e)
        {
            if (sender is SelectorItem Selector && Selector.Content is FileSystemStorageItemBase Item)
            {
                DelayEnterCancel?.Cancel();
                DelayEnterCancel?.Dispose();
                DelayEnterCancel = new CancellationTokenSource();

                Task.Delay(1500).ContinueWith((task, obj) =>
                {
                    try
                    {
                        ValueTuple<CancellationTokenSource, FileSystemStorageItemBase> Tuple = (ValueTuple<CancellationTokenSource, FileSystemStorageItemBase>)obj;

                        if (!Tuple.Item1.IsCancellationRequested)
                        {
                            _ = EnterSelectedItem(Tuple.Item2);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogTracer.Log(ex, "An exception was thew in DelayEnterProcess");
                    }
                }, new ValueTuple<CancellationTokenSource, FileSystemStorageItemBase>(DelayEnterCancel, Item), TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        private async void ItemContainer_DragStarting(UIElement sender, DragStartingEventArgs args)
        {
            DragOperationDeferral Deferral = args.GetDeferral();

            try
            {
                DelayRenameCancel?.Cancel();

                List<FileSystemStorageItemBase> DragList = SelectedItems;

                foreach (FileSystemStorageItemBase Item in DragList)
                {
                    if (ItemPresenter.ContainerFromItem(Item) is SelectorItem SItem && SItem.ContentTemplateRoot.FindChildOfType<TextBox>() is TextBox NameEditBox)
                    {
                        NameEditBox.Visibility = Visibility.Collapsed;
                    }
                }

                IEnumerable<FileSystemStorageItemBase> StorageItems = DragList.Where((Item) => Item is not IUnsupportedStorageItem);

                if (StorageItems.Any())
                {
                    List<IStorageItem> TempList = new List<IStorageItem>();

                    foreach (FileSystemStorageItemBase StorageItem in StorageItems)
                    {
                        if (await StorageItem.GetStorageItemAsync().ConfigureAwait(true) is IStorageItem Item)
                        {
                            TempList.Add(Item);
                        }
                    }

                    if (TempList.Count > 0)
                    {
                        args.Data.SetStorageItems(TempList, false);
                    }
                }

                IEnumerable<FileSystemStorageItemBase> NotStorageItems = DragList.Where((Item) => Item is IUnsupportedStorageItem);

                if (NotStorageItems.Any())
                {
                    XmlDocument Document = new XmlDocument();

                    XmlElement RootElemnt = Document.CreateElement("RX-Explorer");
                    Document.AppendChild(RootElemnt);

                    XmlElement KindElement = Document.CreateElement("Kind");
                    KindElement.InnerText = "RX-Explorer-TransferNotStorageItem";
                    RootElemnt.AppendChild(KindElement);

                    foreach (FileSystemStorageItemBase Item in NotStorageItems)
                    {
                        XmlElement InnerElement = Document.CreateElement("Item");
                        InnerElement.InnerText = Item.Path;
                        RootElemnt.AppendChild(InnerElement);
                    }

                    args.Data.SetText(Document.GetXml());
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex);
            }
            finally
            {
                Deferral.Complete();
            }
        }

        private async void ItemContainer_DragOver(object sender, DragEventArgs e)
        {
            var Deferral = e.GetDeferral();

            try
            {
                if ((sender as SelectorItem)?.Content is FileSystemStorageFolder Item)
                {
                    if (e.DataView.Contains(StandardDataFormats.StorageItems))
                    {
                        if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                        {
                            e.AcceptedOperation = DataPackageOperation.Move;
                            e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_MoveTo")} {Item.Name}";
                        }
                        else
                        {
                            e.AcceptedOperation = DataPackageOperation.Copy;
                            e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_CopyTo")} {Item.Name}";
                        }

                        e.DragUIOverride.IsContentVisible = true;
                        e.DragUIOverride.IsCaptionVisible = true;
                    }
                    else if (e.DataView.Contains(StandardDataFormats.Text))
                    {
                        string XmlText = await e.DataView.GetTextAsync();

                        if (XmlText.Contains("RX-Explorer"))
                        {
                            XmlDocument Document = new XmlDocument();
                            Document.LoadXml(XmlText);

                            IXmlNode KindNode = Document.SelectSingleNode("/RX-Explorer/Kind");

                            if (KindNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                            {
                                if (e.Modifiers.HasFlag(DragDropModifiers.Control))
                                {
                                    e.AcceptedOperation = DataPackageOperation.Move;
                                    e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_MoveTo")} {Item.Name}";
                                }
                                else
                                {
                                    e.AcceptedOperation = DataPackageOperation.Copy;
                                    e.DragUIOverride.Caption = $"{Globalization.GetString("Drag_Tip_CopyTo")} {Item.Name}";
                                }
                            }
                            else
                            {
                                e.AcceptedOperation = DataPackageOperation.None;
                            }
                        }
                        else
                        {
                            e.AcceptedOperation = DataPackageOperation.None;
                        }
                    }
                    else
                    {
                        e.AcceptedOperation = DataPackageOperation.None;
                    }
                }
                else
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                }
            }
            catch (Exception ex)
            {
                LogTracer.Log(ex);
            }
            finally
            {
                Deferral.Complete();
                e.Handled = true;
            }
        }

        private void ItemContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!SettingControl.IsDoubleClickEnable
                && ItemPresenter.SelectionMode != ListViewSelectionMode.Multiple
                && SelectedItems.Count <= 1
                && e.KeyModifiers != VirtualKeyModifiers.Control
                && e.KeyModifiers != VirtualKeyModifiers.Shift
                && !Container.BlockKeyboardShortCutInput)
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Item)
                {
                    SelectedItem = Item;
                }
            }
        }

        private async void ViewControl_Drop(object sender, DragEventArgs e)
        {
            DragOperationDeferral Deferral = e.GetDeferral();

            try
            {
                List<string> PathList = new List<string>();

                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    IReadOnlyList<IStorageItem> ItemList = await e.DataView.GetStorageItemsAsync();
                    PathList.AddRange(ItemList.Select((Item) => Item.Path));
                }

                if (e.DataView.Contains(StandardDataFormats.Text))
                {
                    string XmlText = await e.DataView.GetTextAsync();

                    if (XmlText.Contains("RX-Explorer"))
                    {
                        XmlDocument Document = new XmlDocument();
                        Document.LoadXml(XmlText);

                        IXmlNode KindNode = Document.SelectSingleNode("/RX-Explorer/Kind");

                        if (KindNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                        {
                            PathList.AddRange(Document.SelectNodes("/RX-Explorer/Item").Select((Node) => Node.InnerText));
                        }
                    }
                }

                if (PathList.Count > 0)
                {
                    switch (e.AcceptedOperation)
                    {
                        case DataPackageOperation.Copy:
                            {
                                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Copying")).ConfigureAwait(true);

                                try
                                {
                                    await FileSystemStorageItemBase.CopyAsync(PathList, CurrentFolder.Path, (s, arg) =>
                                    {
                                        if (Container.ProBar.Value < arg.ProgressPercentage)
                                        {
                                            Container.ProBar.IsIndeterminate = false;
                                            Container.ProBar.Value = arg.ProgressPercentage;
                                        }
                                    }).ConfigureAwait(true);
                                }
                                catch (FileNotFoundException)
                                {
                                    QueueContentDialog Dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_CopyFailForNotExist_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                }
                                catch (InvalidOperationException)
                                {
                                    QueueContentDialog dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                        PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                    };

                                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                    {
                                        await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                                    }
                                }
                                catch (Exception)
                                {
                                    QueueContentDialog dialog = new QueueContentDialog
                                    {
                                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                        Content = Globalization.GetString("QueueDialog_CopyFailUnexpectError_Content"),
                                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                    };

                                    _ = await dialog.ShowAsync().ConfigureAwait(true);
                                }

                                break;
                            }
                        case DataPackageOperation.Move:
                            {
                                if (PathList.All((Item) => Path.GetDirectoryName(Item) != CurrentFolder.Path))
                                {
                                    await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Moving")).ConfigureAwait(true);

                                    try
                                    {
                                        await FileSystemStorageItemBase.MoveAsync(PathList, CurrentFolder.Path, (s, arg) =>
                                        {
                                            if (Container.ProBar.Value < arg.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = arg.ProgressPercentage;
                                            }
                                        }).ConfigureAwait(true);
                                    }
                                    catch (FileNotFoundException)
                                    {
                                        QueueContentDialog Dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_MoveFailForNotExist_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    catch (FileCaputureException)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_Item_Captured_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                    catch (InvalidOperationException)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_UnauthorizedPaste_Content"),
                                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                                        };

                                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                                        {
                                            await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        QueueContentDialog dialog = new QueueContentDialog
                                        {
                                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                            Content = Globalization.GetString("QueueDialog_MoveFailUnexpectError_Content"),
                                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                                        };

                                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                                    }
                                }

                                break;
                            }
                    }
                }
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80040064))
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_CopyFromUnsupportedArea_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
            catch
            {
                QueueContentDialog dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_FailToGetClipboardError_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await dialog.ShowAsync().ConfigureAwait(true);
            }
            finally
            {
                await Container.LoadingActivation(false).ConfigureAwait(true);
                e.Handled = true;
                Deferral.Complete();
            }
        }

        private async void ViewControl_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == HoldingState.Started)
            {
                e.Handled = true;

                if (ItemPresenter is GridView)
                {
                    if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Context)
                    {
                        if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                        {
                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                        }
                        else
                        {
                            SelectedItem = Context;

                            switch (Context)
                            {
                                case LinkStorageFile:
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        break;
                                    }
                                case FileSystemStorageFile:
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        break;
                                    }
                                case FileSystemStorageFolder:
                                    {
                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FolderFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        break;
                                    }
                            }
                        }
                    }
                    else
                    {
                        SelectedItem = null;
                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                    }
                }
                else
                {
                    if (e.OriginalSource is FrameworkElement Element)
                    {
                        if (Element.Name == "EmptyTextblock")
                        {
                            SelectedItem = null;
                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                        }
                        else
                        {
                            if (Element.DataContext is FileSystemStorageItemBase Context)
                            {
                                if (SelectedItems.Count > 1 && SelectedItems.Contains(Context))
                                {
                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(MixedFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                }
                                else
                                {
                                    if (SelectedItem == Context && SettingControl.IsDoubleClickEnable)
                                    {
                                        switch (Context)
                                        {
                                            case LinkStorageFile:
                                                {
                                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                    break;
                                                }
                                            case FileSystemStorageFile:
                                                {
                                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                    break;
                                                }
                                            case FileSystemStorageFolder:
                                                {
                                                    await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FolderFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                    break;
                                                }
                                        }
                                    }
                                    else
                                    {
                                        if (e.OriginalSource is TextBlock)
                                        {
                                            SelectedItem = Context;

                                            switch (Context)
                                            {
                                                case LinkStorageFile:
                                                    {
                                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(LnkItemFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                        break;
                                                    }
                                                case FileSystemStorageFile:
                                                    {
                                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FileFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                        break;
                                                    }
                                                case FileSystemStorageFolder:
                                                    {
                                                        await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(FolderFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                                        break;
                                                    }
                                            }
                                        }
                                        else
                                        {
                                            SelectedItem = null;
                                            await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                SelectedItem = null;
                                await ItemPresenter.SetCommandBarFlyoutWithExtraContextMenuItems(EmptyFlyout, e.GetPosition((FrameworkElement)sender)).ConfigureAwait(true);
                            }
                        }
                    }
                }
            }
        }

        private async void MixDecompression_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItems.Any((Item) => Item is LinkStorageFile))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LinkIsNotAllowInMixZip_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            if (SelectedItems.All((Item) => Item.Type.Equals(".zip", StringComparison.OrdinalIgnoreCase)) || SelectedItems.All((Item) => Item.Type.Equals(".tar", StringComparison.OrdinalIgnoreCase)) || SelectedItems.All((Item) => Item.Type.Equals(".gz", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (FileSystemStorageFolder Item in SelectedItems.OfType<FileSystemStorageFolder>())
                {
                    if (!await FileSystemStorageItemBase.CheckExistAsync(Item.Path).ConfigureAwait(true))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await Dialog.ShowAsync().ConfigureAwait(true);

                        return;
                    }
                }

                foreach (FileSystemStorageFile Item in SelectedItems.OfType<FileSystemStorageFile>())
                {
                    if (!await FileSystemStorageItemBase.CheckExistAsync(Item.Path).ConfigureAwait(true))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await Dialog.ShowAsync().ConfigureAwait(true);

                        return;
                    }
                }

                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Extracting")).ConfigureAwait(true);

                try
                {
                    if (SelectedItems.All((Item) => Item.Type.Equals(".zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        await CompressionUtil.ExtractZipAsync(SelectedItems.OfType<FileSystemStorageFile>(), async (s, e) =>
                        {
                            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                            {
                                if (Container.ProBar.Value < e.ProgressPercentage)
                                {
                                    Container.ProBar.IsIndeterminate = false;
                                    Container.ProBar.Value = e.ProgressPercentage;
                                }
                            });
                        }).ConfigureAwait(true);
                    }
                    else if (SelectedItems.All((Item) => Item.Type.Equals(".tar", StringComparison.OrdinalIgnoreCase)))
                    {
                        await CompressionUtil.ExtractTarAsync(SelectedItems.OfType<FileSystemStorageFile>(), async (s, e) =>
                        {
                            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                            {
                                if (Container.ProBar.Value < e.ProgressPercentage)
                                {
                                    Container.ProBar.IsIndeterminate = false;
                                    Container.ProBar.Value = e.ProgressPercentage;
                                }
                            });
                        }).ConfigureAwait(true);
                    }
                    else if (SelectedItems.All((Item) => Item.Type.Equals(".gz", StringComparison.OrdinalIgnoreCase)))
                    {
                        await CompressionUtil.ExtractGZipAsync(SelectedItems.OfType<FileSystemStorageFile>(), async (s, e) =>
                        {
                            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                            {
                                if (Container.ProBar.Value < e.ProgressPercentage)
                                {
                                    Container.ProBar.IsIndeterminate = false;
                                    Container.ProBar.Value = e.ProgressPercentage;
                                }
                            });
                        }).ConfigureAwait(true);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedDecompression_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                    }
                }
                catch (NotImplementedException)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_CanNotDecompressEncrypted_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_DecompressionError_Content") + ex.Message,
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await dialog.ShowAsync().ConfigureAwait(true);
                }

                await Container.LoadingActivation(false).ConfigureAwait(true);
            }
            else
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_FileTypeIncorrect_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                await Dialog.ShowAsync().ConfigureAwait(true);
            }
        }

        private async void MixCompression_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItems.Any((Item) => Item is LinkStorageFile))
            {
                QueueContentDialog Dialog = new QueueContentDialog
                {
                    Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                    Content = Globalization.GetString("QueueDialog_LinkIsNotAllowInMixZip_Content"),
                    CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                };

                _ = await Dialog.ShowAsync().ConfigureAwait(true);

                return;
            }

            foreach (FileSystemStorageFolder Folder in SelectedItems.OfType<FileSystemStorageFolder>())
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(Folder.Path).ConfigureAwait(true))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFolderFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }
            }

            foreach (FileSystemStorageFolder File in SelectedItems.OfType<FileSystemStorageFolder>())
            {
                if (!await FileSystemStorageItemBase.CheckExistAsync(File.Path).ConfigureAwait(true))
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }
            }

            CompressDialog dialog = new CompressDialog(false);

            if ((await dialog.ShowAsync().ConfigureAwait(true)) == ContentDialogResult.Primary)
            {
                await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Compressing")).ConfigureAwait(true);

                try
                {
                    switch (dialog.Type)
                    {
                        case CompressionType.Zip:
                            {
                                await CompressionUtil.CreateZipAsync(SelectedItems, Path.Combine(CurrentFolder.Path, dialog.FileName), (int)dialog.Level, async (s, e) =>
                                {
                                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                    {
                                        if (Container.ProBar.Value < e.ProgressPercentage)
                                        {
                                            Container.ProBar.IsIndeterminate = false;
                                            Container.ProBar.Value = e.ProgressPercentage;
                                        }
                                    });
                                }).ConfigureAwait(true);

                                break;
                            }
                        case CompressionType.Tar:
                            {
                                await CompressionUtil.CreateTarAsync(SelectedItems, Path.Combine(CurrentFolder.Path, dialog.FileName), async (s, e) =>
                                {
                                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                    {
                                        if (Container.ProBar.Value < e.ProgressPercentage)
                                        {
                                            Container.ProBar.IsIndeterminate = false;
                                            Container.ProBar.Value = e.ProgressPercentage;
                                        }
                                    });
                                }).ConfigureAwait(true);

                                break;
                            }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    QueueContentDialog dialog1 = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedCompression_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    if (await dialog1.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                    }
                }
                catch (Exception ex)
                {
                    QueueContentDialog dialog2 = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_CompressionError_Content") + ex.Message,
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await dialog2.ShowAsync().ConfigureAwait(true);
                }

                await Container.LoadingActivation(false).ConfigureAwait(true);
            }
        }

        private async void OpenInTerminal_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (await SQLite.Current.GetTerminalProfileByName(Convert.ToString(ApplicationData.Current.LocalSettings.Values["DefaultTerminal"])).ConfigureAwait(true) is TerminalProfile Profile)
            {
                using (FullTrustProcessController.ExclusiveUsage Exclusive = await FullTrustProcessController.GetAvailableController())
                {
                    try
                    {
                        await Exclusive.Controller.RunAsync(Profile.Path, string.Empty, WindowState.Normal, Profile.RunAsAdmin, false, false, Regex.Matches(Profile.Argument, "[^ \"]+|\"[^\"]*\"").Select((Mat) => Mat.Value.Contains("[CurrentLocation]") ? Mat.Value.Replace("[CurrentLocation]", CurrentFolder.Path) : Mat.Value).ToArray()).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        LogTracer.Log(ex, "An exception was threw when running terminal");

                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LaunchFailed_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        await Dialog.ShowAsync().ConfigureAwait(true);
                    }
                }
            }
        }

        private async void OpenFolderInNewTab_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFolder Folder)
            {
                await TabViewContainer.ThisPage.CreateNewTabAsync(null, Folder.Path).ConfigureAwait(true);
            }
        }

        private void NameLabel_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            TextBlock NameLabel = (TextBlock)sender;

            if ((e.GetCurrentPoint(NameLabel).Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) && SettingControl.IsDoubleClickEnable)
            {
                if ((e.OriginalSource as FrameworkElement)?.DataContext is FileSystemStorageItemBase Item)
                {
                    if (SelectedItem == Item)
                    {
                        DelayRenameCancel?.Dispose();
                        DelayRenameCancel = new CancellationTokenSource();

                        Task.Delay(1000).ContinueWith((task) =>
                        {
                            if (DelayRenameCancel != null && !DelayRenameCancel.IsCancellationRequested)
                            {
                                NameLabel.Visibility = Visibility.Collapsed;

                                if ((NameLabel.Parent as FrameworkElement).FindName("NameEditBox") is TextBox EditBox)
                                {
                                    EditBox.Tag = SelectedItem;
                                    EditBox.Text = NameLabel.Text;
                                    EditBox.Visibility = Visibility.Visible;
                                    EditBox.Focus(FocusState.Programmatic);
                                }

                                Container.BlockKeyboardShortCutInput = true;
                            }
                        }, TaskScheduler.FromCurrentSynchronizationContext());
                    }
                }
            }
        }

        private async void NameEditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            TextBox NameEditBox = (TextBox)sender;

            if ((NameEditBox?.Parent as FrameworkElement)?.FindName("NameLabel") is TextBlock NameLabel && NameEditBox.Tag is FileSystemStorageItemBase CurrentEditItem)
            {
                try
                {
                    if (!FileSystemItemNameChecker.IsValid(NameEditBox.Text))
                    {
                        InvalidNameTip.Target = NameLabel;
                        InvalidNameTip.IsOpen = true;
                        return;
                    }

                    if (CurrentEditItem.Name == NameEditBox.Text)
                    {
                        return;
                    }

                    if (await FileSystemStorageItemBase.CheckExistAsync(Path.Combine(CurrentFolder.Path, NameEditBox.Text)).ConfigureAwait(true))
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_RenameExist_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ContinueButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        if (await Dialog.ShowAsync().ConfigureAwait(true) != ContentDialogResult.Primary)
                        {
                            return;
                        }
                    }

                    try
                    {
                        await CurrentEditItem.RenameAsync(NameEditBox.Text).ConfigureAwait(true);
                    }
                    catch (FileLoadException)
                    {
                        QueueContentDialog LoadExceptionDialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_FileOccupied_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton"),
                        };

                        _ = await LoadExceptionDialog.ShowAsync().ConfigureAwait(true);
                    }
                    catch (InvalidOperationException)
                    {
                        QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedRenameFile_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_ConfirmButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CancelButton")
                        };

                        if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                        {
                            await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                        }
                    }
                }
                catch
                {
                    QueueContentDialog UnauthorizeDialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_UnauthorizedRename_StartExplorer_Content"),
                        PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                    };

                    if (await UnauthorizeDialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                    {
                        _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                    }
                }
                finally
                {
                    NameEditBox.Visibility = Visibility.Collapsed;

                    NameLabel.Visibility = Visibility.Visible;

                    Container.BlockKeyboardShortCutInput = false;
                }
            }
        }

        private void GetFocus_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ItemPresenter.Focus(FocusState.Programmatic);
        }

        private async void OpenFolderInNewWindow_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem is FileSystemStorageFolder Folder)
            {
                await Launcher.LaunchUriAsync(new Uri($"rx-explorer:{Uri.EscapeDataString(Folder.Path)}"));
            }
        }

        private async void Undo_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            await Ctrl_Z_Click().ConfigureAwait(false);
        }

        private void NameEditBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
        {
            if (args.NewText.Any((Item) => Path.GetInvalidFileNameChars().Contains(Item)))
            {
                args.Cancel = true;

                if ((sender.Parent as FrameworkElement).FindName("NameLabel") is TextBlock NameLabel)
                {
                    InvalidCharTip.Target = NameLabel;
                    InvalidCharTip.IsOpen = true;
                }
            }
        }

        private async void OrderByName_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Name, Desc.IsChecked ? SortDirection.Descending : SortDirection.Ascending).ConfigureAwait(true);
        }

        private async void OrderByTime_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.ModifiedTime, Desc.IsChecked ? SortDirection.Descending : SortDirection.Ascending).ConfigureAwait(true);
        }

        private async void OrderByType_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Type, Desc.IsChecked ? SortDirection.Descending : SortDirection.Ascending).ConfigureAwait(true);
        }

        private async void OrderBySize_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortTarget.Size, Desc.IsChecked ? SortDirection.Descending : SortDirection.Ascending).ConfigureAwait(true);
        }

        private async void Desc_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortDirection: SortDirection.Descending).ConfigureAwait(true);
        }

        private async void Asc_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            await SortCollectionGenerator.Current.ModifySortWayAsync(CurrentFolder.Path, SortDirection: SortDirection.Ascending).ConfigureAwait(true);
        }

        private void SortMenuFlyout_Opening(object sender, object e)
        {
            if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
            {
                Desc.IsChecked = false;
                Asc.IsChecked = true;
            }
            else
            {
                Asc.IsChecked = false;
                Desc.IsChecked = true;
            }

            switch (SortCollectionGenerator.Current.SortTarget)
            {
                case SortTarget.Name:
                    {
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = true;
                        break;
                    }
                case SortTarget.Type:
                    {
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = true;
                        break;
                    }
                case SortTarget.ModifiedTime:
                    {
                        OrderBySize.IsChecked = false;
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = true;
                        break;
                    }
                case SortTarget.Size:
                    {
                        OrderByName.IsChecked = false;
                        OrderByType.IsChecked = false;
                        OrderByTime.IsChecked = false;
                        OrderBySize.IsChecked = true;
                        break;
                    }
            }
        }

        private async void BottomCommandBar_Opening(object sender, object e)
        {
            BottomCommandBar.PrimaryCommands.Clear();
            BottomCommandBar.SecondaryCommands.Clear();

            AppBarButton MultiSelectButton = new AppBarButton
            {
                Icon = new FontIcon { Glyph = "\uE762" },
                Label = Globalization.GetString("Operate_Text_MultiSelect")
            };
            MultiSelectButton.Click += MulSelect_Click;
            BottomCommandBar.PrimaryCommands.Add(MultiSelectButton);

            if (SelectedItems.Count > 1)
            {
                AppBarButton CopyButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Copy),
                    Label = Globalization.GetString("Operate_Text_Copy")
                };
                CopyButton.Click += Copy_Click;
                BottomCommandBar.PrimaryCommands.Add(CopyButton);

                AppBarButton CutButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Cut),
                    Label = Globalization.GetString("Operate_Text_Cut")
                };
                CutButton.Click += Cut_Click;
                BottomCommandBar.PrimaryCommands.Add(CutButton);

                AppBarButton DeleteButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Delete),
                    Label = Globalization.GetString("Operate_Text_Delete")
                };
                DeleteButton.Click += Delete_Click;
                BottomCommandBar.PrimaryCommands.Add(DeleteButton);

                AppBarButton CompressionButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Bookmarks),
                    MinWidth = 250,
                    Label = Globalization.GetString("Operate_Text_Compression")
                };
                CompressionButton.Click += MixCompression_Click;
                BottomCommandBar.SecondaryCommands.Add(CompressionButton);

                AppBarButton DecompressionButton = new AppBarButton
                {
                    Icon = new SymbolIcon(Symbol.Bookmarks),
                    MinWidth = 250,
                    Label = Globalization.GetString("Operate_Text_Decompression")
                };
                DecompressionButton.Click += MixDecompression_Click;
                BottomCommandBar.SecondaryCommands.Add(DecompressionButton);
            }
            else
            {
                if (SelectedItem is FileSystemStorageItemBase Item)
                {
                    AppBarButton CopyButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Copy),
                        Label = Globalization.GetString("Operate_Text_Copy")
                    };
                    CopyButton.Click += Copy_Click;
                    BottomCommandBar.PrimaryCommands.Add(CopyButton);

                    AppBarButton CutButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Cut),
                        Label = Globalization.GetString("Operate_Text_Cut")
                    };
                    CutButton.Click += Cut_Click;
                    BottomCommandBar.PrimaryCommands.Add(CutButton);

                    AppBarButton DeleteButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Delete),
                        Label = Globalization.GetString("Operate_Text_Delete")
                    };
                    DeleteButton.Click += Delete_Click;
                    BottomCommandBar.PrimaryCommands.Add(DeleteButton);

                    AppBarButton RenameButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Rename),
                        Label = Globalization.GetString("Operate_Text_Rename")
                    };
                    RenameButton.Click += Rename_Click;
                    BottomCommandBar.PrimaryCommands.Add(RenameButton);

                    if (Item is FileSystemStorageFile)
                    {
                        AppBarButton OpenButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.OpenFile),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_Open")
                        };
                        OpenButton.Click += ItemOpen_Click;
                        BottomCommandBar.SecondaryCommands.Add(OpenButton);

                        MenuFlyout OpenFlyout = new MenuFlyout();
                        MenuFlyoutItem AdminItem = new MenuFlyoutItem
                        {
                            Icon = new FontIcon { Glyph = "\uEA0D" },
                            MinWidth = 150,
                            MaxWidth = 300,
                            Text = Globalization.GetString("Operate_Text_OpenAsAdministrator"),
                            IsEnabled = RunWithSystemAuthority.IsEnabled
                        };
                        AdminItem.Click += RunWithSystemAuthority_Click;
                        OpenFlyout.Items.Add(AdminItem);

                        MenuFlyoutItem OtherItem = new MenuFlyoutItem
                        {
                            Icon = new SymbolIcon(Symbol.SwitchApps),
                            MinWidth = 150,
                            MaxWidth = 300,
                            Text = Globalization.GetString("Operate_Text_ChooseAnotherApp"),
                            IsEnabled = ChooseOtherApp.IsEnabled
                        };
                        OtherItem.Click += ChooseOtherApp_Click;
                        OpenFlyout.Items.Add(OtherItem);

                        BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.OpenWith),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_OpenWith"),
                            Flyout = OpenFlyout
                        });

                        BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                        MenuFlyout EditFlyout = new MenuFlyout();
                        MenuFlyoutItem MontageItem = new MenuFlyoutItem
                        {
                            Icon = new FontIcon { Glyph = "\uE177" },
                            MinWidth = 150,
                            MaxWidth = 300,
                            Text = Globalization.GetString("Operate_Text_Montage"),
                            IsEnabled = VideoEdit.IsEnabled
                        };
                        MontageItem.Click += VideoEdit_Click;
                        EditFlyout.Items.Add(MontageItem);

                        MenuFlyoutItem MergeItem = new MenuFlyoutItem
                        {
                            Icon = new FontIcon { Glyph = "\uE11E" },
                            MinWidth = 150,
                            MaxWidth = 300,
                            Text = Globalization.GetString("Operate_Text_Merge"),
                            IsEnabled = VideoMerge.IsEnabled
                        };
                        MergeItem.Click += VideoMerge_Click;
                        EditFlyout.Items.Add(MergeItem);

                        MenuFlyoutItem TranscodeItem = new MenuFlyoutItem
                        {
                            Icon = new FontIcon { Glyph = "\uE1CA" },
                            MinWidth = 150,
                            MaxWidth = 300,
                            Text = Globalization.GetString("Operate_Text_Transcode"),
                            IsEnabled = Transcode.IsEnabled
                        };
                        TranscodeItem.Click += Transcode_Click;
                        EditFlyout.Items.Add(TranscodeItem);

                        BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Edit),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_Edit"),
                            IsEnabled = FileEdit.IsEnabled,
                            Flyout = EditFlyout
                        });

                        MenuFlyout ShareFlyout = new MenuFlyout();
                        MenuFlyoutItem SystemShareItem = new MenuFlyoutItem
                        {
                            Icon = new SymbolIcon(Symbol.Share),
                            MinWidth = 150,
                            MaxWidth = 300,
                            Text = Globalization.GetString("Operate_Text_SystemShare")
                        };
                        SystemShareItem.Click += SystemShare_Click;
                        ShareFlyout.Items.Add(SystemShareItem);

                        MenuFlyoutItem WIFIShareItem = new MenuFlyoutItem
                        {
                            Icon = new FontIcon { Glyph = "\uE701" },
                            MinWidth = 150,
                            MaxWidth = 300,
                            Text = Globalization.GetString("Operate_Text_WIFIShare")
                        };
                        WIFIShareItem.Click += WIFIShare_Click;
                        ShareFlyout.Items.Add(WIFIShareItem);

                        MenuFlyoutItem BluetoothShare = new MenuFlyoutItem
                        {
                            Icon = new FontIcon { Glyph = "\uE702" },
                            MinWidth = 150,
                            MaxWidth = 300,
                            Text = Globalization.GetString("Operate_Text_BluetoothShare")
                        };
                        BluetoothShare.Click += BluetoothShare_Click;
                        ShareFlyout.Items.Add(BluetoothShare);

                        BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Share),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_Share"),
                            IsEnabled = FileShare.IsEnabled,
                            Flyout = ShareFlyout
                        });

                        BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                        AppBarButton CompressionButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Bookmarks),
                            MinWidth = 250,
                            Label = Compression.Label
                        };
                        CompressionButton.Click += Compression_Click;
                        BottomCommandBar.SecondaryCommands.Add(CompressionButton);

                        MenuFlyout DecompressionFlyout = new MenuFlyout();

                        MenuFlyoutItem DecompressionOption1 = new MenuFlyoutItem
                        {
                            Text = Globalization.GetString("DecompressOption/Text"),
                            MinWidth = 150,
                            MaxWidth = 300,
                            Icon = new FontIcon
                            {
                                Glyph = "\uF0B2"
                            }
                        };
                        DecompressionOption1.Click += DecompressOption_Click;
                        DecompressionFlyout.Items.Add(DecompressionOption1);

                        MenuFlyoutItem DecompressionOption2= new MenuFlyoutItem
                        {
                            Text = Globalization.GetString("DecompressHere/Text"),
                            MinWidth = 150,
                            MaxWidth = 300,
                            Icon = new FontIcon
                            {
                                Glyph = "\uF0B2"
                            },
                        };
                        DecompressionOption2.Click += Decompression_Click;
                        DecompressionFlyout.Items.Add(DecompressionOption2);

                        MenuFlyoutItemWithImage DecompressionOption3 = new MenuFlyoutItemWithImage
                        {
                            Text = $"{Globalization.GetString("DecompressTo")} \"{Path.GetFileNameWithoutExtension(SelectedItem.Path)}\\\"",
                            MinWidth = 150,
                            MaxWidth = 300,
                            Icon = new FontIcon
                            {
                                Glyph = "\uF0B2"
                            }
                        };
                        DecompressionOption3.Click += Decompression_Click;
                        DecompressionFlyout.Items.Add(DecompressionOption3);

                        BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Bookmarks),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_Decompression"),
                            Flyout = DecompressionFlyout
                        });

                        BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                        AppBarButton PropertyButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Tag),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_Property")
                        };
                        PropertyButton.Click += FileProperty_Click;
                        BottomCommandBar.SecondaryCommands.Add(PropertyButton);
                    }
                    else
                    {
                        AppBarButton OpenButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.BackToWindow),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_Open")
                        };
                        OpenButton.Click += ItemOpen_Click;
                        BottomCommandBar.SecondaryCommands.Add(OpenButton);

                        AppBarButton NewWindowButton = new AppBarButton
                        {
                            Icon = new FontIcon { Glyph = "\uE727" },
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_NewWindow")
                        };
                        NewWindowButton.Click += OpenFolderInNewWindow_Click;
                        BottomCommandBar.SecondaryCommands.Add(NewWindowButton);

                        AppBarButton NewTabButton = new AppBarButton
                        {
                            Icon = new FontIcon { Glyph = "\uF7ED" },
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_NewTab")
                        };
                        NewTabButton.Click += OpenFolderInNewTab_Click;
                        BottomCommandBar.SecondaryCommands.Add(NewTabButton);

                        AppBarButton SplitViewButton = new AppBarButton
                        {
                            Icon = new FontIcon { Glyph = "\uE140" },
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_SplitView")
                        };
                        SplitViewButton.Click += OpenFolderInVerticalSplitView_Click;
                        BottomCommandBar.SecondaryCommands.Add(SplitViewButton);

                        BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                        AppBarButton CompressionButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Bookmarks),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_Compression")
                        };
                        CompressionButton.Click += CompressFolder_Click;
                        BottomCommandBar.SecondaryCommands.Add(CompressionButton);

                        BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                        AppBarButton PropertyButton = new AppBarButton
                        {
                            Icon = new SymbolIcon(Symbol.Tag),
                            MinWidth = 250,
                            Label = Globalization.GetString("Operate_Text_Property")
                        };
                        PropertyButton.Click += FolderProperty_Click;
                        BottomCommandBar.SecondaryCommands.Add(PropertyButton);
                    }
                }
                else
                {
                    bool IsEnablePaste = false;

                    try
                    {
                        DataPackageView Package = Clipboard.GetContent();

                        if (Package.Contains(StandardDataFormats.StorageItems))
                        {
                            IsEnablePaste = true;
                        }
                        else if (Package.Contains(StandardDataFormats.Text))
                        {
                            string XmlText = await Package.GetTextAsync();

                            if (XmlText.Contains("RX-Explorer"))
                            {
                                XmlDocument Document = new XmlDocument();
                                Document.LoadXml(XmlText);

                                IXmlNode KindNode = Document.SelectSingleNode("/RX-Explorer/Kind");

                                if (KindNode?.InnerText == "RX-Explorer-TransferNotStorageItem")
                                {
                                    IsEnablePaste = true;
                                }
                            }
                        }
                    }
                    catch
                    {
                        IsEnablePaste = false;
                    }

                    AppBarButton PasteButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Paste),
                        Label = Globalization.GetString("Operate_Text_Paste"),
                        IsEnabled = IsEnablePaste
                    };
                    PasteButton.Click += Paste_Click;
                    BottomCommandBar.PrimaryCommands.Add(PasteButton);

                    AppBarButton UndoButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Undo),
                        Label = Globalization.GetString("Operate_Text_Undo"),
                        IsEnabled = OperationRecorder.Current.Count > 0
                    };
                    UndoButton.Click += Undo_Click;
                    BottomCommandBar.PrimaryCommands.Add(UndoButton);

                    AppBarButton RefreshButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Refresh),
                        Label = Globalization.GetString("Operate_Text_Refresh")
                    };
                    RefreshButton.Click += Refresh_Click;
                    BottomCommandBar.PrimaryCommands.Add(RefreshButton);

                    MenuFlyout NewFlyout = new MenuFlyout();
                    MenuFlyoutItem CreateFileItem = new MenuFlyoutItem
                    {
                        Icon = new SymbolIcon(Symbol.Page2),
                        Text = Globalization.GetString("Operate_Text_CreateFile"),
                        MinWidth = 150,
                        MaxWidth = 300
                    };
                    CreateFileItem.Click += CreateFile_Click;
                    NewFlyout.Items.Add(CreateFileItem);

                    MenuFlyoutItem CreateFolder = new MenuFlyoutItem
                    {
                        Icon = new SymbolIcon(Symbol.NewFolder),
                        Text = Globalization.GetString("Operate_Text_CreateFolder"),
                        MinWidth = 150,
                        MaxWidth = 300
                    };
                    CreateFolder.Click += CreateFolder_Click;
                    NewFlyout.Items.Add(CreateFolder);

                    BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Add),
                        MinWidth = 250,
                        Label = Globalization.GetString("Operate_Text_Create"),
                        Flyout = NewFlyout
                    });

                    bool DescCheck = false;
                    bool AscCheck = false;
                    bool NameCheck = false;
                    bool TimeCheck = false;
                    bool TypeCheck = false;
                    bool SizeCheck = false;

                    if (SortCollectionGenerator.Current.SortDirection == SortDirection.Ascending)
                    {
                        DescCheck = false;
                        AscCheck = true;
                    }
                    else
                    {
                        AscCheck = false;
                        DescCheck = true;
                    }

                    switch (SortCollectionGenerator.Current.SortTarget)
                    {
                        case SortTarget.Name:
                            {
                                TypeCheck = false;
                                TimeCheck = false;
                                SizeCheck = false;
                                NameCheck = true;
                                break;
                            }
                        case SortTarget.Type:
                            {
                                TimeCheck = false;
                                SizeCheck = false;
                                NameCheck = false;
                                TypeCheck = true;
                                break;
                            }
                        case SortTarget.ModifiedTime:
                            {
                                SizeCheck = false;
                                NameCheck = false;
                                TypeCheck = false;
                                TimeCheck = true;
                                break;
                            }
                        case SortTarget.Size:
                            {
                                NameCheck = false;
                                TypeCheck = false;
                                TimeCheck = false;
                                SizeCheck = true;
                                break;
                            }
                    }

                    MenuFlyout SortFlyout = new MenuFlyout();

                    RadioMenuFlyoutItem SortName = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortTarget_Name"),
                        MinWidth = 150,
                        MaxWidth = 300,
                        IsChecked = NameCheck
                    };
                    SortName.Click += OrderByName_Click;
                    SortFlyout.Items.Add(SortName);

                    RadioMenuFlyoutItem SortTime = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortTarget_Time"),
                        MinWidth = 150,
                        MaxWidth = 300,
                        IsChecked = TimeCheck
                    };
                    SortTime.Click += OrderByTime_Click;
                    SortFlyout.Items.Add(SortTime);

                    RadioMenuFlyoutItem SortType = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortTarget_Type"),
                        MinWidth = 150,
                        MaxWidth = 300,
                        IsChecked = TypeCheck
                    };
                    SortType.Click += OrderByType_Click;
                    SortFlyout.Items.Add(SortType);

                    RadioMenuFlyoutItem SortSize = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortTarget_Size"),
                        MinWidth = 150,
                        MaxWidth = 300,
                        IsChecked = SizeCheck
                    };
                    SortSize.Click += OrderBySize_Click;
                    SortFlyout.Items.Add(SortSize);

                    SortFlyout.Items.Add(new MenuFlyoutSeparator());

                    RadioMenuFlyoutItem Asc = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortDirection_Asc"),
                        MinWidth = 150,
                        MaxWidth = 300,
                        IsChecked = AscCheck
                    };
                    Asc.Click += Asc_Click;
                    SortFlyout.Items.Add(Asc);

                    RadioMenuFlyoutItem Desc = new RadioMenuFlyoutItem
                    {
                        Text = Globalization.GetString("Operate_Text_SortDirection_Desc"),
                        MinWidth = 150,
                        MaxWidth = 300,
                        IsChecked = DescCheck
                    };
                    Desc.Click += Desc_Click;
                    SortFlyout.Items.Add(Desc);

                    BottomCommandBar.SecondaryCommands.Add(new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Sort),
                        MinWidth = 250,
                        Label = Globalization.GetString("Operate_Text_Sort"),
                        Flyout = SortFlyout
                    });

                    BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                    AppBarButton WinExButton = new AppBarButton
                    {
                        Icon = new FontIcon { Glyph = "\uEC50" },
                        MinWidth = 250,
                        Label = Globalization.GetString("Operate_Text_OpenInWinExplorer")
                    };
                    WinExButton.Click += UseSystemFileMananger_Click;
                    BottomCommandBar.SecondaryCommands.Add(WinExButton);

                    AppBarButton TerminalButton = new AppBarButton
                    {
                        Icon = new FontIcon { Glyph = "\uE756" },
                        MinWidth = 250,
                        Label = Globalization.GetString("Operate_Text_OpenInTerminal")
                    };
                    TerminalButton.Click += OpenInTerminal_Click;
                    BottomCommandBar.SecondaryCommands.Add(TerminalButton);

                    BottomCommandBar.SecondaryCommands.Add(new AppBarSeparator());

                    AppBarButton PropertyButton = new AppBarButton
                    {
                        Icon = new SymbolIcon(Symbol.Tag),
                        MinWidth = 250,
                        Label = Globalization.GetString("Operate_Text_Property")
                    };
                    PropertyButton.Click += ParentProperty_Click;
                    BottomCommandBar.SecondaryCommands.Add(PropertyButton);
                }
            }
        }

        private void ListHeader_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void LnkOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is LinkStorageFile Item)
            {
                if (Item.LinkTargetPath == Globalization.GetString("UnknownText") || Item.LinkType == ShellLinkType.UWP)
                {
                    QueueContentDialog Dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await Dialog.ShowAsync().ConfigureAwait(true);
                }
                else
                {
                    if (await FileSystemStorageItemBase.OpenAsync(Path.GetDirectoryName(Item.LinkTargetPath)).ConfigureAwait(true) is FileSystemStorageFolder ParentFolder)
                    {
                        await DisplayItemsInFolder(ParentFolder).ConfigureAwait(true);

                        if (FileCollection.FirstOrDefault((SItem) => SItem.Path.Equals(Item.LinkTargetPath, StringComparison.OrdinalIgnoreCase)) is FileSystemStorageItemBase Target)
                        {
                            ItemPresenter.ScrollIntoView(Target);
                            SelectedItem = Target;
                        }
                    }
                    else
                    {
                        QueueContentDialog Dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await Dialog.ShowAsync().ConfigureAwait(true);
                    }
                }
            }
        }

        private void MulSelect_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (ItemPresenter.SelectionMode == ListViewSelectionMode.Extended)
            {
                ItemPresenter.SelectionMode = ListViewSelectionMode.Multiple;
            }
            else
            {
                ItemPresenter.SelectionMode = ListViewSelectionMode.Extended;
            }
        }

        private void ViewControl_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.OriginalSource is not TextBox)
            {
                switch (e.Key)
                {
                    case VirtualKey.Space:
                        {
                            e.Handled = true;
                            break;
                        }
                }
            }
        }

        private void ListHeaderRelativePanel_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if ((sender as FrameworkElement).FindChildOfName<Button>("NameFilterHeader") is Button NameFilterBtn)
            {
                NameFilterBtn.Visibility = Visibility.Visible;
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("ModTimeFilterHeader") is Button ModTimeFilterBtn)
            {
                ModTimeFilterBtn.Visibility = Visibility.Visible;
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("TypeFilterHeader") is Button TypeFilterBtn)
            {
                TypeFilterBtn.Visibility = Visibility.Visible;
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("SizeFilterHeader") is Button SizeFilterBtn)
            {
                SizeFilterBtn.Visibility = Visibility.Visible;
            }
        }

        private void ListHeaderRelativePanel_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if ((sender as FrameworkElement).FindChildOfName<Button>("NameFilterHeader") is Button NameFilterBtn)
            {
                if (!NameFilterBtn.Flyout.IsOpen)
                {
                    NameFilterBtn.Visibility = Visibility.Collapsed;
                }
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("ModTimeFilterHeader") is Button ModTimeFilterBtn)
            {
                if (!ModTimeFilterBtn.Flyout.IsOpen)
                {
                    ModTimeFilterBtn.Visibility = Visibility.Collapsed;
                }
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("TypeFilterHeader") is Button TypeFilterBtn)
            {
                if (!TypeFilterBtn.Flyout.IsOpen)
                {
                    TypeFilterBtn.Visibility = Visibility.Collapsed;
                }
            }
            else if ((sender as FrameworkElement).FindChildOfName<Button>("SizeFilterHeader") is Button SizeFilterBtn)
            {
                if (!SizeFilterBtn.Flyout.IsOpen)
                {
                    SizeFilterBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void FilterFlyout_Closing(FlyoutBase sender, FlyoutBaseClosingEventArgs args)
        {
            Container.BlockKeyboardShortCutInput = false;
            sender.Target.Visibility = Visibility.Collapsed;
        }

        private void FilterFlyout_Opened(object sender, object e)
        {
            Container.BlockKeyboardShortCutInput = true;
        }

        private void Filter_RefreshListRequested(object sender, IEnumerable<FileSystemStorageItemBase> e)
        {
            FileCollection.Clear();

            foreach (var Item in e)
            {
                FileCollection.Add(Item);
            }
        }

        private async void OpenFolderInVerticalSplitView_Click(object sender, RoutedEventArgs e)
        {
            CloseAllFlyout();

            if (SelectedItem != null)
            {
                await Container.CreateNewBlade(SelectedItem.Path).ConfigureAwait(false);
            }
        }

        private void NameEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                ItemPresenter.Focus(FocusState.Programmatic);
            }
        }

        private void DecompressionOptionFlyout_Opening(object sender, object e)
        {
            DecompressionOption2.Text = $"{Globalization.GetString("DecompressTo")} \"{Path.GetFileNameWithoutExtension(SelectedItem.Path)}\\\"";
        }

        private async void DecompressOption_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is FileSystemStorageFile File)
            {
                CloseAllFlyout();

                if (!await FileSystemStorageItemBase.CheckExistAsync(File.Path).ConfigureAwait(true))
                {
                    QueueContentDialog dialog = new QueueContentDialog
                    {
                        Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                        Content = Globalization.GetString("QueueDialog_LocateFileFailure_Content"),
                        CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                    };

                    _ = await dialog.ShowAsync().ConfigureAwait(true);

                    return;
                }

                switch (File.Type.ToLower())
                {
                    case ".zip":
                    case ".tar":
                    case ".gz":
                        {
                            break;
                        }
                    default:
                        {
                            QueueContentDialog dialog = new QueueContentDialog
                            {
                                Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                                Content = Globalization.GetString("QueueDialog_FileTypeIncorrect_Content"),
                                CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                            };

                            await dialog.ShowAsync().ConfigureAwait(true);

                            return;
                        }
                }

                DecompressDialog Dialog = new DecompressDialog(Path.GetDirectoryName(File.Path));

                if (await Dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                {
                    try
                    {
                        await Container.LoadingActivation(true, Globalization.GetString("Progress_Tip_Extracting")).ConfigureAwait(true);

                        FileSystemStorageFolder TargetFolder = await FileSystemStorageItemBase.CreateAsync(Path.Combine(Dialog.ExtractLocation, Path.GetFileNameWithoutExtension(File.Name)), StorageItemTypes.Folder, CreateOption.GenerateUniqueName).ConfigureAwait(true) as FileSystemStorageFolder;

                        if (TargetFolder == null)
                        {
                            throw new UnauthorizedAccessException();
                        }

                        CompressionUtil.SetEncoding(Dialog.CurrentEncoding);

                        switch (File.Type.ToLower())
                        {
                            case ".zip":
                                {
                                    await CompressionUtil.ExtractZipAsync(TargetFolder, File, async (s, e) =>
                                    {
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                        {
                                            if (Container.ProBar.Value < e.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = e.ProgressPercentage;
                                            }
                                        });
                                    }).ConfigureAwait(true);

                                    break;
                                }
                            case ".tar":
                                {
                                    await CompressionUtil.ExtractTarAsync(TargetFolder, File, async (s, e) =>
                                    {
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                        {
                                            if (Container.ProBar.Value < e.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = e.ProgressPercentage;
                                            }
                                        });
                                    }).ConfigureAwait(true);

                                    break;
                                }
                            case ".gz":
                                {
                                    await CompressionUtil.ExtractGZipAsync(TargetFolder, File, async (s, e) =>
                                    {
                                        await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                                        {
                                            if (Container.ProBar.Value < e.ProgressPercentage)
                                            {
                                                Container.ProBar.IsIndeterminate = false;
                                                Container.ProBar.Value = e.ProgressPercentage;
                                            }
                                        });
                                    }).ConfigureAwait(true);

                                    break;
                                }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_UnauthorizedDecompression_Content"),
                            PrimaryButtonText = Globalization.GetString("Common_Dialog_NowButton"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_LaterButton")
                        };

                        if (await dialog.ShowAsync().ConfigureAwait(true) == ContentDialogResult.Primary)
                        {
                            _ = await Launcher.LaunchFolderPathAsync(CurrentFolder.Path);
                        }
                    }
                    catch (NotImplementedException)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_CanNotDecompressEncrypted_Content"),
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        QueueContentDialog dialog = new QueueContentDialog
                        {
                            Title = Globalization.GetString("Common_Dialog_ErrorTitle"),
                            Content = Globalization.GetString("QueueDialog_DecompressionError_Content") + ex.Message,
                            CloseButtonText = Globalization.GetString("Common_Dialog_CloseButton")
                        };

                        _ = await dialog.ShowAsync().ConfigureAwait(true);
                    }
                    finally
                    {
                        await Container.LoadingActivation(false).ConfigureAwait(true);
                    }
                }
            }
        }

        public void Dispose()
        {
            FileCollection.Clear();

            AreaWatcher?.Dispose();
            WiFiProvider?.Dispose();
            SelectionExtention?.Dispose();
            DelayRenameCancel?.Dispose();
            DelayEnterCancel?.Dispose();
            EnterLock?.Dispose();

            AreaWatcher = null;
            WiFiProvider = null;
            SelectionExtention = null;
            DelayRenameCancel = null;
            DelayEnterCancel = null;
            EnterLock = null;

            RecordIndex = 0;
            GoAndBackRecord.Clear();

            FileCollection.CollectionChanged -= FileCollection_CollectionChanged;
            ListViewDetailHeader.Filter.RefreshListRequested -= Filter_RefreshListRequested;

            CoreWindow Window = CoreWindow.GetForCurrentThread();
            Window.KeyDown -= FilePresenter_KeyDown;
            Window.Dispatcher.AcceleratorKeyActivated -= Dispatcher_AcceleratorKeyActivated;

            Application.Current.Suspending -= Current_Suspending;
            Application.Current.Resuming -= Current_Resuming;
            SortCollectionGenerator.Current.SortWayChanged -= Current_SortWayChanged;
            ViewModeController.ViewModeChanged -= Current_ViewModeChanged;
        }
    }
}

