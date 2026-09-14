# WeCom Robot Records in OA

The Open API callback writes verified messages to the OA database before acknowledging receipt. Set `WeComAibot__DatabaseEnabled=true` and `ConnectionStrings__WeComOaConnection` in the Open API service environment. The OA database is separate from the App database; do not substitute `DefaultConnection`.

Apply `sql/mysql/20260914_wecom_robot_archive.sql` to the OA database before enabling writes or deploying the OA conversations page. It adds only new tables. The existing JSON file remains a secondary archive and is imported on startup.

`bee_WeComAibotMessage` stores all delivered message types, original payload, normalized text, attachment metadata, sender ID, group ID, profile fields, and retry count. Unknown types and events are retained. The unique message key prevents duplicate OA messages. Media URLs are metadata only; they expire and are not downloaded/decrypted into permanent file storage by this change.

An explicit `bee_WeComAibotBinding` row associates a WeCom robot ID with an OA merchant and that merchant's CRM chatbot. Unbound messages stay in the inbox without merchant visibility. Never infer a merchant from numeric IDs in the App database. Pending messages are linked automatically after a binding is supplied. Bound conversations appear at `/oa/conversations`, grouped by robot plus group ID or private sender ID. Reads and alias edits enforce the signed-in merchant's ownership. The external Console repository and service are unchanged.

For authorized directory enrichment, set `WeComDirectory__BotId`, `WeComDirectory__CorpId`, and `WeComDirectory__Secret`. This uses a corporate self-built application's credentials, not callback Token/AESKey or the robot's long-connection Secret. Enrichment runs in the background; it converts encrypted user IDs, requests available directory names/avatars and caches the result. Missing authorization never prevents message receipt. These credentials are not configured by default.

Callbacks normally contain only `from.userid` and group `chatid`; they do not provide arbitrary group names or personal nicknames/avatars. Group names supplied by a payload are preserved; otherwise the OA conversation can have a manually saved alias. No unsupported group-directory lookup is attempted. New self-built applications generally need user OAuth authorization for avatars. A directory member name is not necessarily their personal WeChat nickname.

Official references:
- https://developer.work.weixin.qq.com/document/path/100719 (received messages and attachment expiry)
- https://developer.work.weixin.qq.com/document/path/101521 (encrypted ID conversion and visible scope)
- https://developer.work.weixin.qq.com/document/path/90196 (member fields and avatar authorization)

Validation: `dotnet build`, encrypted callback regression tests (`node tests/wecom-callback.mjs`), and the MySQL integration runner in `tests/WeComArchive.Tests`. The MySQL runner creates and removes an isolated test database and must never target an unapproved database name. Production service credentials need not be granted database-creation rights; use an isolated MySQL instance instead.
