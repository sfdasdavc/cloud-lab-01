using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string connString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Строка подключения ConnectionStrings:Default не задана.");

var allowedStatuses = new HashSet<string> { "new", "in_progress", "done" };

app.MapGet("/health", async () =>
{
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync();
        return Results.Ok(new { status = "healthy", database = "connected" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", error = ex.Message }, statusCode: 503);
    }
});

app.MapGet("/api/tickets", async (
    string? status,
    int? priority,
    string? q,
    string? sort = "desc",
    int page = 1,
    int pageSize = 10) =>
{
    if (!string.IsNullOrWhiteSpace(status) && !allowedStatuses.Contains(status))
        return Results.BadRequest(new { error = "Недопустимый status. Доступны: new, in_progress, done." });

    if (priority.HasValue && (priority < 1 || priority > 3))
        return Results.BadRequest(new { error = "Priority должен быть от 1 до 3." });

    if (page < 1) page = 1;
    if (pageSize < 1 || pageSize > 100) pageSize = 10;

    var conditions = new List<string>();
    var parameters = new List<NpgsqlParameter>();

    if (!string.IsNullOrWhiteSpace(status))
    {
        conditions.Add("status = @status");
        parameters.Add(new NpgsqlParameter("@status", status));
    }
    if (priority.HasValue)
    {
        conditions.Add("priority = @priority");
        parameters.Add(new NpgsqlParameter("@priority", priority.Value));
    }
    if (!string.IsNullOrWhiteSpace(q))
    {
        conditions.Add("title ILIKE @q");
        parameters.Add(new NpgsqlParameter("@q", $"%{q}%"));
    }

    string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
    string orderDirection = string.Equals(sort, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    await using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM tickets {whereClause}", conn);
    foreach (var p in parameters)
        countCmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));

    long totalCount = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);

    string sql = $@"
        SELECT id, title, description, priority, status, created_at
        FROM tickets
        {whereClause}
        ORDER BY created_at {orderDirection}
        LIMIT @limit OFFSET @offset";

    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var p in parameters)
        cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));

    cmd.Parameters.AddWithValue("@limit", pageSize);
    cmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);

    var items = new List<TicketDto>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        items.Add(new TicketDto(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetDateTime(5)
        ));
    }

    return Results.Ok(new { total = totalCount, page, pageSize, items });
});

app.MapGet("/api/tickets/{id:int}", async (int id) =>
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        "SELECT id, title, description, priority, status, created_at FROM tickets WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("@id", id);

    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return Results.NotFound(new { error = $"Заявка с ID {id} не найдена." });

    return Results.Ok(new TicketDto(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetDateTime(5)
    ));
});

app.MapPost("/api/tickets", async (CreateTicketRequest req) =>
{
    var status = string.IsNullOrWhiteSpace(req.Status) ? "new" : req.Status;
    var validationError = ValidateTicket(req.Title, req.Description, req.Priority, status, allowedStatuses);
    if (validationError != null)
        return Results.BadRequest(new { error = validationError });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var cmd = new NpgsqlCommand(@"
        INSERT INTO tickets (title, description, priority, status)
        VALUES (@title, @description, @priority, @status)
        RETURNING id, created_at", conn, tx);

    cmd.Parameters.AddWithValue("@title", req.Title.Trim());
    cmd.Parameters.AddWithValue("@description", req.Description.Trim());
    cmd.Parameters.AddWithValue("@priority", req.Priority);
    cmd.Parameters.AddWithValue("@status", status);

    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    int newId = reader.GetInt32(0);
    DateTime createdAt = reader.GetDateTime(1);
    await reader.CloseAsync();

    await using var eventCmd = new NpgsqlCommand(@"
        INSERT INTO ticket_events (ticket_id, old_status, new_status)
        VALUES (@ticketId, NULL, @newStatus)", conn, tx);
    eventCmd.Parameters.AddWithValue("@ticketId", newId);
    eventCmd.Parameters.AddWithValue("@newStatus", status);
    await eventCmd.ExecuteNonQueryAsync();

    await tx.CommitAsync();

    return Results.Created($"/api/tickets/{newId}", new
    {
        id = newId,
        title = req.Title.Trim(),
        description = req.Description.Trim(),
        priority = req.Priority,
        status,
        createdAt
    });
});

app.MapPut("/api/tickets/{id:int}", async (int id, UpdateTicketRequest req) =>
{
    var validationError = ValidateTicket(req.Title, req.Description, req.Priority, req.Status, allowedStatuses);
    if (validationError != null)
        return Results.BadRequest(new { error = validationError });

    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var getCmd = new NpgsqlCommand("SELECT status FROM tickets WHERE id = @id FOR UPDATE", conn, tx);
    getCmd.Parameters.AddWithValue("@id", id);
    var oldStatusObj = await getCmd.ExecuteScalarAsync();

    if (oldStatusObj == null)
        return Results.NotFound(new { error = $"Заявка с ID {id} не найдена." });

    string oldStatus = (string)oldStatusObj;

    await using var updateCmd = new NpgsqlCommand(@"
        UPDATE tickets
        SET title = @title, description = @description, priority = @priority, status = @status
        WHERE id = @id", conn, tx);

    updateCmd.Parameters.AddWithValue("@id", id);
    updateCmd.Parameters.AddWithValue("@title", req.Title.Trim());
    updateCmd.Parameters.AddWithValue("@description", req.Description.Trim());
    updateCmd.Parameters.AddWithValue("@priority", req.Priority);
    updateCmd.Parameters.AddWithValue("@status", req.Status);
    await updateCmd.ExecuteNonQueryAsync();

    if (!string.Equals(oldStatus, req.Status, StringComparison.Ordinal))
    {
        await using var eventCmd = new NpgsqlCommand(@"
            INSERT INTO ticket_events (ticket_id, old_status, new_status)
            VALUES (@ticketId, @oldStatus, @newStatus)", conn, tx);
        eventCmd.Parameters.AddWithValue("@ticketId", id);
        eventCmd.Parameters.AddWithValue("@oldStatus", oldStatus);
        eventCmd.Parameters.AddWithValue("@newStatus", req.Status);
        await eventCmd.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();
    return Results.Ok(new { id, title = req.Title.Trim(), description = req.Description.Trim(), priority = req.Priority, status = req.Status });
});

app.MapDelete("/api/tickets/{id:int}", async (int id) =>
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand("DELETE FROM tickets WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("@id", id);

    int affected = await cmd.ExecuteNonQueryAsync();
    return affected > 0 ? Results.NoContent() : Results.NotFound(new { error = $"Заявка с ID {id} не найдена." });
});

app.MapGet("/api/stats", async () =>
{
    await using var conn = new NpgsqlConnection(connString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(@"
        SELECT
            COUNT(*) AS total,
            COUNT(*) FILTER (WHERE status = 'new') AS new_count,
            COUNT(*) FILTER (WHERE status = 'in_progress') AS in_progress_count,
            COUNT(*) FILTER (WHERE status = 'done') AS done_count
        FROM tickets", conn);

    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();

    return Results.Ok(new
    {
        total = reader.GetInt64(0),
        byStatus = new
        {
            @new = reader.GetInt64(1),
            in_progress = reader.GetInt64(2),
            done = reader.GetInt64(3)
        }
    });
});

app.Run();

static string? ValidateTicket(string? title, string? description, int priority, string? status, HashSet<string> allowedStatuses)
{
    if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 3 || title.Trim().Length > 120)
        return "Поле title должно содержать от 3 до 120 символов.";
    if (string.IsNullOrWhiteSpace(description))
        return "Поле description обязательно для заполнения.";
    if (priority < 1 || priority > 3)
        return "Поле priority должно быть числом от 1 до 3.";
    if (string.IsNullOrWhiteSpace(status) || !allowedStatuses.Contains(status))
        return "Поле status должно принимать значения: new, in_progress или done.";
    return null;
}

record TicketDto(int Id, string Title, string Description, int Priority, string Status, DateTime CreatedAt);
record CreateTicketRequest(string Title, string Description, int Priority, string? Status);
record UpdateTicketRequest(string Title, string Description, int Priority, string Status);
