using Ateliers.Ai.Orchestrator.Core;
using Xunit;

namespace Ateliers.Ai.Orchestrator.UnitTests;

public sealed class WorkflowPhasePolicyTests
{
    [Fact]
    public void Next_returns_the_five_phases_in_the_approved_order()
    {
        Assert.Equal(WorkflowPhase.Phase2ImplementationReview,
            WorkflowPhasePolicy.Next(WorkflowPhase.Phase1Implementation));
        Assert.Equal(WorkflowPhase.Phase3TestChanges,
            WorkflowPhasePolicy.Next(WorkflowPhase.Phase2ImplementationReview));
        Assert.Equal(WorkflowPhase.Phase4FullReview,
            WorkflowPhasePolicy.Next(WorkflowPhase.Phase3TestChanges));
        Assert.Equal(WorkflowPhase.Phase5PullRequestReview,
            WorkflowPhasePolicy.Next(WorkflowPhase.Phase4FullReview));
        Assert.Null(WorkflowPhasePolicy.Next(WorkflowPhase.Phase5PullRequestReview));
    }

    [Fact]
    public void Only_review_phases_require_independent_review_and_have_revision_limits()
    {
        Assert.False(WorkflowPhasePolicy.RequiresIndependentReview(WorkflowPhase.Phase1Implementation));
        Assert.True(WorkflowPhasePolicy.RequiresIndependentReview(WorkflowPhase.Phase2ImplementationReview));
        Assert.False(WorkflowPhasePolicy.RequiresIndependentReview(WorkflowPhase.Phase3TestChanges));
        Assert.True(WorkflowPhasePolicy.RequiresIndependentReview(WorkflowPhase.Phase4FullReview));
        Assert.True(WorkflowPhasePolicy.RequiresIndependentReview(WorkflowPhase.Phase5PullRequestReview));
    }

    [Fact]
    public void Revision_attempts_are_limited_per_review_phase_and_initial_review_is_not_counted()
    {
        var counters = new WorkflowRetryCounters();

        for (var attempt = 0; attempt < WorkflowPhasePolicy.MaxRevisionAttempts; attempt++)
            counters = counters.RecordRevision(WorkflowPhase.Phase2ImplementationReview);

        Assert.Equal(3, counters.RevisionAttempts[WorkflowPhase.Phase2ImplementationReview]);
        Assert.Equal(0, counters.RevisionAttempts.GetValueOrDefault(WorkflowPhase.Phase4FullReview));
        var exception = Assert.Throws<WorkflowRetryLimitExceededException>(() =>
            counters.RecordRevision(WorkflowPhase.Phase2ImplementationReview));
        Assert.Equal("revision", exception.Counter);
        Assert.Equal(WorkflowPhase.Phase2ImplementationReview, exception.Phase);
        Assert.Equal(WorkflowPhasePolicy.MaxRevisionAttempts, exception.Maximum);
        Assert.Equal(1, counters.RecordRevision(WorkflowPhase.Phase3TestChanges)
            .RevisionAttempts[WorkflowPhase.Phase3TestChanges]);
    }

    [Fact]
    public void Transient_gate_retry_is_limited_per_phase_and_source_changes_consume_revision_budget()
    {
        var counters = new WorkflowRetryCounters()
            .RecordTransientGateRetry(WorkflowPhase.Phase4FullReview, sourceChanged: true);

        Assert.Equal(1, counters.TransientGateRetries[WorkflowPhase.Phase4FullReview]);
        Assert.Equal(1, counters.RevisionAttempts[WorkflowPhase.Phase4FullReview]);
        Assert.Equal(0, counters.RevisionAttempts.GetValueOrDefault(WorkflowPhase.Phase5PullRequestReview));

        var exception = Assert.Throws<WorkflowRetryLimitExceededException>(() =>
            counters.RecordTransientGateRetry(WorkflowPhase.Phase4FullReview, sourceChanged: false));
        Assert.Equal("transient_gate_retry", exception.Counter);

        var phaseThree = new WorkflowRetryCounters()
            .RecordTransientGateRetry(WorkflowPhase.Phase3TestChanges, sourceChanged: true);
        Assert.Equal(1, phaseThree.TransientGateRetries[WorkflowPhase.Phase3TestChanges]);
        Assert.Equal(1, phaseThree.RevisionAttempts[WorkflowPhase.Phase3TestChanges]);
    }

    [Fact]
    public void Counter_snapshots_copy_inputs_and_do_not_expose_mutable_dictionaries()
    {
        var source = new Dictionary<WorkflowPhase, int>
        {
            [WorkflowPhase.Phase2ImplementationReview] = 1
        };
        var counters = new WorkflowRetryCounters { RevisionAttempts = source };
        source[WorkflowPhase.Phase2ImplementationReview] = 99;

        Assert.Equal(1, counters.RevisionAttempts[WorkflowPhase.Phase2ImplementationReview]);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<WorkflowPhase, int>)counters.RevisionAttempts)[WorkflowPhase.Phase2ImplementationReview] = 99);

        var updated = counters.RecordRevision(WorkflowPhase.Phase2ImplementationReview);
        Assert.Equal(1, counters.RevisionAttempts[WorkflowPhase.Phase2ImplementationReview]);
        Assert.Equal(2, updated.RevisionAttempts[WorkflowPhase.Phase2ImplementationReview]);
    }

    [Fact]
    public void Run_rollbacks_are_limited_to_six_and_each_update_returns_a_new_snapshot()
    {
        var counters = new WorkflowRetryCounters();
        for (var rollback = 0; rollback < WorkflowPhasePolicy.MaxRunRollbacks; rollback++)
            counters = counters.RecordRollback();

        Assert.Equal(6, counters.RunRollbacks);
        Assert.Equal(0, new WorkflowRetryCounters().RunRollbacks);
        Assert.Throws<WorkflowRetryLimitExceededException>(() => counters.RecordRollback());
        counters.Validate();
    }

    [Fact]
    public void Validate_rejects_persisted_counters_outside_the_approved_policy()
    {
        Assert.Throws<InvalidDataException>(() => new WorkflowRetryCounters { RunRollbacks = 7 }.Validate());
        Assert.Throws<InvalidDataException>(() => new WorkflowRetryCounters
        {
            RevisionAttempts = new Dictionary<WorkflowPhase, int>
            {
                [WorkflowPhase.Phase2ImplementationReview] = 4
            }
        }.Validate());
        Assert.Throws<InvalidDataException>(() => new WorkflowRetryCounters
        {
            RevisionAttempts = new Dictionary<WorkflowPhase, int>
            {
                [WorkflowPhase.Phase3TestChanges] = -1
            }
        }.Validate());
    }
}
