using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Domain;
using Medicount.Chatbot.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Medicount.Chatbot.Services;

public class PatientVerificationRepository
{
    private readonly Lazy<string> connectionString;
    private readonly AppDiagnostics? diagnostics;

    public PatientVerificationRepository(IConfiguration config, AppDiagnostics? diagnostics = null)
    {
        connectionString = new Lazy<string>(() => SqlSettings.ConnectionString(config));
        this.diagnostics = diagnostics;
    }

    private Task<T> Trace<T>(string operation, Func<Task<T>> action) =>
        diagnostics is null ? action() : diagnostics.RunAsync(operation, action);

    private Task Trace(string operation, Func<Task> action) =>
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

    public virtual async Task<Verified?> VerifyPatientAsync(
        string name, string dob, string account, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 150 ||
            string.IsNullOrWhiteSpace(account) || account.Trim().Length > 50 ||
            !DateTime.TryParseExact(dob?.Trim(), new[] { "MM/dd/yyyy", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDob) ||
            parsedDob.Date > DateTime.UtcNow.Date)
        {
            diagnostics?.Write("sql.patient_verification", "invalid_input");
            return null;
        }

        await using var connection = await OpenConnection("sql.patient_verification.connection_open", cancellationToken);
        await using var command = new SqlCommand("dbo.usp_VerifyPatientIdentity", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 300
        };
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = name.Trim();
        command.Parameters.Add("@DOB", SqlDbType.Date).Value = parsedDob.Date;
        command.Parameters.Add("@PatientID", SqlDbType.NVarChar, 50).Value = account.Trim();
        var result = await Trace<Verified?>("sql.patient_verification.query", async () =>
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;

            var clientId = Convert.ToString(reader["ClientId"], CultureInfo.InvariantCulture);
            var accountKey = Convert.ToString(reader["PatientId"], CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(accountKey) || reader["Placed"] is DBNull)
                throw new InvalidOperationException("Patient verification returned an incomplete record.");
            var verified = new Verified(clientId, accountKey, Convert.ToBoolean(reader["Placed"], CultureInfo.InvariantCulture));
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Patient verification returned multiple records.");
            return verified;
        });
        diagnostics?.Write("sql.patient_verification", result is null ? "no_match" : "match");
        return result;
    }

    // Balance verification is independent of the legacy verification stored procedure.
    // The statement account number maps to PatientID in the supplied IVR table.
    public virtual async Task<Verified?> VerifyBalancePatientAsync(string name, string dob, string account, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 150 ||
            string.IsNullOrWhiteSpace(account) || account.Trim().Length > 50 ||
            !DateTime.TryParseExact(dob?.Trim(), new[] { "MM/dd/yyyy", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDob) ||
            parsedDob.Year < 1850 || parsedDob.Date > DateTime.UtcNow.Date)
            return null;

        await using var connection = await OpenConnection("sql.balance_verification.connection_open", cancellationToken);
        //await using var command = new SqlCommand(@"
        //    SELECT TOP (2) ClientID, PatientID, IsInCollections
        //    FROM dbo.tblMediIVRPatient
        //    WHERE CONVERT(nvarchar(50), PatientID) = @PatientID
        //      AND LOWER(LTRIM(RTRIM(PatientName))) = LOWER(@Name)
        //      AND DOB >= @DOB AND DOB < DATEADD(day, 1, @DOB);",
        //      connection) { CommandTimeout = 30 }; "dbo.USP_tblMediIVRPatient_Select",
        await using var command = new SqlCommand(
                        "dbo.usp_VerifyPatientIdentity", connection)
                            {
                                CommandType = CommandType.StoredProcedure,
                                CommandTimeout = 300
                            };
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = name.Trim();
        command.Parameters.Add("@PatientID", SqlDbType.NVarChar, 50).Value = account.Trim();
        command.Parameters.Add("@DOB", SqlDbType.Date).Value = parsedDob.Date;
        return await Trace<Verified?>("sql.balance_verification.query", async () =>
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                throw new InvalidOperationException("Balance verification returned an incomplete record.");
            var identity = new Verified(
                Convert.ToString(reader[0], CultureInfo.InvariantCulture)!,
                Convert.ToString(reader[1], CultureInfo.InvariantCulture)!,
                Convert.ToBoolean(reader[2], CultureInfo.InvariantCulture));
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Balance verification returned multiple records.");
            return identity;
        });
    }

    public virtual async Task<decimal?> GetAccountBalanceAsync(Verified identity, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnection("sql.account_balance.connection_open", cancellationToken);
        await using var command = new SqlCommand(
            "[dbo].[USP_tblMediIVRPatient_Balance]", connection) 
        { 
            CommandType = CommandType.StoredProcedure, 
            CommandTimeout = 300 
        };
        command.Parameters.Add("@PatientID", SqlDbType.NVarChar, 100).Value = identity.AccountKey;
        command.Parameters.Add("@ClientId", SqlDbType.NVarChar, 50).Value = identity.ClientId;
        return await Trace<decimal?>("sql.account_balance.query", async () =>
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            if (reader.IsDBNull(1) || Convert.ToBoolean(reader[1], CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Account placement changed; verify the account again.");
            decimal? balance = null;
            if (!reader.IsDBNull(0))
            {
                if (!decimal.TryParse(Convert.ToString(reader[0], CultureInfo.InvariantCulture),
                        NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                    throw new InvalidOperationException("Account balance is invalid.");
                balance = amount;
            }
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Account balance returned multiple records.");
            return balance;
        });
    }

    public virtual async Task SaveInsuranceUpdateRequestAsync(
        string clientId, string accountKey, string insuranceCompany, string memberId,
        string? groupNumber, CancellationToken cancellationToken = default, string? requestId = null)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 50 ||
            string.IsNullOrWhiteSpace(accountKey) || accountKey.Length > 100 ||
            string.IsNullOrWhiteSpace(insuranceCompany) || insuranceCompany.Length > 150 ||
            string.IsNullOrWhiteSpace(memberId) || memberId.Length > 100 || groupNumber?.Length > 100)
        {
            diagnostics?.Write("sql.insurance", "invalid_input");
            throw new ArgumentException("Insurance update fields are invalid.");
        }
        Guid requestKey = default;
        if (requestId is not null && (!Guid.TryParseExact(requestId, "D", out requestKey) || requestKey == Guid.Empty))
        {
            diagnostics?.Write("sql.insurance", "invalid_request_key");
            throw new ArgumentException("Insurance request ID must be a nonempty GUID.");
        }
        var group = string.IsNullOrWhiteSpace(groupNumber) ? null : groupNumber.Trim();
        await using var connection = await OpenConnection("sql.insurance.connection_open", cancellationToken);
        await using var command = new SqlCommand("dbo.usp_SaveInsuranceUpdateRequest", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        command.Parameters.Add("@ClientId", SqlDbType.NVarChar, 50).Value = clientId;
        command.Parameters.Add("@AccountKey", SqlDbType.NVarChar, 100).Value = accountKey;
        command.Parameters.Add("@InsuranceCompany", SqlDbType.NVarChar, 150).Value = insuranceCompany.Trim();
        command.Parameters.Add("@MemberId", SqlDbType.NVarChar, 100).Value = memberId.Trim();
        command.Parameters.Add("@GroupNumber", SqlDbType.NVarChar, 100).Value =
            (object?)group ?? DBNull.Value;

        // Keep the original controller contract; the chatbot supplies its stable
        // form ID so network retries cannot create duplicate insurance requests.
        if (requestId is null)
        {
            await Trace("sql.insurance.save_query", () => command.ExecuteNonQueryAsync(cancellationToken));
            diagnostics?.Write("sql.insurance", "saved");
            return;
        }

        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new[] { clientId, accountKey, insuranceCompany.Trim(), memberId.Trim(), group })));
        await using var transaction = (SqlTransaction)await Trace("sql.insurance.transaction_begin", () =>
            connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).AsTask());
        command.Transaction = transaction;
        await using var gate = new SqlCommand(@"
            SELECT ClientId, AccountKey, PayloadHash
            FROM dbo.InsuranceRequestKeys WITH (UPDLOCK, HOLDLOCK)
            WHERE RequestId = @RequestId;", connection, transaction) { CommandTimeout = 30 };
        gate.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = requestKey;
        var existing = await Trace("sql.insurance.key_lookup", async () =>
        {
            await using var reader = await gate.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!string.Equals(reader.GetString(0), clientId, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(1), accountKey, StringComparison.Ordinal) ||
                    !CryptographicOperations.FixedTimeEquals((byte[])reader[2], payloadHash))
                {
                    diagnostics?.Write("sql.insurance", "conflict");
                    throw new RequestError(409, "REQUEST_CONFLICT");
                }
                return true;
            }
            return false;
        });
        if (existing)
        {
            await Trace("sql.insurance.commit", () => transaction.CommitAsync(cancellationToken));
            diagnostics?.Write("sql.insurance", "deduplicated");
            return;
        }

        // The range lock remains held until both the business write and its
        // deduplication key commit. Disposal rolls both back on any failure.
        await Trace("sql.insurance.save_query", () => command.ExecuteNonQueryAsync(cancellationToken));
        await using var record = new SqlCommand(@"
            INSERT INTO dbo.InsuranceRequestKeys (RequestId, ClientId, AccountKey, PayloadHash)
            VALUES (@RequestId, @ClientId, @AccountKey, @PayloadHash);",
            connection, transaction) { CommandTimeout = 30 };
        record.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = requestKey;
        record.Parameters.Add("@ClientId", SqlDbType.NVarChar, 50).Value = clientId;
        record.Parameters.Add("@AccountKey", SqlDbType.NVarChar, 100).Value = accountKey;
        record.Parameters.Add("@PayloadHash", SqlDbType.Binary, 32).Value = payloadHash;
        await Trace("sql.insurance.key_record", () => record.ExecuteNonQueryAsync(cancellationToken));
        await Trace("sql.insurance.commit", () => transaction.CommitAsync(cancellationToken));
        diagnostics?.Write("sql.insurance", "saved");
    }
}
