# Chat expense evidence inbox

Apply `sql/mysql/20260921_chat_expense_inbox.sql` before deploying. This adds separate evidence, image and review-audit tables; it does not change payroll, payment or existing receipt tables.

Configure only on the OA service:

- `ChatExpenses__MerchantId`: verified active OA company ID.
- `ChatExpenses__TenantId`: the authorized Chat workspace ID.
- `ChatExpenses__Secret`: random shared HMAC secret of at least 32 characters, stored only in a protected environment file.

`POST /api/oa/chat-expenses` accepts signed `expense.evidence.collected` events from Chat. The payload cannot choose its destination company. HMAC covers timestamp and exact UTF-8 JSON body. Requests expire after five minutes; event ID plus payload hash ensures idempotency. Original image bytes and evidence are committed atomically. Conflicting duplicate event bodies return 409. Other tenants, unsigned requests and corrupted image hashes are rejected.

Company administrators open `/oa/reimbursements` for source messages, original images, classifications, suggested fields, relationships and warnings. All start pending review. Review labels describe evidence quality, not financial approval. Every review update has an audit row. Images require the same active company login, use no-store, and are never exposed through public static storage. Employee sessions cannot access this admin-only inbox.

Validation: `dotnet run --project tests/ChatExpenses`. Operator helper: `tools/ExpenseAdmin` supports `companies`, `migrate <sql-path>`, `counts`, and `version <assembly-path>`. Inject database credentials through the existing service environment; never put them in arguments or source control.

Deploy only `sentribee-oa`. The repository's historical `SentribeeConsole.Web` assembly name does not authorize changes to the separate Console service or repository.
