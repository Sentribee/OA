# WeChat Customer Service callback

This is independent of the AI robot callback. External Console is not managed here.

- GET/POST `/api/sentribeeai/v1/wecom/kf/callback`
- Configuration: `WeComKf__CorpId`, `WeComKf__AgentId` (operator reference),
  `WeComKf__Secret`, `WeComKf__Token`, `WeComKf__EncodingAESKey`.
- AgentId is not required by the KF sync API; Secret must belong to the self-built
  app authorized under WeChat Customer Service's callable applications.
- Uses `ConnectionStrings__WeComOaConnection` and `WeComAibot__DatabaseEnabled=true`.
- Apply `sql/mysql/20260914_wecom_robot_archive.sql`, then
  `sql/mysql/20260914_wecom_kf_sync.sql` to the OA database, not the App database.
- Keep credentials only in the protected server environment file. Do not commit them.

Encrypted XML notifications are signature-checked and recipient-checked, then
persisted before acknowledgment. The background worker pulls `kf/sync_msg`,
persists all returned types (including unknown raw payloads), checkpoints the
cursor only after persistence, and retries failures. Empty pages with `has_more=1`
are not treated as completion. A MySQL named lock serializes each account; a
notification version counter preserves notifications arriving during a sync.

Customer profiles are best-effort and cannot block message persistence. Human
agent messages are linked to the customer conversation, not the agent's identity.
Attachments currently retain media IDs and metadata, not permanent media binaries.
API-sent messages are not returned by the platform sync API. Only the last three
days are recoverable when starting without a cursor.

## OA ownership

Records enter `bee_WeComAibotMessage` with `source=wecom_kf`. The archive binding ID
is `kf:` plus lowercase SHA256 of `CorpId + "\n" + OpenKfId`.
Insert a binding into `bee_WeComAibotBinding` only after the administrator confirms
the OA MerchantId and a ChatbotId belonging to that merchant and project. Until
then, records stay unbound and are not exposed to arbitrary tenants. Pending
records are linked automatically after binding. OA conversations use channel
`WeChatKf`, sender roles `User`, `Agent`, `Event`, and typed details.

## Platform setup and limitations

1. Select the authorized self-built app in WeChat Customer Service API settings.
2. Set the callback URL and matching Token/AESKey; add the server egress IP to the
   app's trusted IP list if requested by the platform.
3. Resolve domain entity verification with Tencent; uploading the ownership TXT
   file does not replace ICP/entity checks.
4. Confirm the operational routing plan before selecting live customer-service
   accounts for API management. That setting disables the platform's original
   routing rules. This integration does not auto-reply or assign conversations.
5. Send a real test from WeChat, verify the durable queue's LastError/cursor and
   the OA inbox, then verify tenant-bound conversation visibility.

Health booleans indicate configured fields, not proof of a successful real callback.
Check queue state without displaying NotifyToken or credentials:

```sql
SELECT CorpId,OpenKfId,RequestedVersion,CompletedVersion,LastSuccessAtUtc,LastError
FROM bee_WeComKfSync;
```

Official references:
- https://developer.work.weixin.qq.com/document/path/94638
- https://developer.work.weixin.qq.com/document/path/94670
- https://developer.work.weixin.qq.com/document/path/95159

## Tests

`tests/WeComArchive.Tests` uses a throwaway MySQL database (never a live schema),
including crypto padding/recipient checks, account isolation, cursor pagination,
concurrent notification generations, failure retries, profiles and agent roles.
Set `WECOM_TEST_CONNECTION_STRING` to an isolated MySQL instance and run from the
repository root. `node tests/wecom-callback.mjs` covers robot regression behavior.
