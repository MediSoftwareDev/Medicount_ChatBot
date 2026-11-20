exports.up = async function (knex) {
  await knex.raw(`
    CREATE TABLE sessions (
      id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
      token_hash CHAR(64) NOT NULL UNIQUE,
      created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
      last_activity DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
      status VARCHAR(10) NOT NULL DEFAULT 'active' CHECK (status IN ('active','expired','closed')),
      state NVARCHAR(MAX) NOT NULL CHECK (ISJSON(state)=1)
    );
    CREATE INDEX ix_sessions_expiry ON sessions(last_activity);
    CREATE TABLE messages (
      id BIGINT IDENTITY PRIMARY KEY, session_id UNIQUEIDENTIFIER NOT NULL REFERENCES sessions(id),
      at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
      who VARCHAR(10) NOT NULL CHECK(who IN ('bot','visitor','system')),
      body NVARCHAR(MAX) NOT NULL, kind VARCHAR(20) NOT NULL DEFAULT 'text'
    );
    CREATE INDEX ix_messages_session ON messages(session_id,id);
    CREATE TABLE events (
      id BIGINT IDENTITY PRIMARY KEY, session_id UNIQUEIDENTIFIER NOT NULL REFERENCES sessions(id),
      at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(), name VARCHAR(100) NOT NULL,
      data NVARCHAR(MAX) NOT NULL DEFAULT '{}' CHECK(ISJSON(data)=1)
    );
    CREATE TABLE verification_attempts (
      id BIGINT IDENTITY PRIMARY KEY, session_id UNIQUEIDENTIFIER NOT NULL REFERENCES sessions(id),
      at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(), account_hash VARCHAR(128) NOT NULL,
      outcome VARCHAR(20) NOT NULL CHECK(outcome IN ('pass','fail','fields_incomplete'))
    );
    CREATE TABLE incidents (
      id BIGINT IDENTITY PRIMARY KEY, session_id UNIQUEIDENTIFIER NULL REFERENCES sessions(id),
      at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(), type VARCHAR(30) NOT NULL,
      detail NVARCHAR(MAX) NOT NULL DEFAULT '{}' CHECK(ISJSON(detail)=1)
    );
    CREATE TABLE escalations (
      id BIGINT IDENTITY PRIMARY KEY, session_id UNIQUEIDENTIFIER NOT NULL REFERENCES sessions(id),
      at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(), reference VARCHAR(30) NULL,
      idempotency_key UNIQUEIDENTIFIER NOT NULL UNIQUE, kind VARCHAR(50) NOT NULL,
      queue_email NVARCHAR(320) NOT NULL, payload NVARCHAR(MAX) NOT NULL CHECK(ISJSON(payload)=1),
      sent BIT NOT NULL DEFAULT 0, sent_at DATETIMEOFFSET NULL, send_attempts INT NOT NULL DEFAULT 0,
      lease_until DATETIMEOFFSET NULL, next_attempt DATETIMEOFFSET NULL
    );
    CREATE UNIQUE INDEX ix_escalation_reference ON escalations(reference) WHERE reference IS NOT NULL;
    CREATE INDEX ix_escalation_outbox ON escalations(sent,next_attempt,lease_until);
    CREATE TABLE client_policy (
      client_id VARCHAR(50) PRIMARY KEY, payee_name NVARCHAR(200) NULL, remit_address NVARCHAR(400) NULL,
      agency_name NVARCHAR(200) NULL, agency_phone VARCHAR(30) NULL, agency_hours NVARCHAR(200) NULL,
      plan_terms NVARCHAR(MAX) NULL CHECK(plan_terms IS NULL OR ISJSON(plan_terms)=1),
      hardship_accepted BIT NULL, deceased_policy NVARCHAR(400) NULL,
      extras NVARCHAR(MAX) NOT NULL DEFAULT '{}' CHECK(ISJSON(extras)=1)
    );
  `);
};
exports.down = async function ()
{
    throw new Error('Destructive rollback disabled. Restore a reviewed backup or write a forward migration.');
};
