using System.Windows;
using System.Windows.Controls;

namespace JsonPad.Ui;

public static class InputDialog
{
    public static string? Show(string title, string prompt, string initialValue = "")
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        var root = new Grid
        {
            Margin = new Thickness(12)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptBlock = new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(promptBlock, 0);

        var inputBox = new TextBox
        {
            Text = initialValue,
            MinWidth = 360
        };
        Grid.SetRow(inputBox, 1);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var okButton = new Button
        {
            Content = "OK",
            Width = 85,
            IsDefault = true
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 85,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true
        };

        okButton.Click += (_, _) => window.DialogResult = true;
        cancelButton.Click += (_, _) => window.DialogResult = false;

        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);
        Grid.SetRow(buttonRow, 2);

        root.Children.Add(promptBlock);
        root.Children.Add(inputBox);
        root.Children.Add(buttonRow);

        window.Content = root;
        window.Loaded += (_, _) =>
        {
            inputBox.Focus();
            inputBox.SelectAll();
        };

        return window.ShowDialog() == true ? inputBox.Text : null;
    }
}
