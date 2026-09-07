using Wdem.Application.Runtime;
using Wdem.Application.Workflows;
using Wdem.Domain.Tasks;
using Wdem.Domain.Versions;
using Wdem.Domain.Workflows;
using Xunit;

namespace Wdem.Application.Tests;

public sealed class DefaultWorkflowActivityExecutorTests
{
  [Fact]
  public async Task CommandActivity_WhenExecuted_ThenRunsThroughRuntimeAndEvaluatesCompliance()
  {
    var command = new CommandDefinition(
        "tool.exe",
        ["--version"],
        "(?<version>\\d+(?:\\.\\d+)+)");
    var task = CreateTask(command);
    var runtime = new StubRuntime(new CommandResult(0, "tool 2.5.0", string.Empty));
    var output = new List<CommandOutput>();
    var context = new WorkflowActivityContext(
        task,
        "detecting",
        WorkflowActivityLocation.Residence,
        runtime,
        output.Add);

    var result = await DefaultWorkflowActivityExecutor.Instance.ExecuteAsync(
        new CommandWorkflowActivity("detect", "detect", command),
        context,
        CancellationToken.None);

    Assert.True(result.Succeeded);
    Assert.True(result.IsTaskSatisfied);
    Assert.Equal("detect", result.Step?.Phase);
    Assert.Equal("detecting", result.Step?.RuntimeStateId);
    Assert.Equal(WorkflowActivityLocation.Residence, result.Step?.ActivityLocation);
    Assert.Equal("tool", runtime.LastInvocation?.TaskId);
  }

  [Fact]
  public async Task UnknownActivity_WhenExecuted_ThenFailsWithoutExternalWork()
  {
    var runtime = new StubRuntime(new CommandResult(0, string.Empty, string.Empty));
    var context = new WorkflowActivityContext(
        CreateTask(new CommandDefinition("tool.exe", [])),
        "custom",
        WorkflowActivityLocation.Entry,
        runtime,
        _ => { });

    var result = await DefaultWorkflowActivityExecutor.Instance.ExecuteAsync(
        new UnknownActivity("extension"),
        context,
        CancellationToken.None);

    Assert.False(result.Succeeded);
    Assert.Contains("No Activity executor", result.Error);
    Assert.Null(runtime.LastInvocation);
  }

  private static TaskDefinition CreateTask(CommandDefinition detect) =>
      new(
          "tool",
          "Tool",
          true,
          [],
          VersionRequirement.Parse(">= 2.0"),
          "2.5.0",
          "https://example.test/tool.exe",
          detect,
          [],
          null,
          []);

  private sealed class UnknownActivity(string id) : WorkflowActivity(id);

  private sealed class StubRuntime(CommandResult result) : ITaskRuntime
  {
    public CommandInvocation? LastInvocation { get; private set; }

    public Task<CommandResult> RunAsync(
        CommandInvocation invocation,
        IProgress<CommandOutput>? output,
        CancellationToken cancellationToken)
    {
      LastInvocation = invocation;
      return Task.FromResult(result);
    }
  }
}
