ALTER TABLE bee_EdgeDevice
  ADD COLUMN ServerResourceInstanceName VARCHAR(80) NULL AFTER IpAddress;

UPDATE bee_EdgeDevice
SET ServerResourceInstanceName = 'i-05a6a5077f2ee8dd4'
WHERE ServerResourceInstanceName IS NULL;
