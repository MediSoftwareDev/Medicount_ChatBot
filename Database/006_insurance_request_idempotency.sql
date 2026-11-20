-- Required for chatbot insurance submissions. Additive and repeatable.
-- No clinical procedure, patient table, or existing insurance request is altered.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(N'dbo.InsuranceRequestKeys', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.InsuranceRequestKeys (
            RequestId UNIQUEIDENTIFIER NOT NULL
                CONSTRAINT PK_InsuranceRequestKeys PRIMARY KEY,
            ClientId NVARCHAR(50) NOT NULL,
            AccountKey NVARCHAR(100) NOT NULL,
            PayloadHash BINARY(32) NOT NULL,
            CreatedAt DATETIME2(3) NOT NULL
                CONSTRAINT DF_InsuranceRequestKeys_CreatedAt DEFAULT SYSUTCDATETIME()
        );
    END;
    IF COL_LENGTH(N'dbo.InsuranceRequestKeys', N'RequestId') IS NULL
       OR COL_LENGTH(N'dbo.InsuranceRequestKeys', N'ClientId') IS NULL
       OR COL_LENGTH(N'dbo.InsuranceRequestKeys', N'AccountKey') IS NULL
       OR COL_LENGTH(N'dbo.InsuranceRequestKeys', N'PayloadHash') IS NULL
        THROW 51021, 'Existing InsuranceRequestKeys has an incompatible schema. Review it before deployment.', 1;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.InsuranceRequestKeys')
          AND i.is_unique = 1 AND i.has_filter = 0 AND i.is_disabled = 0
          AND (SELECT COUNT(*) FROM sys.index_columns ic
               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                 AND ic.key_ordinal > 0) = 1
          AND EXISTS (SELECT 1 FROM sys.index_columns ic
                      JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                        AND ic.key_ordinal = 1 AND c.name = N'RequestId')
    )
        THROW 51022, 'InsuranceRequestKeys requires a unique RequestId index. Review existing data and schema.', 1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
