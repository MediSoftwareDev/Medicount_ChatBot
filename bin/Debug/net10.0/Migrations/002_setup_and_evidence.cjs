// All SQL in this migration is also exported by scripts/export-database.mjs.
// No patient records, passwords, production policy values or approvals are seeded.
const metadata = `
  owner_name NVARCHAR(128) NULL,
  review_status VARCHAR(10) NOT NULL DEFAULT 'draft' CHECK(review_status IN ('draft','approved')),
  change_ticket NVARCHAR(100) NULL,
  updated_by NVARCHAR(128) NOT NULL DEFAULT ORIGINAL_LOGIN(),
  updated_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
  approved_by NVARCHAR(128) NULL, approved_at DATETIMEOFFSET NULL,
  row_version ROWVERSION,
  CHECK(review_status = 'draft' OR (approved_by IS NOT NULL AND approved_at IS NOT NULL
    AND approved_by <> updated_by AND LEN(LTRIM(RTRIM(owner_name))) > 0
    AND owner_name IS NOT NULL AND LEN(LTRIM(RTRIM(change_ticket))) > 0 AND change_ticket IS NOT NULL))`;

const records = [
  { table:'client_policy', key:'client_id', keyLength:50, fields:[
    ['display_name','NVARCHAR(MAX)',200,true], ['payee_name','NVARCHAR(MAX)',200,false],
    ['remit_address','NVARCHAR(MAX)',400,false], ['agency_name','NVARCHAR(MAX)',200,false],
    ['agency_phone','NVARCHAR(MAX)',30,false], ['agency_hours','NVARCHAR(MAX)',200,false],
    ['plan_terms','NVARCHAR(MAX)',16000,false], ['hardship_accepted','BIT',0,false],
    ['deceased_policy','NVARCHAR(MAX)',400,false], ['extras','NVARCHAR(MAX)',16000,true]
  ], validation:`IF (@plan_terms IS NOT NULL AND ISJSON(@plan_terms) <> 1) OR ISJSON(@extras) <> 1
      THROW 51010, 'Policy JSON is invalid.', 1;` },
  { table:'configuration_items', key:'setting_key', keyLength:80, fields:[
    ['category','NVARCHAR(MAX)',30,true], ['value_json','NVARCHAR(MAX)',16000,true]
  ], validation:`IF ISJSON(@value_json) <> 1 THROW 51010, 'Setting JSON is invalid.', 1;
    IF @category NOT IN ('contact','fees','retention','operations','accessibility')
      THROW 51010, 'Invalid configuration category.', 1;` },
  { table:'control_evidence', key:'control_id', keyLength:80, fields:[
    ['framework','NVARCHAR(MAX)',20,true], ['control_summary','NVARCHAR(MAX)',1000,true],
    ['implementation_state','NVARCHAR(MAX)',20,true], ['evidence_url','NVARCHAR(MAX)',2048,false]
  ], validation:`IF @framework NOT IN ('SOC2','ISO27001','HITRUST','WCAG','ADA','SHARED')
      THROW 51010, 'Invalid framework.', 1;
    IF @implementation_state NOT IN ('planned','implemented','not_applicable')
      THROW 51010, 'Invalid implementation state.', 1;
    IF @evidence_url IS NOT NULL AND (@evidence_url NOT LIKE N'https://_%'
      OR @evidence_url LIKE N'%@%' OR @evidence_url LIKE N'%?%' OR @evidence_url LIKE N'%#%'
      OR @evidence_url LIKE N'% %' OR @evidence_url LIKE N'%'+NCHAR(9)+N'%'
      OR @evidence_url LIKE N'%'+NCHAR(10)+N'%' OR @evidence_url LIKE N'%'+NCHAR(13)+N'%')
      THROW 51010, 'Use an HTTPS evidence repository URL without credentials or access tokens.', 1;` }
];

function saveProcedure(r) {
  const args = r.fields.map(([name,type])=>`@${name} ${type}`).join(',\n  ');
  const checks = r.fields.filter(([,type])=>type!=='BIT').map(([name,,max,required])=>
    `IF ${required ? `@${name} IS NULL OR LEN(LTRIM(RTRIM(@${name}))) = 0 OR ` : ''}DATALENGTH(@${name}) > ${max*2} THROW 51010, 'Invalid ${name}.', 1;`).join('\n  ');
  const cols=r.fields.map(([name])=>name);
  return `CREATE OR ALTER PROCEDURE dbo.usp_save_${r.table}
  @${r.key} NVARCHAR(MAX), ${args},
  @owner_name NVARCHAR(MAX), @change_ticket NVARCHAR(MAX)
AS
BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  IF @${r.key} IS NULL OR LEN(LTRIM(RTRIM(@${r.key}))) = 0 OR LEN(@${r.key}) > ${r.keyLength}
    OR @${r.key} LIKE '%[^a-zA-Z0-9_.-]%' COLLATE Latin1_General_100_BIN2
    THROW 51010, 'Use an ASCII record key with letters, digits, dot, underscore or hyphen.', 1;
  IF @owner_name IS NULL OR LEN(LTRIM(RTRIM(@owner_name))) = 0 OR DATALENGTH(@owner_name) > 256
    OR @change_ticket IS NULL OR LEN(LTRIM(RTRIM(@change_ticket))) = 0 OR DATALENGTH(@change_ticket) > 200
    THROW 51010, 'Owner and change ticket are required.', 1;
  ${checks}
  ${r.validation}
  BEGIN TRY
    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM dbo.${r.table} WITH (UPDLOCK,HOLDLOCK) WHERE ${r.key}=@${r.key})
      UPDATE dbo.${r.table} SET ${cols.map(c=>`${c}=@${c}`).join(',')},
        owner_name=@owner_name,change_ticket=@change_ticket,updated_by=ORIGINAL_LOGIN(),updated_at=SYSDATETIMEOFFSET(),
        review_status='draft',approved_by=NULL,approved_at=NULL WHERE ${r.key}=@${r.key};
    ELSE
      INSERT dbo.${r.table} (${r.key},${cols.join(',')},owner_name,change_ticket)
      VALUES (@${r.key},${cols.map(c=>`@${c}`).join(',')},@owner_name,@change_ticket);
    COMMIT;
    SELECT ${r.key},review_status,row_version FROM dbo.${r.table} WHERE ${r.key}=@${r.key};
  END TRY
  BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK;
    THROW;
  END CATCH
END;`;
}

function approveProcedure(r) {
  return `CREATE OR ALTER PROCEDURE dbo.usp_approve_${r.table}
  @${r.key} VARCHAR(${r.keyLength}), @expected_version BINARY(8), @approval_ticket NVARCHAR(MAX)
AS
BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  IF @approval_ticket IS NULL OR LEN(LTRIM(RTRIM(@approval_ticket))) = 0 OR DATALENGTH(@approval_ticket)>200
    THROW 51011, 'An approval ticket is required.', 1;
  UPDATE dbo.${r.table} SET review_status='approved',approved_by=ORIGINAL_LOGIN(),approved_at=SYSDATETIMEOFFSET(),change_ticket=@approval_ticket
    WHERE ${r.key}=@${r.key} AND row_version=@expected_version AND review_status='draft'
      AND updated_by<>ORIGINAL_LOGIN() AND owner_name IS NOT NULL
      ${r.table==='control_evidence' ? "AND implementation_state<>'planned' AND evidence_url IS NOT NULL" : ''};
  IF @@ROWCOUNT<>1 THROW 51011, 'Approval requires a different login, the reviewed row version and complete evidence.', 1;
END;`;
}

function auditTrigger(r) {
  return `CREATE OR ALTER TRIGGER dbo.tr_${r.table}_audit ON dbo.${r.table} AFTER INSERT, UPDATE, DELETE
AS
BEGIN
  SET NOCOUNT ON;
  INSERT dbo.configuration_audit (table_name,record_key,operation,actor_login,before_hash,after_hash,change_ticket)
  SELECT '${r.table}',COALESCE(i.${r.key},d.${r.key}),
    CASE WHEN d.${r.key} IS NULL THEN 'INSERT' WHEN i.${r.key} IS NULL THEN 'DELETE' ELSE 'UPDATE' END,
    ORIGINAL_LOGIN(),
    CASE WHEN d.${r.key} IS NULL THEN NULL ELSE HASHBYTES('SHA2_256',(SELECT d.* FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) END,
    CASE WHEN i.${r.key} IS NULL THEN NULL ELSE HASHBYTES('SHA2_256',(SELECT i.* FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)) END,
    COALESCE(i.change_ticket,d.change_ticket)
  FROM inserted i FULL JOIN deleted d ON i.${r.key}=d.${r.key};
END;`;
}

exports.up = async function(knex) {
  await knex.raw(`ALTER TABLE dbo.client_policy ADD display_name NVARCHAR(200) NULL, ${metadata};
    CREATE TABLE dbo.configuration_items (
      setting_key VARCHAR(80) NOT NULL PRIMARY KEY,
      category VARCHAR(30) NOT NULL CHECK(category IN ('contact','fees','retention','operations','accessibility')),
      value_json NVARCHAR(MAX) NOT NULL CHECK(ISJSON(value_json)=1), ${metadata}
    );
    CREATE TABLE dbo.control_evidence (
      control_id VARCHAR(80) NOT NULL PRIMARY KEY,
      framework VARCHAR(20) NOT NULL CHECK(framework IN ('SOC2','ISO27001','HITRUST','WCAG','ADA','SHARED')),
      control_summary NVARCHAR(1000) NOT NULL,
      implementation_state VARCHAR(20) NOT NULL CHECK(implementation_state IN ('planned','implemented','not_applicable')),
      evidence_url NVARCHAR(2048) NULL CHECK(evidence_url IS NULL OR (evidence_url LIKE N'https://_%'
        AND evidence_url NOT LIKE N'%@%' AND evidence_url NOT LIKE N'%?%' AND evidence_url NOT LIKE N'%#%'
        AND evidence_url NOT LIKE N'% %' AND evidence_url NOT LIKE N'%'+NCHAR(9)+N'%'
        AND evidence_url NOT LIKE N'%'+NCHAR(10)+N'%' AND evidence_url NOT LIKE N'%'+NCHAR(13)+N'%')), ${metadata},
      CHECK(review_status='draft' OR (implementation_state<>'planned' AND evidence_url IS NOT NULL))
    );
    CREATE TABLE dbo.configuration_audit (
      id BIGINT IDENTITY PRIMARY KEY, at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
      table_name VARCHAR(50) NOT NULL, record_key VARCHAR(80) NOT NULL,
      operation VARCHAR(6) NOT NULL CHECK(operation IN ('INSERT','UPDATE','DELETE')),
      actor_login NVARCHAR(128) NOT NULL, before_hash VARBINARY(32) NULL, after_hash VARBINARY(32) NULL,
      change_ticket NVARCHAR(100) NULL
    );
    CREATE INDEX ix_configuration_audit_record ON dbo.configuration_audit(table_name,record_key,id);
    CREATE INDEX ix_events_session ON dbo.events(session_id,id);
    CREATE INDEX ix_attempts_session ON dbo.verification_attempts(session_id,id);
    CREATE INDEX ix_incidents_session ON dbo.incidents(session_id,id);`);
  for (const r of records) {
    await knex.raw(saveProcedure(r));
    await knex.raw(approveProcedure(r));
    await knex.raw(auditTrigger(r));
  }
  await knex.raw(`CREATE OR ALTER VIEW dbo.approved_client_policy AS
    SELECT client_id,display_name,payee_name,remit_address,agency_name,agency_phone,agency_hours,
      plan_terms,hardship_accepted,deceased_policy,extras
    FROM dbo.client_policy WHERE review_status='approved';`);
};
exports.down = async function() { throw new Error('Destructive rollback disabled. Use a reviewed forward migration.'); };
