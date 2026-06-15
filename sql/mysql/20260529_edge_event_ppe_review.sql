SET @ppe_review_column_exists = (
  SELECT COUNT(*)
  FROM information_schema.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'bee_EdgeEvent'
    AND COLUMN_NAME = 'PpeReviewJson'
);

SET @ppe_review_sql = IF(
  @ppe_review_column_exists = 0,
  'ALTER TABLE bee_EdgeEvent ADD COLUMN PpeReviewJson JSON NULL AFTER YoloLabelUrl',
  'SELECT 1'
);

PREPARE ppe_review_stmt FROM @ppe_review_sql;
EXECUTE ppe_review_stmt;
DEALLOCATE PREPARE ppe_review_stmt;
