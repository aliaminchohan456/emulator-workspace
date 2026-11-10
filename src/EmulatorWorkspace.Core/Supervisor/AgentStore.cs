using System.Security.Cryptography;
using System.Text;
using EmulatorWorkspace.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace EmulatorWorkspace.Core.Supervisor;

public sealed record AgentPairing(
    string Id,
    string ClientName,
    string CodeHash,
    string Scopes,
    string? Template,
    string? Instance,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    bool Used);

public sealed record AgentSession(
    string Id,
    string ClientName,
    string Instance,
    string Scopes,
    string TokenHash,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    bool Revoked);

public sealed class AgentStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    public AgentStore(WorkspacePaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        var database = Path.Combine(paths.Root, "supervisor.db");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS agent_pairings (
                id TEXT PRIMARY KEY,
                client_name TEXT NOT NULL,
                code_hash TEXT NOT NULL,
                scopes TEXT NOT NULL DEFAULT '',
                template TEXT NULL,
                instance TEXT NULL,
                status TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                expires_utc TEXT NOT NULL,
                used INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS agent_sessions (
                id TEXT PRIMARY KEY,
                client_name TEXT NOT NULL,
                instance TEXT NOT NULL,
                scopes TEXT NOT NULL,
                token_hash TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                expires_utc TEXT NOT NULL,
                revoked INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS agent_approvals (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                digest TEXT NOT NULL,
                code_hash TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                expires_utc TEXT NOT NULL,
                used INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS idempotency (
                actor TEXT NOT NULL,
                key TEXT NOT NULL,
                operation TEXT NOT NULL,
                digest TEXT NOT NULL,
                state TEXT NOT NULL,
                response TEXT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY (actor, key)
            );
            CREATE TABLE IF NOT EXISTS artifacts (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                instance TEXT NOT NULL,
                kind TEXT NOT NULL,
                path TEXT NOT NULL,
                bytes INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                expires_utc TEXT NOT NULL
            );
            """);
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void CreatePairing(AgentPairing pairing)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_pairings
                    (id, client_name, code_hash, scopes, template, instance, status, created_utc, expires_utc, used)
                VALUES ($id, $client, $code, $scopes, $template, $instance, $status, $created, $expires, 0);
                """;
            command.Parameters.AddWithValue("$id", pairing.Id);
            command.Parameters.AddWithValue("$client", pairing.ClientName);
            command.Parameters.AddWithValue("$code", pairing.CodeHash);
            command.Parameters.AddWithValue("$scopes", pairing.Scopes);
            command.Parameters.AddWithValue("$template", (object?)pairing.Template ?? DBNull.Value);
            command.Parameters.AddWithValue("$instance", (object?)pairing.Instance ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", pairing.Status);
            command.Parameters.AddWithValue("$created", pairing.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$expires", pairing.ExpiresUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public AgentPairing? GetPairing(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM agent_pairings WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AgentPairing(
                reader.GetString(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("client_name")),
                reader.GetString(reader.GetOrdinal("code_hash")),
                reader.GetString(reader.GetOrdinal("scopes")),
                reader.IsDBNull(reader.GetOrdinal("template")) ? null : reader.GetString(reader.GetOrdinal("template")),
                reader.IsDBNull(reader.GetOrdinal("instance")) ? null : reader.GetString(reader.GetOrdinal("instance")),
                reader.GetString(reader.GetOrdinal("status")),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_utc"))),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("expires_utc"))),
                reader.GetInt32(reader.GetOrdinal("used")) != 0);
        }
    }

    public void UpdatePairing(AgentPairing pairing)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_pairings
                SET scopes = $scopes, template = $template, instance = $instance,
                    status = $status, used = $used
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", pairing.Id);
            command.Parameters.AddWithValue("$scopes", pairing.Scopes);
            command.Parameters.AddWithValue("$template", (object?)pairing.Template ?? DBNull.Value);
            command.Parameters.AddWithValue("$instance", (object?)pairing.Instance ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", pairing.Status);
            command.Parameters.AddWithValue("$used", pairing.Used ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AgentPairing> ListPairings()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM agent_pairings ORDER BY created_utc DESC LIMIT 50;";
            using var reader = command.ExecuteReader();
            var list = new List<AgentPairing>();
            while (reader.Read())
            {
                list.Add(new AgentPairing(
                    reader.GetString(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("client_name")),
                    reader.GetString(reader.GetOrdinal("code_hash")),
                    reader.GetString(reader.GetOrdinal("scopes")),
                    reader.IsDBNull(reader.GetOrdinal("template")) ? null : reader.GetString(reader.GetOrdinal("template")),
                    reader.IsDBNull(reader.GetOrdinal("instance")) ? null : reader.GetString(reader.GetOrdinal("instance")),
                    reader.GetString(reader.GetOrdinal("status")),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_utc"))),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("expires_utc"))),
                    reader.GetInt32(reader.GetOrdinal("used")) != 0));
            }

            return list;
        }
    }

    public void CreateSession(AgentSession session)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_sessions
                    (id, client_name, instance, scopes, token_hash, created_utc, expires_utc, revoked)
                VALUES ($id, $client, $instance, $scopes, $token, $created, $expires, 0);
                """;
            command.Parameters.AddWithValue("$id", session.Id);
            command.Parameters.AddWithValue("$client", session.ClientName);
            command.Parameters.AddWithValue("$instance", session.Instance);
            command.Parameters.AddWithValue("$scopes", session.Scopes);
            command.Parameters.AddWithValue("$token", session.TokenHash);
            command.Parameters.AddWithValue("$created", session.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$expires", session.ExpiresUtc.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public AgentSession? FindSessionByToken(string tokenHash)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM agent_sessions WHERE token_hash = $token LIMIT 1;";
            command.Parameters.AddWithValue("$token", tokenHash);
            using var reader = command.ExecuteReader();
            return reader.Read() ? MapSession(reader) : null;
        }
    }

    public AgentSession? GetSession(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM agent_sessions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? MapSession(reader) : null;
        }
    }

    public IReadOnlyList<AgentSession> ListSessions()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM agent_sessions ORDER BY created_utc DESC LIMIT 50;";
            using var reader = command.ExecuteReader();
            var list = new List<AgentSession>();
            while (reader.Read())
            {
                list.Add(MapSession(reader));
            }

            return list;
        }
    }

    public void RevokeSession(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE agent_sessions SET revoked = 1 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public int RevokeSessionsForInstance(string instance)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE agent_sessions SET revoked = 1 WHERE instance = $instance AND revoked = 0;";
            command.Parameters.AddWithValue("$instance", instance);
            return command.ExecuteNonQuery();
        }
    }

    public void CreateApproval(string id, string sessionId, string operation, string digest, string codeHash, DateTimeOffset expires)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agent_approvals
                    (id, session_id, operation, digest, code_hash, created_utc, expires_utc, used)
                VALUES ($id, $session, $operation, $digest, $code, $created, $expires, 0);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$operation", operation);
            command.Parameters.AddWithValue("$digest", digest);
            command.Parameters.AddWithValue("$code", codeHash);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$expires", expires.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public bool ConsumeApproval(string sessionId, string operation, string digest, string codeHash)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_approvals
                SET used = 1
                WHERE session_id = $session AND operation = $operation AND digest = $digest
                  AND code_hash = $code AND used = 0 AND expires_utc > $now;
                """;
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$operation", operation);
            command.Parameters.AddWithValue("$digest", digest);
            command.Parameters.AddWithValue("$code", codeHash);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return command.ExecuteNonQuery() == 1;
        }
    }

    public (string Operation, string Digest, string State, string? Response)? GetIdempotency(string actor, string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT operation, digest, state, response FROM idempotency WHERE actor = $actor AND key = $key;";
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$key", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    public void BeginIdempotency(string actor, string key, string operation, string digest)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO idempotency (actor, key, operation, digest, state, response, created_utc)
                VALUES ($actor, $key, $operation, $digest, 'in_progress', NULL, $created)
                ON CONFLICT(actor, key) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$operation", operation);
            command.Parameters.AddWithValue("$digest", digest);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void CompleteIdempotency(string actor, string key, string state, string? response)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE idempotency SET state = $state, response = $response WHERE actor = $actor AND key = $key;";
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$response", (object?)response ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public int ReconcileInProgressMutations()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE idempotency SET state = 'unknown_outcome' WHERE state = 'in_progress';";
            return command.ExecuteNonQuery();
        }
    }

    public void AddArtifact(string id, string sessionId, string instance, string kind, string path, long bytes, DateTimeOffset expires)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO artifacts (id, session_id, instance, kind, path, bytes, created_utc, expires_utc)
                VALUES ($id, $session, $instance, $kind, $path, $bytes, $created, $expires);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$instance", instance);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$bytes", bytes);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$expires", expires.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public (string SessionId, string Instance, string Kind, string Path, long Bytes)? GetArtifact(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT session_id, instance, kind, path, bytes FROM artifacts WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
        }
    }

    private static AgentSession MapSession(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("client_name")),
        reader.GetString(reader.GetOrdinal("instance")),
        reader.GetString(reader.GetOrdinal("scopes")),
        reader.GetString(reader.GetOrdinal("token_hash")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_utc"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("expires_utc"))),
        reader.GetInt32(reader.GetOrdinal("revoked")) != 0);

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
