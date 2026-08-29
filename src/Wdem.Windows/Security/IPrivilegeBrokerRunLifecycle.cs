namespace Wdem.Windows.Security;

public interface IPrivilegeBrokerRunLifecycle
{
  Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken);
}
