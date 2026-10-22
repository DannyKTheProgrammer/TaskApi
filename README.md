# TaskApi

Тестовое задание: C# / .NET Backend Developer (Middle).
Минимальный API для задач: ASP.NET Core Minimal API, PostgreSQL + EF Core, публикация событий в RabbitMQ.

## Стек

- .NET 9, ASP.NET Core Minimal API
- PostgreSQL 17 + EF Core (миграции применяются при старте)
- RabbitMQ 4 (только publish, consumer нет)
- xUnit + WebApplicationFactory для интеграционного теста

## Структура

```
TaskApi/
├── Program.cs                 # настройка, эндпоинты, валидация
├── TaskItem.cs                # сущность, Priority, запрос на создание
├── TaskDbContext.cs           # EF Core, RowVersion (xmin)
├── RabbitMqService.cs         # публикация событий, graceful shutdown
├── Migrations/                # EF Core миграции
├── appsettings.json
├── Directory.Packages.props   # единые версии NuGet-пакетов на всё решение (EF Core и др.)
├── Dockerfile
├── docker-compose.yml
└── Tests/
    └── CompleteTaskIntegrationTest.cs
```

## Запуск в Docker (рекомендуемый способ)

Нужен только Docker.

```bash
docker compose up --build
```

Поднимаются три сервиса: `api`, `postgres`, `rabbitmq`. API ждёт, пока Postgres и RabbitMQ пройдут healthcheck, и сам применяет миграции при первом старте.

| Что | Адрес |
|-----|-------|
| API | http://localhost:8080 |
| Swagger UI | http://localhost:8080/swagger (корень `/` перенаправляет туда) |
| RabbitMQ Management UI | http://localhost:15672 (guest / guest) |
| PostgreSQL | localhost:5432 (postgres / postgres, база `taskapi`) |

Остановить: `Ctrl+C` или `docker compose stop api`.
Полностью сбросить данные: `docker compose down -v`.

## Запуск локально (без Docker для API)

Нужны .NET 9 SDK и работающие Postgres и RabbitMQ. Проще всего поднять только зависимости:

```bash
docker compose up -d postgres rabbitmq
dotnet run
```

Строки подключения лежат в `appsettings.json` (`ConnectionStrings:Default` и `RabbitMq:Uri`), по умолчанию они указывают на localhost.

## Эндпоинты

Все эндпоинты можно попробовать в Swagger UI: http://localhost:8080/swagger.

| Метод | Путь | Описание | Ответы |
|-------|------|----------|--------|
| POST | `/tasks` | создать задачу | 201, 400 |
| GET | `/tasks` | все задачи, без пагинации | 200 |
| PUT | `/tasks/{id}/complete` | завершить задачу, отправить событие в RabbitMQ | 200, 404, 409 |
| DELETE | `/tasks/{id}` | удалить задачу (жёстко) | 204, 404 |

Правила для `POST /tasks`:

- `title` обязателен, пробелы по краям обрезаются, пустая строка и длина больше 200 дают 400
- `priority` необязателен: `Low`, `Medium` или `High`, по умолчанию `Medium`
- `isCompleted` по умолчанию `false`, `completedAt` пустой до завершения

### Примеры (bash)

```bash
# создать
curl -X POST http://localhost:8080/tasks \
  -H "Content-Type: application/json" \
  -d '{"title":"Buy milk","priority":"High"}'

# список
curl http://localhost:8080/tasks

# завершить (подставьте id из ответа)
curl -X PUT http://localhost:8080/tasks/<id>/complete

# повторное завершение вернёт 409
curl -i -X PUT http://localhost:8080/tasks/<id>/complete

# пустой title вернёт 400
curl -i -X POST http://localhost:8080/tasks \
  -H "Content-Type: application/json" \
  -d '{"title":"   "}'

# удалить
curl -X DELETE http://localhost:8080/tasks/<id>
```

### Пример (PowerShell)

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8080/tasks `
  -ContentType "application/json" `
  -Body '{"title":"Buy milk","priority":"High"}'
```

### Пример ответа

```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "title": "Buy milk",
  "isCompleted": true,
  "createdAt": "2026-05-07T11:59:00+00:00",
  "completedAt": "2026-05-07T12:00:00+00:00",
  "priority": "High"
}
```

## RabbitMQ

При завершении задачи отправляется сообщение:

- обменник: `task.events` (тип topic, durable)
- routing key: `task.completed`
- тело:

```json
{
  "taskId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "title": "Buy milk",
  "completedAt": "2026-05-07T12:00:00Z",
  "priority": "High"
}
```

Как посмотреть сообщение через Management UI:

1. Завершите любую задачу один раз. Обменник создаётся при первой публикации.
2. Откройте http://localhost:15672, вкладка **Queues and Streams**, создайте очередь, например `debug`.
3. Откройте обменник `task.events` (вкладка **Exchanges**), в разделе **Bindings** добавьте очередь `debug` с routing key `task.completed`.
4. Завершите ещё одну задачу.
5. В очереди `debug` нажмите **Get messages**.

Как это работает:

- Событие отправляется после успешного сохранения в БД.
- Запрос не ждёт RabbitMQ: сообщение кладётся во внутреннюю очередь, а фоновый цикл публикует его.
- Если RabbitMQ недоступен, задача всё равно завершается, а сообщение попадает в лог с уровнем Error.
- При остановке приложения (Ctrl+C, SIGTERM) очередь дочитывается до 5 секунд, затем канал и соединение закрываются.

## Конкурентность

Для `Version` используется системная колонка Postgres `xmin` как RowVersion. Если два запроса одновременно завершают одну задачу, первый сохраняет изменения, а второй получает 409 Conflict (по проверке `IsCompleted` или по `DbUpdateConcurrencyException`).

## Тесты

Интеграционному тесту нужен только Postgres — он берёт хост, порт и пароль из той же `ConnectionStrings:Default` в `appsettings.json`, но подставляет отдельную базу `taskapi_tests`. Отдельная база создаётся и мигрируется сама при старте приложения внутри теста, поэтому локальную `taskapi` тесты не трогают. RabbitMQ заменён фейковым publisher.

```bash
docker compose up -d postgres   # Postgres проброшен на localhost:55332
dotnet test
```

Если Postgres живёт в другом месте, строку подключения для тестов можно переопределить целиком:

```bash
TASKAPI_TESTS_CONNECTION="Host=localhost;Port=5432;Database=taskapi_tests;Username=postgres;Password=secret" dotnet test
```

Что проверяют тесты:

- основной сценарий: создать задачу, завершить, событие опубликовано с нужными данными
- `Title`: пустая строка, пробелы, отсутствие поля и длина больше 200 дают 400; ровно 200 символов проходит
- `CompletedAt` пустой при создании и заполняется только при завершении (также проверяется `GET /tasks`)
- событие уходит после сохранения в БД: фейковый publisher в момент вызова читает задачу из базы и запоминает, была ли она уже завершена
- события нет, если задачи не существует
- повторный `complete` возвращает 409 и второго события нет
- 5 параллельных `complete` дают один 200, четыре 409 и одно событие

Тесты используют отдельную базу `taskapi_tests` на том же Postgres, что и приложение.

## Миграции

Первая миграция `Init` лежит в `Migrations/` и применяется при старте (`Database.Migrate()`).
Новая миграция:

```bash
dotnet ef migrations add <Name>
```

Нужен инструмент `dotnet-ef` (`dotnet tool install --global dotnet-ef`).

## Что не сделано

Места, помеченные `// HACK:` в коде:

- неотправленные при остановке сообщения теряются, нужна outbox-таблица
- нет publisher confirms
- миграции применяются при старте приложения, при нескольких инстансах их стоило бы вынести в отдельный шаг
- тесту нужен запущенный Postgres, лучше поднимать его через Testcontainers

Пагинации, фильтрации и сортировки нет.
