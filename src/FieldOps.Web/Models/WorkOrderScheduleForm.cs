using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;
using FieldOps.Features.Work;
using FieldOps.Web.Formatting;

namespace FieldOps.Web.Models;

public sealed class WorkOrderScheduleForm
{
    public Guid Id { get; set; }
    public uint Version { get; set; }
    public WorkOrderStatus Status { get; set; }

    [Required(ErrorMessage = "担当者を選んでください。")]
    [Display(Name = "担当者")]
    public string AssignedUserId { get; set; } = string.Empty;

    [Required(ErrorMessage = "作業日を選んでください。")]
    [Display(Name = "作業日")]
    public DateOnly? ScheduledDate { get; set; }

    [Required(ErrorMessage = "開始時刻を選んでください。")]
    [Display(Name = "開始時刻")]
    public TimeOnly? ScheduledTime { get; set; }

    public static WorkOrderScheduleForm FromCommand(WorkOrderEditInput input) => new()
    {
        Id = input.Id,
        Version = input.Version,
        Status = input.Status,
        AssignedUserId = input.AssignedUserId,
        ScheduledDate = input.ScheduledStartUtc.HasValue
            ? JapanTimeFormatter.ToJapanDate(input.ScheduledStartUtc.Value)
            : null,
        ScheduledTime = input.ScheduledStartUtc.HasValue
            ? JapanTimeFormatter.ToJapanTime(input.ScheduledStartUtc.Value)
            : null
    };

    public WorkOrderEditInput ToCommand() => new()
    {
        Id = Id,
        Version = Version,
        Status = Status,
        AssignedUserId = AssignedUserId,
        ScheduledStartUtc = ScheduledDate.HasValue && ScheduledTime.HasValue
            ? JapanTimeFormatter.ToUtc(ScheduledDate.Value, ScheduledTime.Value)
            : null
    };
}
