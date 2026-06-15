ALTER TABLE bee_Project
  ADD COLUMN Visibility VARCHAR(20) NOT NULL DEFAULT 'Private' AFTER WebsiteUrl;

UPDATE bee_Project
SET Visibility = 'Private'
WHERE Visibility IS NULL OR Visibility = '';
