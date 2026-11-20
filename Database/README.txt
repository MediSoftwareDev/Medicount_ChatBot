Database setup and verification

These files are deployment artifacts. The chatbot does not run them at startup.
Use the database selected by ConnectionStrings__ChatDb. Verification, chat state,
and submitted forms must use that same connection configuration.

Existing installation
1. Run 005_verify_contract_read_only.sql as the database owner. It reads metadata
   and effective permissions only; it does not query patient records or execute
   either patient procedure.
2. Run 003_chat_form_submissions.sql if the form-submission table is absent. It is
   repeatable and leaves an existing table intact. Compare existing column types,
   defaults and the unique (SessionId, FormId) index with the supplied definition;
   existing installations with incompatible schemas require a forward migration.
3. If insurance requests are supported and the objects are absent, run
   004_optional_insurance_requests.sql. It creates missing objects only and never
   replaces an existing insurance stored procedure. A pending request is stored;
   this does not apply insurance changes to a clinical/billing account.
4. Apply 006_insurance_request_idempotency.sql before deploying chatbot insurance
   submissions. It creates a deduplication table and is repeatable. It neither
   rewrites existing insurance requests nor changes the insurance procedure.
5. Grant the application database user only the object permissions listed by 005.
   Repeat 005 using that user to confirm effective permissions. A successful run
   under a DBA account does not demonstrate the website account has permission.

New dedicated chatbot database
1. A DBA creates the database and application login/user.
2. Apply 001_new_database_only.sql exactly once. Its tables are the session,
   transcript, event, verification-attempt, incident, outbox, and policy schema.
3. 002_optional_setup_new_database_only.sql is an export of the supplied optional
   policy/evidence administration migration. Apply once after 001 only if those
   administration features are needed. It is not needed for patient verification.
   Do not run 001 or 002 against an existing schema: they use CREATE/ALTER rather
   than an upgrade/deduplication strategy and can fail on existing objects.
4. Apply 003 and 006 and, when appropriate, 004. Supply the verification procedure below.
5. Grant runtime permissions and run 005. Use SSMS or sqlcmd with a selected target
   database; GO batch separators are intentional. Stop on SQL errors. With sqlcmd,
   use -b so a failing batch does not allow later batches to continue.

Missing patient verification contract
The archive does not contain dbo.usp_VerifyPatientIdentity or the clinical table
schema needed to implement it. A DBA must provide it in the configured database,
using the actual source of patient identity and placement status. No procedure
that accepts arbitrary patients or returns mock success has been added.

Inputs expected by PatientVerificationRepository:
  @Name NVARCHAR(150)
  @DOB DATE
  @AccountNumber NVARCHAR(50)
Expected result: zero rows for no match; exactly one row for a unique match with
nonempty ClientId, nonempty AccountKey, and a non-null Placed value convertible to
BIT/Boolean. The first result set must be this result; do not emit an earlier
diagnostic SELECT. Match all three supplied identity fields. Duplicate matches
must not select an arbitrary patient. Return only the required identifiers and
placement flag. Do not return patient details to this diagnostic script.

Grant expectations
The runtime needs SELECT/INSERT/UPDATE on sessions and escalations; SELECT/INSERT
on messages; INSERT on events, verification_attempts and incidents;
SELECT/INSERT/UPDATE on ChatFormSubmissions; SELECT/INSERT on InsuranceRequestKeys;
and EXECUTE on the two patient
procedures. It does not need db_owner or schema modification permissions. Use
object grants to the real application database user, for example:
  GRANT EXECUTE ON OBJECT::dbo.usp_VerifyPatientIdentity TO [your_app_user];
For access to a clinical database through a procedure, the DBA must configure
the appropriate ownership/signing permissions; do not broadly grant clinical
table access solely to make the website connect.

Insurance retries
The chatbot sends its GUID form ID as the insurance request key. The repository
holds a SQL key-range lock and runs the existing insurance procedure plus the key
insert in one transaction. Matching retries skip the procedure; a reused key with
a different account or payload is rejected. The insurance procedure must preserve
the caller's transaction and perform its database writes in that transaction; it
must not commit the caller's transaction or perform irreversible external work.
Keep key rows for at least as long as a request can be replayed. The legacy patient
controller remains compatible without a request key and is not deduplicated.
Test concurrent same-key requests, payload conflicts, and a forced transaction
failure using synthetic records in staging; no SQL concurrency tests ran here.

The included EF migration has empty Up/Down methods. Running dotnet ef database
update alone will not create the required application tables or patient procedure.
The supplied CJS migration builders have been exported to SQL with:
  node Database/export-schema.cjs
This is an offline export; it never connects to a database.

Validation limits
Scripts were inspected and exported offline. Execute and validate schema changes
in your staging database before deployment; no live SQL access was available.
Use synthetic test records for success, mismatch, duplicate match, and database
failure checks. Confirm TCP reachability, TLS trust and permissions from the web
server itself. A successful localhost test does not establish those conditions.

Metadata diagnostic reference (Microsoft):
https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-exec-describe-first-result-set-for-object-transact-sql
