CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptGroup (
  id BIGINT NOT NULL AUTO_INCREMENT,
  ProjectId INT NOT NULL,
  AppUserId INT NOT NULL,
  Title VARCHAR(160) NOT NULL,
  Description VARCHAR(500) NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UpdatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
  PRIMARY KEY (id),
  KEY IX_bee_SpendBeeReceiptGroup_User_Time (AppUserId, CreatedAtUtc),
  CONSTRAINT FK_bee_SpendBeeReceiptGroup_Project FOREIGN KEY (ProjectId)
    REFERENCES bee_Project (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceiptGroup_User FOREIGN KEY (AppUserId)
    REFERENCES bee_AppUser (id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS bee_SpendBeeReceiptGroupReceipt (
  ReceiptGroupId BIGINT NOT NULL,
  ReceiptId BIGINT NOT NULL,
  CreatedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (ReceiptGroupId, ReceiptId),
  KEY IX_bee_SpendBeeReceiptGroupReceipt_Receipt (ReceiptId),
  CONSTRAINT FK_bee_SpendBeeReceiptGroupReceipt_Group FOREIGN KEY (ReceiptGroupId)
    REFERENCES bee_SpendBeeReceiptGroup (id) ON DELETE CASCADE,
  CONSTRAINT FK_bee_SpendBeeReceiptGroupReceipt_Receipt FOREIGN KEY (ReceiptId)
    REFERENCES bee_SpendBeeReceipt (id) ON DELETE CASCADE
) ENGINE=InnoDB;
