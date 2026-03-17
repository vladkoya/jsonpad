using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JsonPad.Services;
using JsonPad.Ui;
using Microsoft.Win32;
using JsonValidationResult = JsonPad.Services.JsonValidationResult;

namespace JsonPad;

public partial class MainWindow : Window
{
    private const long LargeFileThresholdBytes = 25L * 1024 * 1024;
    private const long UltraLargeFileThresholdBytes = 300L * 1024 * 1024;
    private const int UltraLargePageSizeBytes = 8 * 1024 * 1024;
    private string? _currentFilePath;
    private bool _isDirty;
    private bool _isLargeFileMode;
    private bool _isUltraLargeMode;
    private bool _suppressTextChanged;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _validationCts;
    private Task? _validationTask;
    private JsonValidationResult? _lastBackgroundValidationResult;
    private Stopwatch? _operationStopwatch;
    private long _operationTotalBytes;
    private string _operationName = "Load";
    private DateTime _lastMetricsUpdateUtc = DateTime.MinValue;
    private int _lastFindIndex;
    private string? _lastUltraLargeSearchTerm;
    private long _ultraLargeSearchNextByte;
    private UltraLargeSession? _ultraLargeSession;
    private readonly Dictionary<TabItem, DocumentSession> _documents = new();
    private DocumentSession? _activeDocument;
    private bool _isSwitchingDocument;

    private TextBox Editor =>
        _activeDocument?.Editor
        ?? throw new InvalidOperationException("No active document.");

    private sealed class DocumentSession
    {
        public required TabItem TabItem { get; init; }
        public required TextBox Editor { get; init; }
        public required TextBlock HeaderTextBlock { get; init; }
        public string? FilePath { get; set; }
        public bool IsDirty { get; set; }
        public bool IsLargeFileMode { get; set; }
        public bool IsUltraLargeMode { get; set; }
        public int LastFindIndex { get; set; }
        public string? LastUltraLargeSearchTerm { get; set; }
        public long UltraLargeSearchNextByte { get; set; }
        public UltraLargeSession? UltraLargeSession { get; set; }
        public JsonValidationResult? LastBackgroundValidationResult { get; set; }
    }

    private sealed class UltraLargeSession
    {
        public UltraLargeSession(string path, long fileLength, int pageSizeBytes)
        {
            Path = path;
            FileLength = fileLength;
            PageSizeBytes = pageSizeBytes;
        }

        public string Path { get; }
        public long FileLength { get; }
        public int PageSizeBytes { get; }
        public long CurrentStartByte { get; set; }
        public long CurrentEndByte { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        CreateAndActivateDocument();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private DocumentSession CreateAndActivateDocument(string? initialHeader = null)
    {
        var textBox = new TextBox
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 14,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        textBox.TextChanged += Editor_TextChanged;
        textBox.SelectionChanged += (_, _) => UpdateCaretStatus();

        var tabItem = new TabItem { Content = textBox };
        var headerText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        var session = new DocumentSession
        {
            TabItem = tabItem,
            Editor = textBox,
            HeaderTextBlock = headerText
        };
        tabItem.Header = CreateClosableTabHeader(session);

        _documents[tabItem] = session;
        DocumentTabs.Items.Add(tabItem);
        DocumentTabs.SelectedItem = tabItem;
        session.HeaderTextBlock.Text = string.IsNullOrWhiteSpace(initialHeader) ? "Untitled" : initialHeader;
        ActivateDocument(session);
        return session;
    }

    private StackPanel CreateClosableTabHeader(DocumentSession session)
    {
        var closeButton = new Button
        {
            Content = "x",
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            ToolTip = "Close tab",
            Tag = session
        };
        closeButton.Click += (_, e) =>
        {
            e.Handled = true;
            if (closeButton.Tag is DocumentSession doc)
            {
                CloseSession(doc);
            }
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        panel.Children.Add(session.HeaderTextBlock);
        panel.Children.Add(closeButton);
        return panel;
    }

    private void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSwitchingDocument)
        {
            return;
        }

        if (DocumentTabs.SelectedItem is TabItem tab && _documents.TryGetValue(tab, out var session))
        {
            ActivateDocument(session);
        }
    }

    private void ActivateDocument(DocumentSession session)
    {
        if (ReferenceEquals(_activeDocument, session))
        {
            return;
        }

        SaveActiveSessionState();

        _isSwitchingDocument = true;
        try
        {
            _activeDocument = session;
            _currentFilePath = session.FilePath;
            _isDirty = session.IsDirty;
            _isLargeFileMode = session.IsLargeFileMode;
            _isUltraLargeMode = session.IsUltraLargeMode;
            _lastFindIndex = session.LastFindIndex;
            _lastUltraLargeSearchTerm = session.LastUltraLargeSearchTerm;
            _ultraLargeSearchNextByte = session.UltraLargeSearchNextByte;
            _ultraLargeSession = session.UltraLargeSession;
            _lastBackgroundValidationResult = session.LastBackgroundValidationResult;

            ApplyEditorMode();
            if (_isUltraLargeMode && _ultraLargeSession is not null)
            {
                UpdateUltraLargeStatus(_ultraLargeSession);
            }
            else
            {
                UltraLargeStatusText.Text = "Ultra-large mode";
                PrevPageButton.IsEnabled = false;
                NextPageButton.IsEnabled = false;
            }
            UpdateWindowTitle();
            UpdateTabHeader(session);
            UpdateDocumentStats();
            UpdateCaretStatus();
            FilePathStatusText.Text = string.IsNullOrWhiteSpace(_currentFilePath) ? "No file loaded" : _currentFilePath;
            FindStatusText.Text = "Ready";

            ValidationStatusText.Text = _lastBackgroundValidationResult switch
            {
                null => "Validation: idle",
                { IsValid: true } => "Validation: valid",
                _ => "Validation: invalid"
            };
        }
        finally
        {
            _isSwitchingDocument = false;
        }
    }

    private void SaveActiveSessionState()
    {
        if (_activeDocument is null)
        {
            return;
        }

        _activeDocument.FilePath = _currentFilePath;
        _activeDocument.IsDirty = _isDirty;
        _activeDocument.IsLargeFileMode = _isLargeFileMode;
        _activeDocument.IsUltraLargeMode = _isUltraLargeMode;
        _activeDocument.LastFindIndex = _lastFindIndex;
        _activeDocument.LastUltraLargeSearchTerm = _lastUltraLargeSearchTerm;
        _activeDocument.UltraLargeSearchNextByte = _ultraLargeSearchNextByte;
        _activeDocument.UltraLargeSession = _ultraLargeSession;
        _activeDocument.LastBackgroundValidationResult = _lastBackgroundValidationResult;
        UpdateTabHeader(_activeDocument);
    }

    private void UpdateTabHeader(DocumentSession session)
    {
        var baseName = string.IsNullOrWhiteSpace(session.FilePath)
            ? "Untitled"
            : Path.GetFileName(session.FilePath);
        session.HeaderTextBlock.Text = $"{(session.IsDirty ? "*" : string.Empty)}{baseName}";
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var reusable = CanReuseActiveEmptyDocument();
        foreach (var fileName in dialog.FileNames)
        {
            var session = reusable
                ? _activeDocument!
                : CreateAndActivateDocument(Path.GetFileName(fileName));
            await LoadFileAsync(fileName);
            session.FilePath = _currentFilePath;
            session.IsDirty = _isDirty;
            UpdateTabHeader(session);
            reusable = false;
        }
    }

    private bool CanReuseActiveEmptyDocument()
    {
        if (_activeDocument is null)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(_activeDocument.FilePath)
               && !_activeDocument.IsDirty
               && string.IsNullOrEmpty(_activeDocument.Editor.Text);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentAsync();
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsAsync();
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        CloseActiveTab();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CloseActiveTab()
    {
        if (_activeDocument is null)
        {
            return;
        }

        _ = CloseSession(_activeDocument);
    }

    private bool CloseSession(DocumentSession session)
    {
        SaveActiveSessionState();
        if (!ConfirmCloseSession(session))
        {
            return false;
        }

        var wasActive = ReferenceEquals(_activeDocument, session);
        TabItem? nextTab = null;
        if (wasActive)
        {
            var index = DocumentTabs.Items.IndexOf(session.TabItem);
            var nextIndex = index < DocumentTabs.Items.Count - 1 ? index + 1 : index - 1;
            if (nextIndex >= 0 && nextIndex < DocumentTabs.Items.Count && DocumentTabs.Items[nextIndex] is TabItem tab)
            {
                nextTab = tab;
            }
        }

        _documents.Remove(session.TabItem);
        DocumentTabs.Items.Remove(session.TabItem);

        if (_documents.Count == 0)
        {
            _activeDocument = null;
            _currentFilePath = null;
            _isDirty = false;
            _isLargeFileMode = false;
            _isUltraLargeMode = false;
            _lastFindIndex = 0;
            _lastUltraLargeSearchTerm = null;
            _ultraLargeSearchNextByte = 0;
            _ultraLargeSession = null;
            _lastBackgroundValidationResult = null;
            CreateAndActivateDocument();
            return true;
        }

        if (wasActive)
        {
            if (nextTab is not null && _documents.TryGetValue(nextTab, out var nextSession))
            {
                DocumentTabs.SelectedItem = nextTab;
                ActivateDocument(nextSession);
            }
            else if (DocumentTabs.Items[0] is TabItem firstTab && _documents.TryGetValue(firstTab, out var firstSession))
            {
                DocumentTabs.SelectedItem = firstTab;
                ActivateDocument(firstSession);
            }
        }

        UpdateWindowTitle();
        return true;
    }

    private bool ConfirmCloseSession(DocumentSession session)
    {
        if (!session.IsDirty)
        {
            return true;
        }

        ActivateDocument(session);
        var result = MessageBox.Show(
            this,
            "You have unsaved changes in this tab. Save now?",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.Yes)
        {
            var saved = SaveCurrentSynchronously();
            return saved;
        }

        return true;
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        ShowFindPanel();
    }

    private void HideFind_Click(object sender, RoutedEventArgs e)
    {
        HideFindPanel();
    }

    private async void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await FindNextAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideFindPanel();
            e.Handled = true;
        }
    }

    private async void FindNext_Click(object sender, RoutedEventArgs e)
    {
        await FindNextAsync();
    }

    private void GoToLine_Click(object sender, RoutedEventArgs e)
    {
        var value = InputDialog.Show("Go To Line", "Line number:", "1");
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!int.TryParse(value, out var lineNumber) || lineNumber < 1)
        {
            MessageBox.Show(this, "Please enter a valid positive line number.", "Invalid line", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Editor.LineCount <= 0)
        {
            return;
        }

        if (lineNumber > Editor.LineCount)
        {
            lineNumber = Editor.LineCount;
        }

        var lineIndex = Math.Max(0, lineNumber - 1);
        var charIndex = Editor.GetCharacterIndexFromLineIndex(lineIndex);
        Editor.Select(Math.Max(0, charIndex), 0);
        Editor.ScrollToLine(lineIndex);
        Editor.Focus();
        UpdateCaretStatus();
    }

    private void ValidateJson_Click(object sender, RoutedEventArgs e)
    {
        if (_isUltraLargeMode)
        {
            if (_validationTask is { IsCompleted: false })
            {
                MessageBox.Show(
                    this,
                    "Background validation is still running for this ultra-large file.",
                    "JSON Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_lastBackgroundValidationResult is not null)
            {
                var backgroundValidationIcon = _lastBackgroundValidationResult.IsValid
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Error;
                MessageBox.Show(
                    this,
                    _lastBackgroundValidationResult.Message,
                    "JSON Validation",
                    MessageBoxButton.OK,
                    backgroundValidationIcon);
                return;
            }
        }

        var result = JsonTools.Validate(Editor.Text);
        var validationIcon = result.IsValid ? MessageBoxImage.Information : MessageBoxImage.Error;
        MessageBox.Show(this, result.Message, "JSON Validation", MessageBoxButton.OK, validationIcon);
    }

    private void FormatJson_Click(object sender, RoutedEventArgs e)
    {
        if (_isUltraLargeMode)
        {
            MessageBox.Show(
                this,
                "Format is unavailable in ultra-large mode. Open a smaller file to format it in-memory.",
                "Unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ConfirmLargeTransformation())
        {
            return;
        }

        try
        {
            ReplaceEditorText(JsonTools.Format(Editor.Text));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Format failed: {ex.Message}", "JSON Format Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MinifyJson_Click(object sender, RoutedEventArgs e)
    {
        if (_isUltraLargeMode)
        {
            MessageBox.Show(
                this,
                "Minify is unavailable in ultra-large mode. Open a smaller file to minify it in-memory.",
                "Unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ConfirmLargeTransformation())
        {
            return;
        }

        try
        {
            ReplaceEditorText(JsonTools.Minify(Editor.Text));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Minify failed: {ex.Message}", "JSON Minify Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveActiveSessionState();

        foreach (var session in _documents.Values)
        {
            if (!session.IsDirty)
            {
                continue;
            }

            ActivateDocument(session);
            var result = MessageBox.Show(
                this,
                "You have unsaved changes. Save now?",
                "Unsaved changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                var saveSucceeded = SaveCurrentSynchronously();
                if (!saveSucceeded)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

        _operationCts?.Cancel();
        _validationCts?.Cancel();
        base.OnClosing(e);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        _validationCts?.Cancel();
    }

    private void Editor_TextChanged(object sender, EventArgs e)
    {
        if (_suppressTextChanged)
        {
            return;
        }

        _isDirty = true;
        UpdateWindowTitle();
        UpdateDocumentStats();
        SaveActiveSessionState();
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            BeginOperation();
            var fileInfo = new FileInfo(path);
            _isLargeFileMode = fileInfo.Length >= LargeFileThresholdBytes;
            _isUltraLargeMode = fileInfo.Length >= UltraLargeFileThresholdBytes;
            _ultraLargeSession = _isUltraLargeMode
                ? new UltraLargeSession(path, fileInfo.Length, UltraLargePageSizeBytes)
                : null;
            _currentFilePath = path;
            FilePathStatusText.Text = path;
            UpdateWindowTitle();
            ApplyEditorMode();

            var cts = CreateOperationCancellationSource();
            StartBackgroundValidation(path);
            var progress = CreateOperationProgress(fileInfo.Length, "Load");
            _suppressTextChanged = true;
            Editor.Clear();

            if (_isUltraLargeMode && _ultraLargeSession is not null)
            {
                await LoadUltraLargePageAsync(_ultraLargeSession, 0, progress, cts.Token);
            }
            else if (_isLargeFileMode)
            {
                var pending = new StringBuilder();
                const int uiChunkChars = 1024 * 1024;

                await LargeFileService.StreamTextAsync(
                    path,
                    async chunk =>
                    {
                        pending.Append(chunk);
                        if (pending.Length >= uiChunkChars)
                        {
                            var uiChunk = pending.ToString();
                            pending.Clear();
                            await Dispatcher.InvokeAsync(
                                () => Editor.AppendText(uiChunk),
                                DispatcherPriority.Background,
                                cts.Token);
                        }
                    },
                    progress,
                    cts.Token);

                if (pending.Length > 0)
                {
                    var uiChunk = pending.ToString();
                    await Dispatcher.InvokeAsync(
                        () => Editor.AppendText(uiChunk),
                        DispatcherPriority.Background,
                        cts.Token);
                }
            }
            else
            {
                var text = await LargeFileService.ReadTextAsync(path, progress, cts.Token);
                Editor.Text = text;
            }

            _suppressTextChanged = false;

            _isDirty = false;
            _lastFindIndex = 0;
            _lastUltraLargeSearchTerm = null;
            _ultraLargeSearchNextByte = 0;
            UpdateWindowTitle();
            UpdateDocumentStats();
            UpdateCaretStatus();
            SaveActiveSessionState();
            if (_isUltraLargeMode)
            {
                LoadMetricsStatusText.Text = "Load: first page ready (paged mode)";
            }

            if (_isUltraLargeMode)
            {
                MessageBox.Show(
                    this,
                    "Loaded in ultra-large mode. You can page through the file in read-only mode while background JSON validation continues.",
                    "Ultra-large mode",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else if (_isLargeFileMode)
            {
                MessageBox.Show(
                    this,
                    "Loaded in large-file mode. Word wrap is disabled for better responsiveness.",
                    "Large file mode",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "Operation cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Open failed: {ex.Message}", "Open Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _suppressTextChanged = false;
            EndOperation();
        }
    }

    private async Task SaveCurrentAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            await SaveAsAsync();
            return;
        }

        await SaveToPathAsync(_currentFilePath);
    }

    private async Task SaveAsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(_currentFilePath) ? "document.json" : Path.GetFileName(_currentFilePath)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await SaveToPathAsync(dialog.FileName);
    }

    private async Task SaveToPathAsync(string path)
    {
        try
        {
            if (_isUltraLargeMode)
            {
                MessageBox.Show(
                    this,
                    "Save is unavailable in ultra-large mode because the editor is paged read-only.",
                    "Unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            BeginOperation();
            var cts = CreateOperationCancellationSource();
            var progress = CreateOperationProgress(Math.Max(1, Editor.Text.Length), "Save");
            await LargeFileService.WriteTextAsync(path, Editor.Text, progress, cts.Token);

            _currentFilePath = path;
            _isDirty = false;
            UpdateWindowTitle();
            FilePathStatusText.Text = path;
            SaveActiveSessionState();
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "Save cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Save failed: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private bool SaveCurrentSynchronously()
    {
        if (string.IsNullOrWhiteSpace(_currentFilePath))
        {
            return SaveAsSynchronously();
        }

        return SaveToPathSynchronously(_currentFilePath);
    }

    private bool SaveAsSynchronously()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(_currentFilePath) ? "document.json" : Path.GetFileName(_currentFilePath)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        return SaveToPathSynchronously(dialog.FileName);
    }

    private bool SaveToPathSynchronously(string path)
    {
        try
        {
            if (_isUltraLargeMode)
            {
                MessageBox.Show(
                    this,
                    "Save is unavailable in ultra-large mode because the editor is paged read-only.",
                    "Unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return false;
            }

            LargeFileService.WriteText(path, Editor.Text, progress: null, CancellationToken.None);
            _currentFilePath = path;
            _isDirty = false;
            UpdateWindowTitle();
            FilePathStatusText.Text = path;
            SaveActiveSessionState();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Save failed: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        await NavigateUltraLargeAsync(-1);
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        await NavigateUltraLargeAsync(1);
    }

    private async Task NavigateUltraLargeAsync(int direction)
    {
        if (!_isUltraLargeMode || _ultraLargeSession is null)
        {
            return;
        }

        var target = _ultraLargeSession.CurrentStartByte + (long)direction * _ultraLargeSession.PageSizeBytes;
        target = Math.Clamp(target, 0, Math.Max(0, _ultraLargeSession.FileLength - 1));

        try
        {
            BeginOperation();
            var cts = CreateOperationCancellationSource();
            var progress = CreateOperationProgress(_ultraLargeSession.FileLength, "Page");
            await LoadUltraLargePageAsync(_ultraLargeSession, target, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "Paging cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Page load failed: {ex.Message}", "Page Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task LoadUltraLargePageAsync(
        UltraLargeSession session,
        long startByte,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var startRatio = session.FileLength <= 0 ? 0 : (double)startByte / session.FileLength;
        progress.Report(startRatio);
        var page = await LargeFileService.ReadPageAsync(
            session.Path,
            startByte,
            session.PageSizeBytes,
            cancellationToken);
        var endRatio = page.FileLength <= 0 ? 1 : (double)page.EndByte / page.FileLength;
        progress.Report(endRatio);

        _suppressTextChanged = true;
        Editor.Text = page.Text;
        _suppressTextChanged = false;

        session.CurrentStartByte = page.StartByte;
        session.CurrentEndByte = page.EndByte;
        UpdateUltraLargeStatus(session);
        UpdateDocumentStats();
        UpdateCaretStatus();
        SaveActiveSessionState();
    }

    private void UpdateUltraLargeStatus(UltraLargeSession session)
    {
        var startDisplay = session.CurrentStartByte + 1;
        var endDisplay = Math.Max(session.CurrentStartByte, session.CurrentEndByte);
        UltraLargeStatusText.Text =
            $"Viewing bytes {startDisplay:N0}-{endDisplay:N0} of {session.FileLength:N0} (page size: {session.PageSizeBytes / (1024 * 1024)} MB)";
        PrevPageButton.IsEnabled = session.CurrentStartByte > 0;
        NextPageButton.IsEnabled = session.CurrentEndByte < session.FileLength;
    }

    private void StartBackgroundValidation(string path)
    {
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        var validationToken = _validationCts.Token;

        _lastBackgroundValidationResult = null;
        if (_activeDocument is not null)
        {
            _activeDocument.LastBackgroundValidationResult = null;
        }
        ValidationStatusText.Text = "Validation: running...";

        _validationTask = Task.Run(async () =>
        {
            try
            {
                var result = await LargeFileService.ValidateJsonStreamAsync(path, progress: null, validationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var session in _documents.Values)
                    {
                        if (string.Equals(session.FilePath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            session.LastBackgroundValidationResult = result;
                        }
                    }

                    if (string.Equals(_currentFilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastBackgroundValidationResult = result;
                        ValidationStatusText.Text = result.IsValid ? "Validation: valid" : "Validation: invalid";
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ValidationStatusText.Text = $"Validation: error ({ex.Message})";
                });
            }
        }, validationToken);
    }

    private IProgress<double> CreateOperationProgress(long totalBytes, string operationName)
    {
        _operationTotalBytes = Math.Max(1, totalBytes);
        _operationName = operationName;
        _operationStopwatch = Stopwatch.StartNew();
        _lastMetricsUpdateUtc = DateTime.MinValue;

        return new Progress<double>(value =>
        {
            OperationProgressBar.Value = value;
            var now = DateTime.UtcNow;
            if (value < 1 && (now - _lastMetricsUpdateUtc).TotalMilliseconds < 200)
            {
                return;
            }

            _lastMetricsUpdateUtc = now;
            if (_operationStopwatch is null)
            {
                LoadMetricsStatusText.Text = $"{_operationName}: {value:P0}";
                return;
            }

            var elapsedSeconds = Math.Max(0.001, _operationStopwatch.Elapsed.TotalSeconds);
            var bytesLoaded = _operationTotalBytes * value;
            var bytesPerSecond = bytesLoaded / elapsedSeconds;
            var remainingBytes = Math.Max(0, _operationTotalBytes - bytesLoaded);
            var eta = bytesPerSecond > 1
                ? TimeSpan.FromSeconds(remainingBytes / bytesPerSecond)
                : TimeSpan.Zero;

            if (value >= 1)
            {
                LoadMetricsStatusText.Text = $"{_operationName}: complete in {FormatDuration(_operationStopwatch.Elapsed)}";
                return;
            }

            LoadMetricsStatusText.Text =
                $"{_operationName}: {value:P0} | {FormatBytes(bytesPerSecond)}/s | ETA {FormatDuration(eta)}";
        });
    }

    private static string FormatBytes(double bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;

        if (bytes >= gb)
        {
            return $"{bytes / gb:0.00} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0.00} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:0.00} KB";
        }

        return $"{bytes:0} B";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return duration.ToString(@"hh\:mm\:ss");
        }

        return duration.ToString(@"mm\:ss");
    }

    private void ShowFindPanel()
    {
        FindPanel.Visibility = Visibility.Visible;
        FindStatusText.Text = _isUltraLargeMode
            ? "Ultra-large search scans the whole file."
            : "Ready";
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void HideFindPanel()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        FindStatusText.Text = "Ready";
        Editor.Focus();
    }

    private async Task FindNextAsync()
    {
        var term = FindTextBox.Text;
        if (string.IsNullOrEmpty(term))
        {
            return;
        }

        if (_isUltraLargeMode)
        {
            await FindNextUltraLargeAsync(term);
            return;
        }

        var start = Math.Max(Editor.SelectionStart + Editor.SelectionLength, _lastFindIndex);
        var index = Editor.Text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0 && start > 0)
        {
            index = Editor.Text.IndexOf(term, 0, StringComparison.OrdinalIgnoreCase);
        }

        if (index < 0)
        {
            FindStatusText.Text = "Not found in current document.";
            MessageBox.Show(this, "Search text not found.", "Find", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Editor.Select(index, term.Length);
        var lineIndex = Editor.GetLineIndexFromCharacterIndex(index);
        if (lineIndex >= 0)
        {
            Editor.ScrollToLine(lineIndex);
        }

        Editor.Focus();
        _lastFindIndex = index + term.Length;
        FindStatusText.Text = $"Found at character {index + 1:N0}.";
        UpdateCaretStatus();
        SaveActiveSessionState();
    }

    private async Task FindNextUltraLargeAsync(string term)
    {
        if (_ultraLargeSession is null || string.IsNullOrWhiteSpace(_currentFilePath))
        {
            return;
        }

        if (!string.Equals(_lastUltraLargeSearchTerm, term, StringComparison.Ordinal))
        {
            _lastUltraLargeSearchTerm = term;
            _ultraLargeSearchNextByte = GetCurrentUltraLargeCursorByte();
        }

        try
        {
            BeginOperation();
            var cts = CreateOperationCancellationSource();
            var progress = CreateOperationProgress(_ultraLargeSession.FileLength, "Search");
            FindStatusText.Text = $"Searching from byte {_ultraLargeSearchNextByte + 1:N0}...";

            var match = await LargeFileService.FindTextInRangeAsync(
                _ultraLargeSession.Path,
                term,
                _ultraLargeSearchNextByte,
                _ultraLargeSession.FileLength,
                ignoreCase: true,
                progress,
                cts.Token);
            var wrapped = false;
            var searchRangeStart = _ultraLargeSearchNextByte;
            var searchRangeEnd = _ultraLargeSession.FileLength;

            if (match < 0 && _ultraLargeSearchNextByte > 0)
            {
                wrapped = true;
                match = await LargeFileService.FindTextInRangeAsync(
                    _ultraLargeSession.Path,
                    term,
                    0,
                    _ultraLargeSearchNextByte,
                    ignoreCase: true,
                    progress,
                    cts.Token);
                searchRangeStart = 0;
                searchRangeEnd = _ultraLargeSearchNextByte;
            }

            if (match < 0)
            {
                FindStatusText.Text =
                    $"Searched bytes {searchRangeStart + 1:N0}-{searchRangeEnd:N0}. Not found.";
                MessageBox.Show(this, "Search text not found.", "Find", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await MoveUltraLargeViewToMatchAsync(match, term, cts.Token);
            _ultraLargeSearchNextByte = Math.Min(
                _ultraLargeSession.FileLength,
                match + Encoding.UTF8.GetByteCount(term));
            FindStatusText.Text = wrapped
                ? $"Found at byte {match + 1:N0} (wrapped search)."
                : $"Found at byte {match + 1:N0}.";
            SaveActiveSessionState();

            if (wrapped)
            {
                LoadMetricsStatusText.Text = "Search: wrapped to beginning";
            }
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "Search cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Search failed: {ex.Message}", "Search Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task MoveUltraLargeViewToMatchAsync(long matchByteOffset, string term, CancellationToken cancellationToken)
    {
        if (_ultraLargeSession is null)
        {
            return;
        }

        var centeredPageStart = Math.Max(0, matchByteOffset - _ultraLargeSession.PageSizeBytes / 2);
        await LoadUltraLargePageAsync(
            _ultraLargeSession,
            centeredPageStart,
            progress: new Progress<double>(_ => { }),
            cancellationToken);

        var offsetWithinPage = Math.Max(0, matchByteOffset - _ultraLargeSession.CurrentStartByte);
        var approximateCharIndex = GetCharIndexByUtf8ByteOffset(Editor.Text, offsetWithinPage);
        var searchStart = Math.Max(0, approximateCharIndex - Math.Max(4, term.Length * 2));
        var index = Editor.Text.IndexOf(term, searchStart, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = Editor.Text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        }

        if (index < 0)
        {
            return;
        }

        Editor.Select(index, term.Length);
        var lineIndex = Editor.GetLineIndexFromCharacterIndex(index);
        if (lineIndex >= 0)
        {
            Editor.ScrollToLine(lineIndex);
        }

        Editor.Focus();
        UpdateCaretStatus();
    }

    private long GetCurrentUltraLargeCursorByte()
    {
        if (_ultraLargeSession is null)
        {
            return 0;
        }

        var charOffset = Math.Clamp(Editor.SelectionStart + Editor.SelectionLength, 0, Editor.Text.Length);
        var bytesFromStart = Encoding.UTF8.GetByteCount(Editor.Text.AsSpan(0, charOffset));
        return Math.Clamp(_ultraLargeSession.CurrentStartByte + bytesFromStart, 0, _ultraLargeSession.FileLength);
    }

    private static int GetCharIndexByUtf8ByteOffset(string text, long utf8ByteOffset)
    {
        if (utf8ByteOffset <= 0 || text.Length == 0)
        {
            return 0;
        }

        long consumedBytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            consumedBytes += Encoding.UTF8.GetByteCount(text.AsSpan(i, 1));
            if (consumedBytes >= utf8ByteOffset)
            {
                return i + 1;
            }
        }

        return text.Length;
    }

    private void ReplaceEditorText(string newText)
    {
        _suppressTextChanged = true;
        Editor.Text = newText;
        _suppressTextChanged = false;
        _isDirty = true;
        UpdateWindowTitle();
        UpdateDocumentStats();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            Open_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            Save_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            SaveAs_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            ShowFindPanel();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.G)
        {
            GoToLine_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            CloseActiveTab();
            e.Handled = true;
        }
    }

    private bool ConfirmLargeTransformation()
    {
        if (!_isLargeFileMode)
        {
            return true;
        }

        var response = MessageBox.Show(
            this,
            "This is a large document. Formatting/minifying can consume a lot of memory and time. Continue?",
            "Large file operation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return response == MessageBoxResult.Yes;
    }

    private async Task<bool> ConfirmDiscardUnsavedChangesAsync()
    {
        if (!_isDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "You have unsaved changes. Save now?",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.Yes)
        {
            await SaveCurrentAsync();
            return !_isDirty;
        }

        return true;
    }

    private void ApplyEditorMode()
    {
        if (_isUltraLargeMode)
        {
            ConfigureEditorForUltraLargeMode();
            UltraLargePanel.Visibility = Visibility.Visible;
            return;
        }

        UltraLargePanel.Visibility = Visibility.Collapsed;
        if (_isLargeFileMode)
        {
            ConfigureEditorForLargeMode();
        }
        else
        {
            ConfigureEditorForStandardMode();
        }
    }

    private void ConfigureEditorForStandardMode()
    {
        Editor.IsReadOnly = false;
        Editor.IsUndoEnabled = true;
        Editor.TextWrapping = TextWrapping.Wrap;
        ModeTextBlock.Text = "Mode: Standard";
    }

    private void ConfigureEditorForLargeMode()
    {
        Editor.IsReadOnly = false;
        Editor.IsUndoEnabled = false;
        Editor.TextWrapping = TextWrapping.NoWrap;
        ModeTextBlock.Text = "Mode: Large file";
    }

    private void ConfigureEditorForUltraLargeMode()
    {
        Editor.IsReadOnly = true;
        Editor.IsUndoEnabled = false;
        Editor.TextWrapping = TextWrapping.NoWrap;
        ModeTextBlock.Text = "Mode: Ultra-large (paged read-only)";
    }

    private CancellationTokenSource CreateOperationCancellationSource()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        return _operationCts;
    }

    private void BeginOperation()
    {
        OperationProgressBar.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        OperationProgressBar.Value = 0;
    }

    private void EndOperation()
    {
        OperationProgressBar.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        OperationProgressBar.Value = 0;
        _operationStopwatch = null;
        _operationTotalBytes = 0;

        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void UpdateDocumentStats()
    {
        LengthStatusText.Text = $"Length: {Editor.Text.Length:N0}";
    }

    private void UpdateCaretStatus()
    {
        var offset = Editor.SelectionStart;
        var lineIndex = Editor.GetLineIndexFromCharacterIndex(offset);
        if (lineIndex < 0)
        {
            LineStatusText.Text = "Line: 1, Col: 1";
            return;
        }

        var firstCharOfLine = Editor.GetCharacterIndexFromLineIndex(lineIndex);
        var column = Math.Max(1, offset - firstCharOfLine + 1);
        LineStatusText.Text = $"Line: {lineIndex + 1:N0}, Col: {column:N0}";
    }

    private void UpdateWindowTitle()
    {
        var fileName = string.IsNullOrWhiteSpace(_currentFilePath) ? "Untitled" : Path.GetFileName(_currentFilePath);
        var dirtyPrefix = _isDirty ? "*" : string.Empty;
        Title = $"{dirtyPrefix}{fileName} - JsonPad ({DocumentTabs.Items.Count} tabs)";
        if (_activeDocument is not null)
        {
            UpdateTabHeader(_activeDocument);
        }
    }
}
