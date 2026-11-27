using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Acme.QuoteApi;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var dataSource = NpgsqlDataSource.Create(BuildConnectionString());
builder.Services.AddSingleton(dataSource);

var app = builder.Build();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// ---------------------------------------------------------------- health
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/readyz", async (NpgsqlDataSource db) =>
{
    try
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync();
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "database unavailable", detail = ex.Message }, statusCode: 503);
    }
});

// What is this container made of? The web UI shows this next to the same
// facts about the front end, so the audience can see the base image change.
app.MapGet("/api/info", () => Results.Ok(new
{
    service = "quote-api",
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    baseImage = Environment.GetEnvironmentVariable("BASE_IMAGE") ?? "unknown",
    hasShell = File.Exists("/bin/sh") || File.Exists("/usr/bin/sh"),
    hasPackageManager = File.Exists("/usr/bin/dnf") || File.Exists("/usr/bin/microdnf") || File.Exists("/usr/bin/rpm"),
}));

app.MapGet("/api/vehicles/makes", () => Results.Ok(new[]
{
    "Aurora", "Brightline", "Cascade", "Dynamo", "Evergreen", "Falcon", "Granite", "Horizon",
}));

// ---------------------------------------------------------------- quotes
app.MapPost("/api/quotes", async (QuoteRequest req, NpgsqlDataSource db) =>
{
    var year = DateTime.UtcNow.Year;
    var errors = Pricing.Validate(req, year);
    if (errors.Count > 0) return Results.ValidationProblem(errors);

    var price = Pricing.Calculate(req, year);
    var id = Guid.NewGuid();

    await using var cmd = db.CreateCommand(
        "INSERT INTO quotes (id, request, breakdown, coverage, monthly, annual) " +
        "VALUES (@id, @request, @breakdown, @coverage, @monthly, @annual)");
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.Add(new NpgsqlParameter("request", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(req, jsonOptions) });
    cmd.Parameters.Add(new NpgsqlParameter("breakdown", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(price.Breakdown, jsonOptions) });
    cmd.Parameters.AddWithValue("coverage", req.Coverage!);
    cmd.Parameters.AddWithValue("monthly", price.Monthly);
    cmd.Parameters.AddWithValue("annual", price.Annual);
    await cmd.ExecuteNonQueryAsync();

    return Results.Created($"/api/quotes/{id}", new
    {
        quoteId = id,
        coverage = req.Coverage,
        monthlyPremium = price.Monthly,
        annualPremium = price.Annual,
        breakdown = price.Breakdown,
    });
});

app.MapGet("/api/quotes/{id:guid}", async (Guid id, NpgsqlDataSource db) =>
{
    await using var cmd = db.CreateCommand(
        "SELECT coverage, monthly, annual, breakdown::text, created_at FROM quotes WHERE id = @id");
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound(new { error = "Quote not found." });

    return Results.Ok(new
    {
        quoteId = id,
        coverage = reader.GetString(0),
        monthlyPremium = reader.GetDecimal(1),
        annualPremium = reader.GetDecimal(2),
        breakdown = JsonSerializer.Deserialize<JsonElement>(reader.GetString(3)),
        createdAt = reader.GetDateTime(4),
    });
});

// -------------------------------------------------------------- policies
app.MapPost("/api/policies", async (PurchaseRequest req, NpgsqlDataSource db) =>
{
    if (req.QuoteId == Guid.Empty)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["quoteId"] = ["A quote is required."] });

    await using var conn = await db.OpenConnectionAsync();

    await using (var check = new NpgsqlCommand("SELECT monthly, annual, coverage FROM quotes WHERE id = @id", conn))
    {
        check.Parameters.AddWithValue("id", req.QuoteId);
        await using var reader = await check.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return Results.NotFound(new { error = "Quote not found." });
    }

    await using (var existing = new NpgsqlCommand("SELECT policy_number, effective_date FROM policies WHERE quote_id = @id", conn))
    {
        existing.Parameters.AddWithValue("id", req.QuoteId);
        await using var reader = await existing.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return Results.Ok(new
            {
                policyNumber = reader.GetString(0),
                quoteId = req.QuoteId,
                effectiveDate = reader.GetFieldValue<DateOnly>(1),
                alreadyIssued = true,
            });
    }

    var policyNumber = NewPolicyNumber();
    var effective = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));

    await using (var insert = new NpgsqlCommand(
        "INSERT INTO policies (policy_number, quote_id, effective_date) VALUES (@number, @quote, @effective)", conn))
    {
        insert.Parameters.AddWithValue("number", policyNumber);
        insert.Parameters.AddWithValue("quote", req.QuoteId);
        insert.Parameters.AddWithValue("effective", effective);
        await insert.ExecuteNonQueryAsync();
    }

    return Results.Created($"/api/policies/{policyNumber}", new
    {
        policyNumber,
        quoteId = req.QuoteId,
        effectiveDate = effective,
        alreadyIssued = false,
    });
});

app.MapGet("/api/policies/{number}", async (string number, NpgsqlDataSource db) =>
{
    await using var cmd = db.CreateCommand(
        "SELECT p.policy_number, p.quote_id, p.effective_date, q.coverage, q.monthly, q.annual " +
        "FROM policies p JOIN quotes q ON q.id = p.quote_id WHERE p.policy_number = @number");
    cmd.Parameters.AddWithValue("number", number);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) return Results.NotFound(new { error = "Policy not found." });

    return Results.Ok(new
    {
        policyNumber = reader.GetString(0),
        quoteId = reader.GetGuid(1),
        effectiveDate = reader.GetFieldValue<DateOnly>(2),
        coverage = reader.GetString(3),
        monthlyPremium = reader.GetDecimal(4),
        annualPremium = reader.GetDecimal(5),
    });
});

await InitialiseSchemaAsync(dataSource, app.Logger);
app.Run();

// ----------------------------------------------------------------- helpers
static string BuildConnectionString()
{
    static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    var csb = new NpgsqlConnectionStringBuilder
    {
        Host = Env("DB_HOST", "localhost"),
        Port = int.Parse(Env("DB_PORT", "5432")),
        Database = Env("DB_NAME", "acme"),
        Username = Env("DB_USER", "acme"),
        Password = Env("DB_PASSWORD", ""),
        MaxPoolSize = 20,
        Timeout = 5,
    };
    return csb.ConnectionString;
}

static string NewPolicyNumber()
{
    const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    Span<char> chars = stackalloc char[8];
    for (var i = 0; i < chars.Length; i++)
        chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
    return $"ACME-{new string(chars)}";
}

// The database may still be starting when this pod does. Retry for about a minute.
static async Task InitialiseSchemaAsync(NpgsqlDataSource db, ILogger log)
{
    const string ddl = """
        CREATE TABLE IF NOT EXISTS quotes (
            id          uuid PRIMARY KEY,
            created_at  timestamptz NOT NULL DEFAULT now(),
            coverage    text NOT NULL,
            monthly     numeric(10,2) NOT NULL,
            annual      numeric(10,2) NOT NULL,
            request     jsonb NOT NULL,
            breakdown   jsonb NOT NULL
        );
        CREATE TABLE IF NOT EXISTS policies (
            policy_number  text PRIMARY KEY,
            quote_id       uuid NOT NULL UNIQUE REFERENCES quotes(id),
            effective_date date NOT NULL,
            created_at     timestamptz NOT NULL DEFAULT now()
        );
        """;

    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            await using var cmd = db.CreateCommand(ddl);
            await cmd.ExecuteNonQueryAsync();
            log.LogInformation("Database schema ready.");
            return;
        }
        catch (Exception ex) when (attempt < 30)
        {
            log.LogWarning("Database not ready (attempt {Attempt}/30): {Message}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

public record PurchaseRequest(Guid QuoteId);
