using System.ComponentModel.DataAnnotations;

using FieldOps.Domain.Enums;
using FieldOps.Features.Work;
using FieldOps.Web.Formatting;

namespace FieldOps.Web.Models;

public sealed class WorkEventForm
{
    public uint Version { get; set; }

    [EnumDataType(typeof(WorkEventType))]
    [Display(Name = "作業内容")]
    public WorkEventType EventType { get; set; }

    [Required(ErrorMessage = "作業内容を入力してください。")]
    [StringLength(2000, ErrorMessage = "作業内容は2000文字以内で入力してください。")]
    [Display(Name = "記録内容")]
    public string Summary { get; set; } = string.Empty;

    [Required(ErrorMessage = "記録日を選んでください。")]
    [Display(Name = "記録日")]
    public DateOnly? OccurredDate { get; set; }

    [Required(ErrorMessage = "記録時刻を選んでください。")]
    [Display(Name = "記録時刻")]
    public TimeOnly? OccurredTime { get; set; }

    public WorkEventInput ToCommand() => new()
    {
        Version = Version,
        EventType = EventType,
        Summary = Summary,
        OccurredAtUtc = OccurredDate.HasValue && OccurredTime.HasValue
            ? JapanTimeFormatter.ToUtc(OccurredDate.Value, OccurredTime.Value)
            : null
    };
}
