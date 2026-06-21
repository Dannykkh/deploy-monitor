using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DeployMonitor.Web.Data
{
    public class DeployHistoryStore
    {
        private readonly string _connectionString;

        /// <summary>프로젝트당 보존할 최대 이력 행 수 (장기 실행 시 무한 증가 방지)</summary>
        private const int MaxRowsPerProject = 500;

        public DeployHistoryStore(string dbPath)
        {
            _connectionString = $"Data Source={dbPath}";
        }

        public void Save(string projectName, string? commitHash, string status,
            DateTime startedAt, DateTime? finishedAt, string? logSummary, string triggerType = "auto")
        {
            double? durationSec = finishedAt.HasValue
                ? (finishedAt.Value - startedAt).TotalSeconds
                : null;

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO deploy_history (project_name, commit_hash, status, started_at, finished_at, duration_sec, log_summary, trigger_type)
                VALUES (@pn, @ch, @st, @sa, @fa, @ds, @ls, @tt)";
            cmd.Parameters.AddWithValue("@pn", projectName);
            cmd.Parameters.AddWithValue("@ch", (object?)commitHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@st", status);
            cmd.Parameters.AddWithValue("@sa", startedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@fa", finishedAt.HasValue ? finishedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
            cmd.Parameters.AddWithValue("@ds", durationSec.HasValue ? durationSec.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ls", (object?)logSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tt", triggerType);
            cmd.ExecuteNonQuery();

            PruneOldEntries(conn, projectName);
        }

        /// <summary>프로젝트당 최근 N개만 남기고 오래된 이력을 삭제한다.</summary>
        private static void PruneOldEntries(SqliteConnection conn, string projectName, int keep = MaxRowsPerProject)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM deploy_history
                WHERE project_name = @pn
                  AND id NOT IN (
                      SELECT id FROM deploy_history
                      WHERE project_name = @pn
                      ORDER BY id DESC
                      LIMIT @keep
                  )";
            cmd.Parameters.AddWithValue("@pn", projectName);
            cmd.Parameters.AddWithValue("@keep", keep);
            cmd.ExecuteNonQuery();
        }

        public List<Dictionary<string, object?>> Query(string? projectName = null, int limit = 50)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();

            var sql = "SELECT * FROM deploy_history";
            if (!string.IsNullOrEmpty(projectName))
            {
                sql += " WHERE project_name = @pn";
                cmd.Parameters.AddWithValue("@pn", projectName);
            }
            sql += " ORDER BY id DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.CommandText = sql;

            var results = new List<Dictionary<string, object?>>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add(row);
            }
            return results;
        }
    }
}
