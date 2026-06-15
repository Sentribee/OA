ALTER TABLE bee_EdgeDevice
  ADD COLUMN BindingCode VARCHAR(16) NULL AFTER ServerResourceInstanceName,
  ADD UNIQUE KEY UX_bee_EdgeDevice_BindingCode (BindingCode);
