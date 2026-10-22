using System.Text.Json.Serialization;

/// <summary>
/// Приоритет задачи:
/// 0 = Low,
/// 1 = Medium,
/// 2 = High
/// </summary>
public enum Priority { Low, Medium, High }

public class TaskItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; } // заполняется только в /complete
    public Priority Priority { get; set; }

    // xmin из Postgres, используем как RowVersion для оптимистичной конкурентности
    [JsonIgnore]
    public uint Version { get; set; }
}

// Priority nullable, чтобы отличить "не передан" (-> Medium) от явного значения
public record CreateTaskRequest(string? Title, Priority? Priority);
