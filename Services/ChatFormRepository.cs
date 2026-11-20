using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Diagnostics;
﻿
using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Medicount.Chatbot.Services
{
    public sealed record ChatFormSubmission(long SubmissionId,string SessionId,string FormId,Dictionary<string, string> Values,string ReviewTokenHash,DateTime SubmittedAt );

    public sealed class ChatFormRepository
    {
        private readonly Lazy<string> connectionString;
        private readonly AppDiagnostics? diagnostics;

        public ChatFormRepository(IConfiguration configuration, AppDiagnostics? diagnostics = null)
        {
            connectionString =
                new Lazy<string>(() => SqlSettings.ConnectionString(configuration));
            this.diagnostics = diagnostics;
        }

        private Task<T> Trace<T>(string operation, Func<Task<T>> action) =>
            diagnostics is null ? action() : diagnostics.RunAsync(operation, action);

        private Task<SqlConnection> OpenConnection(string operation, CancellationToken cancellationToken) =>
            Trace(operation, async () =>
            {
                var connection = new SqlConnection(connectionString.Value);
                try
                {
                    await connection.OpenAsync(cancellationToken);
                    return connection;
                }
                catch
                {
                    await connection.DisposeAsync();
                    throw;
                }
            });

        // INSERT if new; UPDATE if already present.
        // Call only after successful server-side form confirmation.
        public async Task SaveAsync(
            string sessionId,
            string formId,
            Dictionary<string, string> values,
            string validatedReviewTokenHash,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SET XACT_ABORT ON;

                BEGIN TRANSACTION;

                IF EXISTS (
                    SELECT 1
                    FROM dbo.ChatFormSubmissions
                         WITH (UPDLOCK, HOLDLOCK)
                    WHERE SessionId = @SessionId
                      AND FormId = @FormId
                )
                BEGIN
                    UPDATE dbo.ChatFormSubmissions
                    SET FormData = @FormData,
                        ReviewTokenHash = @ReviewTokenHash
                    WHERE SessionId = @SessionId
                      AND FormId = @FormId;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.ChatFormSubmissions
                        (SessionId, FormId, FormData, ReviewTokenHash)
                    VALUES
                        (@SessionId, @FormId, @FormData,
                         @ReviewTokenHash);
                END

                COMMIT TRANSACTION;";

            await using var connection = await OpenConnection("sql.forms.save.connection_open", cancellationToken);

            await using var command =
                new SqlCommand(sql, connection);

            AddParameters(command,sessionId,formId,values,validatedReviewTokenHash);

            await Trace("sql.forms.save.query", () => command.ExecuteNonQueryAsync(cancellationToken));
        }

        // SELECT existing submission.
        public async Task<ChatFormSubmission?> GetAsync(
            string sessionId,
            string formId,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                SELECT SubmissionId,
                       SessionId,
                       FormId,
                       FormData,
                       ReviewTokenHash,
                       SubmittedAt
                FROM dbo.ChatFormSubmissions
                WHERE SessionId = @SessionId
                  AND FormId = @FormId;";

            await using var connection = await OpenConnection("sql.forms.get.connection_open", cancellationToken);

            await using var command =
                new SqlCommand(sql, connection);

            command.Parameters.Add(
                "@SessionId", SqlDbType.NVarChar, 100
            ).Value = sessionId;

            command.Parameters.Add(
                "@FormId", SqlDbType.NVarChar, 100
            ).Value = formId;

            return await Trace<ChatFormSubmission?>("sql.forms.get.query", async () =>
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return null;
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3)) ?? new();
                return new ChatFormSubmission(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    values, reader.GetString(4), reader.GetDateTime(5));
            });
        }

        // UPDATE only an existing submission.
        public async Task<bool> UpdateAsync(
            string sessionId,
            string formId,
            Dictionary<string, string> values,
            string validatedReviewTokenHash,
            CancellationToken cancellationToken = default)
        {
            const string sql = @"
                UPDATE dbo.ChatFormSubmissions
                SET FormData = @FormData,
                    ReviewTokenHash = @ReviewTokenHash
                WHERE SessionId = @SessionId
                  AND FormId = @FormId;";

            await using var connection = await OpenConnection("sql.forms.update.connection_open", cancellationToken);

            await using var command =
                new SqlCommand(sql, connection);

            AddParameters(
                command,
                sessionId,
                formId,
                values,
                validatedReviewTokenHash
            );

            int rowsAffected = await Trace("sql.forms.update.query", () => command.ExecuteNonQueryAsync(cancellationToken));

            return rowsAffected > 0;
        }

        // Reuse SQL parameters for INSERT and UPDATE.
        private static void AddParameters(
            SqlCommand command,
            string sessionId,
            string formId,
            Dictionary<string, string> values,
            string reviewTokenHash)
        {
            command.Parameters.Add(
                "@SessionId", SqlDbType.NVarChar, 100
            ).Value = sessionId;

            command.Parameters.Add(
                "@FormId", SqlDbType.NVarChar, 100
            ).Value = formId;

            command.Parameters.Add(
                "@FormData", SqlDbType.NVarChar, -1
            ).Value = JsonSerializer.Serialize(values);

            command.Parameters.Add(
                "@ReviewTokenHash", SqlDbType.Char, 64
            ).Value = reviewTokenHash;
        }
    }
}


