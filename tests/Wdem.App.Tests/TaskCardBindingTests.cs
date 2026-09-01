using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Wdem.Core.Tasks;
using Xunit;

namespace Wdem.App.Tests;

public sealed class TaskCardBindingTests
{
  [Fact]
  public void TaskCard_RendersReadOnlyDetailsWithoutBindingErrors()
  {
    Exception? renderingError = null;
    var thread = new Thread(() =>
    {
      try
      {
        var application = new Application();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
          Source = new Uri(
              "pack://application:,,,/Wdem.App;component/Resources/Strings.en-US.xaml",
              UriKind.Absolute)
        });

        var window = new MainWindow();
        window.Measure(new Size(1200, 800));
        window.Arrange(new Rect(0, 0, 1200, 800));
        var taskList = FindRequiredTaskList(window);
        var taskCard = Assert.IsAssignableFrom<FrameworkElement>(taskList.ItemTemplate.LoadContent());
        taskCard.DataContext = new TaskRow(CreateTask());
        taskCard.Measure(new Size(1200, 800));
        taskCard.Arrange(new Rect(0, 0, 1200, 800));
        taskCard.UpdateLayout();

        window.Close();
        application.Shutdown();
      }
      catch (Exception exception)
      {
        renderingError = exception;
      }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    Assert.Null(renderingError);
  }

  private static ItemsControl FindRequiredTaskList(MainWindow window) =>
      LogicalDescendants(window)
          .OfType<ItemsControl>()
          .Single(control =>
              BindingOperations.GetBinding(control, ItemsControl.ItemsSourceProperty)?.Path.Path ==
              nameof(MainWindow.RequiredTasks));

  private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
  {
    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
    {
      yield return child;
      foreach (var descendant in LogicalDescendants(child))
      {
        yield return descendant;
      }
    }
  }

  private static TaskDefinition CreateTask() =>
      new(
          Id: "test-task",
          DisplayName: "Test Task",
          Required: true,
          DependsOn: ["dependency"],
          VersionConstraint: ">=1.0.0",
          PreferredVersion: "1.0.0",
          Source: "https://example.test/tool.exe",
          Detect: new CommandDefinition("tool.exe", ["--version"]),
          Pre: [],
          Apply: new CommandDefinition("installer.exe", ["--quiet"]),
          Post: [],
          Description: "Exercises the task card bindings.");
}
