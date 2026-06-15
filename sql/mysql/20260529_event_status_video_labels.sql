ALTER TABLE bee_EdgeEvent
  MODIFY COLUMN Status VARCHAR(40) NOT NULL DEFAULT 'Real Risk';

UPDATE bee_EdgeEvent
SET Status = 'Real Risk'
WHERE Status = 'Unhandled';

UPDATE bee_EdgeEvent
SET Status = 'Trained'
WHERE Status = 'Confirmed';
