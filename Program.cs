using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

#region Setup
var builder = WebApplication.CreateBuilder(args);

// enum в запросах и ответах как строки ("High" вместо 2)
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    // расшифровка значений enum Priority (0 = Low, 1 = Medium, 2 = High) в описании схемы
    c.SchemaFilter<PrioritySchemaFilter>());

builder.Services.AddDbContext<TaskDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// один экземпляр работает и как publisher, и как hosted service (для старта и остановки)
builder.Services.AddSingleton<RabbitMqService>();
builder.Services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<RabbitMqService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RabbitMqService>());

var app = builder.Build();

// Swagger включён всегда (и в Production), чтобы он работал в Docker
app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// миграции применяются при старте, для тестового достаточно
// HACK: не успел, доделал бы так: при нескольких инстансах гонял бы миграции отдельным шагом (init-контейнер / CI)
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<TaskDbContext>().Database.Migrate();
#endregion

#region Endpoints
app.MapPost("/tasks", async (CreateTaskRequest req, TaskDbContext db) =>
{
    var title = req.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "Title is required" });
    if (title.Length > 200)
        return Results.BadRequest(new { error = "Title must be at most 200 characters" });

    var priority = req.Priority ?? Priority.Medium;
    if (!Enum.IsDefined(priority))
        return Results.BadRequest(new { error = "Invalid priority" });

    var task = new TaskItem
    {
        Id = Guid.NewGuid(),
        Title = title,
        Priority = priority,
        CreatedAt = DateTimeOffset.UtcNow
    };

    db.Tasks.Add(task);
    await db.SaveChangesAsync();

    return Results.Created($"/tasks/{task.Id}", task);
});

app.MapGet("/tasks", async (TaskDbContext db) =>
    await db.Tasks.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync());

app.MapPut("/tasks/{id:guid}/complete", async (Guid id, TaskDbContext db, IEventPublisher events) =>
{
    var task = await db.Tasks.FindAsync(id);
    if (task is null)
        return Results.NotFound();

    if (task.IsCompleted)
        return Results.Conflict(new { error = "Task is already completed" });

    task.IsCompleted = true;
    task.CompletedAt = DateTimeOffset.UtcNow;

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
        // кто-то завершил задачу между чтением и сохранением (xmin изменился)
        return Results.Conflict(new { error = "Task was modified by another request" });
    }

    // публикуем только после успешного сохранения, чтобы не слать событие о том, чего нет в БД
    events.PublishTaskCompleted(new TaskCompletedMessage(
        task.Id, task.Title, task.CompletedAt.Value.UtcDateTime, task.Priority));

    return Results.Ok(task);
});

app.MapDelete("/tasks/{id:guid}", async (Guid id, TaskDbContext db) =>
{
    var deleted = await db.Tasks.Where(t => t.Id == id).ExecuteDeleteAsync();
    return deleted == 0 ? Results.NotFound() : Results.NoContent();
});
#endregion

app.Run();

public partial class Program { }
