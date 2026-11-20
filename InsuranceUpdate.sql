-- Run in your approved staging database after DBA review.
CREATE TABLE dbo.PatientInsuranceUpdateRequests (
    RequestId BIGINT IDENTITY(1,1) PRIMARY KEY,
    ClientId NVARCHAR(50) NOT NULL,
    AccountKey NVARCHAR(100) NOT NULL,
    InsuranceCompany NVARCHAR(150) NOT NULL,
    MemberId NVARCHAR(100) NOT NULL,
    GroupNumber NVARCHAR(100) NULL,
    RequestStatus NVARCHAR(30) NOT NULL DEFAULT ('Pending'),
    CreatedDate DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
CREATE OR ALTER PROCEDURE dbo.usp_SaveInsuranceUpdateRequest
    @ClientId NVARCHAR(50), @AccountKey NVARCHAR(100),
    @InsuranceCompany NVARCHAR(150), @MemberId NVARCHAR(100),
    @GroupNumber NVARCHAR(100) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    INSERT INTO dbo.PatientInsuranceUpdateRequests
        (ClientId, AccountKey, InsuranceCompany, MemberId, GroupNumber)
    VALUES (@ClientId, @AccountKey, @InsuranceCompany, @MemberId, NULLIF(@GroupNumber, ''));
END;
GO
