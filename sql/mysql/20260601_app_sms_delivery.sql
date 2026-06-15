CREATE TABLE IF NOT EXISTS bee_AppSmsDelivery (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  VerificationCodeId BIGINT NULL,
  PhoneNumber VARCHAR(40) NOT NULL,
  Purpose VARCHAR(40) NOT NULL,
  Provider VARCHAR(40) NOT NULL DEFAULT 'Vonage',
  ProviderMessageId VARCHAR(120) NULL,
  RequestStatus VARCHAR(40) NOT NULL,
  DeliveryStatus VARCHAR(80) NULL,
  ErrorCode VARCHAR(40) NULL,
  ErrorText VARCHAR(500) NULL,
  RawResponseJson JSON NULL,
  DeliveryReceiptJson JSON NULL,
  SentAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  DeliveredAtUtc DATETIME(6) NULL,
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_AppSmsDelivery_Project_Time (ProjectId, SentAtUtc),
  KEY IX_bee_AppSmsDelivery_MessageId (ProviderMessageId),
  CONSTRAINT FK_bee_AppSmsDelivery_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_AppSmsDelivery_Verification FOREIGN KEY (VerificationCodeId)
    REFERENCES bee_AppUserVerificationCode (id) ON DELETE SET NULL
) ENGINE=InnoDB;
