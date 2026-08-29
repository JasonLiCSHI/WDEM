using Wdem.Core.Processes;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.VisualStudio;

public sealed class VsWhereVisualStudioDiscoveryTests
{
  [Fact]
  public async Task DiscoverAsync_UsesVsWherePathAndMapsInstallationJson()
  {
    const string vsWhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
    var process = new RecordingProcessExecutor();
    process.Enqueue(new ProcessExecutionResult(
        true,
        0,
        [
          """
          [
            {
              "instanceId": "17.0_abc",
              "installationPath": "C:\\VS\\Community",
              "productId": "Microsoft.VisualStudio.Product.Community",
              "productPath": "C:\\VS\\Community\\Common7\\IDE\\devenv.exe",
              "installationVersion": "18.3.2.0",
              "channelId": "VisualStudio.18.Release",
              "isComplete": true,
              "isLaunchable": true,
              "catalog": { "productDisplayVersion": "18.3.2" }
            }
          ]
          """
        ],
        []));
    var discovery = new VsWhereVisualStudioDiscovery(process, vsWhere);

    var instances = await discovery.DiscoverAsync([], [], CancellationToken.None);

    var instance = Assert.Single(instances);
    Assert.Equal("17.0_abc", instance.InstanceId);
    Assert.Equal(@"C:\VS\Community", instance.InstallationPath);
    Assert.Equal("Microsoft.VisualStudio.Product.Community", instance.ProductId);
    Assert.Equal(@"C:\VS\Community\Common7\IDE\devenv.exe", instance.ProductPath);
    Assert.Equal("18.3.2", instance.ProductDisplayVersion);
    Assert.Equal("18.3.2.0", instance.InstallationVersion);
    Assert.Equal("VisualStudio.18.Release", instance.ChannelId);
    Assert.Equal("Community", instance.Edition);
    Assert.True(instance.IsComplete);
    Assert.True(instance.IsLaunchable);
    var request = Assert.Single(process.Requests);
    Assert.Equal(vsWhere, request.FileName);
    Assert.Equal(
        ["-products", "*", "-format", "json", "-utf8", "-prerelease"],
        request.Arguments);
  }

  [Fact]
  public async Task DiscoverAsync_QueriesRequestedMembershipAndMatchesByInstanceId()
  {
    const string vsWhere = @"C:\vswhere.exe";
    const string workload = "Microsoft.VisualStudio.Workload.ManagedDesktop";
    const string component = "Microsoft.NetCore.Component.Runtime.10.0";
    var process = new RecordingProcessExecutor();
    process.Enqueue(JsonResult(
        """
        [
          {
            "instanceId": "a",
            "installationPath": "C:\\VS-A",
            "productId": "Microsoft.VisualStudio.Product.Community",
            "productPath": "C:\\VS-A\\devenv.exe",
            "installationVersion": "18.3.2.0",
            "channelId": "VisualStudio.18.Release",
            "isComplete": true,
            "isLaunchable": true,
            "catalog": { "productDisplayVersion": "18.3.2" }
          },
          {
            "instanceId": "b",
            "installationPath": "C:\\VS-B",
            "productId": "Microsoft.VisualStudio.Product.Professional",
            "productPath": "C:\\VS-B\\devenv.exe",
            "installationVersion": "18.3.2.0",
            "channelId": "VisualStudio.18.Release",
            "isComplete": true,
            "isLaunchable": true,
            "catalog": { "productDisplayVersion": "18.3.2" }
          }
        ]
        """));
    process.Enqueue(JsonResult("""[{ "instanceId": "b" }]"""));
    process.Enqueue(JsonResult("""[{ "instanceId": "a" }]"""));
    var discovery = new VsWhereVisualStudioDiscovery(process, vsWhere);

    var instances = await discovery.DiscoverAsync([workload], [component], CancellationToken.None);

    var first = Assert.Single(instances, instance => instance.InstanceId == "a");
    var second = Assert.Single(instances, instance => instance.InstanceId == "b");
    Assert.Empty(first.Workloads);
    Assert.Contains(component, first.Components);
    Assert.Contains(workload, second.Workloads);
    Assert.Empty(second.Components);
    Assert.Equal(3, process.Requests.Count);
    Assert.Equal(
        ["-products", "*", "-requires", workload, "-format", "json", "-utf8"],
        process.Requests[1].Arguments);
    Assert.Equal(
        ["-products", "*", "-requires", component, "-format", "json", "-utf8"],
        process.Requests[2].Arguments);
  }

  [Fact]
  public async Task DiscoverAsync_MembershipQueryFailure_DoesNotReportFalseAbsence()
  {
    var process = new RecordingProcessExecutor();
    process.Enqueue(JsonResult(
        """
        [{
          "instanceId": "a",
          "installationPath": "C:\\VS-A",
          "productId": "Microsoft.VisualStudio.Product.Community",
          "productPath": "C:\\VS-A\\devenv.exe",
          "installationVersion": "18.3.2.0",
          "channelId": "VisualStudio.18.Release",
          "isComplete": true,
          "isLaunchable": true,
          "catalog": { "productDisplayVersion": "18.3.2" }
        }]
        """));
    process.Enqueue(new ProcessExecutionResult(true, 1, [], ["query failed"]));
    var discovery = new VsWhereVisualStudioDiscovery(process, @"C:\vswhere.exe");

    var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        discovery.DiscoverAsync(["workload-a"], [], CancellationToken.None));

    Assert.Contains("workload-a", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DiscoverAsync_NullJsonRoot_ThrowsInvalidDataException()
  {
    var process = new RecordingProcessExecutor();
    process.Enqueue(JsonResult("null"));
    var discovery = new VsWhereVisualStudioDiscovery(process, @"C:\vswhere.exe");

    var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
        discovery.DiscoverAsync([], [], CancellationToken.None));

    Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task DiscoverAsync_EmptyJsonArray_ReturnsNoInstances()
  {
    var process = new RecordingProcessExecutor();
    process.Enqueue(JsonResult("[]"));
    var discovery = new VsWhereVisualStudioDiscovery(process, @"C:\vswhere.exe");

    var instances = await discovery.DiscoverAsync([], [], CancellationToken.None);

    Assert.Empty(instances);
  }

  [Fact]
  public async Task DiscoverAsync_PartialBaseRecord_ThrowsInvalidDataException()
  {
    var process = new RecordingProcessExecutor();
    process.Enqueue(JsonResult("""[{ "instanceId": "a" }]"""));
    var discovery = new VsWhereVisualStudioDiscovery(process, @"C:\vswhere.exe");

    var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
        discovery.DiscoverAsync([], [], CancellationToken.None));

    Assert.Contains("required property", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DiscoverAsync_MembershipRecordWithoutInstanceId_ThrowsInvalidDataException()
  {
    var process = new RecordingProcessExecutor();
    process.Enqueue(JsonResult(
        """
        [{
          "instanceId": "a",
          "installationPath": "C:\\VS-A",
          "productId": "Microsoft.VisualStudio.Product.Community",
          "productPath": "C:\\VS-A\\devenv.exe",
          "installationVersion": "18.3.2.0",
          "channelId": "VisualStudio.18.Release",
          "isComplete": true,
          "isLaunchable": true,
          "catalog": { "productDisplayVersion": "18.3.2" }
        }]
        """));
    process.Enqueue(JsonResult("""[{ "installationPath": "C:\\VS-A" }]"""));
    var discovery = new VsWhereVisualStudioDiscovery(process, @"C:\vswhere.exe");

    var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
        discovery.DiscoverAsync(["workload-a"], [], CancellationToken.None));

    Assert.Contains("instanceId", exception.Message, StringComparison.Ordinal);
  }

  private static ProcessExecutionResult JsonResult(string json) => new(
      true,
      0,
      [json],
      []);

  private sealed class RecordingProcessExecutor : IProcessExecutor
  {
    private readonly Queue<ProcessExecutionResult> _results = new();

    public List<ProcessExecutionRequest> Requests { get; } = [];

    public void Enqueue(ProcessExecutionResult result) => _results.Enqueue(result);

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(request);
      return Task.FromResult(_results.Dequeue());
    }
  }
}
