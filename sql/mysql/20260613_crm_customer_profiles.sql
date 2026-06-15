SET @crm_project_id := (
  SELECT id FROM bee_Project WHERE ProjectName = 'crm.sentribee.ai' LIMIT 1
);

SET @sql := IF(
  NOT EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmMerchant' AND COLUMN_NAME = 'ProfileGuidanceInstructions'
  ),
  'ALTER TABLE bee_CrmMerchant ADD COLUMN ProfileGuidanceInstructions TEXT NULL AFTER ContextInstructions',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
  NOT EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bee_CrmMerchant' AND COLUMN_NAME = 'ProfileDimensionFocus'
  ),
  'ALTER TABLE bee_CrmMerchant ADD COLUMN ProfileDimensionFocus TEXT NULL AFTER ProfileGuidanceInstructions',
  'SELECT 1'
);
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

CREATE TABLE IF NOT EXISTS bee_CrmCustomerProfile (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  ChatbotId BIGINT NULL,
  ConversationId BIGINT NULL,
  VisitorLabel VARCHAR(140) NULL,
  DisplayName VARCHAR(180) NULL,
  Email VARCHAR(180) NULL,
  Phone VARCHAR(80) NULL,
  CompanyName VARCHAR(180) NULL,
  JobTitle VARCHAR(160) NULL,
  Location VARCHAR(180) NULL,
  Language VARCHAR(80) NULL,
  CustomerType VARCHAR(120) NULL,
  LifecycleStage VARCHAR(80) NULL,
  IntentSummary VARCHAR(500) NULL,
  NeedSummary TEXT NULL,
  ProductInterest VARCHAR(500) NULL,
  IndustrySegment VARCHAR(180) NULL,
  BudgetRange VARCHAR(120) NULL,
  Timeline VARCHAR(140) NULL,
  DecisionRole VARCHAR(160) NULL,
  PainPoints TEXT NULL,
  Objections TEXT NULL,
  Preferences TEXT NULL,
  Sentiment VARCHAR(80) NULL,
  PriorityScore TINYINT UNSIGNED NOT NULL DEFAULT 0,
  ProfileCompleteness TINYINT UNSIGNED NOT NULL DEFAULT 0,
  ProfileJson JSON NULL,
  LastExtractedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_CrmCustomerProfile_Conversation (ConversationId),
  KEY IX_bee_CrmCustomerProfile_Merchant_Updated (MerchantId, UpdatedAtUtc),
  KEY IX_bee_CrmCustomerProfile_Merchant_Completeness (MerchantId, ProfileCompleteness),
  KEY IX_bee_CrmCustomerProfile_Merchant_Stage (MerchantId, LifecycleStage),
  KEY IX_bee_CrmCustomerProfile_Merchant_Sentiment (MerchantId, Sentiment),
  CONSTRAINT FK_bee_CrmCustomerProfile_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmCustomerProfile_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmCustomerProfile_Chatbot FOREIGN KEY (ChatbotId)
    REFERENCES bee_CrmChatbot (id) ON DELETE SET NULL,
  CONSTRAINT FK_bee_CrmCustomerProfile_Conversation FOREIGN KEY (ConversationId)
    REFERENCES bee_CrmConversation (id) ON DELETE SET NULL
) ENGINE=InnoDB;

UPDATE bee_CrmMerchant AS merchant
LEFT JOIN bee_CrmIndustry AS industry ON industry.id = merchant.IndustryId
SET
  merchant.ProfileGuidanceInstructions = COALESCE(
    merchant.ProfileGuidanceInstructions,
    'During support chats, progressively understand the customer profile without making the conversation feel like a form. Ask one useful follow-up question when important profile information is missing.'
  ),
  merchant.ProfileDimensionFocus = COALESCE(
    merchant.ProfileDimensionFocus,
    CONCAT(
      'Core profile dimensions: name, contact method, company, role, location, language, customer type, use case, product interest, pain points, urgency, budget, timeline, decision role, objections, preferences, sentiment, next step. ',
      'Industry focus: ', COALESCE(industry.Name, 'General business'), '. ',
      COALESCE(industry.Description, 'Capture the buyer need and operational context relevant to this business.')
    )
  )
WHERE merchant.ProjectId = @crm_project_id;
