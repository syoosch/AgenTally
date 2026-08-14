using AgenTally.Domain.Usage;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Writing;

internal static class UsageTurnSql
{
    public const string UpsertTurn = """
        INSERT INTO usage_turns (
            agent_id,
            source_instance_id,
            source_entity_id,
            session_id,
            turn_id_hash,
            started_at_unix_ms,
            completed_at_unix_ms,
            prompt_preview,
            user_message_count,
            parser_version,
            prompt_origin_turn_id_hash
        ) VALUES (
            $agent_id,
            $source_instance_id,
            $source_entity_id,
            $session_id,
            $turn_id_hash,
            $started_at_unix_ms,
            $completed_at_unix_ms,
            $prompt_preview,
            $user_message_count,
            $parser_version,
            $prompt_origin_turn_id_hash
        )
        ON CONFLICT(
            agent_id,
            source_instance_id,
            session_id,
            turn_id_hash) DO UPDATE SET
            source_entity_id = excluded.source_entity_id,
            started_at_unix_ms = MIN(
                usage_turns.started_at_unix_ms,
                excluded.started_at_unix_ms),
            completed_at_unix_ms = CASE
                WHEN usage_turns.completed_at_unix_ms IS NULL
                THEN excluded.completed_at_unix_ms
                WHEN excluded.completed_at_unix_ms IS NULL
                THEN usage_turns.completed_at_unix_ms
                ELSE MAX(
                    usage_turns.completed_at_unix_ms,
                    excluded.completed_at_unix_ms)
            END,
            prompt_preview = COALESCE(
                usage_turns.prompt_preview,
                excluded.prompt_preview),
            user_message_count = MAX(
                usage_turns.user_message_count,
                excluded.user_message_count),
            parser_version = excluded.parser_version,
            prompt_origin_turn_id_hash = COALESCE(
                excluded.prompt_origin_turn_id_hash,
                usage_turns.prompt_origin_turn_id_hash);
        """;

    public const string UpsertTool = """
        INSERT INTO usage_event_tools (
            agent_id,
            source_instance_id,
            source_entity_id,
            event_dedup_key,
            ordinal,
            tool_name,
            parser_version
        ) VALUES (
            $agent_id,
            $source_instance_id,
            $source_entity_id,
            $event_dedup_key,
            $ordinal,
            $tool_name,
            $parser_version
        )
        ON CONFLICT(
            agent_id,
            source_instance_id,
            event_dedup_key,
            ordinal) DO UPDATE SET
            source_entity_id = excluded.source_entity_id,
            tool_name = excluded.tool_name,
            parser_version = excluded.parser_version;
        """;

    public const string UpsertDispatch = """
        INSERT INTO usage_turn_dispatches (
            agent_id,
            source_instance_id,
            source_entity_id,
            source_session_id,
            source_turn_id_hash,
            dispatch_id_hash,
            target_agent_hash,
            dispatch_kind,
            target_kind,
            occurred_at_unix_ms,
            parser_version
        ) VALUES (
            $agent_id,
            $source_instance_id,
            $source_entity_id,
            $source_session_id,
            $source_turn_id_hash,
            $dispatch_id_hash,
            $target_agent_hash,
            $dispatch_kind,
            $target_kind,
            $occurred_at_unix_ms,
            $parser_version
        )
        ON CONFLICT(
            agent_id,
            source_instance_id,
            dispatch_id_hash) DO UPDATE SET
            source_entity_id = excluded.source_entity_id,
            source_session_id = excluded.source_session_id,
            source_turn_id_hash = excluded.source_turn_id_hash,
            target_agent_hash = excluded.target_agent_hash,
            dispatch_kind = excluded.dispatch_kind,
            target_kind = excluded.target_kind,
            occurred_at_unix_ms = excluded.occurred_at_unix_ms,
            parser_version = excluded.parser_version;
        """;

    public static void BindTurn(SqliteCommand command, UsageTurnMetadata value)
    {
        command.Parameters.Clear();
        AddCommon(command, value.AgentId, value.SourceInstanceId, value.SourceEntityId);
        command.Parameters.AddWithValue("$session_id", value.SessionId);
        command.Parameters.AddWithValue("$turn_id_hash", value.TurnIdHash);
        command.Parameters.AddWithValue(
            "$started_at_unix_ms",
            value.StartedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$completed_at_unix_ms",
            value.CompletedAtUtc.HasValue
                ? value.CompletedAtUtc.Value.ToUnixTimeMilliseconds()
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$prompt_preview",
            (object?)value.PromptPreview ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$user_message_count",
            value.UserMessageCount);
        command.Parameters.AddWithValue("$parser_version", value.ParserVersion);
        command.Parameters.AddWithValue(
            "$prompt_origin_turn_id_hash",
            (object?)value.PromptOriginTurnIdHash ?? DBNull.Value);
    }

    public static void BindTool(
        SqliteCommand command,
        UsageEventToolMetadata value)
    {
        command.Parameters.Clear();
        AddCommon(command, value.AgentId, value.SourceInstanceId, value.SourceEntityId);
        command.Parameters.AddWithValue("$event_dedup_key", value.EventDedupKey);
        command.Parameters.AddWithValue("$ordinal", value.Ordinal);
        command.Parameters.AddWithValue("$tool_name", value.ToolName);
        command.Parameters.AddWithValue("$parser_version", value.ParserVersion);
    }

    public static void BindDispatch(
        SqliteCommand command,
        UsageTurnDispatch value)
    {
        command.Parameters.Clear();
        AddCommon(command, value.AgentId, value.SourceInstanceId, value.SourceEntityId);
        command.Parameters.AddWithValue(
            "$source_session_id",
            value.SourceSessionId);
        command.Parameters.AddWithValue(
            "$source_turn_id_hash",
            value.SourceTurnIdHash);
        command.Parameters.AddWithValue("$dispatch_id_hash", value.DispatchIdHash);
        command.Parameters.AddWithValue("$target_agent_hash", value.TargetAgentHash);
        command.Parameters.AddWithValue("$dispatch_kind", (int)value.DispatchKind);
        command.Parameters.AddWithValue("$target_kind", (int)value.TargetKind);
        command.Parameters.AddWithValue(
            "$occurred_at_unix_ms",
            value.OccurredAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$parser_version", value.ParserVersion);
    }

    private static void AddCommon(
        SqliteCommand command,
        string agentId,
        string sourceInstanceId,
        string sourceEntityId)
    {
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
        command.Parameters.AddWithValue("$source_entity_id", sourceEntityId);
    }
}
