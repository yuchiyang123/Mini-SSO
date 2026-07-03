# Mini-SSO

輕量級的 SSO（Single Sign-On）驗證後端服務。提供帳號密碼登入，並整合 Google / GitHub 第三方登入，簽發統一的 JWT（以 HttpOnly Cookie 保存），供其他服務驗證使用者身分。

## 功能

- 帳號密碼註冊 / 登入 / 登出（JWT 存放於 HttpOnly Cookie）
- Google OAuth 登入
- GitHub OAuth 登入
- 同一個 Email 的帳密帳號與第三方登入帳號會自動綁定，不會產生重複使用者
- 尚未設定金鑰的第三方登入會回傳 `503`，不會讓整個服務掛掉
- 登入端點防暴力破解：同一 IP 連續失敗 5 次鎖定 10 分鐘
- CSRF 防護（雙提交 Cookie 模式），涵蓋所有會改變狀態的 POST 端點
- `GET /api/auth/me` 取得目前登入者資訊
- `GET /healthz` 健康檢查（含資料庫連線檢查），供 Docker/K8s 等 orchestrator 使用
- 預設管理員帳號（seed）可透過設定關閉或改成自訂帳密，不是寫死的後門
- Docker Compose 一鍵部署（API + SQL Server），含 API healthcheck

## 技術棧

- ASP.NET Core 9（Web API）
- Entity Framework Core 9 + SQL Server
- JWT Bearer（驗證）＋ Cookie（傳遞方式）
- Scalar（OpenAPI 文件 UI）

> 原本有用 AutoMapper 做 DTO→Entity 映射，但 AutoMapper 自 v13 起改為商業授權（Lucky Penny Software），正式營運需要付費授權。由於專案裡只有一個單欄位映射，已改為手動映射並移除這個依賴，避免授權風險。

## 專案結構

```
Mini-SSO/
├── Controllers/AuthController.cs      # 登入/註冊/登出/第三方登入/CSRF/me 端點
├── Services/AuthService.cs            # 業務邏輯（含 ExternalLoginAsync、登入鎖定邏輯）
├── Model/
│   ├── Entities/                      # EF Core Entity（Users, UserLogin, LoginAttempt, AuthContext）
│   └── Dtos/                          # LoginDto, CreateUserDto, CurrentUserDto
├── Common/
│   ├── Enums/ProviderEnums.cs         # 支援的登入 provider 列舉
│   ├── Exceptions/                    # 自訂例外，統一由 GlobalExceptionHandler 處理
│   └── Filters/ApiAntiforgeryAttribute.cs  # 純 API 專案用的 CSRF 驗證 filter
├── Middleware/GlobalExceptionHandler.cs
├── Seed/SeedUser.cs                   # 啟動時若無使用者，可選擇建立預設帳號（可設定關閉/改帳密）
├── Migrations/                        # EF Core migrations
├── Program.cs                         # 組合根：JWT / OAuth / CORS / CSRF / 健康檢查 / DB / 安全標頭
└── Dockerfile
docs/
└── FRONTEND_INTEGRATION.md            # 前端串接指南
docker-compose.yml                     # API + SQL Server 一鍵部署
.env.example                           # 環境變數範本
```

## 資料模型

- **Users**：`UserId, UserName, Email, PasswordHash(可為 null), CreateAt, UpdateAt`。`PasswordHash` 為 `null` 代表這是純第三方登入建立的帳號，沒有密碼。
- **UserLogin**：`(Provider, ProviderKey)` 複合主鍵，對應到 `UserId`。一個使用者可以綁定多個第三方帳號；同一個第三方帳號只能綁定一個使用者（唯一索引）。
- **LoginAttempt**：以 `IpAddress` 為主鍵，記錄 `FailedCount`（連續失敗次數）、`LockedUntil`（鎖定到什麼時候）、`LastAttemptAt`。登入成功會重置；失敗達 5 次會設定 10 分鐘鎖定並歸零計數。

## 認證流程

### 帳號密碼

`POST /api/auth` 驗證成功後，用 `AuthService.GenerateeToken` 簽發 JWT，寫入 `HttpOnly` 的 `token` Cookie（`SameSite=Lax`，`Secure` 依請求是否為 HTTPS 動態決定）。之後的請求由 `JwtBearer` 驗證這個 Cookie（見 `Program.cs` 的 `OnMessageReceived`，會從 `Request.Cookies["token"]` 讀 token，而不是走標準的 `Authorization` header）。

登入前會先檢查 `AuthService.EnsureNotLockedOutAsync`：依來源 IP 查 `LoginAttempts`，若在鎖定期內直接丟 `TooManyRequestsException`（→ `429`，帶 `Retry-After` header）。驗證完成後 `RecordLoginAttemptAsync` 會更新該 IP 的失敗計數；連續失敗達 5 次即鎖定 10 分鐘並歸零計數。為避免帳號枚舉，帳號不存在跟密碼錯誤回傳同一種訊息，且不會透露帳號是否存在。

### CSRF

`Program.cs` 用 `AddAntiforgery` 註冊雙提交 Cookie 模式（header 名稱 `X-CSRF-TOKEN`，Cookie 名稱 `XSRF-TOKEN`，非 HttpOnly 讓前端 JS 可讀）。因為這是純 `AddControllers()` 的 API 專案（沒有 `AddControllersWithViews`），內建的 `[ValidateAntiForgeryToken]` 屬性依賴的過濾器服務不會被註冊，所以另外寫了 `Common/Filters/ApiAntiforgeryAttribute.cs`，直接呼叫 `IAntiforgery.ValidateRequestAsync` 達到一樣的效果，套用在 `Login`、`Create`、`Logout` 三個端點。`GET /api/auth/csrf` 負責簽發 token。

### 第三方登入（Google / GitHub）

1. `GET /api/auth/external/{provider}/login` → 用 `ExternalCookie`（暫存 sign-in scheme）發起 Challenge，導向 provider 的授權頁。
2. Provider 導回內建的 callback path（`/signin-google`、`/signin-github`，由對應的 Authentication Handler 處理，不會進到 Controller）。
3. Handler 驗證成功後導向 `GET /api/auth/external/{provider}/callback`（我們自己的 Controller action）。
4. Controller 讀出 `ExternalCookie` 裡的 claims（`NameIdentifier` 當作 `ProviderKey`、`Email`），呼叫 `AuthService.ExternalLoginAsync`：
   - 已有對應的 `UserLogin` → 直接回傳綁定的使用者。
   - 沒有 `UserLogin` 但 Email 已存在 → 綁定到既有帳號。
   - 都沒有 → 建立新使用者（`PasswordHash = null`）+ 新的 `UserLogin`。
5. 簽發本地 JWT，寫入 `token` Cookie，導向 `Frontend:RedirectUrl`。

未設定 `ClientId`/`ClientSecret` 的 provider **完全不會被註冊**到 ASP.NET Core 的 Authentication 中介層（見 `Program.cs` 的 `isGoogleConfigured` / `isGitHubConfigured` 判斷），呼叫其 `/login` 會回 `503`，不會導致服務啟動失敗或整個 API 500。

> GitHub 的 `/user` API 在使用者沒公開 Email 時不會回傳 Email，因此登入流程加了 `OnCreatingTicket` fallback：呼叫 `https://api.github.com/user/emails` 補抓已驗證的主要 Email。

## API 端點

| Method | Path | 說明 | 需要登入 | 需要 CSRF token |
|---|---|---|---|---|
| GET | `/api/auth/csrf` | 取得 CSRF token | 否 | - |
| POST | `/api/auth` | 帳密登入（同 IP 失敗 5 次鎖 10 分鐘） | 否 | 是 |
| POST | `/api/auth/create` | 註冊新帳號 | 否 | 是 |
| GET | `/api/auth/valid/username?username=` | 查詢帳號是否可用 | 否 | - |
| POST | `/api/auth/logout` | 登出 | 是 | 是 |
| GET | `/api/auth/me` | 取得目前登入者資料（UserId/UserName/Email/是否有密碼/已綁定的 provider） | 是 | - |
| GET | `/api/auth/external/{provider}/login` | 觸發第三方登入（`google` / `github`） | 否 | - |
| GET | `/api/auth/external/{provider}/callback` | 第三方登入回呼（供 provider 導回，不用手動呼叫） | 否 | - |
| GET | `/healthz` | 健康檢查（含 DB 連線檢查） | 否 | - |

啟動後可到 `/scalar/v1` 看互動式 API 文件。

前端如何呼叫這些 API，請看 [docs/FRONTEND_INTEGRATION.md](docs/FRONTEND_INTEGRATION.md)。

## 環境設定

所有可設定項目都定義在 `Mini-SSO/appsettings.json`（本機開發用預設值/佔位空字串），部署時用環境變數覆蓋（ASP.NET Core 慣例：`Section:Key` → 環境變數 `Section__Key`）：

| 環境變數 | 對應設定 | 說明 |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `ConnectionStrings:DefaultConnection` | SQL Server 連線字串 |
| `Jwt__Key` / `Jwt__Issuer` / `Jwt__Audience` / `Jwt__ExpireMinutes` | `Jwt:*` | JWT 簽章金鑰與有效期 |
| `Authentication__Google__ClientId` / `...ClientSecret` | `Authentication:Google:*` | Google OAuth 憑證 |
| `Authentication__GitHub__ClientId` / `...ClientSecret` | `Authentication:GitHub:*` | GitHub OAuth 憑證 |
| `Frontend__RedirectUrl` | `Frontend:RedirectUrl` | SSO 登入完成後導回的前端網址 |
| `Cors__AllowedOrigins` | `Cors:AllowedOrigins` | 允許呼叫本 API 的前端網域，逗號分隔 |
| `Seed__CreateDefaultAdmin` | `Seed:CreateDefaultAdmin` | 是否在資料庫是空的時候建立預設管理員帳號，預設 `true`；正式環境建議設 `false` |
| `Seed__AdminUserName` / `...AdminPassword` / `...AdminEmail` | `Seed:*` | 預設管理員帳號的帳密/信箱，未設定則用 `admin` / `admin123` / `123@test.com` |

## 申請第三方登入金鑰

### Google

1. 前往 [Google Cloud Console → API 和服務 → 憑證](https://console.cloud.google.com/apis/credentials)
2. 建立「OAuth 用戶端 ID」，應用程式類型選「網頁應用程式」
3. 已授權的重新導向 URI 填：`http(s)://<你的網域或host:port>/signin-google`
4. 取得 `Client ID` / `Client Secret`，填入 `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`

### GitHub

1. 前往 GitHub → Settings → Developer settings → OAuth Apps → New OAuth App
2. Authorization callback URL 填：`http(s)://<你的網域或host:port>/signin-github`
3. 取得 `Client ID`，並產生一組 `Client Secret`，填入 `GITHUB_CLIENT_ID` / `GITHUB_CLIENT_SECRET`

> Redirect URI 一定要跟實際部署網址（含 port）完全一致，否則會出現 `redirect_uri_mismatch`。本機用 `dotnet run` 測試時預設是 `http://localhost:5274`；用 docker-compose 跑則是你在 `docker-compose.yml` 設定的對外 port（預設 `8080`）。

> ~~Microsoft Entra ID~~：原規劃有納入，目前依需求不提供；`Common/Enums/ProviderEnums.cs` 中仍保留 `Microsoft` 列舉值供未來擴充，但沒有對應的登入流程程式碼。

## 本機開發（不用 Docker）

```bash
cd Mini-SSO
dotnet user-secrets set "Jwt:Key" "your-super-secret-key-at-least-32-chars"
dotnet user-secrets set "Authentication:Google:ClientId" "..."
dotnet user-secrets set "Authentication:Google:ClientSecret" "..."
dotnet user-secrets set "Authentication:GitHub:ClientId" "..."
dotnet user-secrets set "Authentication:GitHub:ClientSecret" "..."
dotnet run
```

需要本機或可連線的 SQL Server（`appsettings.Development.json` 或 user-secrets 設定連線字串）。啟動時會自動執行 EF Core migration 並在資料庫是空的時候建立預設帳號 `admin` / `admin123`。

## Docker 一鍵部署

```bash
cp .env.example .env
# 編輯 .env，填入 SA_PASSWORD / JWT_KEY / GOOGLE_*/GITHUB_* / CORS_ALLOWED_ORIGINS 等

docker compose up -d --build
```

`docker-compose.yml` 會啟動兩個服務：
- `db`：SQL Server 2022，資料存在 named volume `mssql-data`，有 healthcheck
- `api`：本專案，等 `db` healthy 後才啟動，啟動時自動跑 migration + seed

啟動完成後：
- API：`http://localhost:8080`
- API 文件：`http://localhost:8080/scalar/v1`

停止／清除：
```bash
docker compose down        # 停止，保留資料
docker compose down -v     # 停止並刪除資料庫資料
```

> 若你的 Windows 環境裡 `8080` 剛好落在系統保留的通訊埠範圍（`netsh interface ipv4 show excludedportrange protocol=tcp` 可查），`docker-compose up` 會綁定失敗，換一個 `docker-compose.yml` 裡 `api.ports` 的對外 port 即可。

## 測試紀錄（UAT）

以下項目已在 Docker Compose（全新 volume、`--no-cache` 重build）與本機 `dotnet run` 環境下手動驗證通過：

- [x] 全新環境 `docker compose up` 自動建表 + seed 預設帳號，不需手動介入
- [x] 帳密登入成功／密碼錯誤回 400／登出後 Cookie 被清除
- [x] 未帶 Cookie 呼叫 `/api/auth/logout` 回 401
- [x] Google 登入：成功建立新帳號（`PasswordHash` 為 `null`），`UserLogin` 正確記錄 `(Provider, ProviderKey)`
- [x] GitHub 登入：同上，且驗證了 Email 私人時的 fallback 抓取邏輯
- [x] 同一個第三方帳號重複登入 → 綁回原本的使用者，不產生重複帳號
- [x] 未設定金鑰的 provider 呼叫 `/login` 回 `503`，服務其餘功能不受影響
- [x] CORS：白名單網域的 preflight 請求正確回傳 `Access-Control-Allow-Origin`，非白名單網域被擋下
- [x] CSRF：沒帶 `X-CSRF-TOKEN` 呼叫 `/api/auth`、`/create`、`/logout` 回 `400`；帶正確 token 可正常呼叫
- [x] 登入鎖定：連續 5 次密碼錯誤後，第 6 次（即使密碼正確）回 `429`，`Retry-After` 為剩餘鎖定秒數；`LoginAttempts` 資料表狀態正確
- [x] `GET /api/auth/me`：未登入回 `401`，登入後正確回傳使用者資料
- [x] `GET /healthz`：回 `200 Healthy`；Docker Compose 的 healthcheck 正確反映在 `docker compose ps`（`healthy`）
- [x] `dotnet build` / Docker image（`--no-cache`）建置皆無錯誤與警告

## 安全性備註

- JWT 一律走 `HttpOnly` Cookie，前端 JS 無法讀取，降低 XSS 竊取風險；`SameSite=Lax` + `Secure`（依 HTTPS 動態決定）降低 CSRF/中間人風險。
- 另外針對 `Login`/`Create`/`Logout` 三個會改變狀態的端點，額外做了雙提交 Cookie 的 CSRF token 驗證（不只依賴 SameSite）。
- 密碼使用 ASP.NET Core Identity 的 `PasswordHasher`（PBKDF2）。
- 登入端點有 IP 層級的暴力破解防護（5 次失敗鎖 10 分鐘），失敗訊息不透露帳號是否存在，避免帳號枚舉。
- `appsettings.json` 中的密鑰皆為佔位假值，正式環境務必透過環境變數 / secret store 覆蓋，不要把真實密鑰提交進 git（`.env` 已在 `.gitignore`）。
- CORS 預設不開放任何網域（`AllowedOrigins` 為空時該 Policy 不允許任何來源），需明確設定才會放行。
- 預設管理員帳號（seed）在正式環境是個已知風險（帳密公開在原始碼裡），建議部署到真正的正式環境時把 `Seed:CreateDefaultAdmin` 設為 `false`，或至少透過 `Seed:AdminPassword` 換成強密碼。
- **已知技術債**：`Microsoft.Extensions.Identity.Core` 曾經被誤鎖在 `10.0.9`（.NET 10 版本，跟專案的 `net9.0` 不符），導致發佈到容器後 `Microsoft.AspNetCore.Cryptography.Internal.dll` 版本錯位，任何用到 DataProtection/Antiforgery 的請求都會 500（`MissingMethodException`）。已改回 `9.0.17`；之後升級任何 `Microsoft.*` 套件時要注意版本要跟 `TargetFramework` 一致，不要只挑「最新版」。
