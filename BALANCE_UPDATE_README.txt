CHECK MY BALANCE UPDATE

Changes:
- Check my balance verifies Patient Name, Date of Birth, and Account Number directly against dbo.tblMetakediIVRPatient using the existing ChatDb connection.
- Account Number is mapped to PatientID, not the internal ID column.
- After verification, reads AccountBalance scoped by PatientID and ClientID and displays USD with two decimal places (including $0.00).
- NULL/missing/invalid balances produce the existing unavailable-service response; they are never displayed as zero.
- Multiple matching rows are rejected. Existing retry limits, confirmation, diagnostics, and collections routing remain in place.
- Other workflows retain their existing stored procedure and billing integrations.

Deployment:
1. Build and publish the project using its existing .NET 10 target and package versions.
2. Use your existing connection configuration. Its SQL user needs SELECT permission on dbo.tblMetakediIVRPatient.
3. No new stored procedures or schema migrations are required for this balance change. Existing setup scripts remain for other features.
4. Restart the application after deployment so existing chat sessions are cleared.

Table name is exactly dbo.tblMetakediIVRPatient, as requested. If your real table is dbo.tblMediIVRPatient, change the two FROM clauses in Services/PatientVerificationRepository.cs before building.

Validation:
Source paths, flow references, parameterized query scope and ZIP integrity were checked. This environment has no dotnet SDK or access to your SQL Server, so compilation and live database testing could not be performed.

Acceptance checks on your environment:
- Balance & Bill Questions > Check my balance presents the verification form.
- Valid PatientID + PatientName + DOB displays that row's AccountBalance (for example 1 -> $1.00).
- Wrong name/account/DOB shows mismatch and never displays a balance; three failed attempts lock out verification.
- A zero balance displays $0.00; NULL balances and unavailable SQL show the existing outage message.
- IsInCollections=1 follows the existing collections flow.
- Verify insurance updates, bill-copy requests, and other existing features as usual.
