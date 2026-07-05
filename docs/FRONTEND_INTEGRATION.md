# 前端串接指南

Mini-SSO 是純後端 API（ASP.NET Core，無內建畫面），前端需要自己實作登入頁與呼叫下列 API。所有登入狀態都是透過 **HttpOnly Cookie** 保存的 JWT，前端**拿不到、也不需要拿到** token 本身。

## 基本原則

- 所有登入方式（帳密 / Google / GitHub）成功後，後端都會在回應中設定名為 `token` 的 `HttpOnly` Cookie。前端 JS 讀不到這個 Cookie（這是刻意設計，防 XSS 竊取 token），但瀏覽器會自動在後續請求帶上它。
- 因此前端呼叫本 API 時，**一定要帶上 `credentials: 'include'`**（fetch）或 `withCredentials: true`（axios），否則瀏覽器不會送出/接收 Cookie。
- 後端需要在 `Cors:AllowedOrigins`（或部署時的 `CORS_ALLOWED_ORIGINS` 環境變數）加入你的前端網域，否則瀏覽器會擋下跨網域請求。CORS 設定支援多個網域，用逗號分隔。
- 呼叫 `[Authorize]` 保護的端點（例如登出）時不需要手動加 `Authorization: Bearer ...` header，Cookie 會自動帶上。
- `POST /api/auth`、`POST /api/auth/create`、`POST /api/auth/logout` 這三個「會改變狀態」的端點都需要 **CSRF token**，見下方「CSRF 保護」章節，不然會收到 `400`。
- `token` Cookie 目前是 `SameSite=Lax`；如果部署在 HTTPS 上，Cookie 會自動加上 `Secure`（依請求是否為 HTTPS 動態決定，本機 HTTP 開發不受影響）。

## CSRF 保護

後端用**簡易版雙提交 Cookie**模式做 CSRF 防護：呼叫任何**會改變狀態的 POST** 端點（`/api/auth`、`/api/auth/create`、`/api/auth/logout`）之前，都要先呼叫：

```
GET /api/auth/csrf
```
回傳 `{ "csrfToken": "..." }`，同時會設定一個非 HttpOnly 的 `XSRF-TOKEN` Cookie（前端 JS 可以讀到，值跟回應 body 的 `csrfToken` 完全一樣）。接著把這個值放進後續請求的 `X-CSRF-TOKEN` header：

```js
async function getCsrfToken() {
  const res = await fetch(`${API_BASE}/api/auth/csrf`, { credentials: 'include' });
  const { csrfToken } = await res.json();
  return csrfToken;
}

const csrfToken = await getCsrfToken();
await fetch(`${API_BASE}/api/auth`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
  credentials: 'include',
  body: JSON.stringify({ userName, password }),
});
```

驗證規則就是**`X-CSRF-TOKEN` header 的值要跟 `XSRF-TOKEN` cookie 的值完全相等**，沒有其他隱藏邏輯——所以你也可以直接讀 `document.cookie` 裡的 `XSRF-TOKEN` 值來當 header 用，效果一樣（很多前端框架，例如 Angular 的 `HttpClientXsrfModule`，預設就是這樣做的）：

```js
function getCookie(name) {
  return document.cookie.split('; ').find(row => row.startsWith(name + '='))?.split('=')[1];
}
// 等效寫法：headers: { 'X-CSRF-TOKEN': getCookie('XSRF-TOKEN') }
```

這個 token **不綁定登入狀態**，登入/登出前後都可以繼續用同一個，不需要在登入後重新呼叫 `/api/auth/csrf`。

若沒帶 token、或 header 跟 cookie 對不上，會收到 `400 Bad Request`（`title: "Invalid CSRF Token"`）。

## API 一覽

Base URL 以你部署的網址為準（本機開發預設 `http://localhost:5274`，Docker 部署預設 `http://localhost:12080`）。

### 帳密登入

```
POST /api/auth
Content-Type: application/json
X-CSRF-TOKEN: <從 /api/auth/csrf 拿到的 token>

{ "userName": "admin", "password": "admin123" }
```
- 成功：`200 OK`，並設定 `token` Cookie。
- 失敗（帳號不存在/密碼錯誤）：`400 Bad Request`。
- **同一個來源 IP 連續失敗 5 次會被鎖定 10 分鐘**：回傳 `429 Too Many Requests`，並帶 `Retry-After`（秒數）header，`detail` 訊息會告訴使用者還要等多久。就算密碼正確，鎖定期間一樣會被擋。鎖定是以 IP 為單位，不分帳號。

```js
const csrfToken = await getCsrfToken();
const res = await fetch(`${API_BASE}/api/auth`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
  credentials: 'include',
  body: JSON.stringify({ userName, password }),
});
if (res.status === 429) {
  const retryAfter = res.headers.get('Retry-After'); // 秒數
  // 顯示「登入失敗次數過多，請於 X 秒後再試」
}
```

### 註冊

```
POST /api/auth/create
Content-Type: application/json
X-CSRF-TOKEN: <從 /api/auth/csrf 拿到的 token>

{ "userName": "newuser", "password": "P@ssw0rd", "email": "user@example.com" }
```
- 成功：`200 OK`。
- 帳號重複：`400 Bad Request`，回傳 `ProblemDetails`，`errors.UserName` 會有錯誤訊息（`{ "errors": { "UserName": ["Username already exists."] } }`）。

### 帳號是否已被使用

```
GET /api/auth/valid/username?username=someone
```
回傳 `true`/`false`（`true` = 可使用，`false` = 已被註冊）。適合拿來做即時輸入檢查。

### 登出

```
POST /api/auth/logout
X-CSRF-TOKEN: <從 /api/auth/csrf 拿到的 token>
```
需要已登入（帶有效 `token` Cookie），成功後會撤銷該 JWT + refresh token 並清掉兩個 Cookie，回 `200 OK`；未登入呼叫會 `401 Unauthorized`。

```js
const csrfToken = await getCsrfToken(); // 跟登入前用的可以是同一個，不用重拿
await fetch(`${API_BASE}/api/auth/logout`, {
  method: 'POST',
  headers: { 'X-CSRF-TOKEN': csrfToken },
  credentials: 'include',
});
```

### 換發新的 access token（refresh）

登入（帳密或 SSO）成功後，後端除了 `token`（access token，短效，預設 60 分鐘過期）Cookie，還會多發一個 `refresh_token` Cookie（`HttpOnly`，只在呼叫 `/api/auth/*` 底下的端點時會被送出）。access token 過期後，不用叫使用者重新輸入帳密，呼叫這支就能換一組新的：

```
POST /api/auth/refresh
X-CSRF-TOKEN: <從 /api/auth/csrf 拿到的 token>
```
- 成功：`200 OK`，設定新的 `token` + `refresh_token` Cookie（舊的 refresh token 用過即失效，不能再用第二次）。
- 失敗（沒有 refresh token / 已過期 / 已經被用過）：`401 Unauthorized`，這時候才需要導去登入頁重新登入。

```js
async function refreshAccessToken() {
  const csrfToken = await getCsrfToken();
  const res = await fetch(`${API_BASE}/api/auth/refresh`, {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': csrfToken },
    credentials: 'include',
  });
  return res.ok;
}
```

實務上建議的用法：呼叫任何 `[Authorize]` 端點收到 `401` 時，先嘗試呼叫一次 `/api/auth/refresh`，成功的話原本那個請求重打一次；失敗才真的導去登入頁。這樣使用者的 session 可以撐到 refresh token 的有效期（預設 30 天），不會 60 分鐘沒動作就被踢出去。

```js
async function authorizedFetch(url, options = {}) {
  let res = await fetch(url, { ...options, credentials: 'include' });
  if (res.status === 401 && (await refreshAccessToken())) {
    res = await fetch(url, { ...options, credentials: 'include' }); // 重打一次
  }
  return res;
}
```

### 第三方登入（Google / GitHub）

第三方登入**不是**用 fetch/AJAX 呼叫，而是**整頁導向**（跟一般網站的「用 Google 登入」按鈕一樣）：

```html
<a href="http://localhost:12080/api/auth/external/google/login">使用 Google 登入</a>
<a href="http://localhost:12080/api/auth/external/github/login">使用 GitHub 登入</a>
```

或用 JS 導頁：
```js
window.location.href = `${API_BASE}/api/auth/external/google/login`;
```

流程：
1. 使用者點擊連結 → 瀏覽器整頁導向後端 → 後端再導向 Google/GitHub 的授權頁。
2. 使用者在 Google/GitHub 頁面同意授權。
3. Google/GitHub 導回後端的 callback（`/signin-google`、`/signin-github`，這一步前端不用處理）。
4. 後端處理完成後，會設定 `token` Cookie，並把瀏覽器**導向 `Frontend:RedirectUrl` 設定的網址**（例如 `http://localhost:3000/sso/callback`）。
5. 前端在這個 `RedirectUrl` 頁面，此時使用者已經登入完成（Cookie 已經設定好），可以直接呼叫其他需要登入的 API，或導去首頁。

> `Frontend:RedirectUrl` 是後端環境變數 `FRONTEND_REDIRECT_URL` 設定的，前端**不需要**在網址上帶任何 code/token 參數自己解析，登入狀態已經在 Cookie 裡了。

若某個 provider 尚未設定金鑰，點擊該連結會得到 `503 Service Unavailable`（純文字訊息），前端可以用這個狀態碼判斷「此登入方式尚未開放」並隱藏/停用對應按鈕（可另外呼叫後端提供的健康檢查或在前端硬編碼已知可用清單）。

## 判斷目前是否已登入 / 取得使用者資訊

```
GET /api/auth/me
```
- 已登入：`200 OK`，回傳：
  ```json
  {
    "userId": "ebf680c2-aa39-4503-9112-5da86a14d5d2",
    "userName": "admin",
    "email": "123@test.com",
    "hasPassword": true,
    "linkedProviders": ["Google", "GitHub"]
  }
  ```
- 未登入（沒有有效的 `token` Cookie）：`401 Unauthorized`。

前端進站時可以先呼叫這支 API：`200` 就代表已登入並直接拿到使用者資料可以渲染；`401` 就導去登入頁。`hasPassword` 可以用來判斷這個帳號是否為「純 SSO 帳號」（沒設密碼），`linkedProviders` 是這個帳號已經綁定的第三方登入清單，可以用來在「帳號設定」頁顯示已連結的登入方式。

```js
const res = await fetch(`${API_BASE}/api/auth/me`, { credentials: 'include' });
if (res.ok) {
  const me = await res.json();
} else {
  // 401，導去登入頁
}
```

## 常見錯誤排查

| 現象 | 可能原因 |
|---|---|
| fetch 一直失敗，Console 出現 CORS 錯誤 | 前端網域沒有加進後端的 `Cors:AllowedOrigins` |
| 登入 API 回 200 但下一支 API 卻 401 | fetch/axios 沒帶 `credentials: 'include'` / `withCredentials: true` |
| 點擊 Google/GitHub 登入後顯示 `redirect_uri_mismatch` | Google Cloud Console / GitHub OAuth App 裡設定的 callback URL 跟後端實際網址（`http(s)://<host>/signin-google` 或 `/signin-github`）不一致 |
| 點擊登入後端回 503 | 該 provider 尚未設定 `ClientId`/`ClientSecret` |
| `POST /api/auth`、`/create`、`/logout` 回 400，訊息是 CSRF 相關 | 沒帶 `X-CSRF-TOKEN` header、沒先呼叫 `GET /api/auth/csrf`，或是 header 的值跟 `XSRF-TOKEN` cookie 的值對不起來（兩者必須完全相等） |
| 登入回 429 | 同一 IP 連續失敗 5 次，鎖定 10 分鐘，看 `Retry-After` header 決定要等多久 |
| 短時間內大量請求後開始回 429（跟上面的登入鎖定無關） | 全站流量保護（依 IP，每 10 秒 30 次），等一下就會恢復 |
| `/api/auth/refresh` 一直回 401 | refresh token 已經被用過一次（rotate 機制，一次性）、已過期，或使用者已登出；這時候應該導去登入頁，不是重試 |
