using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using UniPad.App.Localization;
using UniPad.Core.Profiles;

namespace UniPad.App.Views;

/// <summary>
/// Small modal window that asks for a new profile name. Closes with the trimmed name, or with null
/// when cancelled.
/// </summary>
public partial class ProfileNameDialog : Window
{
    private readonly Func<string, bool> _exists;
    private readonly TextBox _nameBox;
    private readonly TextBlock _errorText;
    private readonly Button _okButton;

    /// <summary>Parameterless constructor for the XAML loader and the designer.</summary>
    public ProfileNameDialog() : this(_ => false)
    {
    }

    /// <summary>Creates the dialog; <paramref name="exists"/> reports names already taken.</summary>
    public ProfileNameDialog(Func<string, bool> exists)
    {
        AvaloniaXamlLoader.Load(this);
        _exists = exists;

        _nameBox = this.FindControl<TextBox>("NameBox") ?? throw new InvalidOperationException("NameBox missing");
        _errorText = this.FindControl<TextBlock>("ErrorText") ?? throw new InvalidOperationException("ErrorText missing");
        _okButton = this.FindControl<Button>("OkButton") ?? throw new InvalidOperationException("OkButton missing");
        var prompt = this.FindControl<TextBlock>("PromptText");
        var hint = this.FindControl<TextBlock>("HintText");
        var cancel = this.FindControl<Button>("CancelButton");

        Title = Strings.Get("profile.newTitle");
        FlowDirection = Strings.Instance.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        if (prompt is not null)
        {
            prompt.Text = Strings.Get("profile.prompt");
        }

        if (hint is not null)
        {
            // Isolated so the run of punctuation keeps its order inside a right-to-left sentence.
            hint.Text = $"{Strings.Get("profile.hint")} {Strings.Isolate(PlayerProfileStore.ForbiddenCharacters)}";
        }

        _okButton.Content = Strings.Get("action.ok");
        _okButton.Click += OnOkClick;

        if (cancel is not null)
        {
            cancel.Content = Strings.Get("action.cancel");
            cancel.Click += (_, _) => Close(null);
        }

        _nameBox.TextChanged += (_, _) => Validate();
        Opened += (_, _) => _nameBox.Focus();
    }

    /// <summary>
    /// Shows the dialog over the active UniPad window. Returns the chosen name, or null when the
    /// user cancelled or no window is available to own the dialog.
    /// </summary>
    public static async Task<string?> PromptAsync(Func<string, bool> exists)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        var owner = desktop.Windows.FirstOrDefault(w => w.IsActive)
                    ?? desktop.MainWindow
                    ?? desktop.Windows.FirstOrDefault(w => w.IsVisible);

        if (owner is null)
        {
            return null;
        }

        return await new ProfileNameDialog(exists).ShowDialog<string?>(owner);
    }

    /// <summary>Validates the current text, updates the error line and the OK button.</summary>
    /// <returns>The trimmed name when it is acceptable, otherwise null.</returns>
    private string? Validate()
    {
        var name = (_nameBox.Text ?? string.Empty).Trim();

        string? message = PlayerProfileStore.ValidateName(name) switch
        {
            ProfileNameError.None => null,
            ProfileNameError.Empty => string.Empty,
            ProfileNameError.InvalidCharacters => Strings.Get("profile.errChars"),
            ProfileNameError.ReservedName => Strings.Get("profile.errReserved"),
            ProfileNameError.TooLong => Strings.Get("profile.errTooLong"),
            _ => Strings.Get("profile.errChars"),
        };

        if (message is null && _exists(name))
        {
            message = Strings.Get("profile.errExists");
        }

        // An empty box only disables OK; it is not an error worth pointing at.
        _errorText.Text = message ?? string.Empty;
        _errorText.IsVisible = !string.IsNullOrEmpty(message);
        _okButton.IsEnabled = message is null;

        return message is null ? name : null;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var name = Validate();
        if (name is not null)
        {
            Close(name);
        }
    }
}
