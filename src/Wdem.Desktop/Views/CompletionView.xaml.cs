using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wdem.Desktop.ViewModels;
using Windows.Storage.Pickers;

namespace Wdem.Desktop.Views;

public sealed partial class CompletionView : UserControl
{
  private readonly Window _owner;

  public CompletionView(Window owner)
  {
    _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    InitializeComponent();
  }

  private async void ExportJson_Click(object sender, RoutedEventArgs args) =>
      await RunExportInteractionAsync(".json", "WDEM JSON report");

  private async void ExportMarkdown_Click(object sender, RoutedEventArgs args) =>
      await RunExportInteractionAsync(".md", "WDEM Markdown report");

  private async Task RunExportInteractionAsync(string extension, string description)
  {
    try
    {
      await PickAndExportAsync(extension, description);
    }
    catch (Exception exception)
    {
      if (DataContext is CompletionViewModel viewModel)
      {
        viewModel.ReportError(exception);
      }
    }
  }

  private async Task PickAndExportAsync(string extension, string description)
  {
    if (DataContext is not CompletionViewModel viewModel)
    {
      return;
    }

    var picker = new FileSavePicker
    {
      SuggestedFileName = $"wdem-run-{viewModel.RunId}",
      DefaultFileExtension = extension
    };
    picker.FileTypeChoices.Add(description, [extension]);
    nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_owner);
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
    var file = await picker.PickSaveFileAsync();
    if (file is not null)
    {
      await viewModel.ExportAsync(file.Path);
    }
  }
}
