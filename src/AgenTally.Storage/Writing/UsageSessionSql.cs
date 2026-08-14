using AgenTally.Domain.Usage;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Writing;

internal static class UsageSessionSql
{
    public const string Upsert = """
        INSERT INTO usage_sessions (
            agent_id,
            source_instance_id,
            session_id,
            source_entity_id,
            session_kind,
            direct_parent_session_id,
            forked_from_session_id,
            relation_origin,
            relation_state,
            replay_state,
            compatibility_level,
            session_role,
            agent_path_hash,
            agent_leaf_hash,
            project_id,
            project_path,
            project_repository_hash,
            session_name,
            session_name_updated_unix_ms,
            first_observed_unix_ms,
            last_observed_unix_ms,
            parser_version
        ) VALUES (
            $agent_id,
            $source_instance_id,
            $session_id,
            $source_entity_id,
            $session_kind,
            $direct_parent_session_id,
            $forked_from_session_id,
            $relation_origin,
            $relation_state,
            $replay_state,
            $compatibility_level,
            $session_role,
            $agent_path_hash,
            $agent_leaf_hash,
            $project_id,
            $project_path,
            $project_repository_hash,
            $session_name,
            $session_name_updated_unix_ms,
            $observed_at_unix_ms,
            $observed_at_unix_ms,
            $parser_version
        )
        ON CONFLICT(agent_id, source_instance_id, session_id) DO UPDATE SET
            source_entity_id = CASE
                WHEN excluded.last_observed_unix_ms >=
                     usage_sessions.last_observed_unix_ms
                THEN excluded.source_entity_id
                ELSE usage_sessions.source_entity_id
            END,
            session_kind = CASE
                WHEN usage_sessions.session_kind = 2
                  OR excluded.session_kind = 2
                THEN 2
                WHEN excluded.session_kind = 0
                THEN usage_sessions.session_kind
                ELSE excluded.session_kind
            END,
            direct_parent_session_id = CASE
                WHEN usage_sessions.relation_state = 2
                  OR excluded.relation_state = 2
                THEN NULL
                WHEN usage_sessions.relation_state = 1
                 AND excluded.relation_state = 1
                 AND usage_sessions.direct_parent_session_id <>
                     excluded.direct_parent_session_id
                THEN NULL
                WHEN excluded.relation_state = 1
                THEN excluded.direct_parent_session_id
                ELSE usage_sessions.direct_parent_session_id
            END,
            forked_from_session_id = CASE
                WHEN usage_sessions.forked_from_session_id IS NULL
                THEN excluded.forked_from_session_id
                WHEN excluded.forked_from_session_id IS NULL
                THEN usage_sessions.forked_from_session_id
                WHEN usage_sessions.forked_from_session_id =
                     excluded.forked_from_session_id
                THEN usage_sessions.forked_from_session_id
                ELSE NULL
            END,
            relation_origin = CASE
                WHEN usage_sessions.relation_state = 2
                  OR excluded.relation_state = 2
                THEN 0
                WHEN usage_sessions.relation_state = 1
                 AND excluded.relation_state = 1
                 AND usage_sessions.direct_parent_session_id <>
                     excluded.direct_parent_session_id
                THEN 0
                WHEN excluded.relation_state = 1
                THEN excluded.relation_origin
                ELSE usage_sessions.relation_origin
            END,
            relation_state = CASE
                WHEN usage_sessions.relation_state = 2
                  OR excluded.relation_state = 2
                THEN 2
                WHEN usage_sessions.relation_state = 1
                 AND excluded.relation_state = 1
                 AND usage_sessions.direct_parent_session_id <>
                     excluded.direct_parent_session_id
                THEN 2
                WHEN excluded.relation_state = 1
                THEN 1
                ELSE usage_sessions.relation_state
            END,
            replay_state = CASE
                WHEN excluded.last_observed_unix_ms >=
                     usage_sessions.last_observed_unix_ms
                THEN excluded.replay_state
                ELSE usage_sessions.replay_state
            END,
            compatibility_level = MAX(
                usage_sessions.compatibility_level,
                excluded.compatibility_level,
                CASE
                    WHEN usage_sessions.relation_state = 1
                     AND excluded.relation_state = 1
                     AND usage_sessions.direct_parent_session_id <>
                         excluded.direct_parent_session_id
                    THEN 1
                    WHEN usage_sessions.forked_from_session_id IS NOT NULL
                     AND excluded.forked_from_session_id IS NOT NULL
                     AND usage_sessions.forked_from_session_id <>
                         excluded.forked_from_session_id
                    THEN 1
                    ELSE 0
                END
            ),
            session_role = CASE
                WHEN excluded.session_role = 0
                THEN usage_sessions.session_role
                ELSE excluded.session_role
            END,
            agent_path_hash = COALESCE(
                excluded.agent_path_hash,
                usage_sessions.agent_path_hash),
            agent_leaf_hash = COALESCE(
                excluded.agent_leaf_hash,
                usage_sessions.agent_leaf_hash),
            project_id = COALESCE(excluded.project_id, usage_sessions.project_id),
            project_path = COALESCE(excluded.project_path, usage_sessions.project_path),
            project_repository_hash = COALESCE(
                excluded.project_repository_hash,
                usage_sessions.project_repository_hash),
            session_name = CASE
                WHEN excluded.session_name_updated_unix_ms IS NOT NULL
                 AND (
                     usage_sessions.session_name_updated_unix_ms IS NULL
                     OR excluded.session_name_updated_unix_ms >=
                        usage_sessions.session_name_updated_unix_ms
                 )
                THEN excluded.session_name
                ELSE usage_sessions.session_name
            END,
            session_name_updated_unix_ms = CASE
                WHEN excluded.session_name_updated_unix_ms IS NOT NULL
                 AND (
                     usage_sessions.session_name_updated_unix_ms IS NULL
                     OR excluded.session_name_updated_unix_ms >=
                        usage_sessions.session_name_updated_unix_ms
                 )
                THEN excluded.session_name_updated_unix_ms
                ELSE usage_sessions.session_name_updated_unix_ms
            END,
            first_observed_unix_ms = MIN(
                usage_sessions.first_observed_unix_ms,
                excluded.first_observed_unix_ms
            ),
            last_observed_unix_ms = MAX(
                usage_sessions.last_observed_unix_ms,
                excluded.last_observed_unix_ms
            ),
            parser_version = CASE
                WHEN excluded.last_observed_unix_ms >=
                     usage_sessions.last_observed_unix_ms
                THEN excluded.parser_version
                ELSE usage_sessions.parser_version
            END;
        """;

    public static void Bind(
        SqliteCommand command,
        UsageSessionMetadata value)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(value);

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$agent_id", value.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            value.SourceInstanceId);
        command.Parameters.AddWithValue("$session_id", value.SessionId);
        command.Parameters.AddWithValue(
            "$source_entity_id",
            value.SourceEntityId);
        command.Parameters.AddWithValue("$session_kind", (int)value.SessionKind);
        AddNullable(
            command,
            "$direct_parent_session_id",
            value.DirectParentSessionId);
        AddNullable(
            command,
            "$forked_from_session_id",
            value.ForkedFromSessionId);
        command.Parameters.AddWithValue(
            "$relation_origin",
            (int)value.RelationOrigin);
        command.Parameters.AddWithValue(
            "$relation_state",
            (int)value.RelationState);
        command.Parameters.AddWithValue("$replay_state", (int)value.ReplayState);
        command.Parameters.AddWithValue(
            "$compatibility_level",
            (int)value.CompatibilityLevel);
        command.Parameters.AddWithValue("$session_role", (int)value.SessionRole);
        AddNullable(command, "$agent_path_hash", value.AgentPathHash);
        AddNullable(command, "$agent_leaf_hash", value.AgentLeafHash);
        AddNullable(command, "$project_id", value.ProjectId);
        AddNullable(command, "$project_path", value.ProjectPath);
        AddNullable(
            command,
            "$project_repository_hash",
            value.ProjectRepositoryIdentityHash);
        AddNullable(command, "$session_name", value.SessionName);
        AddNullable(
            command,
            "$session_name_updated_unix_ms",
            value.SessionNameUpdatedAtUtc?.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$observed_at_unix_ms",
            value.ObservedAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$parser_version", value.ParserVersion);
    }

    private static void AddNullable(
        SqliteCommand command,
        string name,
        object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
