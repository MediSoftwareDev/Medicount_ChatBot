-- Read-only metadata and permission checks. Never executes patient procedures.
-- Run once as the schema owner to inspect definitions, then as the application
-- database user to inspect effective runtime permissions. No credentials needed.
SET NOCOUNT ON;

SELECT DB_NAME() AS CurrentDatabase, USER_NAME() AS CurrentDatabaseUser;

DECLARE @RequiredObjects TABLE (ObjectName SYSNAME, ObjectType CHAR(2));
INSERT INTO @RequiredObjects VALUES
    (N'dbo.sessions', 'U'), (N'dbo.messages', 'U'), (N'dbo.events', 'U'),
    (N'dbo.verification_attempts', 'U'), (N'dbo.incidents', 'U'),
    (N'dbo.escalations', 'U'), (N'dbo.ChatFormSubmissions', 'U'),
    (N'dbo.InsuranceRequestKeys', 'U'),
    (N'dbo.usp_VerifyPatientIdentity', 'P'),
    (N'dbo.usp_SaveInsuranceUpdateRequest', 'P');
SELECT ObjectName,
       CASE WHEN OBJECT_ID(ObjectName, ObjectType) IS NULL
            THEN 'MISSING OR NOT VISIBLE' ELSE 'PRESENT' END AS ObjectStatus
FROM @RequiredObjects ORDER BY ObjectName;

DECLARE @ExpectedParameters TABLE
    (ProcedureName SYSNAME, ParameterName SYSNAME, TypeName SYSNAME, ByteLength SMALLINT);
INSERT INTO @ExpectedParameters VALUES
    (N'dbo.usp_VerifyPatientIdentity', N'@Name', N'nvarchar', 300),
    (N'dbo.usp_VerifyPatientIdentity', N'@DOB', N'date', 3),
    (N'dbo.usp_VerifyPatientIdentity', N'@AccountNumber', N'nvarchar', 100),
    (N'dbo.usp_SaveInsuranceUpdateRequest', N'@ClientId', N'nvarchar', 100),
    (N'dbo.usp_SaveInsuranceUpdateRequest', N'@AccountKey', N'nvarchar', 200),
    (N'dbo.usp_SaveInsuranceUpdateRequest', N'@InsuranceCompany', N'nvarchar', 300),
    (N'dbo.usp_SaveInsuranceUpdateRequest', N'@MemberId', N'nvarchar', 200),
    (N'dbo.usp_SaveInsuranceUpdateRequest', N'@GroupNumber', N'nvarchar', 200);
SELECT e.ProcedureName, e.ParameterName, e.TypeName AS ExpectedType,
       e.ByteLength AS ExpectedBytes, TYPE_NAME(p.system_type_id) AS ActualType,
       p.max_length AS ActualBytes,
       CASE WHEN p.parameter_id IS NULL THEN 'MISSING OR NOT VISIBLE'
            WHEN TYPE_NAME(p.system_type_id) <> e.TypeName OR p.is_output = 1
                 OR (p.max_length <> -1 AND p.max_length < e.ByteLength)
            THEN 'REVIEW CONTRACT' ELSE 'MATCH' END AS ParameterStatus
FROM @ExpectedParameters e
LEFT JOIN sys.parameters p ON p.object_id = OBJECT_ID(e.ProcedureName)
    AND p.name = e.ParameterName
ORDER BY e.ProcedureName, e.ParameterName;

-- SQL Server may be unable to infer result metadata for dynamic SQL/temp tables.
-- That diagnostic is not itself a failed verification: inspect the procedure.
IF OBJECT_ID(N'dbo.usp_VerifyPatientIdentity', N'P') IS NOT NULL
BEGIN
    BEGIN TRY
        SELECT name AS ResultColumn, system_type_name AS ResultType, is_nullable,
               error_number, error_message
        FROM sys.dm_exec_describe_first_result_set_for_object(
            OBJECT_ID(N'dbo.usp_VerifyPatientIdentity'), 0);
    END TRY
    BEGIN CATCH
        SELECT N'Result metadata unavailable. Review procedure output: ClientId, AccountKey, Placed.' AS Diagnostic,
               ERROR_NUMBER() AS ErrorNumber;
    END CATCH;
END;

SELECT c.name AS ColumnName, TYPE_NAME(c.system_type_id) AS SqlType,
       c.max_length AS ByteLength, c.is_nullable, c.is_identity
FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.ChatFormSubmissions')
ORDER BY c.column_id;
SELECT i.name AS IndexName, i.is_unique, c.name AS ColumnName, ic.key_ordinal
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.ChatFormSubmissions') AND ic.key_ordinal > 0
ORDER BY i.index_id, ic.key_ordinal;

SELECT c.name AS InsuranceKeyColumn, TYPE_NAME(c.system_type_id) AS SqlType,
       c.max_length AS ByteLength, c.is_nullable
FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.InsuranceRequestKeys')
ORDER BY c.column_id;

DECLARE @Permissions TABLE (ObjectName SYSNAME, PermissionName SYSNAME);
INSERT INTO @Permissions VALUES
    (N'dbo.sessions', N'SELECT'), (N'dbo.sessions', N'INSERT'), (N'dbo.sessions', N'UPDATE'),
    (N'dbo.messages', N'SELECT'), (N'dbo.messages', N'INSERT'), (N'dbo.events', N'INSERT'),
    (N'dbo.verification_attempts', N'INSERT'), (N'dbo.incidents', N'INSERT'),
    (N'dbo.escalations', N'SELECT'), (N'dbo.escalations', N'INSERT'), (N'dbo.escalations', N'UPDATE'),
    (N'dbo.ChatFormSubmissions', N'SELECT'), (N'dbo.ChatFormSubmissions', N'INSERT'),
    (N'dbo.ChatFormSubmissions', N'UPDATE'),
    (N'dbo.InsuranceRequestKeys', N'SELECT'), (N'dbo.InsuranceRequestKeys', N'INSERT'),
    (N'dbo.usp_VerifyPatientIdentity', N'EXECUTE'),
    (N'dbo.usp_SaveInsuranceUpdateRequest', N'EXECUTE');
SELECT ObjectName, PermissionName,
       CASE HAS_PERMS_BY_NAME(ObjectName, 'OBJECT', PermissionName)
            WHEN 1 THEN 'GRANTED' WHEN 0 THEN 'NOT GRANTED'
            ELSE 'MISSING OR NOT VISIBLE' END AS EffectivePermission
FROM @Permissions ORDER BY ObjectName, PermissionName;
GO
