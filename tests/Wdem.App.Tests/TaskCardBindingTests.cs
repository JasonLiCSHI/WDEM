using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Runtime.ExceptionServices;
using Wdem.Bootstrapper;
using Wdem.Domain.Tasks;
using Xunit;

namespace Wdem.App.Tests;

public sealed class TaskCardBindingTests
{
  [Fact]
  public void TaskRow_WhenCreated_ThenListsPreAndPostActivitiesByDisplayName()
  {
    var row = new TaskRow(CreateTask());

    var pre = Assert.Single(row.PreActivities);
    Assert.Equal("Prepare the test environment", pre.DisplayName);
    var post = Assert.Single(row.PostActivities);
    Assert.Equal("Verify the test environment", post.DisplayName);
  }

  [Fact]
  public void MainWindow_WhenRendered_ThenBindsTheBrandedTaskCardWithoutErrors()
  {
    ExceptionDispatchInfo? renderingError = null;
    var thread = new Thread(() =>
    {
      var sessionRoot = Path.Combine(
          Path.GetTempPath(),
          "wdem-app-tests",
          Guid.NewGuid().ToString("N"));
      System.Windows.Application? application = null;
      MainWindow? window = null;
      try
      {
        using var session = WdemBootstrapper.StartSession(new WdemBootstrapperOptions("app-test")
        {
          SettingsPath = Path.Combine(sessionRoot, "settings.json"),
          CacheDirectory = Path.Combine(sessionRoot, "cache"),
          LogDirectory = Path.Combine(sessionRoot, "logs"),
          ApplicationDirectory = sessionRoot
        });
        application = new System.Windows.Application();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
          Source = new Uri(
              "pack://application:,,,/Wdem.App;component/Resources/Strings.en-US.xaml",
              UriKind.Absolute)
        });

        window = new MainWindow(session);
        Assert.NotNull(window.Icon);
        window.Measure(new Size(1200, 800));
        window.Arrange(new Rect(0, 0, 1200, 800));
        var taskList = FindRequiredTaskList(window);
        var taskCard = Assert.IsAssignableFrom<FrameworkElement>(taskList.ItemTemplate.LoadContent());
        taskCard.DataContext = new TaskRow(CreateTask());
        taskCard.Measure(new Size(1200, 800));
        taskCard.Arrange(new Rect(0, 0, 1200, 800));
        taskCard.UpdateLayout();

      }
      catch (Exception exception)
      {
        renderingError = ExceptionDispatchInfo.Capture(exception);
      }
      finally
      {
        window?.Close();
        application?.Shutdown();
        if (Directory.Exists(sessionRoot))
        {
          Directory.Delete(sessionRoot, recursive: true);
        }
      }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    var completed = thread.Join(TimeSpan.FromSeconds(15));

    Assert.True(completed, "The WPF rendering test did not finish within 15 seconds.");
    renderingError?.Throw();
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
          id: "test-task",
          displayName: "Test Task",
          required: true,
          dependsOn: ["dependency"],
          versionRequirement: Wdem.Domain.Versions.VersionRequirement.Parse(">=1.0.0"),
          preferredVersion: "1.0.0",
          source: "https://example.test/tool.exe",
          detect: new CommandDefinition("tool.exe", ["--version"]),
          pre:
          [
            new CommandDefinition(
                "prepare.exe",
                ["--quiet"],
                DisplayName: "Prepare the test environment")
          ],
          apply: new CommandDefinition("installer.exe", ["--quiet"]),
          post:
          [
            new CommandDefinition(
                "verify.exe",
                ["--installed"],
                DisplayName: "Verify the test environment")
          ],
          description: "Exercises the task card bindings.");
}
