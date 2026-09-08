namespace MatPaper.Data;

public class TaskRun : BaseEntity
{
    public TaskRunKind Kind { get; set; }
    public long TaskId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public TaskRunState State { get; set; }
    public int ItemsProcessed { get; set; }
    public string? Log { get; set; }
}
