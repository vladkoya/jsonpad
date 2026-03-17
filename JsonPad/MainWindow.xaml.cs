using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Highlighting;
using JsonPad.Services;
using JsonPad.Ui;
using Microsoft.Win32;

namespace JsonPad;

public partial class MainWindow : Window
{
    private const long LargeFileThresholdBytes = 25L * 1024 * 1024;
    private string? _currentFilePath;
    private bool _isDirty;
    private bool _isLargeFileMode;
    private bool _suppressTextChanged;
    private CancellationTokenSource? _operationCts;
    private int _lastFindIndex;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureEditorForStandardMode();
        UpdateWindowTitle();
        UpdateDocumentStats();
        UpdateCaretStatus();

        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretStatus();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardUnsavedChangesAsync())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await LoadFileAsync(dialog.FileName);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentAsync();
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        ShowFindPanel();
    }

    private void HideFind_Click(object sender, RoutedEventArgs e)
    {
        HideFindPanel();
    }

    private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideFindPanel();
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        FindNext();
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

        if (lineNumber > Editor.Document.LineCount)
        {
            lineNumber = Editor.Document.LineCount;
        }

        var line = Editor.Document.GetLineByNumber(lineNumber);
        Editor.CaretOffset = line.Offset;
        Editor.ScrollToLine(lineNumber);
        Editor.Focus();
        UpdateCaretStatus();
    }

    private void ValidateJson_Click(object sender, RoutedEventArgs e)
    {
        var result = JsonTools.Validate(Editor.Text);
        var icon = result.IsValid ? MessageBoxImage.Information : MessageBoxImage.Error;
        MessageBox.Show(this, result.Message, "JSON Validation", MessageBoxButton.OK, icon);
    }

    private void FormatJson_Click(object sender, RoutedEventArgs e)
    {
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
        if (_isDirty)
        {
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
        base.OnClosing(e);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
    }

    private void Editor_TextChanged(object sender, EventArgs e)
    {
        if (!_suppressTextChanged)
        {
            _isDirty = true;
            UpdateWindowTitle();
        }

        UpdateDocumentStats();
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            BeginOperation();
            var fileInfo = new FileInfo(path);
            _isLargeFileMode = fileInfo.Length >= LargeFileThresholdBytes;
            ApplyEditorMode();

            var progress = new Progress<double>(value => OperationProgressBar.Value = value);
            var cts = CreateOperationCancellationSource();
            var text = await LargeFileService.ReadTextAsync(path, progress, cts.Token);

            _suppressTextChanged = true;
            Editor.Text = text;
            _suppressTextChanged = false;

            _currentFilePath = path;
            _isDirty = false;
            _lastFindIndex = 0;
            UpdateWindowTitle();
            UpdateDocumentStats();
            UpdateCaretStatus();
            FilePathStatusText.Text = path;

            if (_isLargeFileMode)
            {
                MessageBox.Show(
                    this,
                    "Loaded in large-file mode. Syntax highlighting is disabled for better responsiveness.",
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
            BeginOperation();
            var progress = new Progress<double>(value => OperationProgressBar.Value = value);
            var cts = CreateOperationCancellationSource();
            await LargeFileService.WriteTextAsync(path, Editor.Text, progress, cts.Token);

            _currentFilePath = path;
            _isDirty = false;
            UpdateWindowTitle();
            FilePathStatusText.Text = path;
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
            LargeFileService.WriteText(path, Editor.Text, progress: null, CancellationToken.None);
            _currentFilePath = path;
            _isDirty = false;
            UpdateWindowTitle();
            FilePathStatusText.Text = path;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Save failed: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ShowFindPanel()
    {
        FindPanel.Visibility = Visibility.Visible;
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void HideFindPanel()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private void FindNext()
    {
        var term = FindTextBox.Text;
        if (string.IsNullOrEmpty(term))
        {
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
            MessageBox.Show(this, "Search text not found.", "Find", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Editor.Select(index, term.Length);
        Editor.ScrollToLine(Editor.Document.GetLineByOffset(index).LineNumber);
        Editor.Focus();
        _lastFindIndex = index + term.Length;
        UpdateCaretStatus();
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
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
        Editor.WordWrap = true;
        ModeTextBlock.Text = "Mode: Standard";
    }

    private void ConfigureEditorForLargeMode()
    {
        Editor.SyntaxHighlighting = null;
        Editor.WordWrap = false;
        ModeTextBlock.Text = "Mode: Large file";
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

        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void UpdateDocumentStats()
    {
        LengthStatusText.Text = $"Length: {Editor.Text.Length:N0}";
    }

    private void UpdateCaretStatus()
    {
        var offset = Editor.CaretOffset;
        var line = Editor.Document.GetLineByOffset(Math.Max(0, offset));
        var column = offset - line.Offset + 1;
        LineStatusText.Text = $"Line: {line.LineNumber:N0}, Col: {column:N0}";
    }

    private void UpdateWindowTitle()
    {
        var fileName = string.IsNullOrWhiteSpace(_currentFilePath) ? "Untitled" : Path.GetFileName(_currentFilePath);
        var dirtyPrefix = _isDirty ? "*" : string.Empty;
        Title = $"{dirtyPrefix}{fileName} - JsonPad";
    }
}
