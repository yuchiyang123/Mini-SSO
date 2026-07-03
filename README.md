# Mini-SSO

輕量級的 SSO（Single Sign-On）驗證後端服務。提供帳號密碼登入，並整合 Google / GitHub 第三方登入，簽發統一的 JWT（以 HttpOnly Cookie 保存），供其他服務驗證使用者身分。

## 功能

- 帳號密碼註冊 / 登入 / 登出（JWT 存放於 HttpOnly Cookie）
- Google OAuth 登入
- GitHub OAuth 登入
- 同一個 Email 的帳密帳號與第三方登入帳號會自動綁定，不會產生重複使用者
- 尚未設定金鑰的第三方登入會回傳 `503`，不會讓整個服務掛掉
- 登入端點防暴力破解：同一 IP 連續失敗 5 次鎖定 10 分鐘
- 全站流量保護：所有端點依來源 IP 做流量限制（應用層 + nginx 層各一道）
- CSRF 防護（雙提交 Cookie 模式），涵蓋所有會改變狀態的 POST 端點
- JWT 撤銷機制：登出會讓那個 token 真的失效，不是只清 Cookie
- `GET /api/auth/me` 取得目前登入者資訊
- `GET /healthz` 健康檢查（含資料庫連線檢查），供 Docker/K8s 等 orchestrator 使用
- 預設管理員帳號（seed）可透過設定關閉或改成自訂帳密，不是寫死的後門
- nginx 反向代理（取代 IIS），對外只曝露一個入口，並在 nginx 層額外做一層 rate limit
- 設定的 Port 被占用時會給清楚的錯誤訊息並正常結束，不會噴一堆看不懂的 stack trace
- Serilog 結構化 log（含每個 HTTP 請求的存取記錄）
- 單元測試（xUnit）涵蓋 `AuthService` 的核心業務邏輯，CI 會實際擋下測試失敗
- Docker Compose 一鍵部署（nginx + API + SQL Server），全部服務都有 healthcheck

## 技術棧

- ASP.NET Core 9（Web API）
- Entity Framework Core 9 + SQL Server
- JWT Bearer（驗證）＋ Cookie（傳遞方式）
- Scalar（OpenAPI 文件 UI）
- Serilog（結構化 log）
- nginx（反向代理）
- xUnit + EF Core InMemory（單元測試）

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
Mini-SSO.Tests/
└── AuthServiceTests.cs                 # AuthService 單元測試（登入/鎖定/SSO 綁定/撤銷等）
docs/
└── FRONTEND_INTEGRATION.md            # 前端串接指南
nginx/
└── nginx.conf                         # 反向代理設定（對外入口，含 rate limit）
docker-compose.yml                     # nginx + API + SQL Server 一鍵部署
.env.example                           # 環境變數範本
```

## 資料模型

- **Users**：`UserId, UserName, Email, PasswordHash(可為 null), CreateAt, UpdateAt`。`PasswordHash` 為 `null` 代表這是純第三方登入建立的帳號，沒有密碼。
- **UserLogin**：`(Provider, ProviderKey)` 複合主鍵，對應到 `UserId`。一個使用者可以綁定多個第三方帳號；同一個第三方帳號只能綁定一個使用者（唯一索引）。
- **LoginAttempt**：以 `IpAddress` 為主鍵，記錄 `FailedCount`（連續失敗次數）、`LockedUntil`（鎖定到什麼時候）、`LastAttemptAt`。登入成功會重置；失敗達 5 次會設定 10 分鐘鎖定並歸零計數。
- **RevokedToken**：以 `Jti`（JWT ID）為主鍵，記錄 `ExpiresAt`（token 原本的自然過期時間）。登出時寫入一筆；`RevokedTokenCleanupService` 每小時清掉已經自然過期、沒必要再保留的紀錄。

## 認證流程

### 帳號密碼

`POST /api/auth` 驗證成功後，用 `AuthService.GenerateeToken` 簽發 JWT，寫入 `HttpOnly` 的 `token` Cookie（`SameSite=Lax`，`Secure` 依請求是否為 HTTPS 動態決定）。之後的請求由 `JwtBearer` 驗證這個 Cookie（見 `Program.cs` 的 `OnMessageReceived`，會從 `Request.Cookies["token"]` 讀 token，而不是走標準的 `Authorization` header）。

登入前會先檢查 `AuthService.EnsureNotLockedOutAsync`：依來源 IP 查 `LoginAttempts`，若在鎖定期內直接丟 `TooManyRequestsException`（→ `429`，帶 `Retry-After` header）。驗證完成後 `RecordLoginAttemptAsync` 會更新該 IP 的失敗計數；連續失敗達 5 次即鎖定 10 分鐘並歸零計數。為避免帳號枚舉，帳號不存在跟密碼錯誤回傳同一種訊息，且不會透露帳號是否存在。

### JWT 撤銷（登出真的會讓 token 失效）

原本登出只是清掉 `token` Cookie，JWT 本身在簽發時設定的到期時間之前，其實還是有效的 token（例如被複製到別的地方，到期前都還能用）。現在的做法：

- 登出時（`AuthController.Logout`），從目前的 JWT claims 取出 `jti`（token 的唯一 ID）和 `exp`，呼叫 `AuthService.RevokeTokenAsync` 寫進 `RevokedTokens` 表。
- `Program.cs` 的 `AddJwtBearer` 加了 `OnTokenValidated` 事件：每次驗證 JWT 時，額外查一次這個 `jti` 是否在 `RevokedTokens` 裡，有的話 `context.Fail(...)`，直接視為驗證失敗（`401`）。
- `RevokedTokenCleanupService`（`BackgroundService`）每小時清掉 `ExpiresAt` 已經過去的紀錄——這些 token 反正也自然過期了，沒必要留著占空間。

### 全站流量保護（Rate Limiting）

兩層防護，各自獨立：
- **應用層**：`Program.cs` 用 ASP.NET Core 內建的 `AddRateLimiter`，依來源 IP 分桶，每 10 秒最多 30 個請求（可爆發到部分緩衝），涵蓋所有端點。超過回 `429`。
- **nginx 層**：`nginx.conf` 的 `limit_req_zone`，作為第二層、更前置的防護。

跟登入鎖定（5 次失敗鎖 10 分鐘）是不同層次：登入鎖定是「防暴力破解密碼」，這裡的 rate limit 是「防單純的流量灌爆」，兩者互不取代。

### CSRF

用**簡易版雙提交 Cookie**：`GET /api/auth/csrf` 產生一個隨機 token（`Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))`），同時寫進非 HttpOnly 的 `XSRF-TOKEN` Cookie，也回在 JSON body 的 `csrfToken` 欄位。`Common/Filters/ApiAntiforgeryAttribute.cs` 套用在 `Login`、`Create`、`Logout` 三個端點，驗證規則就是 **`X-CSRF-TOKEN` header 的值要跟 `XSRF-TOKEN` cookie 的值完全相等**（`string.Equals(..., Ordinal)`），沒有其他隱藏邏輯。

一開始用的是 ASP.NET Core 內建的 `IAntiforgery`（`Program.cs` 的 `AddAntiforgery` + `IAntiforgery.ValidateRequestAsync`），後來換掉了，原因：
1. 內建版本的 cookie 值跟 header 值是**故意設計成不同**（同一把密鑰產生、互相配對驗證，不是字串相等），跟大多數前端框架/既有系統的慣例（例如 Angular 的 `HttpClientXsrfModule`：讀 `XSRF-TOKEN` cookie 值原封不動送回 header）對不上，串接既有前端系統時會直接撞 `AntiforgeryValidationException: The cookie token and the request token were swapped`。
2. 內建版本還會把 token 跟當下的登入狀態（claims）綁在一起，登入前拿的 token 登入後會失效，前端要多做「登入後重新拿一次」的處理。

換成簡易版之後兩個問題都不見了，前端怎麼串接看 [docs/FRONTEND_INTEGRATION.md](docs/FRONTEND_INTEGRATION.md) 的「CSRF 保護」章節。

> 順便留意：token 產生方式選用 **hex** 而不是 Base64。Base64 的 `/`、`+`、`=` 這幾個字元，ASP.NET Core 寫入 `Set-Cookie` 時會被 percent-encode（例如 `/` 變成 `%2F`），但 JSON body 回的是沒編碼過的原始值——如果前端是讀 `document.cookie`（瀏覽器不會自動解碼）取值來當 header 送，會跟伺服器內部解碼過的 cookie 值對不起來。Hex 只有 `0-9a-f`，沒有這個問題，這個坑我們真的踩過一次才發現，故意寫在這裡提醒。

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

## 部署拓樸：nginx 反向代理

Docker Compose 部署下，對外只有 `nginx` 這一個入口（取代原本規劃的 IIS），`api` 容器**不對外開 port**（`expose` 而非 `ports`），只有 `nginx` 能透過 compose 內部網路連到它。拓樸大致是：

```
外部請求 → nginx（:12080 對外，內部轉發到 api:8080）→ api（無對外 port）→ db
```

這個設計有兩個連動的地方要注意，都已經在 `Program.cs` 處理：

1. **`UseForwardedHeaders`**：因為所有請求實際上都是從 `nginx` 轉發過來的，`api` 直接看到的來源 IP、Scheme 都會是 nginx 的，不是真正的使用者。`nginx.conf` 有設定 `X-Forwarded-For`/`X-Forwarded-Proto`，`Program.cs` 用 `UseForwardedHeaders` 讀回來，覆寫 `HttpContext.Connection.RemoteIpAddress` 跟 `Request.Scheme`。**這是登入鎖定功能（依 IP 判斷）跟 Cookie 的 `Secure` 判斷能正確運作的前提**，少了這段，登入鎖定會鎖到 nginx 自己的 IP（等於全站共用一個鎖），Cookie 的 Secure 也會永遠判斷成 false。因為 `api` 沒有對外開 port，唯一進得來的只有 `nginx`，所以這裡直接信任所有來源的 forwarded header（沒有另外設定 `KnownProxies` 白名單）是安全的。
2. **nginx 層的 rate limit**：`nginx.conf` 用 `limit_req_zone` 對所有請求做一層基本的流量限制（每秒 20 個請求、可爆發到 40 個），跟應用層的登入鎖定是不同層次的防護，就算某個 action 還沒被寫成有鎖定邏輯，nginx 這層還是有基本防護。

之後要上真正的 HTTPS，`nginx.conf` 底部有註解好的範例（開 443、掛憑證），把註解打開、把憑證掛進 volume 即可，`Program.cs` 這邊不用改。

> **踩過的坑**：`nginx.conf` 的 `proxy_set_header Host` 一定要用 `$http_host`，不能用 `$host`——nginx 的 `$host` 變數**不含 port**，只會是 `localhost`，不會是 `localhost:12080`。這會導致後端算出來的 Google/GitHub OAuth `redirect_uri` 少了 port（變成 `http://localhost/signin-google`），跟你在 Google/GitHub 後台登記的網址（帶 port）對不起來，SSO 登入會一直失敗，而且錯誤訊息不會直接告訴你是這個原因，要自己 curl `/api/auth/external/google/login` 看 `Location` header 裡的 `redirect_uri` 才找得到。

## API 端點

| Method | Path | 說明 | 需要登入 | 需要 CSRF token |
|---|---|---|---|---|
| GET | `/api/auth/csrf` | 取得 CSRF token | 否 | - |
| POST | `/api/auth` | 帳密登入（同 IP 失敗 5 次鎖 10 分鐘） | 否 | 是 |
| POST | `/api/auth/create` | 註冊新帳號 | 否 | 是 |
| GET | `/api/auth/valid/username?username=` | 查詢帳號是否可用 | 否 | - |
| POST | `/api/auth/logout` | 登出（會撤銷目前的 JWT，不是只清 Cookie） | 是 | 是 |
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

> Redirect URI 一定要跟實際部署網址（含 port）完全一致，否則會出現 `redirect_uri_mismatch`。本機用 `dotnet run` 測試時預設是 `http://localhost:5274`；用 docker-compose 跑則是你在 `docker-compose.yml` 設定的對外 port（預設 `12080`）。

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

`docker-compose.yml` 會啟動三個服務：
- `db`：SQL Server 2022，資料存在 named volume `mssql-data`，有 healthcheck
- `api`：本專案，等 `db` healthy 後才啟動，啟動時自動跑 migration + seed；**不對外開 port**
- `nginx`：反向代理，對外的入口，等 `api` healthy 後才啟動

啟動完成後：
- API（透過 nginx）：`http://localhost:12080`
- API 文件：`http://localhost:12080/scalar/v1`

停止／清除：
```bash
docker compose down        # 停止，保留資料
docker compose down -v     # 停止並刪除資料庫資料
```

> 若你的 Windows 環境裡 `12080` 剛好落在系統保留的通訊埠範圍（`netsh interface ipv4 show excludedportrange protocol=tcp` 可查），`docker-compose up` 會綁定失敗，換一個 `docker-compose.yml` 裡 `nginx.ports` 的對外 port 即可（`api` 不對外開 port，不用改它）。

### Port 被占用時會發生什麼事

不管是本機 `dotnet run` 還是容器內，如果設定的 Port 已經被其他程式占用，服務會印出清楚的錯誤訊息並以非 0 的結束碼結束（不是丟一堆看不懂的 .NET stack trace）：

```
========================================
啟動失敗：設定的 Port 已經被其他程式占用，請更換 Port 號後再試一次。
可透過環境變數 ASPNETCORE_HTTP_PORTS（或 ASPNETCORE_URLS / --urls）指定其他 Port。
原始錯誤：Failed to bind to address http://127.0.0.1:5399: address already in use.
========================================
```
這是 `Program.cs` 裡包住 `app.RunAsync()` 的 try/catch 處理的（判斷 `IOException` 的內層例外是不是 `AddressInUseException`）。Docker Compose 場景下這個情況比較少見（`api` 不對外開 port，`nginx` 的對外 port 若衝突會是 Docker 自己回報 bind 失敗，像這次你遇到的 `8080` 落在 Windows 保留範圍，就是這種情況，不是這支程式的錯誤訊息），主要是本機開發、或多個環境共用同一台機器時會用到。

## 單元測試

`Mini-SSO.Tests` 用 xUnit + EF Core InMemory provider，針對 `AuthService` 的核心業務邏輯寫測試（不需要真的 SQL Server），涵蓋：登入成功/失敗/鎖定/不同 IP 互不影響、SSO 建立新帳號/綁定既有帳號/重複登入去重、`/me` 資料組裝、token 撤銷冪等性等。

```bash
dotnet test Mini-SSO.sln
```

CI（`.github/workflows/ci.yml`）現在會真的執行這個指令並在測試失敗時擋下建置（原本的 `continue-on-error: true` 已移除）。

> 目前只測 Service 層業務邏輯，沒有針對 Controller/HTTP 層或 CSRF/CORS/rate limit 這些中介軟體行為寫整合測試（`WebApplicationFactory`），這些目前是靠手動 curl 驗證（見下方 UAT），之後如果要更完整可以再補。

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
- [x] nginx：`api` 沒有對外開 port 也能透過 nginx 正常存取所有功能；直接打 `api` 的 port 連不到
- [x] `UseForwardedHeaders`：在 nginx 後面測試登入鎖定，`LoginAttempts` 記錄到的是外部真實來源 IP（Docker gateway），不是 nginx 容器自己的 IP，證明轉發標頭有正確被讀取
- [x] 本機測試「Port 被占用」情境：第二個實例啟動時印出清楚的中文錯誤訊息並正常結束，不是丟原始 stack trace
- [x] JWT 撤銷：登入拿到的 token 在登出前可正常呼叫 `/me`；登出後用**同一個** token 呼叫 `/me` 回 `401`（token 本身被撤銷，不是只清 Cookie）
- [x] 全站 rate limit：短時間內對同一端點打超過 30 次會回 `429`，跟登入鎖定各自獨立運作（分開測試驗證，避免互相干擾誤判）
- [x] Serilog：Docker log 正確顯示結構化格式（`[時間 INF] HTTP GET /healthz responded 200 in Xms` 等），Production 環境下也有請求層級的存取記錄
- [x] `dotnet test Mini-SSO.sln`：16 個單元測試全數通過
- [x] CSRF 相容性：模擬「讀 cookie 值原封不動送回 header」的常見前端慣例（例如 Angular 預設行為），確認可以正常登入/登出，不會撞 `AntiforgeryValidationException`
- [x] nginx `Host` header：確認 `curl .../api/auth/external/google/login` 拿到的 `Location` header 裡 `redirect_uri` 有帶正確 port（`$http_host` 修好後），不再是少 port 的 `http://localhost/signin-google`

## 安全性備註

- JWT 一律走 `HttpOnly` Cookie，前端 JS 無法讀取，降低 XSS 竊取風險；`SameSite=Lax` + `Secure`（依 HTTPS 動態決定）降低 CSRF/中間人風險。
- 另外針對 `Login`/`Create`/`Logout` 三個會改變狀態的端點，額外做了雙提交 Cookie 的 CSRF token 驗證（不只依賴 SameSite）。
- 密碼使用 ASP.NET Core Identity 的 `PasswordHasher`（PBKDF2）。
- 登入端點有 IP 層級的暴力破解防護（5 次失敗鎖 10 分鐘），失敗訊息不透露帳號是否存在，避免帳號枚舉。
- `appsettings.json` 中的密鑰皆為佔位假值，正式環境務必透過環境變數 / secret store 覆蓋，不要把真實密鑰提交進 git（`.env` 已在 `.gitignore`）。
- CORS 預設不開放任何網域（`AllowedOrigins` 為空時該 Policy 不允許任何來源），需明確設定才會放行。
- 預設管理員帳號（seed）在正式環境是個已知風險（帳密公開在原始碼裡），建議部署到真正的正式環境時把 `Seed:CreateDefaultAdmin` 設為 `false`，或至少透過 `Seed:AdminPassword` 換成強密碼。
- 登出會撤銷 JWT（見上方「JWT 撤銷」），不是只清 Cookie，降低 token 外洩後仍可長期使用的風險。
- 全站 rate limit（應用層 + nginx 層）降低單純流量灌爆/爬蟲對服務的影響，跟登入鎖定分屬不同層次的防護。
- **已知技術債**：`Microsoft.Extensions.Identity.Core` 曾經被誤鎖在 `10.0.9`（.NET 10 版本，跟專案的 `net9.0` 不符），導致發佈到容器後 `Microsoft.AspNetCore.Cryptography.Internal.dll` 版本錯位，任何用到 DataProtection/Antiforgery 的請求都會 500（`MissingMethodException`）。已改回 `9.0.17`；之後升級任何 `Microsoft.*` 套件時要注意版本要跟 `TargetFramework` 一致，不要只挑「最新版」。
