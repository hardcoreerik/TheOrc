using System.Windows;

namespace BenchmarkRunner;

public partial class PromptViewer : Window
{
    public PromptViewer(string benchmarkName, string prompt)
    {
        InitializeComponent();
        TbTitle.Text  = benchmarkName;
        TbPrompt.Text = prompt;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(TbPrompt.Text);
        Title = "Benchmark Prompt — Copied!";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
