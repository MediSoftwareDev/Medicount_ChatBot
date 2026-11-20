# Insurance update flow

Selecting Add or update my insurance verifies the patient through SQL when needed, then shows the insurance form. Other protected actions resume their own selected destination after verification.

The widget reviews and confirms the form through /api/form. The server verifies the review token, calls dbo.usp_SaveInsuranceUpdateRequest, and uses the stable form ID with dbo.InsuranceRequestKeys to deduplicate retries. The SQL procedure and deduplication record commit together. Apply Database/006_insurance_request_idempotency.sql before deploying this version. Database-level concurrency and rollback tests still need to run on staging.

The stored procedure creates a pending request; it does not itself change coverage on the clinical account. Notification failure does not report the saved request as lost or prompt the user to submit it again.

The legacy /api/Patient endpoints use a separate HTTPS cookie session and are not used by the iframe widget. Legacy insurance clients may send requestId (a GUID) and must reuse it for retries to enable deduplication.

See DEPLOYMENT.txt and Database/README.txt for setup and the required SQL permissions. Use synthetic records for acceptance testing.
