INSERT INTO bee_Admin (LoginID, Pwd, Roles, DisplayName, Email)
SELECT 'admin@sentribee.ai',
  admin.Pwd,
  'Administrator',
  COALESCE(admin.DisplayName, 'SentriBee Admin'),
  'admin@sentribee.ai'
FROM bee_Admin AS admin
WHERE admin.LoginID = 'admin'
ON DUPLICATE KEY UPDATE
  Pwd = VALUES(Pwd),
  Roles = VALUES(Roles),
  DisplayName = VALUES(DisplayName),
  Email = VALUES(Email),
  UpdatedAtUtc = UTC_TIMESTAMP(6);

INSERT INTO bee_Project
  (AdminId, ProjectName, ProjectDescription, LogoUrl, CompanyName, WebsiteUrl, Visibility, TimeZoneId)
SELECT admin.id,
  'crm.sentribee.ai',
  'General-purpose customer service CRM for registered businesses. Merchants create ChatGPT-style support bots, upload documents and screenshots as knowledge, test public support chat at chat.sentribee.ai/corpid, and use SentriBee OpenAI-backed model routing.',
  '/images/sentribee-mark.png',
  'SentriBee',
  'https://crm.sentribee.ai',
  'Private',
  'Pacific/Auckland'
FROM bee_Admin AS admin
WHERE admin.Email = 'admin@sentribee.ai'
  AND NOT EXISTS (
    SELECT 1
    FROM bee_Project AS existing
    WHERE existing.ProjectName = 'crm.sentribee.ai'
  );

UPDATE bee_Project AS project
INNER JOIN bee_Admin AS admin ON admin.Email = 'admin@sentribee.ai'
SET project.AdminId = admin.id,
    project.ProjectDescription = 'General-purpose customer service CRM for registered businesses. Merchants create ChatGPT-style support bots, upload documents and screenshots as knowledge, test public support chat at chat.sentribee.ai/corpid, and use SentriBee OpenAI-backed model routing.',
    project.LogoUrl = '/images/sentribee-mark.png',
    project.CompanyName = 'SentriBee',
    project.WebsiteUrl = 'https://crm.sentribee.ai',
    project.Visibility = 'Private',
    project.TimeZoneId = 'Pacific/Auckland',
    project.UpdatedAtUtc = UTC_TIMESTAMP(6)
WHERE project.ProjectName = 'crm.sentribee.ai';

INSERT INTO bee_ProjectMember (ProjectId, AdminId, Role)
SELECT project.id, admin.id, 'Administrator'
FROM bee_Project AS project
INNER JOIN bee_Admin AS admin ON admin.Email = 'admin@sentribee.ai'
WHERE project.ProjectName = 'crm.sentribee.ai'
ON DUPLICATE KEY UPDATE Role = VALUES(Role);

CREATE TABLE IF NOT EXISTS bee_CrmIndustry (
  id INT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  Name VARCHAR(120) NOT NULL,
  Slug VARCHAR(120) NOT NULL,
  Description VARCHAR(500) NULL,
  SortOrder INT NOT NULL DEFAULT 0,
  IsActive TINYINT(1) NOT NULL DEFAULT 1,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_CrmIndustry_Project_Slug (ProjectId, Slug),
  KEY IX_bee_CrmIndustry_Project_Active (ProjectId, IsActive, SortOrder),
  CONSTRAINT FK_bee_CrmIndustry_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmMerchant (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  IndustryId INT NULL,
  BusinessName VARCHAR(180) NOT NULL,
  CorpId VARCHAR(80) NOT NULL,
  ContactName VARCHAR(120) NULL,
  Email VARCHAR(150) NOT NULL,
  WebsiteUrl VARCHAR(500) NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Active',
  PlanName VARCHAR(80) NOT NULL DEFAULT 'Starter',
  TimeZoneId VARCHAR(80) NOT NULL DEFAULT 'Pacific/Auckland',
  RegisteredAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  LastLoginAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_CrmMerchant_Project_CorpId (ProjectId, CorpId),
  UNIQUE KEY UX_bee_CrmMerchant_Project_Email (ProjectId, Email),
  KEY IX_bee_CrmMerchant_Project_Status (ProjectId, Status, RegisteredAtUtc),
  KEY IX_bee_CrmMerchant_Industry (IndustryId),
  CONSTRAINT FK_bee_CrmMerchant_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmMerchant_Industry FOREIGN KEY (IndustryId)
    REFERENCES bee_CrmIndustry (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmChatbot (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  BotName VARCHAR(140) NOT NULL,
  AvatarUrl VARCHAR(800) NULL,
  PublicChatPath VARCHAR(160) NOT NULL,
  ModelName VARCHAR(80) NOT NULL DEFAULT 'gpt-5.4-mini',
  SystemPrompt TEXT NULL,
  Status VARCHAR(40) NOT NULL DEFAULT 'Draft',
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_CrmChatbot_Project_Path (ProjectId, PublicChatPath),
  KEY IX_bee_CrmChatbot_Merchant_Status (MerchantId, Status),
  CONSTRAINT FK_bee_CrmChatbot_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmChatbot_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmKnowledgeDocument (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  ChatbotId BIGINT NULL,
  FileName VARCHAR(260) NOT NULL,
  ContentType VARCHAR(120) NULL,
  FileSizeBytes BIGINT NULL,
  SourceType VARCHAR(40) NOT NULL DEFAULT 'Document',
  Status VARCHAR(40) NOT NULL DEFAULT 'Uploaded',
  UploadedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  ProcessedAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmKnowledge_Project_Status (ProjectId, Status, UploadedAtUtc),
  KEY IX_bee_CrmKnowledge_Merchant (MerchantId, UploadedAtUtc),
  KEY IX_bee_CrmKnowledge_Chatbot (ChatbotId),
  CONSTRAINT FK_bee_CrmKnowledge_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmKnowledge_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmKnowledge_Chatbot FOREIGN KEY (ChatbotId)
    REFERENCES bee_CrmChatbot (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmConversation (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  ChatbotId BIGINT NULL,
  VisitorLabel VARCHAR(140) NULL,
  Channel VARCHAR(40) NOT NULL DEFAULT 'Web',
  Status VARCHAR(40) NOT NULL DEFAULT 'Open',
  MessageCount INT NOT NULL DEFAULT 0,
  ImageMessageCount INT NOT NULL DEFAULT 0,
  StartedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  LastMessageAtUtc DATETIME(6) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_CrmConversation_Project_Status (ProjectId, Status, LastMessageAtUtc),
  KEY IX_bee_CrmConversation_Merchant_Time (MerchantId, LastMessageAtUtc),
  KEY IX_bee_CrmConversation_Chatbot (ChatbotId),
  CONSTRAINT FK_bee_CrmConversation_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmConversation_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmConversation_Chatbot FOREIGN KEY (ChatbotId)
    REFERENCES bee_CrmChatbot (id) ON DELETE SET NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_CrmUsageDaily (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  MerchantId BIGINT NOT NULL,
  UsageDate DATE NOT NULL,
  ModelName VARCHAR(80) NOT NULL,
  PromptTokens BIGINT NOT NULL DEFAULT 0,
  CompletionTokens BIGINT NOT NULL DEFAULT 0,
  ImageCount INT NOT NULL DEFAULT 0,
  ConversationCount INT NOT NULL DEFAULT 0,
  MessageCount INT NOT NULL DEFAULT 0,
  EstimatedCostUsd DECIMAL(12,6) NOT NULL DEFAULT 0,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  UNIQUE KEY UX_bee_CrmUsage_Project_Merchant_Date_Model (ProjectId, MerchantId, UsageDate, ModelName),
  KEY IX_bee_CrmUsage_Project_Date (ProjectId, UsageDate),
  KEY IX_bee_CrmUsage_Merchant_Date (MerchantId, UsageDate),
  CONSTRAINT FK_bee_CrmUsage_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_CrmUsage_Merchant FOREIGN KEY (MerchantId)
    REFERENCES bee_CrmMerchant (id) ON DELETE CASCADE
) ENGINE=InnoDB;

INSERT INTO bee_CrmIndustry (ProjectId, Name, Slug, Description, SortOrder, IsActive)
SELECT project.id, seed.Name, seed.Slug, seed.Description, seed.SortOrder, 1
FROM bee_Project AS project
INNER JOIN (
  SELECT 'Retail and eCommerce' AS Name, 'retail-ecommerce' AS Slug, 'Online shops, retail stores, product support, order and refund questions.' AS Description, 10 AS SortOrder
  UNION ALL SELECT 'Hospitality', 'hospitality', 'Restaurants, cafes, hotels, bookings, menus, store hours, and guest support.', 20
  UNION ALL SELECT 'Professional Services', 'professional-services', 'Agencies, consultants, clinics, local services, and appointment-based businesses.', 30
  UNION ALL SELECT 'Education and Training', 'education-training', 'Schools, courses, onboarding, training material, and student support.', 40
  UNION ALL SELECT 'Software and SaaS', 'software-saas', 'Software products, technical documentation, onboarding, billing, and support workflows.', 50
) AS seed
WHERE project.ProjectName = 'crm.sentribee.ai'
ON DUPLICATE KEY UPDATE
  Name = VALUES(Name),
  Description = VALUES(Description),
  SortOrder = VALUES(SortOrder),
  IsActive = VALUES(IsActive),
  UpdatedAtUtc = UTC_TIMESTAMP(6);

INSERT INTO bee_ProjectRule (ProjectId, Dimension, RuleText, SourcePrompt)
SELECT project.id, seed.Dimension, seed.RuleText, seed.SourcePrompt
FROM bee_Project AS project
INNER JOIN (
  SELECT 'Merchant Management' AS Dimension,
    'Track registered business users, industry, plan, status, contact email, corp id, and chat.sentribee.ai/corpid public chat path.' AS RuleText,
    'Derived from crm.sentribee.ai product requirements.' AS SourcePrompt
  UNION ALL SELECT 'Bot Configuration',
    'Allow each merchant to create customer service chatbots with a support name, avatar, model setting, and ChatGPT-style public chat experience.',
    'Derived from crm.sentribee.ai product requirements.'
  UNION ALL SELECT 'Knowledge Base',
    'Ingest merchant-uploaded documents and screenshots into a historical knowledge base for customer support answers.',
    'Derived from crm.sentribee.ai product requirements.'
  UNION ALL SELECT 'Conversation Context',
    'Manage customer conversation context, message volume, image messages, channel, and open or closed conversation state.',
    'Derived from crm.sentribee.ai product requirements.'
  UNION ALL SELECT 'Usage Governance',
    'Track model tokens, chat messages, image uploads, conversation counts, and estimated model cost by merchant and day.',
    'Derived from crm.sentribee.ai product requirements.'
) AS seed
WHERE project.ProjectName = 'crm.sentribee.ai'
  AND NOT EXISTS (
    SELECT 1
    FROM bee_ProjectRule AS existingRule
    WHERE existingRule.ProjectId = project.id
      AND existingRule.RuleText = seed.RuleText
  );
