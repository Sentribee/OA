SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmMerchant' AND COLUMN_NAME = 'PasswordHash'
  ),
  'SELECT 1',
  'ALTER TABLE bee_CrmMerchant ADD COLUMN PasswordHash VARCHAR(512) NULL AFTER Email'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmMerchant' AND COLUMN_NAME = 'AvatarUrl'
  ),
  'SELECT 1',
  'ALTER TABLE bee_CrmMerchant ADD COLUMN AvatarUrl VARCHAR(800) NULL AFTER WebsiteUrl'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmMerchant' AND COLUMN_NAME = 'ContextInstructions'
  ),
  'SELECT 1',
  'ALTER TABLE bee_CrmMerchant ADD COLUMN ContextInstructions TEXT NULL AFTER TimeZoneId'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmChatbot' AND COLUMN_NAME = 'WelcomeMessage'
  ),
  'SELECT 1',
  'ALTER TABLE bee_CrmChatbot ADD COLUMN WelcomeMessage VARCHAR(500) NULL AFTER SystemPrompt'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmKnowledgeDocument' AND COLUMN_NAME = 'FileUrl'
  ),
  'SELECT 1',
  'ALTER TABLE bee_CrmKnowledgeDocument ADD COLUMN FileUrl VARCHAR(1000) NULL AFTER FileName'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql := IF(
  EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmKnowledgeDocument' AND COLUMN_NAME = 'ExtractedText'
  ),
  'SELECT 1',
  'ALTER TABLE bee_CrmKnowledgeDocument ADD COLUMN ExtractedText MEDIUMTEXT NULL AFTER SourceType'
);
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

CREATE TABLE IF NOT EXISTS bee_CrmConversationMessage (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  ConversationId BIGINT NOT NULL,
  MerchantId BIGINT NOT NULL,
  ChatbotId BIGINT NULL,
  SenderRole VARCHAR(40) NOT NULL,
  Body MEDIUMTEXT NULL,
  ImageUrl VARCHAR(1000) NULL,
  ModelName VARCHAR(80) NULL,
  PromptTokens INT NOT NULL DEFAULT 0,
  CompletionTokens INT NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmConversationMessage_Conversation_Time (ConversationId, CreatedAtUtc),
  KEY IX_bee_CrmConversationMessage_Project_Time (ProjectId, CreatedAtUtc),
  CONSTRAINT FK_bee_CrmConversationMessage_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmConversationMessage_Conversation FOREIGN KEY (ConversationId)
    REFERENCES bee_CrmConversation (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmConversationMessage_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmConversationMessage_Chatbot FOREIGN KEY (ChatbotId)
    REFERENCES bee_CrmChatbot (id) ON DELETE SET NULL
) ENGINE=InnoDB;
