using System.Collections.ObjectModel;
using Wdem.Core.Reporting;
using Wdem.Core.Runs;

namespace Wdem.Desktop.ViewModels;

public sealed class RecoveryCandidateViewModel
{
  internal RecoveryCandidateViewModel(RecoveryCandidate candidate, LogRedactor redactor)
  {
    ArgumentNullException.ThrowIfNull(candidate);
    ArgumentNullException.ThrowIfNull(redactor);
    Candidate = candidate;
    string profileName = Path.GetFileNameWithoutExtension(candidate.ProfileSourcePath);
    Profile = string.IsNullOrEmpty(profileName)
        ? "Unknown profile"
        : redactor.Redact(profileName);
    PendingResources = string.Join(
        ", ",
        candidate.PendingResourceIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(redactor.Redact));
  }

  internal RecoveryCandidate Candidate { get; }

  public Guid RunId => Candidate.RunId;

  public string Profile { get; }

  public string Status => $"{Candidate.PendingResourceIds.Count} pending resources";

  public string PendingResources { get; }

  public string StartedAtDisplay =>
      Candidate.StartedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");
}

public sealed class RecoveryCandidatesViewModel : ObservableObject
{
  private readonly Func<RecoveryCandidate, Task> _recover;
  private readonly Func<RecoveryCandidate, Task> _abandon;
  private RecoveryCandidateViewModel? _selectedCandidate;
  private bool _isBusy;

  public RecoveryCandidatesViewModel(
      IEnumerable<RecoveryCandidate> candidates,
      LogRedactor redactor,
      Func<RecoveryCandidate, Task> recover,
      Func<RecoveryCandidate, Task> abandon)
  {
    ArgumentNullException.ThrowIfNull(candidates);
    ArgumentNullException.ThrowIfNull(redactor);
    ArgumentNullException.ThrowIfNull(recover);
    ArgumentNullException.ThrowIfNull(abandon);
    _recover = recover;
    _abandon = abandon;
    Candidates = new ObservableCollection<RecoveryCandidateViewModel>(
        candidates
            .OrderBy(candidate => candidate.StartedAtUtc)
            .Select(candidate => new RecoveryCandidateViewModel(candidate, redactor)));
    RecoverCommand = new AsyncRelayCommand(
        _ => RunSelectedAsync(_recover),
        _ => SelectedCandidate is not null && !_isBusy);
    AbandonCommand = new AsyncRelayCommand(
        _ => RunSelectedAsync(_abandon),
        _ => SelectedCandidate is not null && !_isBusy);
    SelectedCandidate = Candidates.FirstOrDefault();
  }

  public ObservableCollection<RecoveryCandidateViewModel> Candidates { get; }

  public RecoveryCandidateViewModel? SelectedCandidate
  {
    get => _selectedCandidate;
    set
    {
      if (SetProperty(ref _selectedCandidate, value))
      {
        RaiseCommandStates();
      }
    }
  }

  public AsyncRelayCommand RecoverCommand { get; }

  public AsyncRelayCommand AbandonCommand { get; }

  internal void Remove(RecoveryCandidate candidate)
  {
    RecoveryCandidateViewModel? item = Candidates.FirstOrDefault(
        candidateViewModel => candidateViewModel.RunId == candidate.RunId);
    if (item is null)
    {
      return;
    }

    int removedIndex = Candidates.IndexOf(item);
    Candidates.Remove(item);
    SelectedCandidate = Candidates.Count == 0
        ? null
        : Candidates[Math.Min(removedIndex, Candidates.Count - 1)];
  }

  private async Task RunSelectedAsync(Func<RecoveryCandidate, Task> action)
  {
    RecoveryCandidateViewModel? selected = SelectedCandidate;
    if (selected is null || _isBusy)
    {
      return;
    }

    _isBusy = true;
    RaiseCommandStates();
    try
    {
      await action(selected.Candidate);
    }
    finally
    {
      _isBusy = false;
      RaiseCommandStates();
    }
  }

  private void RaiseCommandStates()
  {
    RecoverCommand.RaiseCanExecuteChanged();
    AbandonCommand.RaiseCanExecuteChanged();
  }
}
