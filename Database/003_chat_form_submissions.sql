-- Additive and repeatable. Run in the chatbot database using a schema-owner login.
-- Creates the table required by ChatFormRepository; no patient data is read.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.ChatFormSubmissions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.ChatFormSubmissions (
            SubmissionId BIGINT IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_ChatFormSubmissions PRIMARY KEY,
            SessionId NVARCHAR(100) NOT NULL,
            FormId NVARCHAR(100) NOT NULL,
            FormData NVARCHAR(MAX) NOT NULL
                CONSTRAINT CK_ChatFormSubmissions_Json CHECK (ISJSON(FormData) = 1),
            ReviewTokenHash CHAR(64) NOT NULL,
            SubmittedAt DATETIME2(3) NOT NULL
                CONSTRAINT DF_ChatFormSubmissions_SubmittedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UQ_ChatFormSubmissions_Session_Form UNIQUE (SessionId, FormId)
        );
    END;

    IF COL_LENGTH(N'dbo.ChatFormSubmissions', N'SubmissionId') IS NULL
       OR COL_LENGTH(N'dbo.ChatFormSubmissions', N'SessionId') IS NULL
       OR COL_LENGTH(N'dbo.ChatFormSubmissions', N'FormId') IS NULL
       OR COL_LENGTH(N'dbo.ChatFormSubmissions', N'FormData') IS NULL
       OR COL_LENGTH(N'dbo.ChatFormSubmissions', N'ReviewTokenHash') IS NULL
       OR COL_LENGTH(N'dbo.ChatFormSubmissions', N'SubmittedAt') IS NULL
        THROW 51020, 'Existing ChatFormSubmissions has an incompatible schema. Review it before deployment.', 1;

    -- Existing installations are left intact. The diagnostic script lists the
    -- actual types and indexes for comparison with this table definition.
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
