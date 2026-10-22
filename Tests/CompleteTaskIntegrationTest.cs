using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;
namespace TaskApi.Tests;

// Событие плюс состояние задачи в БД в момент публикации
public record PublishedEvent(TaskCompletedMessage Message, bool? IsCompletedInDbAtPublish);

// Заменяет RabbitMQ: запоминает, что опубликовали
public class FakeEventPublisher(IServiceProvider services) : IEventPublisher
{
    public ConcurrentBag<PublishedEvent> Events { get; } = new();

    public void PublishTaskCompleted(TaskCompletedMessage message)
    {
        // смотрим в БД в момент публикации: если событие ушло раньше сохранения, тут будет false
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
        var isCompleted = db.Tasks.AsNoTracking()
            .Where(t => t.Id == message.TaskId)
            .Select(t => (bool?)t.IsCompleted)
            .SingleOrDefault();

        Events.Add(new PublishedEvent(message, isCompleted));
    }
}

public class TestAppFactory : WebApplicationFactory<Program>
{
    // Отдельная БД на той же Postgres, что в appsettings.json (docker-compose пробрасывает её на 55332).
    // Так тесты не трогают dev-данные, но и не нужно дублировать хост/порт/пароль.
    private const string TestDatabase = "taskapi_tests";

    public FakeEventPublisher Publisher => Services.GetRequiredService<FakeEventPublisher>();

    public TestAppFactory()
    {
        // HACK: не успел, доделал бы так: поднимал бы свою базу через Testcontainers,
        // чтобы тесту не нужен был внешний Postgres.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ResolveConnectionString());
    }

    private static string ResolveConnectionString()
    {
        // полный контроль, если нужно: TASKAPI_TESTS_CONNECTION=Host=...;Database=...
        var fullOverride = Environment.GetEnvironmentVariable("TASKAPI_TESTS_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fullOverride))
            return fullOverride;

        // берём строку подключения приложения (appsettings.json копируется в output тестового проекта)
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var appConnectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default не найден в appsettings.json — не могу понять, к какой Postgres подключаться.");

        // хост/порт/логин/пароль — как у приложения, меняем только имя базы
        return new NpgsqlConnectionStringBuilder(appConnectionString)
        {
            Database = TestDatabase
        }.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // в тестах без настоящего RabbitMQ: убираем hosted service, ставим фейковый publisher
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IEventPublisher>();
            services.AddSingleton<FakeEventPublisher>();
            services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<FakeEventPublisher>());
        });
    }
}

public class CompleteTaskIntegrationTest : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    private readonly HttpClient _client;

    public CompleteTaskIntegrationTest(TestAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<JsonElement> CreateTaskAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/tasks", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private IEnumerable<PublishedEvent> EventsFor(Guid id) =>
        _factory.Publisher.Events.Where(e => e.Message.TaskId == id);

    // --- Основной сценарий из задания: создать -> завершить -> проверить событие ---

    [Fact]
    public async Task Completing_a_task_publishes_task_completed_event()
    {
        // 1. создаём
        var created = await CreateTaskAsync(new { title = "Buy milk", priority = "High" });
        var id = created.GetProperty("id").GetGuid();

        // 2. завершаем
        var completeResponse = await _client.PutAsync($"/tasks/{id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        // 3. событие опубликовано
        var published = Assert.Single(EventsFor(id));
        Assert.Equal("Buy milk", published.Message.Title);
        Assert.Equal(Priority.High, published.Message.Priority);
    }

    // --- Title: пустые строки не проходят ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_with_empty_or_whitespace_title_returns_400(string title)
    {
        var response = await _client.PostAsJsonAsync("/tasks", new { title });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_without_title_returns_400()
    {
        var response = await _client.PostAsJsonAsync("/tasks", new { priority = "Low" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_title_longer_than_200_returns_400()
    {
        var response = await _client.PostAsJsonAsync("/tasks", new { title = new string('a', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_with_title_of_exactly_200_chars_is_ok()
    {
        var created = await CreateTaskAsync(new { title = new string('a', 200) });

        Assert.Equal(200, created.GetProperty("title").GetString()!.Length);
    }

    // --- CompletedAt: пусто при создании, заполняется при завершении ---

    [Fact]
    public async Task CompletedAt_is_empty_on_create_and_set_on_complete()
    {
        // priority не передаём, должен быть Medium
        var created = await CreateTaskAsync(new { title = "Check CompletedAt" });
        var id = created.GetProperty("id").GetGuid();

        Assert.False(created.GetProperty("isCompleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("completedAt").ValueKind);
        Assert.Equal("Medium", created.GetProperty("priority").GetString());

        var completeResponse = await _client.PutAsync($"/tasks/{id}/complete", null);
        var completed = await completeResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(completed.GetProperty("isCompleted").GetBoolean());
        var createdAt = completed.GetProperty("createdAt").GetDateTimeOffset();
        var completedAt = completed.GetProperty("completedAt").GetDateTimeOffset();
        Assert.True(completedAt >= createdAt);

        // то же самое видно в GET /tasks
        var all = await _client.GetFromJsonAsync<JsonElement>("/tasks");
        var fromList = all.EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == id);
        Assert.NotEqual(JsonValueKind.Null, fromList.GetProperty("completedAt").ValueKind);
    }

    // --- RabbitMQ: событие уходит только после сохранения в БД ---

    [Fact]
    public async Task Event_is_published_only_after_task_is_saved_as_completed()
    {
        var created = await CreateTaskAsync(new { title = "Order check" });
        var id = created.GetProperty("id").GetGuid();

        await _client.PutAsync($"/tasks/{id}/complete", null);

        var published = Assert.Single(EventsFor(id));
        Assert.True(published.IsCompletedInDbAtPublish);
    }

    [Fact]
    public async Task No_event_when_task_does_not_exist()
    {
        var id = Guid.NewGuid();

        var response = await _client.PutAsync($"/tasks/{id}/complete", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(EventsFor(id));
    }

    // --- Конкурентность: второй complete получает 409 ---

    [Fact]
    public async Task Second_complete_returns_409_and_does_not_publish_again()
    {
        var created = await CreateTaskAsync(new { title = "Twice" });
        var id = created.GetProperty("id").GetGuid();

        var first = await _client.PutAsync($"/tasks/{id}/complete", null);
        var second = await _client.PutAsync($"/tasks/{id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Single(EventsFor(id));
    }

    [Fact]
    public async Task Parallel_complete_calls_give_one_200_and_the_rest_409()
    {
        var created = await CreateTaskAsync(new { title = "Race" });
        var id = created.GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => _client.PutAsync($"/tasks/{id}/complete", null)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Single(EventsFor(id));
    }
}
