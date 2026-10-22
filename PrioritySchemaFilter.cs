using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

/// <summary>
/// Swashbuckle описывает enum Priority как голый integer (0, 1, 2) без расшифровки значений,
/// из-за чего в Swagger UI не видно, что означает каждое число. Добавляем описание и пример.
/// </summary>
public class PrioritySchemaFilter : ISchemaFilter
{
    private const string Description =
        "Приоритет задачи: 0 = Low (низкий), 1 = Medium (средний), 2 = High (высокий). " +
        "Если поле не передано — ставится Medium (1). " +
        "В JSON принимается и имя (\"Low\", \"Medium\", \"High\"), и число (0, 1, 2); " +
        "в ответах приоритет всегда приходит именем, например \"High\".";

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(Priority))
            return;

        schema.Description = Description;
        schema.Example = new OpenApiInteger((int)Priority.Medium);
    }
}
