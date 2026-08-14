using System.Globalization;
using AgenTally.Domain.Usage;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Writing;

internal static class UsageEventSql
{
    public const string Upsert = """
        INSERT INTO usage_events (
            agent_id,
            source_instance_id,
            source_entity_id,
            event_id,
            dedup_key,
            source_kind,
            occurred_at_unix_ms,
            imported_at_unix_ms,
            session_id,
            parent_session_id,
            turn_id_hash,
            project_id,
            project_path,
            project_repository_hash,
            raw_model,
            normalized_model,
            route_model_id,
            model_display_name,
            provider_id,
            model_resolution_origin,
            input_reported_value,
            input_reported_origin,
            uncached_input_value,
            uncached_input_origin,
            cache_read_value,
            cache_read_origin,
            cache_write_value,
            cache_write_origin,
            output_value,
            output_origin,
            reasoning_value,
            reasoning_origin,
            tool_value,
            tool_origin,
            reported_total_value,
            reported_total_origin,
            normalized_total_value,
            normalized_total_origin,
            cache_included_in_input,
            reasoning_included_in_output,
            completion_state,
            data_quality,
            reported_cost,
            currency,
            parser_version,
            source_fingerprint,
            source_revision
        ) VALUES (
            $agent_id,
            $source_instance_id,
            $source_entity_id,
            $event_id,
            $dedup_key,
            $source_kind,
            $occurred_at_unix_ms,
            $imported_at_unix_ms,
            $session_id,
            $parent_session_id,
            $turn_id_hash,
            $project_id,
            $project_path,
            $project_repository_hash,
            $raw_model,
            $normalized_model,
            $route_model_id,
            $model_display_name,
            $provider_id,
            $model_resolution_origin,
            $input_reported_value,
            $input_reported_origin,
            $uncached_input_value,
            $uncached_input_origin,
            $cache_read_value,
            $cache_read_origin,
            $cache_write_value,
            $cache_write_origin,
            $output_value,
            $output_origin,
            $reasoning_value,
            $reasoning_origin,
            $tool_value,
            $tool_origin,
            $reported_total_value,
            $reported_total_origin,
            $normalized_total_value,
            $normalized_total_origin,
            $cache_included_in_input,
            $reasoning_included_in_output,
            $completion_state,
            $data_quality,
            $reported_cost,
            $currency,
            $parser_version,
            $source_fingerprint,
            $source_revision
        )
        ON CONFLICT(agent_id, source_instance_id, dedup_key) DO UPDATE SET
            source_entity_id = excluded.source_entity_id,
            event_id = excluded.event_id,
            source_kind = excluded.source_kind,
            occurred_at_unix_ms = excluded.occurred_at_unix_ms,
            imported_at_unix_ms = excluded.imported_at_unix_ms,
            session_id = excluded.session_id,
            parent_session_id = excluded.parent_session_id,
            turn_id_hash = excluded.turn_id_hash,
            project_id = excluded.project_id,
            project_path = excluded.project_path,
            project_repository_hash = excluded.project_repository_hash,
            raw_model = excluded.raw_model,
            normalized_model = excluded.normalized_model,
            route_model_id = excluded.route_model_id,
            model_display_name = excluded.model_display_name,
            provider_id = excluded.provider_id,
            model_resolution_origin = excluded.model_resolution_origin,
            input_reported_value = excluded.input_reported_value,
            input_reported_origin = excluded.input_reported_origin,
            uncached_input_value = excluded.uncached_input_value,
            uncached_input_origin = excluded.uncached_input_origin,
            cache_read_value = excluded.cache_read_value,
            cache_read_origin = excluded.cache_read_origin,
            cache_write_value = excluded.cache_write_value,
            cache_write_origin = excluded.cache_write_origin,
            output_value = excluded.output_value,
            output_origin = excluded.output_origin,
            reasoning_value = excluded.reasoning_value,
            reasoning_origin = excluded.reasoning_origin,
            tool_value = excluded.tool_value,
            tool_origin = excluded.tool_origin,
            reported_total_value = excluded.reported_total_value,
            reported_total_origin = excluded.reported_total_origin,
            normalized_total_value = excluded.normalized_total_value,
            normalized_total_origin = excluded.normalized_total_origin,
            cache_included_in_input = excluded.cache_included_in_input,
            reasoning_included_in_output = excluded.reasoning_included_in_output,
            completion_state = excluded.completion_state,
            data_quality = excluded.data_quality,
            reported_cost = excluded.reported_cost,
            currency = excluded.currency,
            parser_version = excluded.parser_version,
            source_fingerprint = excluded.source_fingerprint,
            source_revision = excluded.source_revision
        WHERE
            excluded.completion_state > usage_events.completion_state
            OR (
                excluded.completion_state >= usage_events.completion_state
                AND excluded.source_revision > usage_events.source_revision
            )
            OR (
                $write_intent = 1
                AND excluded.completion_state >= usage_events.completion_state
                AND excluded.parser_version <> usage_events.parser_version
            );
        """;

    public static void Bind(
        SqliteCommand command,
        UsageEvent value,
        WriteIntent intent)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(value);

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$agent_id", value.AgentId);
        command.Parameters.AddWithValue("$source_instance_id", value.SourceInstanceId);
        command.Parameters.AddWithValue("$source_entity_id", value.SourceEntityId);
        command.Parameters.AddWithValue("$event_id", value.EventId);
        command.Parameters.AddWithValue("$dedup_key", value.DedupKey);
        command.Parameters.AddWithValue("$source_kind", (int)value.SourceKind);
        command.Parameters.AddWithValue(
            "$occurred_at_unix_ms",
            value.OccurredAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$imported_at_unix_ms",
            value.ImportedAtUtc.ToUnixTimeMilliseconds());
        AddNullable(command, "$session_id", value.SessionId);
        AddNullable(command, "$parent_session_id", value.ParentSessionId);
        AddNullable(command, "$turn_id_hash", value.TurnIdHash);
        AddNullable(command, "$project_id", value.ProjectId);
        AddNullable(command, "$project_path", value.ProjectPath);
        AddNullable(
            command,
            "$project_repository_hash",
            value.ProjectRepositoryIdentityHash);
        AddNullable(command, "$raw_model", value.Model.RawModel);
        AddNullable(
            command,
            "$normalized_model",
            ModelIdentityCanonicalizer.Canonicalize(
                value.Model.NormalizedModel,
                value.AgentId,
                value.Model.ProviderId));
        AddNullable(command, "$route_model_id", value.Model.RouteModelId);
        AddNullable(command, "$model_display_name", value.Model.DisplayName);
        AddNullable(command, "$provider_id", value.Model.ProviderId);
        command.Parameters.AddWithValue(
            "$model_resolution_origin",
            (int)value.Model.ResolutionOrigin);
        AddMetric(command, "input_reported", value.Tokens.InputReported);
        AddMetric(command, "uncached_input", value.Tokens.UncachedInput);
        AddMetric(command, "cache_read", value.Tokens.CacheRead);
        AddMetric(command, "cache_write", value.Tokens.CacheWrite);
        AddMetric(command, "output", value.Tokens.Output);
        AddMetric(command, "reasoning", value.Tokens.Reasoning);
        AddMetric(command, "tool", value.Tokens.Tool);
        AddMetric(command, "reported_total", value.Tokens.ReportedTotal);
        AddMetric(command, "normalized_total", value.Tokens.NormalizedTotal);
        command.Parameters.AddWithValue(
            "$cache_included_in_input",
            (int)value.Tokens.CacheIncludedInInput);
        command.Parameters.AddWithValue(
            "$reasoning_included_in_output",
            (int)value.Tokens.ReasoningIncludedInOutput);
        command.Parameters.AddWithValue("$completion_state", (int)value.CompletionState);
        command.Parameters.AddWithValue("$data_quality", (int)value.DataQuality);
        AddNullable(
            command,
            "$reported_cost",
            value.ReportedCost?.ToString(CultureInfo.InvariantCulture));
        AddNullable(command, "$currency", value.Currency);
        command.Parameters.AddWithValue("$parser_version", value.ParserVersion);
        command.Parameters.AddWithValue("$source_fingerprint", value.SourceFingerprint);
        command.Parameters.AddWithValue("$source_revision", value.SourceRevision);
        command.Parameters.AddWithValue("$write_intent", (int)intent);
    }

    private static void AddMetric(
        SqliteCommand command,
        string name,
        TokenMetric metric)
    {
        AddNullable(command, $"${name}_value", metric.Value);
        command.Parameters.AddWithValue($"${name}_origin", (int)metric.Origin);
    }

    private static void AddNullable(
        SqliteCommand command,
        string name,
        object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
