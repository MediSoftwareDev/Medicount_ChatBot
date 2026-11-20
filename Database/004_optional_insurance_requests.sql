-- Additive and repeatable alternative to the original InsuranceUpdate.sql.
-- Existing tables and stored procedures are not replaced or altered.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.PatientInsuranceUpdateRequests', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.PatientInsuranceUpdateRequests (
            RequestId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            ClientId NVARCHAR(50) NOT NULL,
            AccountKey NVARCHAR(100) NOT NULL,
            InsuranceCompany NVARCHAR(150) NOT NULL,
            MemberId NVARCHAR(100) NOT NULL,
            GroupNumber NVARCHAR(100) NULL,
            RequestStatus NVARCHAR(30) NOT NULL DEFAULT (N'Pending'),
            CreatedDate DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
        );
    END;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
IF OBJECT_ID(N'dbo.usp_SaveInsuranceUpdateRequest', N'P') IS NULL
BEGIN
    EXEC(N'CREATE PROCEDURE dbo.usp_SaveInsuranceUpdateRequest
        @ClientId NVARCHAR(50), @AccountKey NVARCHAR(100),
        @InsuranceCompany NVARCHAR(150), @MemberId NVARCHAR(100),
        @GroupNumber NVARCHAR(100) = NULL
    AS
    BEGIN
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        INSERT INTO dbo.PatientInsuranceUpdateRequests
            (ClientId, AccountKey, InsuranceCompany, MemberId, GroupNumber)
        VALUES (@ClientId, @AccountKey, @InsuranceCompany, @MemberId,
                NULLIF(LTRIM(RTRIM(@GroupNumber)), N''''));
    END;');
END;
GO
