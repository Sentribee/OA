CREATE TABLE IF NOT EXISTS bee_CrmChatExpenseEvidence (
  id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
  MerchantId BIGINT NOT NULL,
  ProjectId INT NOT NULL,
  SourceTenantId VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  EventId CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  CaseKey CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PayloadHash CHAR(64) CHARACTER SET ascii NOT NULL,
  Category VARCHAR(40) NOT NULL,
  DocumentJson JSON NOT NULL,
  ReviewStatus VARCHAR(32) NOT NULL DEFAULT 'pending',
  Notes TEXT NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UNIQUE KEY UX_ChatExpense_Event (SourceTenantId,EventId),
  KEY IX_ChatExpense_Merchant (MerchantId,id),
  KEY IX_ChatExpense_Case (MerchantId,CaseKey)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bee_CrmChatExpenseImage (
  EvidenceId BIGINT NOT NULL,
  ImageId CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  MerchantId BIGINT NOT NULL,
  ContentType VARCHAR(40) NOT NULL,
  Sha256 CHAR(64) CHARACTER SET ascii NOT NULL,
  ImageBytes LONGBLOB NOT NULL,
  PRIMARY KEY (EvidenceId,ImageId),
  KEY IX_ChatExpenseImage_Merchant (MerchantId,EvidenceId),
  CONSTRAINT FK_ChatExpenseImage_Evidence FOREIGN KEY (EvidenceId) REFERENCES bee_CrmChatExpenseEvidence(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bee_CrmChatExpenseReview (
  id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
  EvidenceId BIGINT NOT NULL,
  MerchantId BIGINT NOT NULL,
  ReviewStatus VARCHAR(32) NOT NULL,
  Notes TEXT NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  KEY IX_ChatExpenseReview_Evidence (MerchantId,EvidenceId)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
