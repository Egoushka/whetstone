using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Whetstone.Contracts;
using Whetstone.Gym;
using Whetstone.Templates;

namespace Whetstone.Storage;

/// <summary>Reads, counts and deletes in a user's existing file. The server may be writing to it: waits are bounded, not skipped.</summary>
internal sealed class SqliteStoreAdmin(string path) : IStoreAdmin
{
    private const int BusyMilliseconds = 5_000;

    /// <summary>The prefix of every id an import gives a past prompt (Whetstone.Import.Importer).</summary>
    public const string ImportedPrefix = "imp-";

    private const string Where = "WHERE ($repository IS NULL OR repository = $repository) AND ($before IS NULL OR created_at < $before) AND ($text IS NULL OR prompt_matches(prompt)) AND ($imported IS NULL OR substr(request_id, 1, length($imported)) = $imported)";

    public async Task<int> CountAsync(RowFilter filter, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        var (connection, _) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM requests {Where}";
        Bind(command, filter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<ExportRecord> ExportAsync(RowFilter filter, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(path))
            yield break;
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        await using var command = connection.CreateCommand();
        // An older file lacks the columns of a later version; the server adds them the next time it opens the file.
        var measures = version >= 3
            ? "completed, tokens_in, tokens_out, cache_read_tokens, cache_write_tokens, duration_ms, tool_calls, asked_again, effort"
            : "NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL";
        var rewards = version >= 5
            ? "session_id, commits, pr_merged, ci_first_push_green, reverted, item_closed, correction_reason, review_findings, review_findings_severe, person_ms"
            : "NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL";
        command.CommandText = $"""
            SELECT request_id, created_at, prompt, truncated, repository, commit_sha, task_kind, client, changed, template_id, template_version, held_out,
                   rewrite_accepted, model_overridden, score, cost_usd, model, feedback_at, {(version >= 2 ? "source_request_id" : "NULL")}, {measures},
                   {(version >= 4 ? "arm" : "NULL")}, {rewards}, {(version >= 6 ? "hand_label" : "NULL")}, {(version >= 8 ? "rewritten_prompt" : "NULL")}, {(version >= 9 ? "traits" : "NULL")}
            FROM requests {Where} ORDER BY created_at, request_id
            """;
        Bind(command, filter);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var reported = reader.IsDBNull(17) ? null : reader.GetString(17);
            yield return new ExportRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0,
                new ExportContext(Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 29)),
                new ExportAnswer(reader.GetInt64(8) != 0, Text(reader, 9), Text(reader, 10), reader.GetInt64(11) != 0, Text(reader, 18), Text(reader, 28), TraitsOf(Text(reader, 41))),
                reported is null
                    ? null
                    : new ExportOutcome(Flag(reader, 12), Flag(reader, 13), Number(reader, 14), Number(reader, 15), Text(reader, 16), reported,
                        Flag(reader, 19), Count(reader, 20), Count(reader, 21), Count(reader, 22), Count(reader, 23), Count(reader, 24), Count(reader, 25),
                        Flag(reader, 26), Text(reader, 27),
                        Count(reader, 30), Flag(reader, 31), Flag(reader, 32), Flag(reader, 33), Flag(reader, 34), Text(reader, 35), Count(reader, 36),
                        Count(reader, 37), Count(reader, 38), Text(reader, 40)),
                Flag(reader, 39));
        }
    }

    public async Task<int> ForgetAsync(RowFilter filter, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        // Deleted text is overwritten with zeros, the rebuild below drops the pages, and nothing goes to a temp file.
        foreach (var pragma in new[] { "PRAGMA secure_delete = ON", "PRAGMA temp_store = MEMORY" })
        {
            await using var set = connection.CreateCommand();
            set.CommandText = pragma;
            await set.ExecuteNonQueryAsync(ct);
        }
        int deleted;
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM requests {Where}";
            Bind(delete, filter);
            deleted = await delete.ExecuteNonQueryAsync(ct);
        }
        if (deleted > 0 && version >= 6)
        {
            await using var orphans = connection.CreateCommand();
            orphans.CommandText = StoreSchema.ForgetOrphanedGymRows;
            await orphans.ExecuteNonQueryAsync(ct);
        }
        if (deleted > 0)
        {
            // The index kept the deleted words until it is rebuilt from what is left; do that before the pages are rewritten.
            if (version >= 2)
            {
                await using var rebuild = connection.CreateCommand();
                rebuild.CommandText = StoreSchema.RebuildIndex;
                await rebuild.ExecuteNonQueryAsync(ct);
            }
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            await vacuum.ExecuteNonQueryAsync(ct);
        }
        return deleted;
    }

    public async Task<IReadOnlyList<GymCase>> GymCasesAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        if (version < 6)
            return [];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT case_id, source_request_id, repository, commit_sha, task_kind, prompt_hash, pinned, selected_at FROM gym_cases ORDER BY selected_at, case_id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = new List<GymCase>();
        while (await reader.ReadAsync(ct))
            found.Add(new GymCase(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt64(6) != 0, DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
        return found;
    }

    public async Task<string?> AddGymCasesAsync(IReadOnlyList<GymCase> cases, CancellationToken ct)
    {
        if (!File.Exists(path))
            return "there is no store file yet";
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        if (version < 6)
            return "the store has no gym tables yet; start the server once to upgrade it";
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var c in cases)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO gym_cases (case_id, source_request_id, repository, commit_sha, task_kind, prompt_hash, pinned, selected_at)
                VALUES ($id, $source, $repository, $commit, $kind, $hash, $pinned, $at)
                """;
            insert.Parameters.AddWithValue("$id", c.CaseId);
            insert.Parameters.AddWithValue("$source", c.SourceRequestId);
            insert.Parameters.AddWithValue("$repository", c.Repository);
            insert.Parameters.AddWithValue("$commit", c.Commit);
            insert.Parameters.AddWithValue("$kind", c.TaskKind);
            insert.Parameters.AddWithValue("$hash", c.PromptHash);
            insert.Parameters.AddWithValue("$pinned", c.Pinned ? 1 : 0);
            insert.Parameters.AddWithValue("$at", c.SelectedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return null;
    }

    public async Task<(IReadOnlyList<GymRun> Runs, IReadOnlyList<GymVerdict> Verdicts)> GymResultsAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return ([], []);
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        if (version < 6)
            return ([], []);
        var runs = new List<GymRun>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT run_id, case_id, arm, template_id, template_version, client, client_run_id, preset, model, effort, completed, status,
                       tokens_in, tokens_out, cache_read_tokens, cache_write_tokens, cost_usd, duration_ms, evidence_resolved, started_at
                FROM gym_runs ORDER BY started_at, run_id
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                runs.Add(new GymRun(reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), reader.GetString(5),
                    new ReplayResult(Flag(reader, 10), Text(reader, 11), Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9), Count(reader, 12),
                        Count(reader, 13), Count(reader, 14), Count(reader, 15), Number(reader, 16), Count(reader, 17), Number(reader, 18)),
                    Instant(reader.GetString(19))));
        }
        var verdicts = new List<GymVerdict>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT case_id, template_id, template_version, judge_model, winner, order_flipped, judge_cost_usd, decided_at FROM gym_verdicts ORDER BY decided_at";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                verdicts.Add(new GymVerdict(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt64(5) != 0, Number(reader, 6), Instant(reader.GetString(7))));
        }
        return (runs, verdicts);
    }

    public async Task<string?> AddGymResultsAsync(IReadOnlyList<GymRun> runs, IReadOnlyList<GymVerdict> verdicts, CancellationToken ct)
    {
        if (!File.Exists(path))
            return "there is no store file yet";
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        if (version < 6)
            return "the store has no gym tables yet; start the server once to upgrade it";
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var run in runs)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO gym_runs (run_id, case_id, arm, template_id, template_version, client, client_run_id, preset, model, effort, completed, status,
                  tokens_in, tokens_out, cache_read_tokens, cache_write_tokens, cost_usd, duration_ms, evidence_resolved, started_at)
                VALUES ($id, $case, $arm, $tid, $tversion, $client, $crun, $preset, $model, $effort, $completed, $status,
                  $tin, $tout, $cread, $cwrite, $cost, $duration, $evidence, $at)
                """;
            var r = run.Result;
            foreach (var (name, value) in new (string, object?)[]
            {
                ("$id", run.RunId), ("$case", run.CaseId), ("$arm", run.Arm), ("$tid", run.TemplateId), ("$tversion", run.TemplateVersion), ("$client", run.Client),
                ("$crun", r.ClientRunId), ("$preset", r.Preset), ("$model", r.Model), ("$effort", r.Effort), ("$completed", r.Completed is { } d ? d ? 1 : 0 : null),
                ("$status", r.Status), ("$tin", r.TokensIn), ("$tout", r.TokensOut), ("$cread", r.CacheReadTokens), ("$cwrite", r.CacheWriteTokens), ("$cost", r.CostUsd),
                ("$duration", r.DurationMs), ("$evidence", r.EvidenceResolved), ("$at", run.StartedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            })
                insert.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }
        foreach (var v in verdicts)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO gym_verdicts (case_id, template_id, template_version, judge_model, winner, order_flipped, judge_cost_usd, decided_at)
                VALUES ($case, $tid, $tversion, $judge, $winner, $flipped, $cost, $at)
                """;
            insert.Parameters.AddWithValue("$case", v.CaseId);
            insert.Parameters.AddWithValue("$tid", v.TemplateId);
            insert.Parameters.AddWithValue("$tversion", v.TemplateVersion);
            insert.Parameters.AddWithValue("$judge", v.JudgeModel);
            insert.Parameters.AddWithValue("$winner", v.Winner);
            insert.Parameters.AddWithValue("$flipped", v.OrderFlipped ? 1 : 0);
            insert.Parameters.AddWithValue("$cost", (object?)v.CostUsd ?? DBNull.Value);
            insert.Parameters.AddWithValue("$at", v.DecidedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return null;
    }

    public async Task<string?> PinGymCasesAsync(IReadOnlyList<string> caseIds, CancellationToken ct)
    {
        if (!File.Exists(path))
            return "there is no store file yet";
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        if (version < 6)
            return "the store has no gym tables yet; start the server once to upgrade it";
        foreach (var id in caseIds)
        {
            await using var pin = connection.CreateCommand();
            pin.CommandText = "UPDATE gym_cases SET pinned = 1 WHERE case_id = $id";
            pin.Parameters.AddWithValue("$id", id);
            await pin.ExecuteNonQueryAsync(ct);
        }
        return null;
    }

    private static DateTimeOffset Instant(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public async Task<string?> LabelAsync(string requestId, bool? label, CancellationToken ct)
    {
        if (!File.Exists(path))
            return "there is no store file yet";
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        if (version < 6)
            return "the store has no hand-label column yet; start the server once to upgrade it";
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE requests SET hand_label = $label WHERE request_id = $id";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$label", label is { } l ? l ? 1 : 0 : DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) > 0 ? null : $"no stored request '{requestId}'";
    }

    public async Task<string?> FindAsync(string importedId, string client, string prompt, DateTimeOffset at, TimeSpan within, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        var (connection, _) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id FROM requests
            WHERE request_id = $self
               OR (substr(request_id, 1, length($prefix)) <> $prefix
                   AND client = $client AND prompt = $prompt AND created_at >= $from AND created_at <= $to)
            ORDER BY request_id = $self DESC, created_at, request_id LIMIT 1
            """;
        command.Parameters.AddWithValue("$self", importedId);
        command.Parameters.AddWithValue("$prefix", ImportedPrefix);
        command.Parameters.AddWithValue("$client", client);
        command.Parameters.AddWithValue("$prompt", prompt);
        command.Parameters.AddWithValue("$from", (at - within).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", (at + within).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<TemplateRow>> TemplatesAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        if (version < 4)
            return [];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT template_id, version, kind, before_text, after_text, source, role, created_at FROM templates ORDER BY kind, created_at, template_id, version";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = new List<TemplateRow>();
        while (await reader.ReadAsync(ct))
            found.Add(new TemplateRow(
                new Template(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)),
                reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
        return found;
    }

    private static Dictionary<string, bool>? TraitsOf(string? json) =>
        json is null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(json);

    public async Task<double> WriterSpendSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        if (version < 10)
            return 0;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(cost_usd), 0) FROM writer_spend WHERE called_at >= $since";
        command.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToDouble(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task AddWriterSpendAsync(DateTimeOffset at, string kind, string model, double usd, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("there is no store file yet");
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        if (version < 10)
            throw new InvalidOperationException("the store has no spend ledger yet; start the server once to upgrade it");
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO writer_spend (called_at, kind, model, cost_usd) VALUES ($at, $kind, $model, $usd)";
        command.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$usd", usd);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<(Trait Trait, bool Active)>> TraitsAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        if (version < 9)
            return [];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT trait_id, kind, text, active FROM traits ORDER BY kind, trait_id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = new List<(Trait, bool)>();
        while (await reader.ReadAsync(ct))
            found.Add((new Trait(reader.GetString(0), reader.GetString(1), reader.GetString(2)), reader.GetInt64(3) != 0));
        return found;
    }

    public async Task<string?> AddTraitAsync(Trait trait, CancellationToken ct)
    {
        if (!File.Exists(path))
            return "there is no store file yet";
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        if (version < 9)
            return "the store has no trait table yet; start the server once to upgrade it";
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT active FROM traits WHERE kind = $kind AND trait_id = $id";
            exists.Parameters.AddWithValue("$kind", trait.Kind);
            exists.Parameters.AddWithValue("$id", trait.Id);
            if (await exists.ExecuteScalarAsync(ct) is not null)
                return $"trait {trait.Id} already exists for {trait.Kind}; give changed text a new id";
        }
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM traits WHERE kind = $kind AND active = 1";
            count.Parameters.AddWithValue("$kind", trait.Kind);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) >= Trait.MaxPerKind)
                return $"{trait.Kind} already has {Trait.MaxPerKind} traits; retire one first";
        }
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO traits (trait_id, kind, text, active, created_at) VALUES ($id, $kind, $text, 1, $at)";
        insert.Parameters.AddWithValue("$id", trait.Id);
        insert.Parameters.AddWithValue("$kind", trait.Kind);
        insert.Parameters.AddWithValue("$text", trait.Text);
        insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return null;
    }

    public async Task<string?> RetireTraitAsync(string kind, string id, CancellationToken ct)
    {
        if (!File.Exists(path))
            return "there is no store file yet";
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        if (version < 9)
            return "the store has no trait table yet; start the server once to upgrade it";
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE traits SET active = 0 WHERE kind = $kind AND trait_id = $id AND active = 1";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0 ? null : $"no active trait {id} for {kind}";
    }

    public async Task<IReadOnlyList<(string Kind, string TemplateId, string Version, LibraryDecision Decision)>> TemplateDecisionsAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadOnly, ct);
        await using var _ = connection;
        if (version < 6)
            return [];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, template_id, version, decided_at, action, counts, note FROM template_decisions ORDER BY decided_at, decision_id";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = new List<(string, string, string, LibraryDecision)>();
        while (await reader.ReadAsync(ct))
            found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), new LibraryDecision(reader.GetString(3), reader.GetString(4), Text(reader, 5), Text(reader, 6))));
        return found;
    }

    public async Task<string?> AddTemplateAsync(Template version, string role, DecisionRecord? decision, CancellationToken ct, bool allowSimilar = false)
    {
        if (role is not (TemplateRoles.Champion or TemplateRoles.Challenger))
            return "a new template starts as a champion or a challenger";
        return await ChangeTemplatesAsync(async (connection, transaction) =>
        {
            if (await RoleOfAsync(connection, transaction, version.Kind, version.Id, version.Version, ct) is not null)
                return $"{version.Id} v{version.Version} already exists for {version.Kind}; a version is immutable, give new text a new version";
            if (role == TemplateRoles.Challenger)
            {
                if (await CountAsync(connection, transaction, version.Kind, role, ct) >= KindTemplates.MaxChallengers)
                    return $"{version.Kind} already has {KindTemplates.MaxChallengers} challengers; retire one first";
            }
            else if (await HolderAsync(connection, transaction, version.Kind, role, ct) is { } holder)
            {
                return $"{version.Kind} already has a {role} ({holder}); retire it first";
            }
            if (!allowSimilar && await RetiredAsync(connection, transaction, version.Kind, ct) is { Count: > 0 } retired
                && Similarity.Closest(version, retired) is { } near && near.Closeness >= Similarity.TooClose)
                return $"{version.Id} v{version.Version} is too close to {near.Retired.Id} v{near.Retired.Version}, retired for {version.Kind} (closeness {near.Closeness:0.00}); "
                    + "the negative library keeps it as a miss. Pass --allow-similar to store it anyway";
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO templates (template_id, version, kind, before_text, after_text, source, role, created_at)
                VALUES ($id, $version, $kind, $before, $after, $source, $role, $at)
                """;
            insert.Parameters.AddWithValue("$id", version.Id);
            insert.Parameters.AddWithValue("$version", version.Version);
            insert.Parameters.AddWithValue("$kind", version.Kind);
            insert.Parameters.AddWithValue("$before", version.Before);
            insert.Parameters.AddWithValue("$after", version.After);
            insert.Parameters.AddWithValue("$source", version.Source);
            insert.Parameters.AddWithValue("$role", role);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(ct);
            return null;
        }, ("add", version.Kind, version.Id, version.Version, decision), ct);
    }

    public async Task<string?> PromoteTemplateAsync(string kind, string id, string version, DecisionRecord? decision, CancellationToken ct) =>
        await ChangeTemplatesAsync(async (connection, transaction) =>
        {
            var role = await RoleOfAsync(connection, transaction, kind, id, version, ct);
            if (role is null)
                return $"{id} v{version} is not stored for {kind}";
            if (role != TemplateRoles.Challenger)
                return $"{id} v{version} is a {role}; only a challenger is promoted";
            await SetRoleAsync(connection, transaction, "UPDATE templates SET role = 'retired' WHERE kind = $kind AND role = 'champion'", kind, null, null, ct);
            await SetRoleAsync(connection, transaction, "UPDATE templates SET role = 'champion' WHERE kind = $kind AND template_id = $id AND version = $version", kind, id, version, ct);
            return null;
        }, ("promote", kind, id, version, decision), ct);

    public async Task<string?> RetireTemplateAsync(string kind, string id, string version, DecisionRecord? decision, CancellationToken ct) =>
        await ChangeTemplatesAsync(async (connection, transaction) =>
        {
            var role = await RoleOfAsync(connection, transaction, kind, id, version, ct);
            if (role is null)
                return $"{id} v{version} is not stored for {kind}";
            if (role == TemplateRoles.Retired)
                return $"{id} v{version} is already retired";
            await SetRoleAsync(connection, transaction, "UPDATE templates SET role = 'retired' WHERE kind = $kind AND template_id = $id AND version = $version", kind, id, version, ct);
            return null;
        }, ("retire", kind, id, version, decision), ct);

    private async Task<string?> ChangeTemplatesAsync(
        Func<SqliteConnection, SqliteTransaction, Task<string?>> change, (string Action, string Kind, string Id, string Version, DecisionRecord? Decision) done, CancellationToken ct)
    {
        if (!File.Exists(path))
            return "there is no store file yet";
        var (connection, version) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        if (version < 4)
            return "the store has no template table yet; start the server once to upgrade it";
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var problem = await change(connection, transaction);
        if (problem is not null)
            return problem;
        if (version >= 6)
        {
            await using var log = connection.CreateCommand();
            log.Transaction = transaction;
            log.CommandText = "INSERT INTO template_decisions (decided_at, kind, template_id, version, action, counts, note) VALUES ($at, $kind, $id, $version, $action, $counts, $note)";
            log.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            log.Parameters.AddWithValue("$kind", done.Kind);
            log.Parameters.AddWithValue("$id", done.Id);
            log.Parameters.AddWithValue("$version", done.Version);
            log.Parameters.AddWithValue("$action", done.Action);
            log.Parameters.AddWithValue("$counts", (object?)done.Decision?.Counts ?? DBNull.Value);
            log.Parameters.AddWithValue("$note", (object?)done.Decision?.Note ?? DBNull.Value);
            await log.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return null;
    }

    private static async Task<string?> RoleOfAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, string id, string version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT role FROM templates WHERE kind = $kind AND template_id = $id AND version = $version";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<List<Template>> RetiredAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT template_id, version, kind, before_text, after_text, source FROM templates WHERE kind = $kind AND role = 'retired'";
        command.Parameters.AddWithValue("$kind", kind);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var found = new List<Template>();
        while (await reader.ReadAsync(ct))
            found.Add(new Template(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return found;
    }

    private static async Task<long> CountAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, string role, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM templates WHERE kind = $kind AND role = $role";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$role", role);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> HolderAsync(SqliteConnection connection, SqliteTransaction transaction, string kind, string role, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT template_id || ' v' || version FROM templates WHERE kind = $kind AND role = $role";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$role", role);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task SetRoleAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, string kind, string? id, string? version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$kind", kind);
        if (id is not null)
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$version", version);
        }
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> ReindexAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
            return 0;
        var (connection, _) = await OpenAsync(SqliteOpenMode.ReadWrite, ct);
        await using var _ = connection;
        // Upgrades a version 1 file first, then rebuilds the index from the rows.
        await StoreSchema.EnsureAsync(connection, ct);
        await using var rebuild = connection.CreateCommand();
        rebuild.CommandText = StoreSchema.RebuildIndex;
        await rebuild.ExecuteNonQueryAsync(ct);
        return await CountAsync(RowFilter.Everything, ct);
    }

    private async Task<(SqliteConnection Connection, int Version)> OpenAsync(SqliteOpenMode mode, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            await using var busy = connection.CreateCommand();
            busy.CommandText = $"PRAGMA busy_timeout = {BusyMilliseconds}";
            await busy.ExecuteNonQueryAsync(ct);
            var found = await StoreSchema.VersionAsync(connection, ct);
            if (found < StoreSchema.OldestReadable || found > StoreSchema.Version)
                throw new InvalidOperationException($"the store is schema version {found}; this whetstone reads {StoreSchema.OldestReadable} to {StoreSchema.Version}");
            return (connection, found);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void Bind(SqliteCommand command, RowFilter filter)
    {
        command.Parameters.AddWithValue("$repository", (object?)filter.Repository ?? DBNull.Value);
        command.Parameters.AddWithValue("$before", (object?)filter.BeforeText ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", filter.Text is null ? DBNull.Value : 1);
        command.Parameters.AddWithValue("$imported", filter.Imported ? ImportedPrefix : DBNull.Value);
        // SQLite has no regular expressions; the filter's own expression runs in .NET, row by row.
        var text = filter.Text;
        command.Connection!.CreateFunction("prompt_matches", (string prompt) => text is not null && text.IsMatch(prompt), isDeterministic: true);
    }

    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static bool? Flag(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i) != 0;

    private static double? Number(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);

    private static long? Count(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
}
