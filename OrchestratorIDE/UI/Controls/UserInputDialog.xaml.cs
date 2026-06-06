using System.Windows;
using System.Windows.Input;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// Modal input dialog surfaced by the ask_user tool.
///
/// The agent calls ask_user("question") → this window pops up →
/// user types their answer and clicks "Send to Agent" →
/// the answer string is returned to the agent and execution continues.
///
/// Usage:
///   var dlg = new UserInputDialog("System-wide or workspace-only?") { Owner = this };
///   if (dlg.ShowDialog() == true) use dlg.Answer;
/// </summary>
public partial class UserInputDialog : Window
{
    /// <summary>The text the user typed. Empty string if cancelled.</summary>
    public string Answer { get; private set; } = "";

    public UserInputDialog(string question)
    {
        InitializeComponent();
        TbQuestion.Text = question;

        // Extract hint: if question contains parenthetical like "(type X or Y)"
        // show it below the TextBox in muted style
        var hint = ExtractHint(question);
        if (hint != null)
        {
            TbHint.Text       = hint;
            TbHint.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => TbAnswer.Focus();
        UpdateOkButton();
    }

    // ── Handlers ──────────────────────────────────────────────────────────

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Answer = TbAnswer.Text.Trim();
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Answer       = "";
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { BtnCancel_Click(sender, e); e.Handled = true; }
    }

    private void TbAnswer_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateOkButton();

    private void UpdateOkButton()
        => BtnOk.IsEnabled = !string.IsNullOrWhiteSpace(TbAnswer.Text);

    // ── Helper ────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to pull a parenthetical hint from the question, e.g.:
    ///   "Install scope? (type 'system' or 'workspace')"
    ///   → hint: "type 'system' or 'workspace'"
    /// </summary>
    private static string? ExtractHint(string question)
    {
        var start = question.LastIndexOf('(');
        var end   = question.LastIndexOf(')');
        if (start >= 0 && end > start)
            return question[(start + 1)..end];
        return null;
    }
}
