CREATE TABLE IF NOT EXISTS bee_WeComAibotBinding (
  BotId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  MerchantId BIGINT NOT NULL,
  ChatbotId BIGINT NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  FOREIGN KEY (MerchantId) REFERENCES bee_CrmMerchant(id),
  FOREIGN KEY (ChatbotId) REFERENCES bee_CrmChatbot(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_WeComAibotMessage (
  MessageKey CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  BotId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ExternalMessageId VARCHAR(256) NOT NULL,
  MessageType VARCHAR(40) NOT NULL,
  UserId VARCHAR(256) NOT NULL,
  ChatId VARCHAR(256) NOT NULL,
  ChatType VARCHAR(32) NOT NULL,
  SenderName VARCHAR(180) NULL,
  SenderAvatarUrl VARCHAR(1000) NULL,
  ChatName VARCHAR(180) NULL,
  ProfileCheckedAtUtc DATETIME(6) NULL,
  ProfileStatus VARCHAR(40) NOT NULL DEFAULT 'not_configured',
  NormalizedJson JSON NOT NULL,
  DeliveryCount INT NOT NULL DEFAULT 1,
  ReceivedAtUtc DATETIME(6) NOT NULL,
  LastReceivedAtUtc DATETIME(6) NOT NULL,
  MerchantId BIGINT NULL,
  CrmMessageId BIGINT NULL,
  UNIQUE KEY UX_WeCom_CrmMessage (CrmMessageId),
  KEY IX_WeCom_Pending (CrmMessageId, ReceivedAtUtc),
  KEY IX_WeCom_Bot_User (BotId, UserId),
  FOREIGN KEY (MerchantId) REFERENCES bee_CrmMerchant(id),
  FOREIGN KEY (CrmMessageId) REFERENCES bee_CrmConversationMessage(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS bee_WeComAibotConversation (
  ConversationKey CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
  BotId VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  ConversationId BIGINT NOT NULL,
  UNIQUE KEY UX_WeCom_Conversation (ConversationId),
  FOREIGN KEY (ConversationId) REFERENCES bee_CrmConversation(id)
) ENGINE=InnoDB;
