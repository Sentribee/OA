CREATE TABLE IF NOT EXISTS bee_CrmMindMap (
    id BIGINT NOT NULL AUTO_INCREMENT,
    ProjectId INT NOT NULL,
    MerchantId BIGINT NOT NULL,
    Title VARCHAR(180) NOT NULL,
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
