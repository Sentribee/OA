CREATE TABLE IF NOT EXISTS bee_CrmMindMap (
    id BIGINT NOT NULL AUTO_INCREMENT,
    ProjectId INT NOT NULL,
    MerchantId BIGINT NOT NULL,
    Title VARCHAR(180) NOT NULL,
    MapStatus VARCHAR(40) NOT NULL DEFAULT 'Draft',
    MapJson LONGTEXT NOT NULL,
    ParticipantEmails TEXT NULL,
    ShareToken VARCHAR(80) NOT NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Active',
    LastSentAtUtc DATETIME(6) NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    PRIMARY KEY (id),
    UNIQUE KEY UX_bee_CrmMindMap_ShareToken (ShareToken),
    KEY IX_bee_CrmMindMap_Merchant (MerchantId, Status, UpdatedAtUtc),
    KEY IX_bee_CrmMindMap_Project (ProjectId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

SET @crm_mind_map_status_column := (
    SELECT IF(
        COUNT(*) = 0,
        'ALTER TABLE bee_CrmMindMap ADD COLUMN MapStatus VARCHAR(40) NOT NULL DEFAULT ''Draft'' AFTER Title',
        'SELECT 1'
    )
    FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'bee_CrmMindMap'
      AND COLUMN_NAME = 'MapStatus'
);
PREPARE crm_mind_map_status_stmt FROM @crm_mind_map_status_column;
EXECUTE crm_mind_map_status_stmt;
DEALLOCATE PREPARE crm_mind_map_status_stmt;

CREATE TABLE IF NOT EXISTS bee_CrmMindMapParticipant (
    id BIGINT NOT NULL AUTO_INCREMENT,
    ProjectId INT NOT NULL,
    MerchantId BIGINT NOT NULL,
    MindMapId BIGINT NOT NULL,
    DisplayName VARCHAR(160) NOT NULL,
    Email VARCHAR(180) NOT NULL,
    SourceType VARCHAR(40) NOT NULL DEFAULT 'Manual',
    SourceId BIGINT NULL,
    InviteToken VARCHAR(80) NOT NULL,
    ColorTag VARCHAR(20) NOT NULL,
    LastSeenAtUtc DATETIME(6) NULL,
    LastInvitedAtUtc DATETIME(6) NULL,
    Status VARCHAR(40) NOT NULL DEFAULT 'Active',
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    PRIMARY KEY (id),
    UNIQUE KEY UX_bee_CrmMindMapParticipant_Token (InviteToken),
    UNIQUE KEY UX_bee_CrmMindMapParticipant_Email (MindMapId, Email),
    KEY IX_bee_CrmMindMapParticipant_Map (MindMapId, Status),
    KEY IX_bee_CrmMindMapParticipant_Merchant (MerchantId, Status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS bee_CrmMindMapActivity (
    id BIGINT NOT NULL AUTO_INCREMENT,
    MindMapId BIGINT NOT NULL,
    ParticipantId BIGINT NULL,
    ActorName VARCHAR(160) NOT NULL,
    ActorEmail VARCHAR(180) NOT NULL,
    ColorTag VARCHAR(20) NOT NULL,
    NodeId VARCHAR(120) NULL,
    NodeTopic VARCHAR(500) NULL,
    Summary VARCHAR(700) NOT NULL,
    CreatedAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    PRIMARY KEY (id),
    KEY IX_bee_CrmMindMapActivity_Map (MindMapId, CreatedAtUtc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
