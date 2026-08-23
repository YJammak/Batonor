using Batonor.Abstractions;
using Batonor.Json;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Batonor.Persistence.Sqlite;

/// <summary>
/// <see cref="IWorkflowStore"/> over SQLite (ADO.NET). Persists definitions, instance snapshots and
/// pending decisions as JSON text. AOT-safe: no reflection, no EF Core. Holds one cached connection.
/// </summary>
public sealed class SqliteWorkflowStore : IWorkflowStore, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private volatile bool _disposed;

    public SqliteWorkflowStore(string connectionString) => _connectionString = connectionString;

    private SqliteConnection Connection
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SqliteWorkflowStore));

            if (_connection is null)
            {
                var conn = new SqliteConnection(_connectionString);
                try
                {
                    conn.Open();
                    CreateTables(conn);
                    _connection = conn;
                }
                catch
                {
                    conn.Dispose();
                    throw;
                }
            }

            return _connection;
        }
    }

    private static void CreateTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS workflow_definitions (
              definition_id TEXT NOT NULL,
              definition_version INTEGER NOT NULL,
              definition_json TEXT NOT NULL,
              PRIMARY KEY (definition_id, definition_version));
            CREATE TABLE IF NOT EXISTS workflow_instances (
              instance_id TEXT PRIMARY KEY,
              definition_id TEXT NOT NULL,
              definition_version INTEGER NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NULL,
              completed_at TEXT NULL,
              instance_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS pending_decisions (
              decision_id TEXT PRIMARY KEY,
              instance_id TEXT NOT NULL,
              decision_json TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task SaveDefinitionAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO workflow_definitions (definition_id, definition_version, definition_json)
                VALUES ($id, $ver, $json)
                ON CONFLICT(definition_id, definition_version) DO UPDATE SET definition_json = $json;
                """;
            cmd.Parameters.AddWithValue("$id", definition.Id);
            cmd.Parameters.AddWithValue("$ver", definition.Version);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(definition, BatonorJsonContext.Default.WorkflowDefinition));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkflowDefinition?> LoadDefinitionAsync(string id, int version, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT definition_json FROM workflow_definitions WHERE definition_id = $id AND definition_version = $ver;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ver", version);
            var json = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize(json, BatonorJsonContext.Default.WorkflowDefinition);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveInstanceAsync(WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO workflow_instances (instance_id, definition_id, definition_version, status, created_at, completed_at, instance_json)
                VALUES ($id, $did, $dver, $status, $created, $completed, $json)
                ON CONFLICT(instance_id) DO UPDATE SET
                  definition_id = $did, definition_version = $dver, status = $status,
                  created_at = $created, completed_at = $completed, instance_json = $json;
                """;
            cmd.Parameters.AddWithValue("$id", instance.InstanceId);
            cmd.Parameters.AddWithValue("$did", instance.DefinitionId);
            cmd.Parameters.AddWithValue("$dver", instance.DefinitionVersion);
            cmd.Parameters.AddWithValue("$status", instance.Status.ToString());
            cmd.Parameters.AddWithValue("$created", instance.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$completed", instance.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(instance, BatonorJsonContext.Default.WorkflowInstance));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkflowInstance?> LoadInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT instance_json FROM workflow_instances WHERE instance_id = $id;";
            cmd.Parameters.AddWithValue("$id", instanceId);
            var json = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize(json, BatonorJsonContext.Default.WorkflowInstance);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingDecision>> ListPendingDecisionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<PendingDecision>();
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT decision_json FROM pending_decisions;";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var json = reader.GetString(0);
                result.Add(JsonSerializer.Deserialize(json, BatonorJsonContext.Default.PendingDecision)!);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePendingDecisionAsync(PendingDecision decision, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pending_decisions (decision_id, instance_id, decision_json)
                VALUES ($id, $iid, $json)
                ON CONFLICT(decision_id) DO UPDATE SET instance_id = $iid, decision_json = $json;
                """;
            cmd.Parameters.AddWithValue("$id", decision.DecisionId);
            cmd.Parameters.AddWithValue("$iid", decision.InstanceId);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(decision, BatonorJsonContext.Default.PendingDecision));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingDecision?> LoadPendingDecisionAsync(string decisionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT decision_json FROM pending_decisions WHERE decision_id = $id;";
            cmd.Parameters.AddWithValue("$id", decisionId);
            var json = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize(json, BatonorJsonContext.Default.PendingDecision);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteDecisionAsync(string decisionId, string choice, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM pending_decisions WHERE decision_id = $id;";
            cmd.Parameters.AddWithValue("$id", decisionId);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
