ALTER TABLE bee_AppRiskNotification
  ADD COLUMN PushProviderMessageId VARCHAR(100) NULL AFTER PushStatus,
  ADD COLUMN PushAttemptedAtUtc DATETIME(6) NULL AFTER PushProviderMessageId,
  ADD COLUMN PushErrorText VARCHAR(500) NULL AFTER PushAttemptedAtUtc;
