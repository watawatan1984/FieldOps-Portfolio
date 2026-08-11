using FieldOps.Domain.Common;
using FieldOps.Domain.Entities;
using FieldOps.Domain.Enums;

using System.Reflection;

namespace FieldOps.Domain.Tests.Work;

public sealed class WorkOrderTests
{
    [Fact]
    public void CreationInterface_ExposesOnlyWonOpportunityFactory()
    {
        MethodInfo[] publicFactories = typeof(WorkOrder).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(WorkOrder))
            .ToArray();

        MethodInfo factory = Assert.Single(publicFactories);
        Assert.Equal("CreateFromWon", factory.Name);
        Assert.Equal(typeof(SalesOpportunity), factory.GetParameters()[0].ParameterType);
    }

    public static TheoryData<WorkOrderStatus, WorkOrderStatus> AllowedTransitions =>
        new()
        {
            { WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress },
            { WorkOrderStatus.InProgress, WorkOrderStatus.Completed },
            { WorkOrderStatus.Planned, WorkOrderStatus.Cancelled },
            { WorkOrderStatus.Scheduled, WorkOrderStatus.Cancelled },
            { WorkOrderStatus.InProgress, WorkOrderStatus.Cancelled }
        };

    public static TheoryData<WorkOrderStatus, WorkOrderStatus> RejectedTransitions =>
        new()
        {
            { WorkOrderStatus.Planned, WorkOrderStatus.InProgress },
            { WorkOrderStatus.Scheduled, WorkOrderStatus.Completed },
            { WorkOrderStatus.Completed, WorkOrderStatus.Cancelled },
            { WorkOrderStatus.Cancelled, WorkOrderStatus.Scheduled }
        };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void MoveTo_AllowsDocumentedTransitions(WorkOrderStatus current, WorkOrderStatus next)
    {
        WorkOrder workOrder = CreateAt(current, includeCompletionEvent: next == WorkOrderStatus.Completed);

        workOrder.MoveTo(next, Utc(12));

        Assert.Equal(next, workOrder.Status);
    }

    [Theory]
    [MemberData(nameof(RejectedTransitions))]
    public void MoveTo_RejectsUndocumentedOrTerminalTransitions(WorkOrderStatus current, WorkOrderStatus next)
    {
        WorkOrder workOrder = CreateAt(current, includeCompletionEvent: true);

        DomainException exception = Assert.Throws<DomainException>(() => workOrder.MoveTo(next, Utc(12)));

        Assert.Contains(nameof(WorkOrder), exception.Message);
        Assert.Contains(current.ToString(), exception.Message);
        Assert.Contains(next.ToString(), exception.Message);
    }

    [Fact]
    public void MoveTo_CompletedRequiresACompletionEvent()
    {
        WorkOrder workOrder = CreateAt(WorkOrderStatus.InProgress);

        DomainException exception = Assert.Throws<DomainException>(() => workOrder.MoveTo(WorkOrderStatus.Completed, Utc(12)));

        Assert.Contains(nameof(WorkOrder), exception.Message);
        Assert.Contains(WorkOrderStatus.InProgress.ToString(), exception.Message);
        Assert.Contains(WorkOrderStatus.Completed.ToString(), exception.Message);

        workOrder.AddEvent(WorkEventType.Completion, Utc(11), "Work completed", "operator-42");
        workOrder.MoveTo(WorkOrderStatus.Completed, Utc(12));

        Assert.Equal(WorkOrderStatus.Completed, workOrder.Status);
    }

    [Fact]
    public void Schedule_SetsUtcStartAndMovesPlannedWorkOrderToScheduled()
    {
        WorkOrder workOrder = CreateAt(WorkOrderStatus.Planned);
        DateTime scheduledStartUtc = Utc(20);

        workOrder.Schedule(scheduledStartUtc, Utc(12));

        Assert.Equal(WorkOrderStatus.Scheduled, workOrder.Status);
        Assert.Equal(scheduledStartUtc, workOrder.ScheduledStartUtc);
    }

    [Fact]
    public void MoveTo_ScheduledRequiresTheSchedulingOperation()
    {
        WorkOrder workOrder = CreateAt(WorkOrderStatus.Planned);

        DomainException exception = Assert.Throws<DomainException>(() => workOrder.MoveTo(WorkOrderStatus.Scheduled, Utc(12)));

        Assert.Contains("scheduled start", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schedule_RequiresUtcScheduledStart()
    {
        WorkOrder workOrder = CreateAt(WorkOrderStatus.Planned);

        Assert.Throws<DomainException>(() => workOrder.Schedule(new DateTime(2026, 8, 20, 9, 0, 0), Utc(12)));
    }

    [Fact]
    public void AddEvent_AppendsAnImmutableHistoricalEvent()
    {
        WorkOrder workOrder = CreateAt(WorkOrderStatus.Planned);

        workOrder.AddEvent(WorkEventType.Note, Utc(11), "Arrival confirmed", "operator-42");
        WorkEvent workEvent = Assert.Single(workOrder.Events);

        Assert.Equal(workOrder.BranchId, workEvent.BranchId);
        Assert.Equal("Arrival confirmed", workEvent.Summary);
        Assert.Equal("operator-42", workEvent.ActorUserId);
        Assert.Equal(Utc(11), workEvent.OccurredAtUtc);
    }

    [Fact]
    public void AddEvent_RequiresUtcTimestamp()
    {
        WorkOrder workOrder = CreateAt(WorkOrderStatus.Planned);

        Assert.Throws<DomainException>(() => workOrder.AddEvent(WorkEventType.Note, new DateTime(2026, 8, 11), "Arrival confirmed", "operator-42"));
    }

    [Fact]
    public void AddEvent_AcceptsCompletionEvidenceOnlyWhileWorkIsInProgress()
    {
        WorkOrder planned = CreateAt(WorkOrderStatus.Planned);
        WorkOrder scheduled = CreateAt(WorkOrderStatus.Scheduled);
        WorkOrder inProgress = CreateAt(WorkOrderStatus.InProgress);

        Assert.Throws<DomainException>(() =>
            planned.AddEvent(WorkEventType.Completion, Utc(11), "Completed too early", "operator-42"));
        Assert.Throws<DomainException>(() =>
            scheduled.AddEvent(WorkEventType.Completion, Utc(11), "Completed too early", "operator-42"));

        inProgress.AddEvent(WorkEventType.Completion, Utc(11), "Completion evidence", "operator-42");

        Assert.Single(inProgress.Events);
    }

    [Fact]
    public void AddEvent_EnforcesTerminalHistoryRules()
    {
        WorkOrder completed = CreateAt(WorkOrderStatus.Completed, includeCompletionEvent: true);
        WorkOrder cancelled = CreateAt(WorkOrderStatus.Cancelled);
        WorkOrder planned = CreateAt(WorkOrderStatus.Planned);

        Assert.Throws<DomainException>(() =>
            completed.AddEvent(WorkEventType.Note, Utc(12), "Rewrite attempt", "operator-42"));
        Assert.Throws<DomainException>(() =>
            cancelled.AddEvent(WorkEventType.Note, Utc(12), "Late note", "operator-42"));
        Assert.Throws<DomainException>(() =>
            planned.AddEvent(WorkEventType.Correction, Utc(12), "Premature correction", "operator-42"));

        completed.AddEvent(WorkEventType.Correction, Utc(12), "Append-only correction", "administrator-42");

        Assert.Equal(2, completed.Events.Count);
    }

    [Fact]
    public void CreateFromWon_RequiresTheOpportunityBranchPartyAndSite()
    {
        Branch branch = Branch.Create("Harbor Office");
        Party assignedParty = Party.CreateOrganization("Northwind Service Works");
        assignedParty.AssignToBranch(branch);
        assignedParty.AddSite(branch, "Pier 8 Workshop");
        Site site = assignedParty.Sites.Single();
        SalesOpportunity opportunity = CreateWonOpportunity(branch, assignedParty, site);

        Party otherParty = Party.CreateOrganization("Contoso Facilities");
        otherParty.AssignToBranch(branch);
        otherParty.AddSite(branch, "Pier 8 Workshop");
        Branch otherBranch = Branch.Create("Remote Office");
        assignedParty.AssignToBranch(otherBranch);
        assignedParty.AddSite(otherBranch, "Remote Workshop");

        Assert.Throws<DomainException>(() => WorkOrder.CreateFromWon(opportunity, otherBranch, assignedParty, site));
        Assert.Throws<DomainException>(() => WorkOrder.CreateFromWon(opportunity, branch, otherParty, site));
        Assert.Throws<DomainException>(() => WorkOrder.CreateFromWon(
            opportunity,
            branch,
            assignedParty,
            assignedParty.Sites.Single(item => item.BranchId == otherBranch.Id)));
    }

    [Fact]
    public void AddEvent_RejectsUndefinedEventType()
    {
        WorkOrder workOrder = CreateAt(WorkOrderStatus.Planned);

        Assert.Throws<DomainException>(() => workOrder.AddEvent((WorkEventType)999, Utc(11), "Arrival confirmed", "operator-42"));
    }

    [Fact]
    public void AssignToUser_RequiresAStableIdentityId()
    {
        WorkOrder workOrder = CreateAt(WorkOrderStatus.Planned);

        workOrder.AssignToUser("technician-user-id");

        Assert.Equal("technician-user-id", workOrder.AssignedUserId);
        Assert.Throws<DomainException>(() => workOrder.AssignToUser(" "));
    }

    private static WorkOrder CreateAt(WorkOrderStatus status, bool includeCompletionEvent = false)
    {
        Branch branch = Branch.Create("Harbor Office");
        Party party = Party.CreateOrganization("Northwind Service Works");
        party.AssignToBranch(branch);
        party.AddSite(branch, "Pier 8 Workshop");
        Site site = party.Sites.Single();
        WorkOrder workOrder = WorkOrder.CreateFromWon(CreateWonOpportunity(branch, party, site), branch, party, site);

        foreach (WorkOrderStatus next in PathTo(status))
        {
            if (next == WorkOrderStatus.Scheduled)
            {
                workOrder.Schedule(Utc(20), Utc(10));
            }
            else
            {
                if (next == WorkOrderStatus.Completed && includeCompletionEvent)
                {
                    workOrder.AddEvent(WorkEventType.Completion, Utc(11), "Work completed", "operator-42");
                }
                workOrder.MoveTo(next, Utc(10));
            }
        }

        if (includeCompletionEvent && status == WorkOrderStatus.InProgress)
        {
            workOrder.AddEvent(WorkEventType.Completion, Utc(11), "Work completed", "operator-42");
        }

        return workOrder;
    }

    private static SalesOpportunity CreateWonOpportunity(Branch branch, Party party, Site site)
    {
        SalesOpportunity opportunity = SalesOpportunity.Create(branch, party, site);
        opportunity.SetProposal(1000m, new DateTime(2026, 9, 1));
        foreach (SalesOpportunityStatus status in new[]
        {
            SalesOpportunityStatus.Contacted,
            SalesOpportunityStatus.SurveyScheduled,
            SalesOpportunityStatus.Quoting,
            SalesOpportunityStatus.Proposed,
            SalesOpportunityStatus.Won
        })
        {
            opportunity.MoveTo(status, Utc(10));
        }
        return opportunity;
    }

    private static IEnumerable<WorkOrderStatus> PathTo(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Planned => [],
        WorkOrderStatus.Scheduled => [WorkOrderStatus.Scheduled],
        WorkOrderStatus.InProgress => [WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress],
        WorkOrderStatus.Completed => [WorkOrderStatus.Scheduled, WorkOrderStatus.InProgress, WorkOrderStatus.Completed],
        WorkOrderStatus.Cancelled => [WorkOrderStatus.Cancelled],
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static DateTime Utc(int day) => new(2026, 8, day, 9, 0, 0, DateTimeKind.Utc);
}
